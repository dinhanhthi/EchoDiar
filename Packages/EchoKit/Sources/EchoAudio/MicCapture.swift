import Accelerate
import AudioToolbox
import CoreAudio
import Darwin
import Foundation
import IOKit

public enum MicCaptureError: Error, Sendable {
    /// No input device, or it reports no channels or no sample rate.
    case noInputDevice
    /// A HAL unit call failed while the capture was set up or started.
    case startUnit(OSStatus)
}

/// Input-only HAL unit bound to one device before it is initialized; downmixes to mono Float32 at the hardware rate
/// into `currentSource`'s ring. Its output IO is off and it never touches the default output: an `AVAudioEngine`
/// input opens a `CADefaultDeviceAggregate` of the default input and output, which switched AirPods to their
/// hands-free profile (24 kHz mono) even with the built-in mic picked.
/// Never requests microphone permission (onboarding does); open it only while recording.
///
/// `@unchecked Sendable`: unit state is owned by a private serial queue; `start`/`stop` and the getters hop onto it.
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
    /// Fired on a utility queue after a change of the device's rate or channels, or the device going away, rebuilt
    /// the unit (new ring, maybe a new rate). Read at `start`. If the rebuild fails, capture stops and nothing fires.
    public var onConfigurationChange: (@Sendable (Format) -> Void)?

    private static let ringSeconds = 4.0
    /// The bound device's properties whose change rebuilds the unit.
    private static let deviceSelectors = [
        (kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal),
        (kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeInput),
        (kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal),
    ]

    private let queue = DispatchQueue(label: "EchoDiar.MicCapture", qos: .userInitiated)
    private let queueKey = DispatchSpecificKey<Void>()
    private let listenerQueue = DispatchQueue(label: "EchoDiar.MicCapture.listener", qos: .userInitiated)

    private var unit: AudioUnit?
    /// The input callback's state; released only after the unit is disposed.
    private var input: Input?
    private var device = AudioDeviceID(kAudioObjectUnknown)
    private var listener: AudioObjectPropertyListenerBlock?
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

    /// `deviceUID` nil means the system default input (see `inputDevice(uid:)`); an unknown or unusable UID falls
    /// back to it. Stops any previous run first.
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

    /// Enables input IO and disables output IO before binding the device, as an input-only device is rejected
    /// otherwise; the client format is the device's rate (the unit does no rate conversion on input) and channels,
    /// Float32 non-interleaved. Stores the unit at once, so `teardown` disposes it whatever step fails.
    private func build() throws {
        guard let device = Self.inputDevice(uid: deviceUID) else { throw MicCaptureError.noInputDevice }
        var description = AudioComponentDescription(
            componentType: kAudioUnitType_Output, componentSubType: kAudioUnitSubType_HALOutput,
            componentManufacturer: kAudioUnitManufacturer_Apple, componentFlags: 0, componentFlagsMask: 0
        )
        var instance: AudioUnit?
        guard let component = AudioComponentFindNext(nil, &description) else { throw MicCaptureError.startUnit(kAudioUnitErr_FailedInitialization) }
        try Self.check(AudioComponentInstanceNew(component, &instance))
        guard let unit = instance else { throw MicCaptureError.startUnit(kAudioUnitErr_FailedInitialization) }
        self.unit = unit

        try Self.set(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1, UInt32(1))
        try Self.set(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Output, 0, UInt32(0))
        try Self.set(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, device)

        var hardware = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        try Self.check(AudioUnitGetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 1, &hardware, &size))
        let channels = Int(hardware.mChannelsPerFrame)
        guard hardware.mSampleRate > 0, channels > 0 else { throw MicCaptureError.noInputDevice }
        let bytes = UInt32(MemoryLayout<Float>.size)
        let client = AudioStreamBasicDescription(
            mSampleRate: hardware.mSampleRate, mFormatID: kAudioFormatLinearPCM,
            mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
            mBytesPerPacket: bytes, mFramesPerPacket: 1, mBytesPerFrame: bytes,
            mChannelsPerFrame: UInt32(channels), mBitsPerChannel: 32, mReserved: 0
        )
        try Self.set(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1, client)

        // A slice is one device IO buffer; the largest the device allows also bounds it (clamped: a driver's value).
        var maxFrames: UInt32 = 0
        size = UInt32(MemoryLayout<UInt32>.size)
        try Self.check(AudioUnitGetProperty(unit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0, &maxFrames, &size))
        let deviceMax = Self.property(device, kAudioDevicePropertyBufferFrameSizeRange, initial: AudioValueRange())?.mMaximum ?? 0
        maxFrames = max(maxFrames, deviceMax.isFinite ? UInt32(min(max(deviceMax, 0), 65_536)) : 0)
        try Self.set(unit, kAudioUnitProperty_MaximumFramesPerSlice, kAudioUnitScope_Global, 0, maxFrames)

        let ring = RingBuffer(capacityFrames: Int((hardware.mSampleRate * Self.ringSeconds).rounded(.up)), sampleRate: hardware.mSampleRate)
        let input = Input(unit: unit, ring: ring, channels: channels, capacity: Int(maxFrames))
        self.input = input
        let callback = AURenderCallbackStruct(
            inputProc: { refCon, flags, timestamp, _, frames, _ in
                Unmanaged<Input>.fromOpaque(refCon).takeUnretainedValue().render(flags: flags, timestamp: timestamp, frames: Int(frames))
            },
            inputProcRefCon: Unmanaged.passUnretained(input).toOpaque()
        )
        try Self.set(unit, kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global, 0, callback)

        generation &+= 1
        listen(device, generation: generation)
        try Self.check(AudioUnitInitialize(unit))
        try Self.check(AudioOutputUnitStart(unit))
        self.ring = ring
        currentFormat = Format(sampleRate: hardware.mSampleRate, channels: 1)
    }

    /// Stopping waits for a running input callback, so `input` is freed after the unit is gone.
    private func teardown() {
        unlisten()
        if let unit {
            AudioOutputUnitStop(unit)
            AudioUnitUninitialize(unit)
            AudioComponentInstanceDispose(unit)
            self.unit = nil
        }
        input = nil
    }

    private func listen(_ device: AudioDeviceID, generation: UInt64) {
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            guard let self else { return }
            self.queue.async { self.handleConfigurationChange(generation: generation) }
        }
        self.listener = listener
        self.device = device
        for (selector, scope) in Self.deviceSelectors {
            var address = Self.address(selector, scope)
            _ = AudioObjectAddPropertyListenerBlock(device, &address, listenerQueue, listener)
        }
    }

    /// Fails harmlessly for a device that is already gone.
    private func unlisten() {
        guard let listener else { return }
        for (selector, scope) in Self.deviceSelectors {
            var address = Self.address(selector, scope)
            _ = AudioObjectRemovePropertyListenerBlock(device, &address, listenerQueue, listener)
        }
        self.listener = nil
        device = AudioDeviceID(kAudioObjectUnknown)
    }

    /// A fresh unit re-reads the device and its format; a device that went away is replaced by `inputDevice(uid:)`.
    private func handleConfigurationChange(generation: UInt64) {
        guard unit != nil, generation == self.generation else { return }
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

    /// The input a recording uses: the picked device while it exists, is alive and has input; else the default input,
    /// unless it is a Bluetooth input of the default output's device (AirPods). Opening that one switches the headset
    /// to its hands-free profile, which drops its playback to 24 kHz mono, so `replacementMic()` is used instead when
    /// there is one. Nil when there is no input at all.
    static func inputDevice(uid: String?) -> AudioDeviceID? {
        if let uid, let device = deviceID(forUID: uid), isAlive(device), hasInput(device) { return device }
        guard let input = defaultDevice(kAudioHardwarePropertyDefaultInputDevice) else { return nil }
        guard isBluetooth(input), let output = defaultDevice(kAudioHardwarePropertyDefaultOutputDevice),
              isSameHeadset(input, output), let replacement = replacementMic() else { return input }
        return replacement
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

    /// The same device, one of its related devices (the other half of a headset), or, when the device does not list
    /// them, a Bluetooth output with the same model UID or name.
    private static func isSameHeadset(_ input: AudioDeviceID, _ output: AudioDeviceID) -> Bool {
        if input == output || relatedDevices(output).contains(input) { return true }
        guard isBluetooth(output) else { return false }
        if let model = string(input, kAudioDevicePropertyModelUID), model == string(output, kAudioDevicePropertyModelUID) { return true }
        if let name = string(input, kAudioObjectPropertyName), name == string(output, kAudioObjectPropertyName) { return true }
        return false
    }

    /// The built-in mic unless the lid is closed (it is then cut off and records silence); else the first other
    /// input by name that is not Bluetooth, an aggregate or virtual (a USB webcam, a wired mic).
    private static func replacementMic() -> AudioDeviceID? {
        var address = Self.address(kAudioHardwarePropertyDevices)
        var size: UInt32 = 0
        let system = AudioObjectID(kAudioObjectSystemObject)
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return nil }
        var devices = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &devices) == noErr else { return nil }
        let inputs = devices.filter { isAlive($0) && hasInput($0) }
        if !isLidClosed(), let builtIn = inputs.first(where: { transport($0) == kAudioDeviceTransportTypeBuiltIn }) { return builtIn }
        let excluded = [
            kAudioDeviceTransportTypeBuiltIn, kAudioDeviceTransportTypeBluetooth, kAudioDeviceTransportTypeBluetoothLE,
            kAudioDeviceTransportTypeAggregate, kAudioDeviceTransportTypeVirtual,
        ]
        return inputs
            .filter { !excluded.contains(transport($0)) }
            .min { (string($0, kAudioObjectPropertyName) ?? "") < (string($1, kAudioObjectPropertyName) ?? "") }
    }

    /// A MacBook's clamshell state; a Mac without a lid has none. No notification covers it, so `AudioRouteMonitor`
    /// polls it while recording.
    static func isLidClosed() -> Bool {
        let rootDomain = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPMrootDomain"))
        guard rootDomain != 0 else { return false }
        defer { IOObjectRelease(rootDomain) }
        let state = IORegistryEntryCreateCFProperty(rootDomain, "AppleClamshellState" as CFString, kCFAllocatorDefault, 0)
        return (state?.takeRetainedValue() as? Bool) ?? false
    }

    private static func transport(_ device: AudioDeviceID) -> UInt32 {
        property(device, kAudioDevicePropertyTransportType, initial: UInt32(0)) ?? 0
    }

    private static func relatedDevices(_ device: AudioDeviceID) -> [AudioDeviceID] {
        var address = Self.address(kAudioDevicePropertyRelatedDevices)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr else { return [] }
        var devices = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &devices) == noErr else { return [] }
        return devices
    }

    private static func defaultDevice(_ selector: AudioObjectPropertySelector) -> AudioDeviceID? {
        let device = property(AudioObjectID(kAudioObjectSystemObject), selector, initial: AudioDeviceID(kAudioObjectUnknown))
        return device == kAudioObjectUnknown ? nil : device
    }

    private static func isBluetooth(_ device: AudioDeviceID) -> Bool {
        let transport = transport(device)
        return transport == kAudioDeviceTransportTypeBluetooth || transport == kAudioDeviceTransportTypeBluetoothLE
    }

    private static func isAlive(_ device: AudioDeviceID) -> Bool {
        (property(device, kAudioDevicePropertyDeviceIsAlive, initial: UInt32(0)) ?? 0) != 0
    }

    private static func hasInput(_ device: AudioDeviceID) -> Bool {
        var address = Self.address(kAudioDevicePropertyStreams, kAudioObjectPropertyScopeInput)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func string(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = Self.address(selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr else { return nil }
        return value?.takeRetainedValue() as String?
    }

    private static func property<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, initial: T) -> T? {
        var address = Self.address(selector)
        var value = initial
        var size = UInt32(MemoryLayout<T>.size)
        return AudioObjectGetPropertyData(object, &address, 0, nil, &size, &value) == noErr ? value : nil
    }

    private static func address(
        _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal
    ) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    // MARK: - Unit

    private static func check(_ status: OSStatus) throws {
        guard status == noErr else { throw MicCaptureError.startUnit(status) }
    }

    private static func set<T>(
        _ unit: AudioUnit, _ property: AudioUnitPropertyID, _ scope: AudioUnitScope, _ element: AudioUnitElement, _ value: T
    ) throws {
        var value = value
        try check(AudioUnitSetProperty(unit, property, scope, element, &value, UInt32(MemoryLayout<T>.size)))
    }
}

