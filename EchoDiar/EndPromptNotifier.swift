import AppKit
import UserNotifications

/// The OS notification beside the pill's "meeting ended" prompt, while EchoDiar is in the background: "Stop now"
/// and "Keep recording" act like the pill's buttons, a click on the banner opens the recording page.
@MainActor
final class EndPromptNotifier: NSObject, UNUserNotificationCenterDelegate {
    private nonisolated static let category = "endPrompt"
    private nonisolated static let identifier = "endPrompt"
    private nonisolated static let stopAction = "stopNow"
    private nonisolated static let keepAction = "keepRecording"

    weak var model: AppModel?

    /// At launch: the center's delegate (weak; the model owns the notifier) and the prompt's buttons.
    func install(model: AppModel) {
        self.model = model
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        let stop = UNNotificationAction(
            identifier: Self.stopAction, title: String(localized: "Stop now"), options: [.destructive])
        let keep = UNNotificationAction(identifier: Self.keepAction, title: String(localized: "Keep recording"))
        center.setNotificationCategories([
            UNNotificationCategory(identifier: Self.category, actions: [stop, keep], intentIdentifiers: []),
        ])
    }

    /// Posts (or replaces) the notification; nothing when the setting is off, notifications are not allowed or
    /// EchoDiar is the frontmost app.
    func show(question: String, deadline: Date) {
        guard let model, model.settings.endPromptNotification, !NSApp.isActive,
              [.authorized, .provisional].contains(model.notificationStatus) else { return }
        let content = UNMutableNotificationContent()
        content.title = question
        let left = max(0, Int(deadline.timeIntervalSinceNow.rounded(.up)))
        content.body = String(localized: "Stopping in \(left) s")
        content.categoryIdentifier = Self.category
        // No sound: a chime would land in the recording (speakers into the mic, or the global tap).
        let request = UNNotificationRequest(identifier: Self.identifier, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request) { error in
            if let error { NSLog("EchoDiar: could not post the end prompt notification: \(error)") }
        }
    }

    /// Removes the notification, shown or about to be.
    func dismiss() {
        let center = UNUserNotificationCenter.current()
        center.removePendingNotificationRequests(withIdentifiers: [Self.identifier])
        center.removeDeliveredNotifications(withIdentifiers: [Self.identifier])
    }

    nonisolated func userNotificationCenter(
        _ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse
    ) async {
        guard response.notification.request.identifier == Self.identifier else { return }
        await handle(response.actionIdentifier)
    }

    private func handle(_ action: String) {
        guard let model else { return }
        switch action {
        case Self.keepAction:
            model.keepRecording()
        case Self.stopAction:
            // A notification left over from an earlier prompt never stops the recording.
            guard case .meetingEnded? = model.pillPrompt else { return }
            model.stopFromEndPrompt()
        case UNNotificationDefaultActionIdentifier:
            if model.isRecording { model.route = .recording }
            (NSApp.delegate as? AppDelegate)?.showMainWindow()
        default:
            break
        }
    }
}
