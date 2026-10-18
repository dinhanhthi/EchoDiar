import CoreAudio
import Darwin
import EchoCore
import Foundation
import Synchronization

/// What a recording captures as system audio.
public enum CaptureTarget: Sendable, Hashable {
    case app(AudioProcessGroup)
    /// Every process except EchoDiar itself.
    case global

    /// Groups whose processes cannot all be attributed fall back to a global tap (see `globalTapFallback`).
    public var tapTarget: TapTarget {
        switch self {
        case .app(let group) where !group.needsGlobalTap: .perApp(group: group)
        default: .global
        }
    }

    /// The app group containing `pid`, else a group with only that process. Nil if Core Audio does not know the pid.
    public static func process(pid: pid_t) -> CaptureTarget? {
        if let group = AudioProcessList.groups().first(where: { $0.processes.contains { $0.pid == pid } }) {
            return .app(group)
        }
        guard let objectID = AudioProcessList.processObject(for: pid),
              let process = AudioProcessList.process(for: objectID) else { return nil }
        let group = AudioProcessGroup(bundleID: process.bundleID ?? "pid:\(pid)", name: "pid \(pid)",
                                      processes: [process], needsGlobalTap: false)
        return .app(group)
    }

    /// An app was picked but `tapTarget` records every process except EchoDiar.
    public var globalTapFallback: Bool {
        if case .app(let group) = self { return group.needsGlobalTap }
        return false
    }

    var tapMode: RecordingSession.TapMode {
        if case .perApp = tapTarget { return .perApp }
        return .global
    }

    var sourceApps: [RecordingSession.SourceApp] {
        guard case .app(let group) = self else { return [] }
        var bundleIDs = [group.bundleID]
        for id in group.processes.compactMap(\.bundleID) where !bundleIDs.contains(id) {
            bundleIDs.append(id)
        }
        return [RecordingSession.SourceApp(name: group.name, bundleIDs: bundleIDs, pids: group.processes.map(\.pid))]
    }

    /// Fresh Core Audio query, so call it rarely.
    var isPlayingOutput: Bool {
        switch self {
        case .app(let group):
            return group.processes.contains { AudioProcessList.process(for: $0.objectID)?.isRunningOutput == true }
        case .global:
            let own = getpid()
            return AudioProcessList.processes().contains { $0.pid != own && $0.isRunningOutput }
        }
    }
}

public enum RecordingEngineError: Error, Sendable {
    case alreadyRecording
    /// Less than `RecordingEngine.minimumFreeBytes` available for important usage.
    case diskFull(availableBytes: Int64)
}

/// Orchestrates the system tap, the mic and the writer for one recording at a time, and keeps `session.json` current.
///
/// `@unchecked Sendable`: mutable state is owned by one serial queue (the public API hops onto it);
/// what the UI pulls without hopping (`currentLevels`, `currentSnapshot`) sits behind a `Mutex`.
public final class RecordingEngine: @unchecked Sendable {
    public enum State: Sendable, Equatable {
        case idle, recording, paused
    }

    /// Stored as `RecordingSession.endReason`.
    public enum EndReason: String, Sendable {
        case user, diskFull, writeFailed, captureFailed
    }

    public struct Snapshot: Sendable, Equatable {
        public var state: State = .idle
        /// Seconds written to the audio file.
        public var elapsed: Double = 0
        public var silentTapSuspected = false
        /// The picked app could not be isolated, so every process except EchoDiar is recorded; warn the user.
        public var globalTapFallback = false
        /// Less than 1 GB left on the recording volume.
        public var lowDisk = false
        public var micSilentSeconds: Double = 0
        public var systemSilentSeconds: Double = 0
        /// Set on the snapshot published when a recording ends.
        public var endReason: EndReason?

        mutating func apply(_ stats: ChannelWriter.Stats) {
            elapsed = stats.duration
            silentTapSuspected = stats.silentTapSuspected
            micSilentSeconds = stats.micSilentSeconds
            systemSilentSeconds = stats.systemSilentSeconds
        }
    }

