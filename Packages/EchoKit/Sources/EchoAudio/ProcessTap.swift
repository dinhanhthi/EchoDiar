import CoreAudio
import Darwin
import EchoCore
import Foundation

/// What the system-audio tap records. `RecordingEngine` (2.8) maps its capture target onto this.
public enum TapTarget: Sendable, Hashable {
    case perApp(group: AudioProcessGroup)
    /// Every process except EchoDiar itself.
    case global
}

public enum ProcessTapError: Error, Sendable {
    case noProcesses
    case createTap(OSStatus)
    case createAggregateDevice(OSStatus)
    case addFormatListener(OSStatus)
    case readFormat(OSStatus)
    /// The tap is not mono Float32 linear PCM.
    case unsupportedFormat(formatID: AudioFormatID, flags: AudioFormatFlags, bitsPerChannel: UInt32, channels: UInt32)
    case createIOProc(OSStatus)
    case startDevice(OSStatus)
}

/// Core Audio process tap in a private aggregate device; the IOProc copies mono Float32 frames into `currentRing`.
///
/// `@unchecked Sendable`: call `start`/`stop` from one serial queue. The IO thread only touches the ring.
public final class ProcessTap: @unchecked Sendable {
    public struct Format: Sendable, Equatable {
        public let sampleRate: Double
        public let channels: Int
    }

    /// Ring of the current (or last) run; kept after `stop` so it can be drained, replaced by the next `start`.
    public private(set) var currentRing: RingBuffer?
    public private(set) var format: Format?
    /// `currentRing` and `format` together. Both change only in `start`, on the caller's queue.
    public var currentSource: (ring: RingBuffer, format: Format)? {
        guard let currentRing, let format else { return nil }
        return (currentRing, format)
    }
    /// Fired on a utility queue (never the IO thread) when the tap format changes; rebuild the tap in response.
    /// Read at `start`. May fire once more right after `stop`.
    public var onFormatChange: (@Sendable (Format) -> Void)?

    private static let ringSeconds = 4.0

    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var ioProcID: AudioDeviceIOProcID?
    private var isRunning = false
    private var formatListener: AudioObjectPropertyListenerBlock?
    private let listenerQueue = DispatchQueue(label: "EchoDiar.ProcessTap.format", qos: .utility)

    public init() {}

    deinit {
        stop()
    }

    /// Stops any previous run first, so calling `start` again rebuilds the tap.
    public func start(target: TapTarget) throws {
        stop()
        do {
            let description = try makeDescription(target)
            try check(AudioHardwareCreateProcessTap(description, &tapID), ProcessTapError.createTap)
            try createAggregateDevice(tapUID: description.uuid.uuidString)
            let format = try Self.readFormat(tapID)
            self.format = format
            try addFormatListener(initial: format)

            let ring = RingBuffer(capacityFrames: Int((format.sampleRate * Self.ringSeconds).rounded(.up)), sampleRate: format.sampleRate)
            currentRing = ring
            try check(
                AudioDeviceCreateIOProcID(aggregateID, tapIOProc, Unmanaged.passUnretained(ring).toOpaque(), &ioProcID),
                ProcessTapError.createIOProc
            )
            try check(AudioDeviceStart(aggregateID, ioProcID), ProcessTapError.startDevice)
            isRunning = true
        } catch {
            stop()
            throw error
        }
    }

    /// Reverse-order teardown; safe to call repeatedly.
    public func stop() {
        if let listener = formatListener {
            var address = Self.formatAddress
            _ = AudioObjectRemovePropertyListenerBlock(tapID, &address, listenerQueue, listener)
            formatListener = nil
        }
        if isRunning {
            _ = AudioDeviceStop(aggregateID, ioProcID)
            isRunning = false
        }
        if let procID = ioProcID {
            _ = AudioDeviceDestroyIOProcID(aggregateID, procID)
            ioProcID = nil
        }
        if aggregateID != kAudioObjectUnknown {
            _ = AudioHardwareDestroyAggregateDevice(aggregateID)
            aggregateID = AudioObjectID(kAudioObjectUnknown)
        }
        if tapID != kAudioObjectUnknown {
            _ = AudioHardwareDestroyProcessTap(tapID)
            tapID = AudioObjectID(kAudioObjectUnknown)
        }
    }

    // MARK: - Setup

