import Accelerate
import AudioToolbox
import AVFoundation
import CoreAudio
import Darwin

public enum MicCaptureError: Error, Sendable {
    /// The input node reports no channels or no sample rate (no input device).
    case noInputDevice
    /// The hardware format is not Float32 PCM; `commonFormat` is `AVAudioCommonFormat.rawValue`.
    case unsupportedFormat(sampleRate: Double, channels: Int, commonFormat: UInt)
    case startEngine(any Error)
}

/// AVAudioEngine input tap; downmixes to mono Float32 at the hardware rate into `currentSource`'s ring.
/// Never requests microphone permission (onboarding does); open it only while recording.
///
/// `@unchecked Sendable`: engine state is owned by a private serial queue; `start`/`stop` and the getters hop onto it.
public final class MicCapture: @unchecked Sendable {
    public struct Format: Sendable, Equatable {
        public let sampleRate: Double
        /// Channels in the ring: always 1 (the hardware input is downmixed).
        public let channels: Int
    }

    /// Ring and format of the current (or last) run, read together so a rebuild in between cannot mix them.
    /// Kept after `stop` so the ring can be drained; replaced on `start` and on a configuration change.
    public var currentSource: (ring: RingBuffer, format: Format)? {
        onQueue {
            guard let ring, let currentFormat else { return nil }
            return (ring, currentFormat)
        }
    }
    /// Fired on a utility queue after a configuration change rebuilt the engine (new ring, maybe a new rate).
    /// Read at `start`. If the rebuild fails, capture stops and nothing fires.
    public var onConfigurationChange: (@Sendable (Format) -> Void)?

    private static let ringSeconds = 4.0
    private static let scratchFrames = 65_536

    private let queue = DispatchQueue(label: "EchoDiar.MicCapture", qos: .userInitiated)
    private let queueKey = DispatchSpecificKey<Void>()

    private var engine: AVAudioEngine?
    private var observer: (any NSObjectProtocol)?
    private var generation: UInt64 = 0
    private var deviceUID: String?
    private var handler: (@Sendable (Format) -> Void)?
    private var ring: RingBuffer?
    private var currentFormat: Format?

    public init() {
        queue.setSpecific(key: queueKey, value: ())
    }

    deinit {
        stop()
    }

    /// `deviceUID` nil means the system default input; an unknown or unusable UID falls back to the default.
    /// Stops any previous run first.
    public func start(deviceUID: String?) throws {
        let handler = onConfigurationChange
        try onQueue {
            teardown()
            self.deviceUID = deviceUID
            self.handler = handler
            do {
                try build()
            } catch {
                teardown()
                throw error
            }
        }
    }

    /// Safe to call repeatedly.
    public func stop() {
        onQueue { teardown() }
    }

    private func onQueue<T>(_ body: () throws -> T) rethrows -> T {
        if DispatchQueue.getSpecific(key: queueKey) != nil { return try body() }
        return try queue.sync(execute: body)
    }

    // MARK: - Queue-owned

    private func build() throws {
        let engine = Self.makeEngine(deviceUID: deviceUID)
        let input = engine.inputNode
        let hardware = input.inputFormat(forBus: 0)
        guard hardware.sampleRate > 0, hardware.channelCount > 0 else { throw MicCaptureError.noInputDevice }
        guard hardware.commonFormat == .pcmFormatFloat32 else {
            throw MicCaptureError.unsupportedFormat(
                sampleRate: hardware.sampleRate,
                channels: Int(hardware.channelCount),
                commonFormat: hardware.commonFormat.rawValue
            )
        }

        let ring = RingBuffer(capacityFrames: Int((hardware.sampleRate * Self.ringSeconds).rounded(.up)), sampleRate: hardware.sampleRate)
        input.installTap(
            onBus: 0,
            bufferSize: 4096,
            format: hardware,
            block: Self.makeTapBlock(ring: ring, scratch: Scratch(capacity: Self.scratchFrames), ticksPerFrame: Self.hostTicksPerFrame(hardware.sampleRate))
        )
        self.engine = engine
        generation &+= 1
        observe(engine, generation: generation)

        engine.prepare()
        do {
            try engine.start()
        } catch {
            throw MicCaptureError.startEngine(error)
        }
        self.ring = ring
        currentFormat = Format(sampleRate: hardware.sampleRate, channels: 1)
    }

