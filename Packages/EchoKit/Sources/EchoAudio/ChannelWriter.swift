import Accelerate
import AudioToolbox
import AVFoundation
import Darwin
import EchoCore
import Synchronization

public enum ChannelWriterError: Error, Sendable, Equatable {
    case createFile(OSStatus)
    case setClientFormat(OSStatus)
    case openFile(errno: Int32)
    /// The source is not mono, or its rate differs from the ring's.
    case unsupportedFormat(sampleRate: Double, channels: Int, ringSampleRate: Double)
    case converterUnavailable(sampleRate: Double)
    /// `NSError.code` of the failed `AVAudioConverter.convert`.
    case conversion(code: Int)
    case write(OSStatus)
    case close(OSStatus)
    /// `start` called twice or after `stop`, or `replaceRing` before `start`.
    case invalidState
}

/// Drains the mic and system rings, aligns them by host time and writes a 2-channel Int16 16 kHz CAF
/// (ch0 = mic, ch1 = system, see `RecordingSession.Channel`).
///
/// `@unchecked Sendable`: all state lives on a private utility queue; the public API hops onto it.
public final class ChannelWriter: @unchecked Sendable {
    public enum Channel: Sendable {
        case mic, system

        var index: Int { self == .mic ? RecordingSession.Channel.mic : RecordingSession.Channel.system }
    }

    /// The format of a capture ring: mono Float32 at `sampleRate`.
    public struct SourceFormat: Sendable, Equatable {
        public let sampleRate: Double
        public let channels: Int

        public init(sampleRate: Double, channels: Int) {
            self.sampleRate = sampleRate
            self.channels = channels
        }

        public init(_ format: MicCapture.Format) {
            self.init(sampleRate: format.sampleRate, channels: format.channels)
        }

        public init(_ format: ProcessTap.Format) {
            self.init(sampleRate: format.sampleRate, channels: format.channels)
        }
    }

    public struct Stats: Sendable, Equatable {
        public var framesWritten: Int64 = 0
        /// Continuous trailing duration under -60 dBFS.
        public var micSilentSeconds: Double = 0
        public var systemSilentSeconds: Double = 0
        /// System channel all zeros since the start, for 5 s at least, while `isTargetPlayingOutput` reports output.
        /// Zeros after sound mean the target went quiet (a call ended with its output still open), not a blocked tap.
        public var silentTapSuspected = false
        /// First write-path error; writing stops after it and `stop()` rethrows it.
        public var failure: ChannelWriterError?

        public var duration: Double { Double(framesWritten) / ChannelWriter.sampleRate }
    }

    public static let sampleRate: Double = 16_000
    public static let channelCount = 2

    /// Called on the writer queue with each newly written block: interleaved samples (ch0 mic, ch1 system),
    /// frame count, channel count. Read at `start`.
    public var liveSink: ((UnsafePointer<Int16>, Int, Int) -> Void)?
    /// Whether the captured app is currently producing output. Read at `start`; called on the writer queue.
    public var isTargetPlayingOutput: (@Sendable () -> Bool)?

    public var stats: Stats { statsLock.withLock { $0 } }

    private enum State { case idle, running, paused, stopped }

    private static let drainInterval = DispatchTimeInterval.milliseconds(200)
    private static let syncInterval: UInt64 = 15_000_000_000
    private static let jitterTolerance = 0.02 * sampleRate
    private static let maxLag = 1.0 * sampleRate
    private static let silenceWindow = 1_600
    private static let silenceThreshold: Float = 0.001
    private static let zeroThreshold: Float = 1 / 32_768
    private static let silentTapFrames = 5 * 16_000

    private let queue = DispatchQueue(label: "EchoDiar.ChannelWriter", qos: .utility)
    private let queueKey = DispatchSpecificKey<Void>()
    private let statsLock = Mutex(Stats())
    private let hostTicksPerSecond: Double

