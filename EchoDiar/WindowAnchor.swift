import AppKit
import ApplicationServices

/// Follows an app's focused window through Accessibility notifications (window moved, resized, focused window
/// changed, window destroyed), so the pill can sit at its top-right corner. Used only with the opt-in
/// `AppSettings.attachPillToWindow` and when EchoDiar is trusted for Accessibility; nothing polls.
/// Call `stop()` before releasing it.
@MainActor
final class WindowAnchor {
    static var isTrusted: Bool { AXIsProcessTrusted() }

    /// System Settings › Privacy & Security › Accessibility, for the Settings button.
    static func openAccessibilitySettings() {
        PrivacySettings.openAccessibility()
    }

    let pid: pid_t
    private let app: AXUIElement
    private let onChange: (CGRect?) -> Void
    private var observer: AXObserver?
    private var window: AXUIElement?

    private static let appNotifications = [
        kAXFocusedWindowChangedNotification, kAXWindowMovedNotification, kAXWindowResizedNotification,
    ]

    /// nil when not trusted or the app cannot be observed. `onChange` gets the window's frame in AppKit screen
    /// coordinates (nil without a window): once now, then on every change.
    init?(pid: pid_t, onChange: @escaping (CGRect?) -> Void) {
        guard Self.isTrusted else { return nil }
        self.pid = pid
        self.onChange = onChange
        app = AXUIElementCreateApplication(pid)
        // A hung app must not stall the main thread for the default 6 s.
        AXUIElementSetMessagingTimeout(app, 0.5)
        var created: AXObserver?
        guard AXObserverCreate(pid, Self.callback, &created) == .success, let created else { return nil }
        observer = created
        for name in Self.appNotifications {
            AXObserverAddNotification(created, app, name as CFString, refcon)
        }
        CFRunLoopAddSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(created), .defaultMode)
        trackFocusedWindow()
    }

    func stop() {
        guard let observer else { return }
        for name in Self.appNotifications {
            AXObserverRemoveNotification(observer, app, name as CFString)
        }
        if let window { AXObserverRemoveNotification(observer, window, kAXUIElementDestroyedNotification as CFString) }
        CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .defaultMode)
        self.observer = nil
        window = nil
    }

    private var refcon: UnsafeMutableRawPointer { Unmanaged.passUnretained(self).toOpaque() }

    /// Runs on the main run loop, where the source was added.
    private static let callback: AXObserverCallback = { _, _, notification, refcon in
        guard let refcon else { return }
        let anchor = Unmanaged<WindowAnchor>.fromOpaque(refcon).takeUnretainedValue()
        let name = notification as String
        MainActor.assumeIsolated { anchor.handle(name) }
    }

    private func handle(_ name: String) {
        guard observer != nil else { return }
        if name == kAXFocusedWindowChangedNotification || name == kAXUIElementDestroyedNotification {
            trackFocusedWindow()
        } else {
            onChange(window.flatMap(Self.frame(of:)))
        }
    }

    /// Moves the destroyed-window notification to the app's focused window (else its main or first window).
    private func trackFocusedWindow() {
        guard let observer else { return }
        let destroyed = kAXUIElementDestroyedNotification as CFString
        if let window { AXObserverRemoveNotification(observer, window, destroyed) }
        window = Self.element(app, kAXFocusedWindowAttribute) ?? Self.element(app, kAXMainWindowAttribute)
            ?? Self.firstWindow(of: app)
        if let window {
            // Copied elements keep the default 6 s timeout; the frame reads go through this one.
            AXUIElementSetMessagingTimeout(window, 0.5)
            AXObserverAddNotification(observer, window, destroyed, refcon)
        }
        onChange(window.flatMap(Self.frame(of:)))
    }

    private static func element(_ element: AXUIElement, _ attribute: String) -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success, let value,
              CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        return (value as! AXUIElement)
    }

    private static func firstWindow(of app: AXUIElement) -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(app, kAXWindowsAttribute as CFString, &value) == .success else { return nil }
        return (value as? [AXUIElement])?.first
    }

    /// AX positions are top-left based on the primary display; AppKit's are bottom-left based.
    private static func frame(of window: AXUIElement) -> CGRect? {
        var position = CGPoint.zero
        var size = CGSize.zero
        guard value(window, kAXPositionAttribute, .cgPoint, &position),
              value(window, kAXSizeAttribute, .cgSize, &size),
              let primary = NSScreen.screens.first else { return nil }
        return CGRect(x: position.x, y: primary.frame.maxY - position.y - size.height, width: size.width, height: size.height)
    }

    private static func value<T: BitwiseCopyable>(_ element: AXUIElement, _ attribute: String, _ type: AXValueType, _ result: inout T) -> Bool {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success, let value,
              CFGetTypeID(value) == AXValueGetTypeID() else { return false }
        return AXValueGetValue(value as! AXValue, type, &result)
    }
}