    public static let lowDiskBytes: Int64 = 1_000_000_000
    public static let minimumFreeBytes: Int64 = 200_000_000

    /// Published on state changes and flag flips only; single consumer.
    public let snapshots: AsyncStream<Snapshot>

    /// Read at `start`, so set it before; nil (the default) costs nothing.
    public var liveSink: LiveSink? {
        get { shared.withLock { $0.liveSink } }
        set { shared.withLock { $0.liveSink = newValue } }
    }

    private struct Shared {
        var snapshot = Snapshot()
        var writer: ChannelWriter?
        var micRing: RingBuffer?
        var systemRing: RingBuffer?
        var liveSink: LiveSink?
        var language: String?
    }

    /// Gets each written block on the writer queue: interleaved Int16 16 kHz samples (see `RecordingSession.Channel`),
    /// frame count, channel count. The pointer is valid only during the call.
    public typealias LiveSink = @Sendable (UnsafePointer<Int16>, Int, Int) -> Void

    private static let monitorInterval = 1
    private static let diskCheckTicks = 60
    private static let silentFormat = ChannelWriter.SourceFormat(sampleRate: ChannelWriter.sampleRate, channels: 1)

    private let queue = DispatchQueue(label: "EchoDiar.RecordingEngine", qos: .userInitiated)
    private let queueKey = DispatchSpecificKey<Void>()
    private let listenerQueue = DispatchQueue(label: "EchoDiar.RecordingEngine.output", qos: .utility)
    private let continuation: AsyncStream<Snapshot>.Continuation
    private let shared = Mutex(Shared())
    private let tap = ProcessTap()
    private let mic = MicCapture()

    private var state = State.idle
    private var target = CaptureTarget.global
    private var session: RecordingSession?
    private var folder: URL?
    private var writer: ChannelWriter?
    private var activity: (any NSObjectProtocol)?
    private var monitor: DispatchSourceTimer?
    private var ticks = 0
    private var outputListener: AudioObjectPropertyListenerBlock?
    private var sessionGeneration: UInt64 = 0
    private var tapGeneration: UInt64 = 0
    private var micGeneration: UInt64 = 0
    private var lowDisk = false
    private var silentTapSuspected = false

    public init() {
        (snapshots, continuation) = AsyncStream.makeStream(of: Snapshot.self, bufferingPolicy: .bufferingNewest(16))
        queue.setSpecific(key: queueKey, value: ())
    }

    deinit {
        continuation.finish()
    }

    // MARK: - Public API

    /// Writes `session.json` before any IO, then starts the tap, the mic (silence if unavailable) and the writer.
    /// Returns the session folder. Throws if the disk is nearly full or the tap cannot start.
    /// `probe` marks the session as recorded by the DEBUG `--capture-probe` harness.
    @discardableResult
    public func start(target: CaptureTarget, language: String, root: URL = RecordingSession.conversationsRoot, probe: Bool = false) throws -> URL {
        try onQueue { try startLocked(target: target, language: language, root: root, probe: probe) }
    }

    /// Closes the tap and the mic (privacy indicator off) and pauses the writer.
    public func pause() {
        onQueue { pauseLocked() }
    }

    /// Reopens the tap and the mic. Throws, staying paused, if the tap cannot restart.
    public func resume() throws {
        try onQueue { try resumeLocked() }
    }

    /// Returns the final session, or nil when idle.
    @discardableResult
    public func stop(reason: EndReason = .user) -> RecordingSession? {
        onQueue { stopLocked(reason: reason) }
    }

    /// Appends a language marker ("auto" or BCP-47) at the current file time.
    public func setLanguage(_ language: String) {
        queue.async { [self] in
            guard state != .idle, let writer, var session else { return }
            session.languageMarkers.append(.init(fileTime: writer.stats.duration, language: language))
            save(session)
            shared.withLock { $0.language = language }
        }
    }