    private var file: ExtAudioFileRef?
    private var fd: Int32 = -1
    private var state = State.idle
    private var timer: DispatchSourceTimer?
    private var streams: [Stream] = []
    private var sink: ((UnsafePointer<Int16>, Int, Int) -> Void)?
    private var targetPlaying: (@Sendable () -> Bool)?
    private var baseHost: UInt64 = 0
    private var baseFrame: Double = 0
    private var framesWritten: Int64 = 0
    private var failure: ChannelWriterError?
    private var lastSync: UInt64 = 0
    private var scaled: [Float] = []
    private var interleaved: [Int16] = []

    private static var fileFormat: AudioStreamBasicDescription {
        AudioStreamBasicDescription(
            mSampleRate: sampleRate,
            mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsSignedInteger | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 4,
            mFramesPerPacket: 1,
            mBytesPerFrame: 4,
            mChannelsPerFrame: UInt32(channelCount),
            mBitsPerChannel: 16,
            mReserved: 0
        )
    }

    /// Creates (or replaces) the CAF at `fileURL`.
    public init(fileURL: URL) throws {
        var timebase = mach_timebase_info_data_t()
        mach_timebase_info(&timebase)
        hostTicksPerSecond = 1e9 * Double(timebase.denom) / Double(timebase.numer)
        queue.setSpecific(key: queueKey, value: ())

        var format = Self.fileFormat
        var file: ExtAudioFileRef?
        var status = ExtAudioFileCreateWithURL(fileURL as CFURL, kAudioFileCAFType, &format, nil, AudioFileFlags.eraseFile.rawValue, &file)
        guard status == noErr, let file else { throw ChannelWriterError.createFile(status) }
        status = ExtAudioFileSetProperty(file, kExtAudioFileProperty_ClientDataFormat, UInt32(MemoryLayout<AudioStreamBasicDescription>.size), &format)
        guard status == noErr else {
            ExtAudioFileDispose(file)
            throw ChannelWriterError.setClientFormat(status)
        }
        let fd = open(fileURL.path, O_RDONLY)
        guard fd >= 0 else {
            ExtAudioFileDispose(file)
            throw ChannelWriterError.openFile(errno: errno)
        }
        self.file = file
        self.fd = fd
    }

    deinit {
        try? stop()
    }

    // MARK: - Public API