    private func makeDescription(_ target: TapTarget) throws -> CATapDescription {
        let description: CATapDescription
        switch target {
        case .perApp(let group):
            var bundleIDs: [String] = []
            if #available(macOS 26, *), !PlatformFeatures.forceFallbacks {
                bundleIDs = Self.bundleIDs(of: group)
            }
            guard !group.objectIDs.isEmpty || !bundleIDs.isEmpty else { throw ProcessTapError.noProcesses }
            description = CATapDescription(monoMixdownOfProcesses: group.objectIDs)
            if #available(macOS 26, *), !bundleIDs.isEmpty {
                description.bundleIDs = bundleIDs
                description.isProcessRestoreEnabled = true
            }
        case .global:
            // Without a process object EchoDiar has no audio output to exclude.
            let own = AudioProcessList.ownProcessObject().map { [$0] } ?? []
            description = CATapDescription(monoGlobalTapButExcludeProcesses: own)
        }
        description.name = "EchoDiar Tap"
        description.isPrivate = true
        description.muteBehavior = .unmuted
        return description
    }

    /// Real bundle IDs only: groups can carry synthetic "pid:N" or path identities,
    /// and the shared WebKit GPU id would restore other apps' web audio.
    private static func bundleIDs(of group: AudioProcessGroup) -> [String] {
        var result: [String] = []
        for id in [group.bundleID] + group.processes.compactMap(\.bundleID)
        where !id.hasPrefix("pid:") && !id.contains("/") && id != AudioProcessList.webKitGPUBundleID && !result.contains(id) {
            result.append(id)
        }
        return result
    }

    private func createAggregateDevice(tapUID: String) throws {
        let composition: [String: Any] = [
            kAudioAggregateDeviceNameKey: "EchoDiar Tap",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceTapListKey: [
                [kAudioSubTapUIDKey: tapUID, kAudioSubTapDriftCompensationKey: true],
            ],
        ]
        try check(
            AudioHardwareCreateAggregateDevice(composition as CFDictionary, &aggregateID),
            ProcessTapError.createAggregateDevice
        )
    }

    private func addFormatListener(initial: Format) throws {
        let tap = tapID
        let handler = onFormatChange
        let listener: AudioObjectPropertyListenerBlock = { _, _ in
            guard let current = Self.currentFormat(tap), current != initial else { return }
            handler?(current)
        }
        var address = Self.formatAddress
        try check(AudioObjectAddPropertyListenerBlock(tap, &address, listenerQueue, listener), ProcessTapError.addFormatListener)
        formatListener = listener
        // A change between the first read and the listener install.
        if let current = Self.currentFormat(tap), current != initial {
            listenerQueue.async { handler?(current) }
        }
    }

    private static let formatAddress = AudioObjectPropertyAddress(
        mSelector: kAudioTapPropertyFormat,
        mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain
    )

    private static func readASBD(_ tap: AudioObjectID) throws -> AudioStreamBasicDescription {
        var address = formatAddress
        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        try check(AudioObjectGetPropertyData(tap, &address, 0, nil, &size, &asbd), ProcessTapError.readFormat)
        return asbd
    }

    /// Unvalidated, so an unsupported new format still reaches `onFormatChange`.
    private static func currentFormat(_ tap: AudioObjectID) -> Format? {
        guard let asbd = try? readASBD(tap) else { return nil }
        return Format(sampleRate: asbd.mSampleRate, channels: Int(asbd.mChannelsPerFrame))
    }

    private static func readFormat(_ tap: AudioObjectID) throws -> Format {
        let asbd = try readASBD(tap)
        guard asbd.mFormatID == kAudioFormatLinearPCM,
              asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0,
              asbd.mBitsPerChannel == 32,
              asbd.mChannelsPerFrame == 1,
              asbd.mSampleRate > 0 else {
            throw ProcessTapError.unsupportedFormat(
                formatID: asbd.mFormatID, flags: asbd.mFormatFlags,
                bitsPerChannel: asbd.mBitsPerChannel, channels: asbd.mChannelsPerFrame
            )
        }
        return Format(sampleRate: asbd.mSampleRate, channels: Int(asbd.mChannelsPerFrame))
    }
}

private func check(_ status: OSStatus, _ error: (OSStatus) -> ProcessTapError) throws {
    guard status == noErr else { throw error(status) }
}

/// IO thread: copies the first buffer's mono Float32 frames into the ring passed as client data. No allocation, no locks.
private let tapIOProc: AudioDeviceIOProc = { _, _, inputData, inputTime, _, _, clientData in
    guard let clientData, inputData.pointee.mNumberBuffers > 0 else { return noErr }
    let buffer = inputData.pointee.mBuffers
    guard buffer.mNumberChannels == 1, let data = buffer.mData else { return noErr }
    let hostTime = inputTime.pointee.mFlags.contains(.hostTimeValid) ? inputTime.pointee.mHostTime : mach_absolute_time()
    let ring = Unmanaged<RingBuffer>.fromOpaque(clientData).takeUnretainedValue()
    ring.write(
        data.assumingMemoryBound(to: Float.self),
        count: Int(buffer.mDataByteSize) / MemoryLayout<Float>.size,
        hostTime: hostTime
    )
    return noErr
}