    /// The language of the last marker ("auto" or BCP-47), or nil when idle. Lock-only.
    public func currentLanguage() -> String? {
        shared.withLock { $0.language }
    }

    /// Peak levels (0...1) since the previous call. Lock-only; one caller, at ≤ 15 Hz.
    public func currentLevels() -> (mic: Float, system: Float) {
        let (mic, system) = shared.withLock { ($0.micRing, $0.systemRing) }
        return (mic?.readLevels().peak ?? 0, system?.readLevels().peak ?? 0)
    }

    /// The last published snapshot with live writer stats (elapsed, silence). Lock-only.
    public func currentSnapshot() -> Snapshot {
        let (snapshot, writer) = shared.withLock { ($0.snapshot, $0.writer) }
        guard let writer else { return snapshot }
        var live = snapshot
        live.apply(writer.stats)
        return live
    }

    // MARK: - Engine queue

    private func onQueue<T>(_ body: () throws -> T) rethrows -> T {
        if DispatchQueue.getSpecific(key: queueKey) != nil { return try body() }
        return try queue.sync(execute: body)
    }

    private func startLocked(target: CaptureTarget, language: String, root: URL, probe: Bool) throws -> URL {
        guard state == .idle else { throw RecordingEngineError.alreadyRecording }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let available = Self.availableCapacity(at: root)
        if let available, available < Self.minimumFreeBytes { throw RecordingEngineError.diskFull(availableBytes: available) }
        lowDisk = available.map { $0 < Self.lowDiskBytes } ?? false
        silentTapSuspected = false
        self.target = target

        var session = RecordingSession(
            state: .recording,
            sourceApps: target.sourceApps,
            tapMode: target.tapMode,
            globalTapFallback: target.globalTapFallback,
            micDeviceName: Self.defaultDeviceName(kAudioHardwarePropertyDefaultInputDevice),
            outputDeviceName: Self.defaultDeviceName(kAudioHardwarePropertyDefaultOutputDevice),
            languageMarkers: [.init(fileTime: 0, language: language)],
            probe: probe
        )
        let folder = session.folder(in: root)
        try session.write(to: folder)

        let system: (ring: RingBuffer, format: ChannelWriter.SourceFormat)
        do {
            system = try startTap()
        } catch {
            markFailed(session, in: folder)
            throw error
        }
        let micSource = startMic()
        if !micSource.isLive {
            session.micDeviceName = nil
            save(session, in: folder)
        }

        let writer: ChannelWriter
        do {
            writer = try ChannelWriter(fileURL: folder.appendingPathComponent(session.audioFileName))
            let check = OutputCheck(target: target)
            writer.isTargetPlayingOutput = { check.isPlaying() }
            writer.liveSink = liveSink
            try writer.start(micRing: micSource.ring, systemRing: system.ring, micFormat: micSource.format, systemFormat: system.format)
        } catch {
            tap.stop()
            mic.stop()
            markFailed(session, in: folder)
            throw error
        }

        sessionGeneration &+= 1
        state = .recording
        self.session = session
        self.folder = folder
        self.writer = writer
        activity = ProcessInfo.processInfo.beginActivity(options: [.userInitiated, .idleSystemSleepDisabled], reason: "Recording meeting")
        installOutputListener()
        startMonitor()
        shared.withLock {
            $0.writer = writer
            $0.micRing = micSource.ring
            $0.systemRing = system.ring
            $0.language = language
        }
        publish()
        return folder
    }

    private func pauseLocked() {
        guard state == .recording, let writer, var session else { return }
        tap.stop()
        mic.stop()
        writer.pause()
        session.pauses.append(.init(fileTime: writer.stats.duration, start: Date()))
        session.state = .paused
        state = .paused
        save(session)
        publish()
    }