/// One run's input callback state, allocated before the unit starts: one non-interleaved buffer per device channel
/// and a downmix buffer, each `capacity` frames.
///
/// `@unchecked Sendable`: only the unit's IO thread touches the buffers after `init`.
private final class Input: @unchecked Sendable {
    let unit: AudioUnit
    let ring: RingBuffer
    let channels: Int
    let capacity: Int
    let buffers: UnsafeMutableAudioBufferListPointer
    let data: UnsafeMutablePointer<Float>
    let mono: UnsafeMutablePointer<Float>

    init(unit: AudioUnit, ring: RingBuffer, channels: Int, capacity: Int) {
        self.unit = unit
        self.ring = ring
        self.channels = channels
        self.capacity = capacity
        buffers = AudioBufferList.allocate(maximumBuffers: channels)
        data = .allocate(capacity: channels * capacity)
        data.initialize(repeating: 0, count: channels * capacity)
        mono = .allocate(capacity: capacity)
        mono.initialize(repeating: 0, count: capacity)
    }

    deinit {
        free(buffers.unsafeMutablePointer)
        data.deallocate()
        mono.deallocate()
    }

    /// Runs on the unit's IO thread: no allocation, no locks. Stamps the ring with the slice's capture host time.
    func render(flags: UnsafeMutablePointer<AudioUnitRenderActionFlags>, timestamp: UnsafePointer<AudioTimeStamp>, frames: Int) -> OSStatus {
        guard frames <= capacity else { return kAudioUnitErr_TooManyFramesToProcess }
        // The render rewrites the sizes (and may the pointers), so both are set again for every slice.
        for channel in 0..<channels {
            buffers[channel] = AudioBuffer(
                mNumberChannels: 1, mDataByteSize: UInt32(frames * MemoryLayout<Float>.size),
                mData: UnsafeMutableRawPointer(data + channel * capacity)
            )
        }
        let status = AudioUnitRender(unit, flags, timestamp, 1, UInt32(frames), buffers.unsafeMutablePointer)
        guard status == noErr else { return status }
        let time = timestamp.pointee
        let hostTime = time.mFlags.contains(.hostTimeValid) ? time.mHostTime : mach_absolute_time()
        if channels == 1 {
            ring.write(data, count: frames, hostTime: hostTime)
            return noErr
        }

        var scale = 1 / Float(channels)
        vDSP_vsmul(data, 1, &scale, mono, 1, vDSP_Length(frames))
        for channel in 1..<channels {
            vDSP_vsma(data + channel * capacity, 1, &scale, mono, 1, mono, 1, vDSP_Length(frames))
        }
        ring.write(mono, count: frames, hostTime: hostTime)
        return noErr
    }
}
