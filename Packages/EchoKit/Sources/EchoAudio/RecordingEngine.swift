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
        /// Times the mic device in use disappeared during this recording (the mic moved to the default input).
        public var micRoutesLost = 0
        /// The mic device is closed (privacy indicator off) and its channel is written as silence; see `toggleMicMuted`.
        public var micMuted = false
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
    private let continuation: AsyncStream<Snapshot>.Continuation
    private let shared = Mutex(Shared())
    private let tap = ProcessTap()
    private let mic = MicCapture()
    private let routes = AudioRouteMonitor()

    private var state = State.idle
    private var target = CaptureTarget.global
    private var micDeviceUID: String?
    private var session: RecordingSession?
    private var folder: URL?
    private var writer: ChannelWriter?
    private var activity: (any NSObjectProtocol)?
    private var monitor: DispatchSourceTimer?
    private var ticks = 0
    private var sessionGeneration: UInt64 = 0
    private var tapGeneration: UInt64 = 0
    private var micGeneration: UInt64 = 0
    private var lowDisk = false
    private var silentTapSuspected = false
    private var micRoutesLost = 0
    private var micMuted = false

    public init() {
        (snapshots, continuation) = AsyncStream.makeStream(of: Snapshot.self, bufferingPolicy: .bufferingNewest(16))
        queue.setSpecific(key: queueKey, value: ())
    }

    deinit {
        continuation.finish()
    }

    // MARK: - Public API

    /// Writes `session.json` before any IO, then starts the tap, the mic (silence if unavailable) and the writer.
    /// Returns the session folder. Throws if the disk is nearly full or the tap cannot start; the folder is then removed.
    /// `micDeviceUID` nil (or a device that is gone) records the default input.
    /// `probe` marks the session as recorded by the DEBUG `--capture-probe` harness.
    @discardableResult
    public func start(
        target: CaptureTarget, language: String, micDeviceUID: String? = nil,
        root: URL = RecordingSession.conversationsRoot, probe: Bool = false
    ) throws -> URL {
        try onQueue {
            try startLocked(target: target, language: language, micDeviceUID: micDeviceUID, root: root, probe: probe)
        }
    }

    /// Closes the tap and the mic (privacy indicator off) and pauses the writer.
    public func pause() {
        onQueue { pauseLocked() }
    }

    /// Reopens the tap and the mic (unless muted). Throws, staying paused, if the tap cannot restart.
    public func resume() throws {
        try onQueue { try resumeLocked() }
    }

    /// Flips the mute on the engine's queue, so quick toggles apply in order. Muted, the mic device is closed (privacy
    /// indicator off) and the mic channel is written as silence, so the file stays time-aligned; unmuted, the picked
    /// device (else the default input) is reopened, and while recording, if it cannot be, the mute stays on. While
    /// paused only the flag changes and `resume()` honours it; it is kept across pauses and cleared when the recording stops, so
    /// every recording starts unmuted. No-op when idle.
    public func toggleMicMuted() {
        onQueue { toggleMicMutedLocked() }
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

    private func startLocked(target: CaptureTarget, language: String, micDeviceUID: String?, root: URL, probe: Bool) throws -> URL {
        guard state == .idle else { throw RecordingEngineError.alreadyRecording }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let available = Self.availableCapacity(at: root)
        if let available, available < Self.minimumFreeBytes { throw RecordingEngineError.diskFull(availableBytes: available) }
        lowDisk = available.map { $0 < Self.lowDiskBytes } ?? false
        silentTapSuspected = false
        micRoutesLost = 0
        micMuted = false
        self.target = target
        self.micDeviceUID = micDeviceUID

        var session = RecordingSession(
            state: .recording,
            sourceApps: target.sourceApps,
            tapMode: target.tapMode,
            globalTapFallback: target.globalTapFallback,
            micDeviceName: Self.inputDeviceName(uid: micDeviceUID),
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
            discard(folder)
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
            discard(folder)
            throw error
        }

        sessionGeneration &+= 1
        state = .recording
        self.session = session
        self.folder = folder
        self.writer = writer
        activity = ProcessInfo.processInfo.beginActivity(options: [.userInitiated, .idleSystemSleepDisabled], reason: "Recording meeting")
        startRouteMonitor()
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
        // The input may have changed while paused (the swap is deferred to here).
        session.micDeviceName = micSource.isLive ? Self.inputDeviceName(uid: micDeviceUID) : nil
        session.state = .recording
        state = .recording
        save(session)
        shared.withLock {
            $0.micRing = micSource.ring
            $0.systemRing = system.ring
        }
        publish()
    }

    private func toggleMicMutedLocked() {
        guard state != .idle, var session else { return }
        micMuted.toggle()
        if state == .recording, let writer {
            // A failed unmute stays muted, so the controls say so and offer Unmute again; the mic channel stays silent.
            if !swapMic(&session, writer: writer), !micMuted {
                NSLog("EchoDiar: could not reopen the microphone, staying muted")
                micMuted = true
            }
            save(session)
        }
        publish()
    }

    private func stopLocked(reason: EndReason) -> RecordingSession? {
        guard state != .idle, var session, let folder else { return nil }
        monitor?.cancel()
        monitor = nil
        routes.stop()
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
        micMuted = false
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

    /// Falls back to a ring nothing writes to, which the writer fills with silence. Muted, the mic is closed and
    /// not reopened, and that silent ring is returned.
    private func startMic() -> (ring: RingBuffer, format: ChannelWriter.SourceFormat, isLive: Bool) {
        micGeneration &+= 1
        let generation = micGeneration
        if micMuted {
            mic.stop()
            return (Self.silentRing(), Self.silentFormat, false)
        }
        mic.onConfigurationChange = { [weak self] _ in
            guard let self else { return }
            self.queue.async { self.micChanged(generation: generation) }
        }
        if (try? mic.start(deviceUID: micDeviceUID)) != nil, let source = mic.currentSource {
            return (source.ring, ChannelWriter.SourceFormat(source.format), true)
        }
        mic.stop()
        return (Self.silentRing(), Self.silentFormat, false)
    }

    private static func silentRing() -> RingBuffer {
        RingBuffer(capacityFrames: 1_024, sampleRate: silentFormat.sampleRate)
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
        guard state == .recording, !micMuted, generation == micGeneration, let writer,
              let source = mic.currentSource else { return }
        guard (try? writer.replaceRing(channel: .mic, ring: source.ring, format: ChannelWriter.SourceFormat(source.format))) != nil else { return }
        shared.withLock { $0.micRing = source.ring }
    }

    // MARK: - Routes

    private func startRouteMonitor() {
        let generation = sessionGeneration
        routes.start(inputDeviceUID: micDeviceUID) { [weak self] event in
            guard let self else { return }
            self.queue.async { self.routeChanged(event, generation: generation) }
        }
    }

    private func routeChanged(_ event: AudioRouteMonitor.Event, generation: UInt64) {
        guard generation == sessionGeneration, var session else { return }
        switch event {
        case .outputChanged:
            session.outputDeviceName = Self.defaultDeviceName(kAudioHardwarePropertyDefaultOutputDevice)
            save(session)
            rebuildTap(generation: tapGeneration)
        case .inputLost, .inputChanged:
            // Muted, no device is open: unmuting opens the current one.
            guard !micMuted else { return }
            if event == .inputLost {
                micRoutesLost += 1
                publish()
            }
            // While paused the mic is closed; resume opens the new device.
            guard state == .recording, let writer else { return }
            swapMic(&session, writer: writer)
            save(session)
        }
    }

    /// Swaps a fresh `startMic()` source (silence while muted) into the writer and the meter while recording. Returns
    /// whether a live mic now feeds the writer.
    @discardableResult
    private func swapMic(_ session: inout RecordingSession, writer: ChannelWriter) -> Bool {
        // `startMic` has closed the previous device either way.
        let micSource = startMic()
        if (try? writer.replaceRing(channel: .mic, ring: micSource.ring, format: micSource.format)) != nil {
            shared.withLock { $0.micRing = micSource.ring }
            session.micDeviceName = micSource.isLive ? Self.inputDeviceName(uid: micDeviceUID) : nil
            return micSource.isLive
        } else {
            // Not left open on a ring nothing reads: the mic channel pads silence, as with no mic.
            mic.stop()
            session.micDeviceName = nil
            return false
        }
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
        snapshot.micRoutesLost = micRoutesLost
        snapshot.micMuted = micMuted
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

    /// A start that failed before the writer ran has no audio: no conversation is left behind. A crash before this
    /// point leaves `session.json` in `recording`, which launch recovery marks failed.
    private func discard(_ folder: URL) {
        try? FileManager.default.removeItem(at: folder)
    }

    private static func availableCapacity(at url: URL) -> Int64? {
        var url = url
        url.removeAllCachedResourceValues()
        return try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage
    }

    /// The picked device's name while it exists, else the default input's.
    private static func inputDeviceName(uid: String?) -> String? {
        if let uid, let device = MicCapture.deviceID(forUID: uid) { return deviceName(device) }
        return defaultDeviceName(kAudioHardwarePropertyDefaultInputDevice)
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
        return deviceName(device)
    }

    private static func deviceName(_ device: AudioDeviceID) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioObjectPropertyName,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain
        )
        var name: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
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