    private func resumeLocked() throws {
        guard state == .paused, let writer, var session else { return }
        let system = try startTap()
        let micSource = startMic()
        do {
            try writer.replaceRing(channel: .system, ring: system.ring, format: system.format)
            try writer.replaceRing(channel: .mic, ring: micSource.ring, format: micSource.format)
        } catch {
            tap.stop()
            mic.stop()
            throw error
        }
        writer.resume()
        closeOpenPause(&session)
        session.state = .recording
        state = .recording
        save(session)
        shared.withLock {
            $0.micRing = micSource.ring
            $0.systemRing = system.ring
        }
        publish()
    }

    private func stopLocked(reason: EndReason) -> RecordingSession? {
        guard state != .idle, var session, let folder else { return nil }
        monitor?.cancel()
        monitor = nil
        removeOutputListener()
        tap.stop()
        mic.stop()
        var reason = reason
        do {
            try writer?.stop()
        } catch {
            reason = .writeFailed
        }
        if let activity { ProcessInfo.processInfo.endActivity(activity) }
        activity = nil

        closeOpenPause(&session)
        session.state = .stopped
        session.endReason = reason.rawValue
        save(session, in: folder)

        let stats = writer?.stats
        sessionGeneration &+= 1
        state = .idle
        self.session = nil
        self.folder = nil
        writer = nil
        shared.withLock {
            $0.writer = nil
            $0.micRing = nil
            $0.systemRing = nil
            $0.language = nil
        }
        publish(stats: stats, endReason: reason)
        return session
    }

    // MARK: - Sources

    private func startTap() throws -> (ring: RingBuffer, format: ChannelWriter.SourceFormat) {
        tapGeneration &+= 1
        let generation = tapGeneration
        tap.onFormatChange = { [weak self] _ in
            guard let self else { return }
            self.queue.async { self.rebuildTap(generation: generation) }
        }
        try tap.start(target: target.tapTarget)
        guard let source = tap.currentSource else {
            tap.stop()
            throw ProcessTapError.noProcesses
        }
        return (source.ring, ChannelWriter.SourceFormat(source.format))
    }

    /// Falls back to a ring nothing writes to, which the writer fills with silence.
    private func startMic() -> (ring: RingBuffer, format: ChannelWriter.SourceFormat, isLive: Bool) {
        micGeneration &+= 1
        let generation = micGeneration
        mic.onConfigurationChange = { [weak self] _ in
            guard let self else { return }
            self.queue.async { self.micChanged(generation: generation) }
        }
        if (try? mic.start(deviceUID: nil)) != nil, let source = mic.currentSource {
            return (source.ring, ChannelWriter.SourceFormat(source.format), true)
        }
        mic.stop()
        let ring = RingBuffer(capacityFrames: 1_024, sampleRate: Self.silentFormat.sampleRate)
        return (ring, Self.silentFormat, false)
    }

    /// On failure the mic keeps recording and the system channel pads silence.
    private func rebuildTap(generation: UInt64) {
        guard state == .recording, generation == tapGeneration, let writer else { return }
        do {
            let system = try startTap()
            try writer.replaceRing(channel: .system, ring: system.ring, format: system.format)
            shared.withLock { $0.systemRing = system.ring }
        } catch {
            tap.stop()
        }
    }

    /// `MicCapture` has already rebuilt itself; only its new ring is swapped in.
    /// A rejected replace is skipped, keeping the mic open: the next change event applies.
    private func micChanged(generation: UInt64) {
        guard state == .recording, generation == micGeneration, let writer,
              let source = mic.currentSource else { return }
        guard (try? writer.replaceRing(channel: .mic, ring: source.ring, format: ChannelWriter.SourceFormat(source.format))) != nil else { return }
        shared.withLock { $0.micRing = source.ring }
    }

    // MARK: - Default output device

