import AudioToolbox
import AVFoundation
import EchoCore
import Foundation

public enum AudioCompressorError: Error, Sendable, Equatable {
    /// `session.json` is missing or unreadable.
    case sessionUnreadable
    /// The session is still recording or paused; nothing was touched.
    case stillRecording
    /// The source audio is missing, unreadable or empty (`noErr`).
    case sourceUnreadable(OSStatus)
    /// `session.audioFileName` is not a plain file in the folder (a path, `..` or a symlink); nothing was touched.
    case unsafeAudioFileName
    /// The encoder refused the output format or bitrate.
    case settingRejected(OSStatus)
    case encode(OSStatus)
    case outputUnreadable(OSStatus)
    /// Decoded output length (seconds) differs from the source by more than `AudioCompressor.durationTolerance`.
    case durationMismatch(source: Double, output: Double)
    /// Writing `session.json` or `conversation.json` failed (`NSError.code`).
    case metadata(code: Int)
}

public struct CompressionResult: Sendable, Equatable {
    public let folder: URL
    /// Compressed files in the folder; empty on failure.
    public let outputFileNames: [String]
    public let failure: AudioCompressorError?
}

/// Compresses a recording folder's 2-channel CAF to AAC-LC m4a, one job at a time, each at its own QoS.
///
/// The CAF is deleted only after the decoded output matches its duration and `session.json` points to the new
/// file; on any failure the CAF stays and `conversation.json` is marked `compressionFailed`.
public final class AudioCompressor: Sendable {
    /// Encodes `source` into an m4a at `destination` with a total bitrate in bit/s. `channel` nil keeps every
    /// source channel; otherwise only that channel is written, as mono. Throws `settingRejected` when the
    /// encoder refuses the setting, which triggers the mono fallback.
    public typealias Encoder = @Sendable (_ source: URL, _ destination: URL, _ channel: Int?, _ bitRate: Int) throws -> Void

    /// The recorder's uncompressed file name.
    public static let cafFileName = "audio.caf"
    public static let stereoFileName = "audio.m4a"
    /// Mono fallback: `session.audioFileName` becomes the mic file; the system file sits next to it.
    public static let micFileName = "audio-mic.m4a"
    public static let systemFileName = "audio-system.m4a"
    public static let durationTolerance = 0.1

    private static let chunkFrames: AVAudioFrameCount = 8_192

    /// No QoS of its own: each job runs at the one `compress` is given.
    private let queue = DispatchQueue(label: "EchoDiar.AudioCompressor")
    private let encoder: Encoder

    /// `encoder` is injectable so tests can force failures.
    public init(encoder: @escaping Encoder = { try AudioCompressor.encodeAAC(source: $0, destination: $1, channel: $2, bitRate: $3) }) {
        self.encoder = encoder
    }

    /// Queued behind earlier jobs; returns when this one is done. `started` runs on the compressor's queue right
    /// after `conversation.json` is marked `compressing`, before the encode. `qos` is the job's (`.background` keeps
    /// it off the P-cores, e.g. while a recording runs).
    public func compress(
        folder: URL, bitrateKbps: Int = 64, keepCAF: Bool = false, qos: DispatchQoS = .background,
        started: (@Sendable () -> Void)? = nil
    ) async -> CompressionResult {
        await withCheckedContinuation { continuation in
            queue.async(qos: qos, flags: .enforceQoS) {
                let result = self.run(folder: folder, bitrateKbps: bitrateKbps, keepCAF: keepCAF, started: started)
                // The decoders and the encoder are gone; their freed pages would stay with the process.
                MemoryRelief.trim()
                continuation.resume(returning: result)
            }
        }
    }

    // MARK: - Job