    /// Frames captured before this call are discarded; file time 0 is now.
    public func start(micRing: RingBuffer, systemRing: RingBuffer, micFormat: SourceFormat, systemFormat: SourceFormat) throws {
        let sink = liveSink
        let targetPlaying = isTargetPlayingOutput
        try onQueue {
            guard state == .idle else { throw ChannelWriterError.invalidState }
            streams = [
                try Stream(ring: micRing, format: micFormat),
                try Stream(ring: systemRing, format: systemFormat),
            ]
            self.sink = sink
            self.targetPlaying = targetPlaying
            lastSync = mach_absolute_time()
            rebase()
            state = .running

            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now() + Self.drainInterval, repeating: Self.drainInterval, leeway: .milliseconds(50))
            timer.setEventHandler { [weak self] in self?.tick() }
            timer.resume()
            self.timer = timer
        }
    }

    /// Swaps in the ring of a rebuilt tap or mic; the old ring is drained first and the channel's converter is recreated.
    public func replaceRing(channel: Channel, ring: RingBuffer, format: SourceFormat) throws {
        try onQueue {
            guard state == .running || state == .paused else { throw ChannelWriterError.invalidState }
            let stream = streams[channel.index]
            if state == .running, failure == nil {
                do {
                    try drain(stream)
                    try stream.convert(end: true)
                } catch {
                    fail(error)
                }
            } else {
                stream.discard()
            }
            try stream.configure(ring: ring, format: format)
        }
    }

    /// Writes what was captured so far; afterwards the rings are drained and discarded until `resume()`.
    public func pause() {
        onQueue {
            guard state == .running else { return }
            finishSegment()
            state = .paused
        }
    }

    /// File time continues from the paused position; frames captured before this call are discarded.
    public func resume() {
        onQueue {
            guard state == .paused else { return }
            for stream in streams { stream.discard() }
            rebase()
            state = .running
        }
    }

    /// Final drain, flush and close. Safe to call repeatedly; rethrows the first write-path error.
    public func stop() throws {
        try onQueue {
            if state == .running { finishSegment() }
            timer?.cancel()
            timer = nil
            state = .stopped
            if let file {
                let status = ExtAudioFileDispose(file)
                self.file = nil
                if status != noErr, failure == nil { fail(ChannelWriterError.close(status)) }
            }
            if fd >= 0 {
                fsync(fd)
                close(fd)
                fd = -1
            }
            if let failure { throw failure }
        }
    }

    // MARK: - Writer queue

    private func onQueue<T>(_ body: () throws -> T) rethrows -> T {
        if DispatchQueue.getSpecific(key: queueKey) != nil { return try body() }
        return try queue.sync(execute: body)
    }

    private func rebase() {
        baseHost = mach_absolute_time()
        baseFrame = Double(framesWritten)
        for stream in streams { stream.position = baseFrame }
    }

    private func tick() {
        guard state == .running, failure == nil else {
            for stream in streams { stream.discard() }
            return
        }
        do {
            for stream in streams { try drain(stream) }
            let deadline = outputIndex(of: mach_absolute_time()) - Self.maxLag
            for stream in streams where stream.position < deadline {
                try stream.appendSilence(outputFrames: deadline - stream.position)
                try stream.convert(end: false)
            }
            try writeStaged(padToLongest: false)
            if mach_absolute_time() &- lastSync >= UInt64(Double(Self.syncInterval) * hostTicksPerSecond / 1e9) {
                fsync(fd)
                lastSync = mach_absolute_time()
            }
        } catch {
            fail(error)
        }
    }

    /// Writes everything staged, padding the shorter channel, so the next segment starts aligned.
    private func finishSegment() {
        guard failure == nil else { return }
        do {
            for stream in streams {
                try drain(stream)
                try stream.convert(end: true)
            }
            try writeStaged(padToLongest: true)
        } catch {
            fail(error)
        }
    }

    private func outputIndex(of hostTime: UInt64) -> Double {
        baseFrame + Double(Int64(bitPattern: hostTime &- baseHost)) / hostTicksPerSecond * Self.sampleRate
    }

    /// Places each ring block at the file position of its host time: gaps become silence, overlaps are dropped.
    private func drain(_ stream: Stream) throws {
        while true {
            let (frames, hostTime) = stream.ring.read(into: stream.scratch, maxFrames: Stream.scratchFrames)
            guard frames > 0 else { break }
            let offset = outputIndex(of: hostTime) - stream.position
            var skip = 0
            if offset > Self.jitterTolerance {
                try stream.appendSilence(outputFrames: offset)
            } else if offset < -Self.jitterTolerance {
                skip = min(frames, Int((-offset * stream.sampleRate / Self.sampleRate).rounded()))
            }
            if skip < frames {
                try stream.append(stream.scratch + skip, count: frames - skip)
            }
        }
        try stream.convert(end: false)
    }

    private func writeStaged(padToLongest: Bool) throws {
        let counts = streams.map(\.staged.count)
        guard let shortest = counts.min(), let longest = counts.max() else { return }
        let frames = padToLongest ? longest : shortest
        guard frames > 0, let file else { return }
        if padToLongest {
            for stream in streams where stream.staged.count < frames {
                stream.staged.append(contentsOf: repeatElement(0, count: frames - stream.staged.count))
            }
        }

        if scaled.count < frames { scaled = [Float](repeating: 0, count: frames) }
        if interleaved.count < frames * Self.channelCount { interleaved = [Int16](repeating: 0, count: frames * Self.channelCount) }
        let length = vDSP_Length(frames)
        let status = scaled.withUnsafeMutableBufferPointer { scaledBuffer in
            interleaved.withUnsafeMutableBufferPointer { output in
                let tmp = scaledBuffer.baseAddress!
                let out = output.baseAddress!
                for (channel, stream) in streams.enumerated() {
                    var gain: Float = 32_767
                    var low: Float = -32_768
                    var high: Float = 32_767
                    vDSP_vsmul(stream.staged, 1, &gain, tmp, 1, length)
                    vDSP_vclip(tmp, 1, &low, &high, tmp, 1, length)
                    vDSP_vfixr16(tmp, 1, out + channel, vDSP_Stride(Self.channelCount), length)
                }
                var list = AudioBufferList(
                    mNumberBuffers: 1,
                    mBuffers: AudioBuffer(
                        mNumberChannels: UInt32(Self.channelCount),
                        mDataByteSize: UInt32(frames * Self.channelCount * MemoryLayout<Int16>.size),
                        mData: UnsafeMutableRawPointer(out)
                    )
                )
                let status = ExtAudioFileWrite(file, UInt32(frames), &list)
                if status == noErr { sink?(UnsafePointer(out), frames, Self.channelCount) }
                return status
            }
        }
        guard status == noErr else { throw ChannelWriterError.write(status) }

        for stream in streams {
            stream.trackSilence(frames: frames)
            stream.staged.removeFirst(frames)
        }
        framesWritten += Int64(frames)
        publishStats()
    }

    private func publishStats() {
        let mic = streams[RecordingSession.Channel.mic]
        let system = streams[RecordingSession.Channel.system]
        let suspected = !system.heard && system.zeroFrames >= Self.silentTapFrames && (targetPlaying?() ?? false)
        let frames = framesWritten
        statsLock.withLock {
            $0.framesWritten = frames
            $0.micSilentSeconds = Double(mic.silentFrames) / Self.sampleRate
            $0.systemSilentSeconds = Double(system.silentFrames) / Self.sampleRate
            $0.silentTapSuspected = suspected
        }
    }

    private func fail(_ error: any Error) {
        guard failure == nil else { return }
        let typed = error as? ChannelWriterError ?? .conversion(code: (error as NSError).code)
        failure = typed
        statsLock.withLock { $0.failure = typed }
    }

    // MARK: - Stream

    /// One channel: its ring, a persistent converter to 16 kHz mono Float32, and converted frames not yet written.
    /// Used only on the writer queue.
    private final class Stream {
        static let scratchFrames = 8_192

        let scratch = UnsafeMutablePointer<Float>.allocate(capacity: scratchFrames)
        private(set) var ring: RingBuffer
        private(set) var sampleRate: Double
        /// File frame index of the next frame fed to the converter.
        var position: Double = 0
        /// Converted frames starting at the writer's `framesWritten`.
        var staged: [Float] = []
        private(set) var silentFrames = 0
        private(set) var zeroFrames = 0
        /// A non-zero window was written: the tap delivers audio.
        private(set) var heard = false

        private var converter: AVAudioConverter
        private var input: AVAudioPCMBuffer
        private var output: AVAudioPCMBuffer

        private static let outputFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32, sampleRate: ChannelWriter.sampleRate, channels: 1, interleaved: false
        )!

        init(ring: RingBuffer, format: SourceFormat) throws {
            (converter, input, output) = try Self.makeConverter(ring: ring, format: format)
            self.ring = ring
            sampleRate = format.sampleRate
            scratch.initialize(repeating: 0, count: Self.scratchFrames)
            staged.reserveCapacity(Int(ChannelWriter.sampleRate))
        }

        deinit {
            scratch.deallocate()
        }

        func configure(ring: RingBuffer, format: SourceFormat) throws {
            (converter, input, output) = try Self.makeConverter(ring: ring, format: format)
            self.ring = ring
            sampleRate = format.sampleRate
        }

        private static func makeConverter(ring: RingBuffer, format: SourceFormat) throws -> (AVAudioConverter, AVAudioPCMBuffer, AVAudioPCMBuffer) {
            guard format.channels == 1, format.sampleRate > 0, format.sampleRate == ring.sampleRate else {
                throw ChannelWriterError.unsupportedFormat(sampleRate: format.sampleRate, channels: format.channels, ringSampleRate: ring.sampleRate)
            }
            let outputCapacity = AVAudioFrameCount((Double(scratchFrames) * ChannelWriter.sampleRate / format.sampleRate).rounded(.up)) + 64
            guard let source = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: format.sampleRate, channels: 1, interleaved: false),
                  let converter = AVAudioConverter(from: source, to: outputFormat),
                  let input = AVAudioPCMBuffer(pcmFormat: source, frameCapacity: AVAudioFrameCount(scratchFrames)),
                  let output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: outputCapacity) else {
                throw ChannelWriterError.converterUnavailable(sampleRate: format.sampleRate)
            }
            return (converter, input, output)
        }

        /// Reads and drops everything in the ring and resets the converter.
        func discard() {
            while ring.read(into: scratch, maxFrames: Self.scratchFrames).frames > 0 {}
            input.frameLength = 0
            converter.reset()
        }

        func append(_ samples: UnsafePointer<Float>, count: Int) throws {
            try feed(count: count) { $0.update(from: samples + $1, count: $2) }
        }

        func appendSilence(outputFrames: Double) throws {
            let count = Int((outputFrames * sampleRate / ChannelWriter.sampleRate).rounded())
            try feed(count: count) { $0.update(repeating: 0, count: $2) }
        }

        private func feed(count: Int, copy: (UnsafeMutablePointer<Float>, Int, Int) -> Void) throws {
            guard count > 0 else { return }
            var done = 0
            while done < count {
                let space = Int(input.frameCapacity - input.frameLength)
                if space == 0 {
                    try convert(end: false)
                    continue
                }
                let n = min(space, count - done)
                copy(input.floatChannelData![0] + Int(input.frameLength), done, n)
                input.frameLength += AVAudioFrameCount(n)
                done += n
            }
            position += Double(count) * ChannelWriter.sampleRate / sampleRate
        }

        /// Converts the pending input into `staged`. `end` also flushes the converter's tail and resets it.
        func convert(end: Bool) throws {
            guard input.frameLength > 0 || end else { return }
            let pending = input
            var consumed = pending.frameLength == 0
            loop: while true {
                output.frameLength = 0
                var error: NSError?
                let status = converter.convert(to: output, error: &error) { _, inputStatus in
                    if !consumed {
                        consumed = true
                        inputStatus.pointee = .haveData
                        return pending
                    }
                    inputStatus.pointee = end ? .endOfStream : .noDataNow
                    return nil
                }
                if output.frameLength > 0 {
                    staged.append(contentsOf: UnsafeBufferPointer(start: output.floatChannelData![0], count: Int(output.frameLength)))
                }
                switch status {
                case .haveData:
                    continue
                case .inputRanDry, .endOfStream:
                    break loop
                case .error:
                    throw ChannelWriterError.conversion(code: error?.code ?? -1)
                @unknown default:
                    throw ChannelWriterError.conversion(code: -1)
                }
            }
            input.frameLength = 0
            if end { converter.reset() }
        }

        /// Updates the silence runs with the first `frames` staged frames, in 100 ms windows.
        func trackSilence(frames: Int) {
            staged.withUnsafeBufferPointer { buffer in
                var offset = 0
                while offset < frames {
                    let n = min(ChannelWriter.silenceWindow, frames - offset)
                    var peak: Float = 0
                    vDSP_maxmgv(buffer.baseAddress! + offset, 1, &peak, vDSP_Length(n))
                    silentFrames = peak < ChannelWriter.silenceThreshold ? silentFrames + n : 0
                    zeroFrames = peak < ChannelWriter.zeroThreshold ? zeroFrames + n : 0
                    heard = heard || peak >= ChannelWriter.zeroThreshold
                    offset += n
                }
            }
        }
    }
}