    private func teardown() {
        if let observer {
            NotificationCenter.default.removeObserver(observer)
            self.observer = nil
        }
        if let engine {
            engine.stop()
            engine.inputNode.removeTap(onBus: 0)
            self.engine = nil
        }
    }

    private func observe(_ engine: AVAudioEngine, generation: UInt64) {
        observer = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange,
            object: engine,
            queue: nil
        ) { [weak self] _ in
            guard let self else { return }
            self.queue.async { self.handleConfigurationChange(generation: generation) }
        }
    }

    /// A fresh engine re-reads the device and format; reusing the old one can report a stale format.
    private func handleConfigurationChange(generation: UInt64) {
        guard engine != nil, generation == self.generation else { return }
        teardown()
        do {
            try build()
        } catch {
            teardown()
            return
        }
        if let format = currentFormat, let handler {
            DispatchQueue.global(qos: .utility).async { handler(format) }
        }
    }

    // MARK: - Device

    private static func makeEngine(deviceUID: String?) -> AVAudioEngine {
        if let deviceUID, let device = deviceID(forUID: deviceUID) {
            let engine = AVAudioEngine()
            if setCurrentDevice(device, on: engine.inputNode), engine.inputNode.inputFormat(forBus: 0).channelCount > 0 {
                return engine
            }
        }
        return AVAudioEngine()
    }

    static func deviceID(forUID uid: String) -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyTranslateUIDToDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        let cfUID = uid as CFString
        var device = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        let status = withUnsafePointer(to: cfUID) {
            AudioObjectGetPropertyData(
                AudioObjectID(kAudioObjectSystemObject), &address,
                UInt32(MemoryLayout<CFString>.size), $0, &size, &device
            )
        }
        guard status == noErr, device != kAudioObjectUnknown else { return nil }
        return device
    }

    private static func setCurrentDevice(_ device: AudioDeviceID, on input: AVAudioInputNode) -> Bool {
        guard let unit = input.audioUnit else { return false }
        var device = device
        let status = AudioUnitSetProperty(
            unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
            &device, UInt32(MemoryLayout<AudioDeviceID>.size)
        )
        return status == noErr
    }

    // MARK: - Tap

    private static func hostTicksPerFrame(_ sampleRate: Double) -> Double {
        var timebase = mach_timebase_info_data_t()
        mach_timebase_info(&timebase)
        return 1e9 * Double(timebase.denom) / Double(timebase.numer) / sampleRate
    }

    /// Runs on AVAudioEngine's internal tap thread: no allocation, no locks. Buffers longer than the scratch go in chunks.
    private static func makeTapBlock(ring: RingBuffer, scratch: Scratch, ticksPerFrame: Double) -> AVAudioNodeTapBlock {
        { buffer, when in
            let frames = Int(buffer.frameLength)
            guard frames > 0, let data = buffer.floatChannelData else { return }
            let hostTime = when.isHostTimeValid ? when.hostTime : mach_absolute_time()
            let channels = Int(buffer.format.channelCount)
            if channels == 1 {
                ring.write(data[0], count: frames, hostTime: hostTime)
                return
            }

            let interleaved = buffer.format.isInterleaved
            let stride = vDSP_Stride(interleaved ? buffer.stride : 1)
            var scale = 1 / Float(channels)
            var offset = 0
            while offset < frames {
                let n = min(frames - offset, scratch.capacity)
                for channel in 0..<channels {
                    let source = interleaved
                        ? UnsafePointer(data[0] + offset * buffer.stride + channel)
                        : UnsafePointer(data[channel] + offset)
                    if channel == 0 {
                        vDSP_vsmul(source, stride, &scale, scratch.pointer, 1, vDSP_Length(n))
                    } else {
                        vDSP_vsma(source, stride, &scale, scratch.pointer, 1, scratch.pointer, 1, vDSP_Length(n))
                    }
                }
                let chunkTime = hostTime &+ UInt64((Double(offset) * ticksPerFrame).rounded())
                ring.write(scratch.pointer, count: n, hostTime: chunkTime)
                offset += n
            }
        }
    }
}

/// Downmix buffer owned by one tap block, allocated before the engine starts.
private final class Scratch: @unchecked Sendable {
    let pointer: UnsafeMutablePointer<Float>
    let capacity: Int

    init(capacity: Int) {
        self.capacity = capacity
        pointer = .allocate(capacity: capacity)
        pointer.initialize(repeating: 0, count: capacity)
    }

    deinit {
        pointer.deallocate()
    }
}
