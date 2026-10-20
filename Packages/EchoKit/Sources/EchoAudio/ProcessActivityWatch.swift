import CoreAudio
import Foundation

/// Whether any process of a group runs audio input or output, from `IsRunningInput`/`IsRunningOutput` listeners on
/// their process objects, with a 2 s poll as the fallback because those notifications proved unreliable (often missing
/// on macOS 27, in a VM). Feeds the end monitor's "source inactive" trigger while recording an app.
///
/// `@unchecked Sendable`: state is owned by a private serial queue (the poll fires on it too); `stop` hops onto it.
public final class ProcessActivityWatch: @unchecked Sendable {
    private static let selectors = [kAudioProcessPropertyIsRunningInput, kAudioProcessPropertyIsRunningOutput]

    private let objectIDs: [AudioObjectID]
    private let handler: @Sendable (Bool) -> Void
    private let queue = DispatchQueue(label: "EchoDiar.ProcessActivityWatch", qos: .utility)
    private let queueKey = DispatchSpecificKey<Void>()
    /// Listener blocks run here and only hop onto `queue`, so removing them inside `queue.sync` cannot deadlock.
    private let listenerQueue = DispatchQueue(label: "EchoDiar.ProcessActivityWatch.listener", qos: .utility)
    private var listener: AudioObjectPropertyListenerBlock?
    private var poll: DispatchSourceTimer?
    private var active: Bool?

    /// `handler` runs on a private queue: once with the current state, then on every change.
    public init(group: AudioProcessGroup, handler: @escaping @Sendable (Bool) -> Void) {
        objectIDs = group.objectIDs
        self.handler = handler
        queue.setSpecific(key: queueKey, value: ())
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            guard let self else { return }
            self.queue.async { self.update() }
        }
        self.listener = listener
        for id in objectIDs {
            for selector in Self.selectors {
                var address = Self.address(selector)
                _ = AudioObjectAddPropertyListenerBlock(id, &address, listenerQueue, listener)
            }
        }
        let poll = DispatchSource.makeTimerSource(queue: queue)
        poll.schedule(deadline: .now() + 2, repeating: 2, leeway: .milliseconds(500))
        poll.setEventHandler { [weak self] in self?.update() }
        poll.resume()
        self.poll = poll
        queue.async { [weak self] in self?.update() }
    }

    deinit {
        stop()
    }

    /// Removes the listeners and the poll; the handler is not called afterwards. Safe to call repeatedly.
    public func stop() {
        onQueue {
            guard let listener else { return }
            for id in objectIDs {
                // Fails harmlessly when the process object is already gone.
                for selector in Self.selectors {
                    var address = Self.address(selector)
                    _ = AudioObjectRemovePropertyListenerBlock(id, &address, listenerQueue, listener)
                }
            }
            self.listener = nil
            poll?.cancel()
            poll = nil
        }
    }

    private func onQueue(_ body: () -> Void) {
        if DispatchQueue.getSpecific(key: queueKey) != nil { return body() }
        queue.sync(execute: body)
    }

    /// Fresh flags; a process whose object is gone counts as inactive.
    private func update() {
        guard listener != nil else { return }
        let running = objectIDs.contains { id in
            guard let process = AudioProcessList.process(for: id) else { return false }
            return process.isRunningInput || process.isRunningOutput
        }
        guard running != active else { return }
        active = running
        handler(running)
    }

    private static func address(_ selector: AudioObjectPropertySelector) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    }
}
