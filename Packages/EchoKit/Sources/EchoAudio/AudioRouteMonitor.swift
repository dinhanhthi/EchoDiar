import CoreAudio
import Foundation

/// Route changes during a recording, from Core Audio listeners: default input and output device, the device list,
/// and `DeviceIsAlive`/`NominalSampleRate` of the input device in use and of the default output.
/// Events within 300 ms are coalesced and the state is compared as a whole, since a disconnect fires its
/// notifications (device dead, defaults changed, list changed) in no guaranteed order.
///
/// `@unchecked Sendable`: state is owned by a private serial queue; `start`/`stop` hop onto it.
public final class AudioRouteMonitor: @unchecked Sendable {
    public enum Event: Sendable, Equatable {
        /// The default output device, or its sample rate, changed.
        case outputChanged
        /// The input device in use was replaced (a new default, or the picked device is back) or its rate changed.
        case inputChanged
        /// The input device in use died or disappeared (AirPods disconnected); input now follows the default.
        case inputLost
    }

    private static let coalesce = DispatchTimeInterval.milliseconds(300)
    private static let systemSelectors = [
        kAudioHardwarePropertyDefaultInputDevice, kAudioHardwarePropertyDefaultOutputDevice, kAudioHardwarePropertyDevices,
    ]
    private static let deviceSelectors = [kAudioDevicePropertyDeviceIsAlive, kAudioDevicePropertyNominalSampleRate]
    private static let system = AudioObjectID(kAudioObjectSystemObject)

    private let queue = DispatchQueue(label: "EchoDiar.AudioRouteMonitor", qos: .utility)
    private let queueKey = DispatchSpecificKey<Void>()
    private let listenerQueue = DispatchQueue(label: "EchoDiar.AudioRouteMonitor.listener", qos: .utility)

    private var handler: (@Sendable (Event) -> Void)?
    private var pinnedUID: String?
    private var generation: UInt64 = 0
    private var listener: AudioObjectPropertyListenerBlock?
    private var pending: DispatchWorkItem?
    private var input = (device: AudioDeviceID(kAudioObjectUnknown), rate: 0.0)
    private var output = (device: AudioDeviceID(kAudioObjectUnknown), rate: 0.0)
    /// Devices whose `deviceSelectors` are listened to.
    private var watched: [AudioDeviceID] = []

    public init() {
        queue.setSpecific(key: queueKey, value: ())
    }

    deinit {
        stop()
    }

    /// `inputDeviceUID`: the picked mic (nil = the default input); while it is present it is the input in use.
    /// `handler` runs on the monitor's queue. Stops any previous run first.
    public func start(inputDeviceUID: String?, handler: @escaping @Sendable (Event) -> Void) {
        onQueue {
            stopLocked()
            generation &+= 1
            pinnedUID = inputDeviceUID
            self.handler = handler
            let generation = generation
            let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
                guard let self else { return }
                self.queue.async { self.scheduleEvaluate(generation: generation) }
            }
            self.listener = listener
            for selector in Self.systemSelectors {
                var address = Self.address(selector)
                _ = AudioObjectAddPropertyListenerBlock(Self.system, &address, listenerQueue, listener)
            }
            input = (resolveInput(), 0)
            input.rate = Self.nominalRate(input.device)
            let device = Self.defaultDevice(kAudioHardwarePropertyDefaultOutputDevice)
            output = (device, Self.nominalRate(device))
            watchDevices()
        }
    }

    /// Safe to call repeatedly.
    public func stop() {
        onQueue { stopLocked() }
    }

    private func onQueue<T>(_ body: () throws -> T) rethrows -> T {
        if DispatchQueue.getSpecific(key: queueKey) != nil { return try body() }
        return try queue.sync(execute: body)
    }

    // MARK: - Queue-owned

    private func stopLocked() {
        generation &+= 1
        pending?.cancel()
        pending = nil
        handler = nil
        guard let listener else { return }
        for selector in Self.systemSelectors {
            var address = Self.address(selector)
            _ = AudioObjectRemovePropertyListenerBlock(Self.system, &address, listenerQueue, listener)
        }
        unwatchDevices()
        self.listener = nil
    }

    private func scheduleEvaluate(generation: UInt64) {
        guard generation == self.generation, pending == nil else { return }
        let item = DispatchWorkItem { [weak self] in self?.evaluate() }
        pending = item
        queue.asyncAfter(deadline: .now() + Self.coalesce, execute: item)
    }

    private func evaluate() {
        pending = nil
        guard let handler else { return }
        var events: [Event] = []

        let inputLost = input.device != kAudioObjectUnknown && !Self.isAlive(input.device)
        let inputDevice = resolveInput()
        let inputRate = Self.nominalRate(inputDevice)
        if inputLost {
            events.append(.inputLost)
        } else if inputDevice != input.device || inputRate != input.rate {
            events.append(.inputChanged)
        }
        input = (inputDevice, inputRate)

        let outputDevice = Self.defaultDevice(kAudioHardwarePropertyDefaultOutputDevice)
        let outputRate = Self.nominalRate(outputDevice)
        if outputDevice != output.device || outputRate != output.rate {
            events.append(.outputChanged)
        }
        output = (outputDevice, outputRate)

        if watched != [input.device, output.device] {
            unwatchDevices()
            watchDevices()
        }
        events.forEach(handler)
    }

    /// The picked device while it exists and is alive, else the default input.
    private func resolveInput() -> AudioDeviceID {
        if let pinnedUID, let device = MicCapture.deviceID(forUID: pinnedUID), Self.isAlive(device) { return device }
        return Self.defaultDevice(kAudioHardwarePropertyDefaultInputDevice)
    }

    private func watchDevices() {
        guard let listener else { return }
        watched = [input.device, output.device]
        for device in Set(watched) where device != kAudioObjectUnknown {
            for selector in Self.deviceSelectors {
                var address = Self.address(selector)
                _ = AudioObjectAddPropertyListenerBlock(device, &address, listenerQueue, listener)
            }
        }
    }

    /// Fails harmlessly for devices that are already gone.
    private func unwatchDevices() {
        guard let listener else { return }
        for device in Set(watched) where device != kAudioObjectUnknown {
            for selector in Self.deviceSelectors {
                var address = Self.address(selector)
                _ = AudioObjectRemovePropertyListenerBlock(device, &address, listenerQueue, listener)
            }
        }
        watched = []
    }

    // MARK: - Core Audio

    private static func address(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    }

    private static func defaultDevice(_ selector: AudioObjectPropertySelector) -> AudioDeviceID {
        var address = Self.address(selector)
        var device = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &device) == noErr else { return AudioDeviceID(kAudioObjectUnknown) }
        return device
    }

    /// A device that no longer answers counts as dead.
    private static func isAlive(_ device: AudioDeviceID) -> Bool {
        var address = Self.address(kAudioDevicePropertyDeviceIsAlive)
        var alive: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(device, &address, 0, nil, &size, &alive) == noErr && alive != 0
    }

    private static func nominalRate(_ device: AudioDeviceID) -> Double {
        guard device != kAudioObjectUnknown else { return 0 }
        var address = Self.address(kAudioDevicePropertyNominalSampleRate)
        var rate: Float64 = 0
        var size = UInt32(MemoryLayout<Float64>.size)
        return AudioObjectGetPropertyData(device, &address, 0, nil, &size, &rate) == noErr ? rate : 0
    }
}
