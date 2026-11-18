import CoreAudio

/// The OS input mute of a device: its mute switch on, or its input volume at 0 (System Settings' input slider).
enum InputMute {
    /// Muted when every channel is muted, or every channel's volume is 0. An empty list (no such property) mutes nothing.
    static func isMuted(mutes: [Bool], volumes: [Float]) -> Bool {
        (!mutes.isEmpty && mutes.allSatisfy { $0 }) || (!volumes.isEmpty && volumes.allSatisfy { $0 <= 0.001 })
    }

    /// Reads the device's input-scope mute and volume (see `addresses`).
    static func isMuted(_ device: AudioDeviceID) -> Bool {
        let mutes = addresses(device, kAudioDevicePropertyMute).compactMap { value(device, $0, initial: UInt32(0)) }
        let volumes = addresses(device, kAudioDevicePropertyVolumeScalar).compactMap { value(device, $0, initial: Float32(0)) }
        return isMuted(mutes: mutes.map { $0 != 0 }, volumes: volumes)
    }

    /// The device `MicCapture` opens is muted, or, when following the system default, the default input is: with
    /// AirPods as default input the built-in mic is opened, but muting AirPods is what the user did.
    static func isSystemMuted(micDeviceUID: String?) -> Bool {
        if let device = MicCapture.inputDevice(uid: micDeviceUID), isMuted(device) { return true }
        guard micDeviceUID == nil, let input = MicCapture.defaultDevice(kAudioHardwarePropertyDefaultInputDevice) else { return false }
        return isMuted(input)
    }

    /// The device's mute and volume addresses that exist, for property listeners.
    static func propertyAddresses(_ device: AudioDeviceID) -> [AudioObjectPropertyAddress] {
        addresses(device, kAudioDevicePropertyMute) + addresses(device, kAudioDevicePropertyVolumeScalar)
    }

    /// The main element when the device has the property there, else each input channel that has it.
    private static func addresses(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> [AudioObjectPropertyAddress] {
        let main = address(selector, kAudioObjectPropertyElementMain)
        if has(device, main) { return [main] }
        let channels = inputChannels(device)
        guard channels > 0 else { return [] }
        return (1...channels).map { address(selector, AudioObjectPropertyElement($0)) }.filter { has(device, $0) }
    }

    private static func inputChannels(_ device: AudioDeviceID) -> Int {
        var address = Self.address(kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr, size > 0 else { return 0 }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, raw) == noErr else { return 0 }
        let buffers = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return buffers.reduce(0) { $0 + Int($1.mNumberChannels) }
    }

    private static func has(_ device: AudioDeviceID, _ address: AudioObjectPropertyAddress) -> Bool {
        var address = address
        return AudioObjectHasProperty(device, &address)
    }

    private static func value<T>(_ device: AudioDeviceID, _ address: AudioObjectPropertyAddress, initial: T) -> T? {
        var address = address
        var value = initial
        var size = UInt32(MemoryLayout<T>.size)
        return AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr ? value : nil
    }

    private static func address(_ selector: AudioObjectPropertySelector, _ element: AudioObjectPropertyElement) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeInput, mElement: element)
    }
}
