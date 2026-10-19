import CoreAudio
import EchoCore
import Foundation

/// A known meeting app whose mic input has been running for at least 3 s.
public struct DetectedMeeting: Sendable, Hashable {
    public let app: MeetingApp
    /// The app's audio processes when it was detected; `CaptureTarget.app(group)` records them.
    public let group: AudioProcessGroup

    public var name: String { app.name }
    /// The matched id (one of `app.bundleIDs`).
    public var bundleID: String { group.bundleID }
    public var pids: [pid_t] { group.processes.map(\.pid) }
}

/// Watches known meeting apps' mic use through Core Audio property listeners: the process object list, and
/// `IsRunningInput`/`IsRunningOutput` of each known app's processes. Nothing polls; events within 500 ms are
/// coalesced into one refresh, and an app whose input turns on is confirmed by a one-shot 3 s timer.
///
/// `@unchecked Sendable`: state is owned by a private serial queue; `start`/`stop` hop onto it.
public final class MeetingDetector: @unchecked Sendable {
    public enum Event: Sendable, Equatable {
        case detected(DetectedMeeting)
        /// The detected app's mic input stopped, or its processes are gone.
        case ended(DetectedMeeting)
    }

    /// Single consumer.
    public let events: AsyncStream<Event>

    private static let debounce = DispatchTimeInterval.milliseconds(500)
    private static let confirmDelay = DispatchTimeInterval.seconds(3)
    private static let processSelectors = [kAudioProcessPropertyIsRunningInput, kAudioProcessPropertyIsRunningOutput]

    private let apps: [MeetingApp]
    private let continuation: AsyncStream<Event>.Continuation
    private let queue = DispatchQueue(label: "EchoDiar.MeetingDetector", qos: .utility)
    private let queueKey = DispatchSpecificKey<Void>()
    private let listenerQueue = DispatchQueue(label: "EchoDiar.MeetingDetector.listener", qos: .utility)

    private var isRunning = false
    private var generation: UInt64 = 0
    private var listListener: AudioObjectPropertyListenerBlock?
    private var processListeners: [AudioObjectID: AudioObjectPropertyListenerBlock] = [:]
    private var listChanged = false
    private var pendingRefresh: DispatchWorkItem?
    /// Known apps' process groups, by group bundle ID, with the flags of the last refresh.
    private var groups: [String: AudioProcessGroup] = [:]
    private var confirming: [String: DispatchWorkItem] = [:]
    private var detected: [String: DetectedMeeting] = [:]

    public init(apps: [MeetingApp] = MeetingApps.known) {
        self.apps = apps
        (events, continuation) = AsyncStream.makeStream(of: Event.self, bufferingPolicy: .bufferingNewest(16))
        queue.setSpecific(key: queueKey, value: ())
    }

    deinit {
        stop()
        continuation.finish()
    }

    /// Apps already using the mic are picked up (after the 3 s confirmation). Safe to call repeatedly.
    public func start() {
        onQueue { startLocked() }
    }

    /// Removes every listener and forgets detected apps without emitting `ended`. Safe to call repeatedly.
    public func stop() {
        onQueue { stopLocked() }
    }

    private func onQueue<T>(_ body: () throws -> T) rethrows -> T {
        if DispatchQueue.getSpecific(key: queueKey) != nil { return try body() }
        return try queue.sync(execute: body)
    }

    // MARK: - Queue-owned

    private func startLocked() {
        guard !isRunning else { return }
        isRunning = true
        generation &+= 1
        var address = Self.address(kAudioHardwarePropertyProcessObjectList)
        let listener = makeListener(listChange: true)
        if AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, listenerQueue, listener) == noErr {
            listListener = listener
        }
        listChanged = true
        refresh()
    }

    private func stopLocked() {
        guard isRunning else { return }
        isRunning = false
        generation &+= 1
        if let listener = listListener {
            var address = Self.address(kAudioHardwarePropertyProcessObjectList)
            _ = AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, listenerQueue, listener)
            listListener = nil
        }
        syncProcessListeners([])
        pendingRefresh?.cancel()
        pendingRefresh = nil
        confirming.values.forEach { $0.cancel() }
        confirming = [:]
        groups = [:]
        detected = [:]
    }

    /// Listener blocks run on `listenerQueue` and only hop onto `queue`.
    private func makeListener(listChange: Bool) -> AudioObjectPropertyListenerBlock {
        let generation = generation
        return { [weak self] _, _ in
            guard let self else { return }
            self.queue.async {
                guard self.isRunning, generation == self.generation else { return }
                if listChange { self.listChanged = true }
                self.scheduleRefresh()
            }
        }
    }

    private func scheduleRefresh() {
        guard pendingRefresh == nil else { return }
        let item = DispatchWorkItem { [weak self] in self?.refresh() }
        pendingRefresh = item
        queue.asyncAfter(deadline: .now() + Self.debounce, execute: item)
    }

    /// Regroups processes only when the list changed; otherwise re-reads the tracked processes' flags.
    private func refresh() {
        pendingRefresh = nil
        guard isRunning else { return }
        if listChanged {
            listChanged = false
            let known = AudioProcessList.groups().filter { app(for: $0.bundleID) != nil }
            groups = Dictionary(known.map { ($0.bundleID, $0) }, uniquingKeysWith: { first, _ in first })
            syncProcessListeners(Set(known.flatMap(\.objectIDs)))
        } else {
            groups = groups.mapValues(Self.reread)
        }
        evaluate()
    }

    private func evaluate() {
        for key in Set(groups.keys).union(confirming.keys).union(detected.keys) {
            if groups[key]?.isRunningInput == true {
                if detected[key] == nil, confirming[key] == nil { armConfirmation(key) }
            } else {
                confirming.removeValue(forKey: key)?.cancel()
                if let meeting = detected.removeValue(forKey: key) { continuation.yield(.ended(meeting)) }
            }
        }
    }

    /// One-shot: input still running 3 s later (fresh read) → detected.
    private func armConfirmation(_ key: String) {
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.confirming.removeValue(forKey: key) != nil,
                  let group = self.groups[key].map(Self.reread), let app = self.app(for: key) else { return }
            self.groups[key] = group
            guard group.isRunningInput else { return }
            let meeting = DetectedMeeting(app: app, group: group)
            self.detected[key] = meeting
            self.continuation.yield(.detected(meeting))
        }
        confirming[key] = item
        queue.asyncAfter(deadline: .now() + Self.confirmDelay, execute: item)
    }

    private func syncProcessListeners(_ ids: Set<AudioObjectID>) {
        for (id, listener) in processListeners where !ids.contains(id) {
            // Fails harmlessly when the process object is already gone.
            for selector in Self.processSelectors {
                var address = Self.address(selector)
                _ = AudioObjectRemovePropertyListenerBlock(id, &address, listenerQueue, listener)
            }
            processListeners[id] = nil
        }
        for id in ids where processListeners[id] == nil {
            let listener = makeListener(listChange: false)
            for selector in Self.processSelectors {
                var address = Self.address(selector)
                _ = AudioObjectAddPropertyListenerBlock(id, &address, listenerQueue, listener)
            }
            processListeners[id] = listener
        }
    }

    private func app(for bundleID: String) -> MeetingApp? {
        apps.first { $0.bundleIDs.contains(bundleID) }
    }

    /// Fresh flags; processes whose object is gone are dropped.
    private static func reread(_ group: AudioProcessGroup) -> AudioProcessGroup {
        var group = group
        group.processes = group.processes.compactMap { AudioProcessList.process(for: $0.objectID) }
        return group
    }

    private static func address(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    }
}