    private static var defaultOutputAddress: AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
    }

    private func installOutputListener() {
        let generation = sessionGeneration
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            guard let self else { return }
            self.queue.async { self.outputDeviceChanged(generation: generation) }
        }
        var address = Self.defaultOutputAddress
        if AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, listenerQueue, listener) == noErr {
            outputListener = listener
        }
    }

    private func removeOutputListener() {
        guard let listener = outputListener else { return }
        var address = Self.defaultOutputAddress
        _ = AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, listenerQueue, listener)
        outputListener = nil
    }

    private func outputDeviceChanged(generation: UInt64) {
        guard generation == sessionGeneration, var session else { return }
        session.outputDeviceName = Self.defaultDeviceName(kAudioHardwarePropertyDefaultOutputDevice)
        save(session)
        rebuildTap(generation: tapGeneration)
    }

    // MARK: - Monitor (only while a session is open)

    private func startMonitor() {
        ticks = 0
        let timer = DispatchSource.makeTimerSource(queue: queue)
        let interval = DispatchTimeInterval.seconds(Self.monitorInterval)
        timer.schedule(deadline: .now() + interval, repeating: interval, leeway: .milliseconds(250))
        timer.setEventHandler { [weak self] in self?.tick() }
        timer.resume()
        monitor = timer
    }

    private func tick() {
        guard state != .idle, let writer else { return }
        ticks += 1
        if ticks % Self.diskCheckTicks == 0, let folder, let available = Self.availableCapacity(at: folder) {
            if available < Self.minimumFreeBytes {
                _ = stopLocked(reason: .diskFull)
                return
            }
            if (available < Self.lowDiskBytes) != lowDisk {
                lowDisk.toggle()
                publish()
            }
        }
        guard state == .recording else { return }
        let stats = writer.stats
        if stats.failure != nil {
            _ = stopLocked(reason: .writeFailed)
        } else if stats.silentTapSuspected != silentTapSuspected {
            silentTapSuspected = stats.silentTapSuspected
            publish(stats: stats)
        }
    }

    // MARK: - Helpers

    private func publish(stats: ChannelWriter.Stats? = nil, endReason: EndReason? = nil) {
        var snapshot = Snapshot(state: state, globalTapFallback: target.globalTapFallback, lowDisk: lowDisk, endReason: endReason)
        if let stats = stats ?? writer?.stats { snapshot.apply(stats) }
        shared.withLock { $0.snapshot = snapshot }
        continuation.yield(snapshot)
    }

    private func closeOpenPause(_ session: inout RecordingSession) {
        if let last = session.pauses.indices.last, session.pauses[last].end == nil {
            session.pauses[last].end = Date()
        }
    }

    private func save(_ session: RecordingSession, in folder: URL? = nil) {
        guard let folder = folder ?? self.folder else { return }
        if state != .idle { self.session = session }
        try? session.write(to: folder)
    }

    private func markFailed(_ session: RecordingSession, in folder: URL) {
        var session = session
        session.state = .failed
        session.endReason = EndReason.captureFailed.rawValue
        try? session.write(to: folder)
    }

    private static func availableCapacity(at url: URL) -> Int64? {
        var url = url
        url.removeAllCachedResourceValues()
        return try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage
    }

    private static func defaultDeviceName(_ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var device = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device) == noErr,
              device != kAudioObjectUnknown else { return nil }
        address.mSelector = kAudioObjectPropertyName
        var name: Unmanaged<CFString>?
        size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &name) == noErr,
              let value = name?.takeRetainedValue() as String? else { return nil }
        return value
    }
}

/// `isTargetPlayingOutput` for the writer, which asks every drain once the system channel has been zero for 5 s:
/// one Core Audio query per 2 s at most.
private final class OutputCheck: Sendable {
    private let target: CaptureTarget
    private let cache = Mutex<(uptime: TimeInterval, playing: Bool)?>(nil)

    init(target: CaptureTarget) {
        self.target = target
    }

    func isPlaying() -> Bool {
        let now = ProcessInfo.processInfo.systemUptime
        if let cached = cache.withLock({ $0 }), now - cached.uptime < 2 { return cached.playing }
        let playing = target.isPlayingOutput
        cache.withLock { $0 = (now, playing) }
        return playing
    }
}