    private func run(folder: URL, bitrateKbps: Int, keepCAF: Bool, started: (@Sendable () -> Void)?) -> CompressionResult {
        guard var session = try? RecordingSession.read(from: folder) else {
            return CompressionResult(folder: folder, outputFileNames: [], failure: .sessionUnreadable)
        }
        guard session.state != .recording, session.state != .paused else {
            return CompressionResult(folder: folder, outputFileNames: [], failure: .stillRecording)
        }
        let sourceName = session.audioFileName
        guard let source = ConversationFiles.audioURL(in: folder, fileName: sourceName) else {
            try? Self.updateConversation(in: folder, session: session, duration: 0) { $0.status = .compressionFailed }
            return CompressionResult(folder: folder, outputFileNames: [], failure: .unsafeAudioFileName)
        }
        guard source.pathExtension.lowercased() == "caf" else {
            return alreadyCompressed(session, audio: source, folder: folder, keepCAF: keepCAF)
        }

        var sourceDuration = 0.0
        var before: Conversation.Status?
        let outputs: [String]
        do {
            sourceDuration = try Self.decodedDuration(of: source, channels: nil, error: AudioCompressorError.sourceUnreadable)
            guard sourceDuration > 0 else { throw AudioCompressorError.sourceUnreadable(noErr) }
            before = (try? Conversation.read(from: folder))?.status
            try Self.updateConversation(in: folder, session: session, duration: sourceDuration) { $0.status = .compressing }
            started?()
            outputs = try encodeAndVerify(source: source, folder: folder, bitRate: bitrateKbps * 1_000, sourceDuration: sourceDuration)
            session.audioFileName = outputs[0]
            do {
                try session.write(to: folder)
            } catch {
                Self.removeOutputs(in: folder)
                throw error
            }
        } catch {
            try? Self.updateConversation(in: folder, session: session, duration: sourceDuration) { $0.status = .compressionFailed }
            return CompressionResult(folder: folder, outputFileNames: [], failure: Self.typed(error))
        }

        // session.json already points to the verified output, so the CAF is no longer needed.
        if !keepCAF { Self.removeCAF(named: sourceName, in: folder) }
        do {
            try Self.updateConversation(in: folder, session: session, duration: sourceDuration) {
                $0.status = Self.doneStatus(before: before, hasTranscript: $0.hasTranscript)
                $0.duration = sourceDuration
                $0.sizeBytes = LibraryStore.sizeOnDisk(of: folder)
            }
        } catch {
            return CompressionResult(folder: folder, outputFileNames: outputs, failure: Self.typed(error))
        }
        return CompressionResult(folder: folder, outputFileNames: outputs, failure: nil)
    }

    /// The session already points to compressed audio (`audio`): nothing to encode; heals a status, and removes
    /// a CAF, left by a job interrupted after `session.json` was rewritten.
    private func alreadyCompressed(
        _ session: RecordingSession, audio: URL, folder: URL, keepCAF: Bool
    ) -> CompressionResult {
        guard FileManager.default.fileExists(atPath: audio.path) else {
            return CompressionResult(folder: folder, outputFileNames: [], failure: .sourceUnreadable(noErr))
        }
        if !keepCAF { Self.removeCAF(named: Self.cafFileName, in: folder) }
        let outputs = session.audioFileName == Self.micFileName ? [Self.micFileName, Self.systemFileName] : [session.audioFileName]
        if let conversation = try? Conversation.read(from: folder),
           conversation.status == .compressing || conversation.status == .compressionFailed {
            try? Self.updateConversation(in: folder, session: session, duration: conversation.duration) {
                $0.status = Self.doneStatus(before: conversation.status, hasTranscript: $0.hasTranscript)
                $0.sizeBytes = LibraryStore.sizeOnDisk(of: folder)
            }
        }
        return CompressionResult(folder: folder, outputFileNames: outputs, failure: nil)
    }

    /// Stereo first; two mono files when the encoder rejects the stereo setting. Removes partial outputs on failure.
    private func encodeAndVerify(source: URL, folder: URL, bitRate: Int, sourceDuration: Double) throws -> [String] {
        Self.removeOutputs(in: folder)
        do {
            let stereo = folder.appendingPathComponent(Self.stereoFileName)
            try encoder(source, stereo, nil, bitRate)
            try Self.verify(stereo, channels: 2, sourceDuration: sourceDuration)
            return [Self.stereoFileName]
        } catch AudioCompressorError.settingRejected {
            Self.removeOutputs(in: folder)
        } catch {
            Self.removeOutputs(in: folder)
            throw error
        }

        let mono = [(Self.micFileName, RecordingSession.Channel.mic), (Self.systemFileName, RecordingSession.Channel.system)]
        do {
            for (name, channel) in mono {
                let url = folder.appendingPathComponent(name)
                try encoder(source, url, channel, bitRate / 2)
                try Self.verify(url, channels: 1, sourceDuration: sourceDuration)
            }
        } catch {
            Self.removeOutputs(in: folder)
            throw error
        }
        return mono.map(\.0)
    }

    private static func verify(_ url: URL, channels: Int, sourceDuration: Double) throws {
        let duration = try decodedDuration(of: url, channels: channels, error: AudioCompressorError.outputUnreadable)
        guard abs(duration - sourceDuration) <= durationTolerance else {
            throw AudioCompressorError.durationMismatch(source: sourceDuration, output: duration)
        }
    }

