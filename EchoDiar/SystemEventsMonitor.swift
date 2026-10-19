import AppKit

/// Sleep/wake, screen lock and app quit notifications, delivered synchronously on the main actor (a `willSleep`
/// handler has little time before the system sleeps). Notification-driven; nothing polls.
@MainActor
final class SystemEventsMonitor {
    enum Event: Equatable {
        case willSleep, didWake, screenLocked, screenUnlocked
        /// A LaunchServices app quit (not plain processes such as `afplay`).
        case appTerminated(pid: pid_t, bundleID: String?)
    }

    private var observers: [(center: NotificationCenter, token: any NSObjectProtocol)] = []

    /// Replaces any previous handler.
    func start(onEvent: @escaping @MainActor (Event) -> Void) {
        stop()
        let workspace = NSWorkspace.shared.notificationCenter
        let distributed = DistributedNotificationCenter.default()
        observe(workspace, NSWorkspace.willSleepNotification) { _ in onEvent(.willSleep) }
        observe(workspace, NSWorkspace.didWakeNotification) { _ in onEvent(.didWake) }
        observe(distributed, Notification.Name("com.apple.screenIsLocked")) { _ in onEvent(.screenLocked) }
        observe(distributed, Notification.Name("com.apple.screenIsUnlocked")) { _ in onEvent(.screenUnlocked) }
        observe(workspace, NSWorkspace.didTerminateApplicationNotification) { app in
            guard let app else { return }
            onEvent(.appTerminated(pid: app.pid, bundleID: app.bundleID))
        }
    }

    func stop() {
        for observer in observers { observer.center.removeObserver(observer.token) }
        observers = []
    }

    /// `handler` gets the notification's app (pid, bundle id), if any.
    private func observe(
        _ center: NotificationCenter, _ name: Notification.Name,
        handler: @escaping @MainActor ((pid: pid_t, bundleID: String?)?) -> Void
    ) {
        let token = center.addObserver(forName: name, object: nil, queue: .main) { note in
            let app = (note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)
                .map { (pid: $0.processIdentifier, bundleID: $0.bundleIdentifier) }
            MainActor.assumeIsolated { handler(app) }
        }
        observers.append((center, token))
    }
}