    /// Deletes the CAF only when the name still resolves to a plain file inside `folder`, never through a symlink.
    private static func removeCAF(named name: String, in folder: URL) {
        guard let caf = ConversationFiles.audioURL(in: folder, fileName: name), caf.pathExtension.lowercased() == "caf" else {
            return
        }
        try? FileManager.default.removeItem(at: caf)
    }

    private static func removeOutputs(in folder: URL) {
        for name in [stereoFileName, micFileName, systemFileName] {
            try? FileManager.default.removeItem(at: folder.appendingPathComponent(name))
        }
    }

    /// Read-modify-write, so a rename made meanwhile survives; creates the file from the session when missing.
    private static func updateConversation(
        in folder: URL, session: RecordingSession, duration: Double, _ change: (inout Conversation) -> Void
    ) throws {
        let exists = FileManager.default.fileExists(atPath: folder.appendingPathComponent(Conversation.fileName).path)
        var conversation = exists
            ? try Conversation.read(from: folder)
            : Conversation(session: session, folder: folder, duration: duration)
        change(&conversation)
        try conversation.write()
    }

    /// Compression runs after transcription, so it keeps a transcription outcome instead of resetting it to `ready`.
    private static func doneStatus(before: Conversation.Status?, hasTranscript: Bool) -> Conversation.Status {
        before == .transcriptionFailed ? .transcriptionFailed : (hasTranscript ? .transcribed : .ready)
    }

    private static func typed(_ error: any Error) -> AudioCompressorError {
        error as? AudioCompressorError ?? .metadata(code: (error as NSError).code)
    }

    // MARK: - Core Audio

    /// Seconds of audio obtained by decoding the whole file. `channels` nil accepts any channel count.
    private static func decodedDuration(
        of url: URL, channels: Int?, error makeError: (OSStatus) -> AudioCompressorError
    ) throws -> Double {
        let file = try open(url, error: makeError)
        defer { ExtAudioFileDispose(file) }
        let fileFormat = try dataFormat(of: file, error: makeError)
        if let channels, Int(fileFormat.mChannelsPerFrame) != channels { throw makeError(kAudioFileInvalidFileError) }
        let buffer = try pcmBuffer(for: file, format: fileFormat, error: makeError)
        var total: Int64 = 0
        while true {
            let frames = try read(file, into: buffer, error: makeError)
            if frames == 0 { break }
            total += Int64(frames)
        }
        return Double(total) / fileFormat.mSampleRate
    }

    /// The default encoder: `ExtAudioFile` to AAC-LC m4a at 16 kHz, constant bitrate.
    public static func encodeAAC(source: URL, destination: URL, channel: Int?, bitRate: Int) throws {
        let reader = try open(source, error: AudioCompressorError.sourceUnreadable)
        defer { ExtAudioFileDispose(reader) }
        let sourceFormat = try dataFormat(of: reader, error: AudioCompressorError.sourceUnreadable)
        let sourceChannels = Int(sourceFormat.mChannelsPerFrame)
        if let channel, channel >= sourceChannels { throw AudioCompressorError.sourceUnreadable(kAudio_ParamError) }
        let buffer = try pcmBuffer(for: reader, format: sourceFormat, error: AudioCompressorError.sourceUnreadable)

        let outputChannels = channel == nil ? sourceChannels : 1
        var aac = AudioStreamBasicDescription()
        aac.mFormatID = kAudioFormatMPEG4AAC
        aac.mSampleRate = ChannelWriter.sampleRate
        aac.mChannelsPerFrame = UInt32(outputChannels)
        var created: ExtAudioFileRef?
        var status = ExtAudioFileCreateWithURL(
            destination as CFURL, kAudioFileM4AType, &aac, nil, AudioFileFlags.eraseFile.rawValue, &created)
        guard status == noErr, let writer = created else { throw AudioCompressorError.settingRejected(status) }
        var disposed = false
        defer { if !disposed { ExtAudioFileDispose(writer) } }

        guard let client = AVAudioFormat(
            commonFormat: .pcmFormatFloat32, sampleRate: sourceFormat.mSampleRate,
            channels: AVAudioChannelCount(outputChannels), interleaved: false) else {
            throw AudioCompressorError.encode(kAudio_ParamError)
        }
        var clientFormat = client.streamDescription.pointee
        status = ExtAudioFileSetProperty(
            writer, kExtAudioFileProperty_ClientDataFormat, UInt32(MemoryLayout<AudioStreamBasicDescription>.size), &clientFormat)
        guard status == noErr else { throw AudioCompressorError.settingRejected(status) }
        try setBitRate(bitRate, on: writer)

        while true {
            let frames = try read(reader, into: buffer, error: AudioCompressorError.sourceUnreadable)
            if frames == 0 { break }
            if let channel {
                var list = AudioBufferList(
                    mNumberBuffers: 1,
                    mBuffers: AudioBuffer(
                        mNumberChannels: 1,
                        mDataByteSize: frames * UInt32(MemoryLayout<Float>.size),
                        mData: UnsafeMutableRawPointer(buffer.floatChannelData![channel])))
                status = ExtAudioFileWrite(writer, frames, &list)
            } else {
                status = ExtAudioFileWrite(writer, frames, buffer.audioBufferList)
            }
            guard status == noErr else { throw AudioCompressorError.encode(status) }
        }
        disposed = true
        status = ExtAudioFileDispose(writer)
        guard status == noErr else { throw AudioCompressorError.encode(status) }
    }

    /// Sets constant bitrate mode and the bitrate on the file's converter, then applies them.
    private static func setBitRate(_ bitRate: Int, on file: ExtAudioFileRef) throws {
        var converter: AudioConverterRef?
        var size = UInt32(MemoryLayout<AudioConverterRef?>.size)
        var status = ExtAudioFileGetProperty(file, kExtAudioFileProperty_AudioConverter, &size, &converter)
        guard status == noErr, let converter else { throw AudioCompressorError.settingRejected(status) }
        var mode = UInt32(kAudioCodecBitRateControlMode_Constant)
        status = AudioConverterSetProperty(converter, kAudioCodecPropertyBitRateControlMode, UInt32(MemoryLayout<UInt32>.size), &mode)
        guard status == noErr else { throw AudioCompressorError.settingRejected(status) }
        var rate = UInt32(clamping: bitRate)
        status = AudioConverterSetProperty(converter, kAudioConverterEncodeBitRate, UInt32(MemoryLayout<UInt32>.size), &rate)
        guard status == noErr else { throw AudioCompressorError.settingRejected(status) }
        // A NULL config makes the file apply the converter's new settings.
        var config: OpaquePointer?
        status = ExtAudioFileSetProperty(file, kExtAudioFileProperty_ConverterConfig, UInt32(MemoryLayout<OpaquePointer?>.size), &config)
        guard status == noErr else { throw AudioCompressorError.settingRejected(status) }
    }

    private static func open(_ url: URL, error makeError: (OSStatus) -> AudioCompressorError) throws -> ExtAudioFileRef {
        var file: ExtAudioFileRef?
        let status = ExtAudioFileOpenURL(url as CFURL, &file)
        guard status == noErr, let file else { throw makeError(status) }
        return file
    }

    private static func dataFormat(
        of file: ExtAudioFileRef, error makeError: (OSStatus) -> AudioCompressorError
    ) throws -> AudioStreamBasicDescription {
        var format = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        let status = ExtAudioFileGetProperty(file, kExtAudioFileProperty_FileDataFormat, &size, &format)
        guard status == noErr, format.mSampleRate > 0, format.mChannelsPerFrame > 0 else {
            throw makeError(status == noErr ? kAudioFileInvalidFileError : status)
        }
        return format
    }

    /// Sets the file's client format to Float32 non-interleaved at its own rate and channels; returns a matching buffer.
    private static func pcmBuffer(
        for file: ExtAudioFileRef, format: AudioStreamBasicDescription, error makeError: (OSStatus) -> AudioCompressorError
    ) throws -> AVAudioPCMBuffer {
        guard let client = AVAudioFormat(
            commonFormat: .pcmFormatFloat32, sampleRate: format.mSampleRate,
            channels: AVAudioChannelCount(format.mChannelsPerFrame), interleaved: false),
              let buffer = AVAudioPCMBuffer(pcmFormat: client, frameCapacity: chunkFrames) else {
            throw makeError(kAudio_ParamError)
        }
        var description = client.streamDescription.pointee
        let status = ExtAudioFileSetProperty(
            file, kExtAudioFileProperty_ClientDataFormat, UInt32(MemoryLayout<AudioStreamBasicDescription>.size), &description)
        guard status == noErr else { throw makeError(status) }
        return buffer
    }

    /// Reads the next chunk; 0 at the end of the file.
    private static func read(
        _ file: ExtAudioFileRef, into buffer: AVAudioPCMBuffer, error makeError: (OSStatus) -> AudioCompressorError
    ) throws -> UInt32 {
        buffer.frameLength = buffer.frameCapacity
        var frames = buffer.frameCapacity
        let status = ExtAudioFileRead(file, &frames, buffer.mutableAudioBufferList)
        guard status == noErr else { throw makeError(status) }
        buffer.frameLength = frames
        return frames
    }
}
