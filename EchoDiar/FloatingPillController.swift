import AppKit
import EchoCore
import SwiftUI

/// The floating pill: a non-activating panel above other apps and full-screen spaces, kept out of screen sharing.
/// Shows while recording (unless "Show floating indicator" is off, or an EchoDiar window is key and
/// `hidePillWhenAppActive` is on) and for every prompt, which must stay answerable. Its top-right corner stays put
/// while its content grows or shrinks. Dragged positions are saved per display (default: the top-right edge); with
/// "Attach to meeting window" and Accessibility it follows the meeting window instead, where it was dragged relative
/// to the window's top-right corner (default: just inside it).
@MainActor
final class FloatingPillController {
    /// Gap between the pill and the screen edge or the meeting window's corner.
    private static let inset: CGFloat = 8

    private let model: AppModel
    private let panel: NSPanel
    private let hover = PillHover()
    /// Recreated on each show, so its timers and meter run only while the pill is visible.
    private var hosting: PillHostingView?
    private var contentSize = CGSize(width: 1, height: 1)
    private var anchor: WindowAnchor?
    /// The meeting window's frame while anchored and it has one.
    private var anchorFrame: CGRect?
    /// The pill's top-right corner from the meeting window's while anchored, set at once by a drag (its save is
    /// delayed); nil = `inset` inside it.
    private var windowOffset: PillPosition?
    private var placing = false
    /// The next `place` starts from the saved spot; otherwise it keeps the current top-right corner, so a drag whose
    /// save is still pending is not undone.
    private var restoreSaved = true
    private var saveTask: Task<Void, Never>?
    private var observers: [any NSObjectProtocol] = []

    init(model: AppModel) {
        self.model = model
        panel = PillPanel(
            contentRect: NSRect(x: 0, y: 0, width: 1, height: 1),
            styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: true)
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        // Kept out of screenshots and screen sharing (to verify per sharing app, V6).
        panel.sharingType = .none
        panel.isOpaque = false
        panel.backgroundColor = .clear
        // The window server's shadow traces the clear panel's alpha, a jagged dark rim around the rounded pill; the
        // glass draws its own edge.
        panel.hasShadow = false
        panel.isReleasedWhenClosed = false
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: NSWindow.didMoveNotification, object: panel, queue: .main) {
            [weak self] _ in
            MainActor.assumeIsolated { self?.panelMoved() }
        })
        // An EchoDiar window becoming or leaving key (`hidePillWhenAppActive`).
        for name in [NSWindow.didBecomeKeyNotification, NSWindow.didResignKeyNotification] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self.map { $0.apply($0.wanted()) } }
            })
        }
        observers.append(center.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.place() }
        })
        // The anchored app quitting changes nothing observed, so the anchor is stopped here.
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didTerminateApplicationNotification, object: nil, queue: .main
        ) { [weak self] notification in
            let pid = (notification.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication)?
                .processIdentifier
            MainActor.assumeIsolated {
                guard let self, pid == self.anchor?.pid else { return }
                self.syncAnchor(bundleID: nil)
            }
        })
        observeModel()
    }

    /// Re-armed after each change; nothing polls. Only the decision is tracked: showing lays the view out, whose
    /// reads (the level meter) must not re-run this.
    private func observeModel() {
        let wanted = withObservationTracking {
            wanted()
        } onChange: { [weak self] in
            Task { @MainActor in self?.observeModel() }
        }
        apply(wanted)
    }

    /// Whether the pill shows, and the app whose window it follows.
    private func wanted() -> (show: Bool, anchorBundleID: String?) {
        let settings = model.settings
        let ownWindowKey = NSApp.isActive && NSApp.keyWindow.map { $0 !== panel } == true
        // An automatic start shows for its first 10 s even with the indicator off.
        let showPill = settings.showPill || model.autoStartedAppName != nil
        let indicator = model.isRecording && showPill && !(settings.hidePillWhenAppActive && ownWindowKey)
        let show = model.pillPrompt != nil || indicator
        return (show, show && settings.attachPillToWindow ? model.pillAppBundleID : nil)
    }

    private func apply(_ wanted: (show: Bool, anchorBundleID: String?)) {
        if wanted.show {
            show()
            syncAnchor(bundleID: wanted.anchorBundleID)
        } else {
            hide()
        }
    }

    private func show() {
        guard hosting == nil else { return }
        let view = PillHostingView(rootView: FloatingPillView(model: model, hover: hover) { [weak self] size in
            self?.contentSizeChanged(size)
        })
        view.sizingOptions = []
        view.onHover = { [hover] in hover.isHovering = $0 }
        hosting = view
        panel.contentView = view
        contentSize = view.fittingSize
        restoreSaved = true
        place()
        panel.orderFrontRegardless()
    }

    private func hide() {
        guard hosting != nil else { return }
        syncAnchor(bundleID: nil)
        panel.orderOut(nil)
        panel.contentView = nil
        hosting = nil
        hover.isHovering = false
    }

    private func contentSizeChanged(_ size: CGSize) {
        guard size != contentSize, size.width > 0, size.height > 0 else { return }
        contentSize = size
        place()
    }

    // MARK: - Placement

    /// The top-right corner at its offset from the anchored window's top-right, else where it is, else (when shown) at
    /// the saved spot of the pill's display (default: its top-right edge); clamped into that display's visible frame.
    private func place() {
        guard hosting != nil, let screen = anchorFrame.flatMap(Self.screen(containing:)) ?? currentScreen else { return }
        let visible = screen.visibleFrame
        let corner: CGPoint
        if let window = anchorFrame {
            let offset = windowOffset ?? PillPosition(x: -Self.inset, y: -Self.inset)
            corner = CGPoint(x: window.maxX + offset.x, y: window.maxY + offset.y)
        } else if !restoreSaved {
            corner = CGPoint(x: panel.frame.maxX, y: panel.frame.maxY)
        } else if let key = Self.displayKey(screen), let saved = model.settings.pillPositions[key] {
            corner = CGPoint(x: visible.maxX + saved.x, y: visible.maxY + saved.y)
        } else {
            corner = CGPoint(x: visible.maxX - Self.inset, y: visible.maxY - Self.inset)
        }
        restoreSaved = false
        let size = contentSize
        let x = min(max(corner.x - size.width, visible.minX), visible.maxX - size.width)
        let y = min(max(corner.y - size.height, visible.minY), visible.maxY - size.height)
        placing = true
        panel.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
        placing = false
        panel.invalidateShadow()
    }

    /// The display the pill is on, else the one with the key window.
    private var currentScreen: NSScreen? {
        Self.screen(containing: panel.frame) ?? NSScreen.main ?? NSScreen.screens.first
    }

    /// A drag by the user (not `place`), saved 0.5 s after the last move; anchored pills keep it relative to the window.
    private func panelMoved() {
        guard !placing else { return }
        if let window = anchorFrame {
            windowOffset = PillPosition(x: panel.frame.maxX - window.maxX, y: panel.frame.maxY - window.maxY)
        }
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled else { return }
            self?.savePosition()
        }
    }

    private func savePosition() {
        if anchorFrame != nil {
            return model.updateSettings { [windowOffset] in $0.pillWindowOffset = windowOffset }
        }
        guard let screen = currentScreen, let key = Self.displayKey(screen) else { return }
        let visible = screen.visibleFrame
        let frame = panel.frame
        let position = PillPosition(x: frame.maxX - visible.maxX, y: frame.maxY - visible.maxY)
        model.updateSettings { $0.pillPositions[key] = position }
    }

    private static func screen(containing rect: CGRect) -> NSScreen? {
        NSScreen.screens.max { $0.frame.intersection(rect).area < $1.frame.intersection(rect).area }
            .flatMap { $0.frame.intersects(rect) ? $0 : nil }
    }

    /// The display's UUID, stable across reboots and arrangement changes.
    private static func displayKey(_ screen: NSScreen) -> String? {
        guard let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber,
              let uuid = CGDisplayCreateUUIDFromDisplayID(number.uint32Value)?.takeRetainedValue() else { return nil }
        return CFUUIDCreateString(nil, uuid) as String
    }

    // MARK: - Window anchor

    /// Follows `bundleID`'s window while trusted for Accessibility; when it stops, the pill returns to the saved spot.
    private func syncAnchor(bundleID: String?) {
        let pid = WindowAnchor.isTrusted
            ? bundleID.flatMap { NSRunningApplication.runningApplications(withBundleIdentifier: $0).first }?
                .processIdentifier
            : nil
        guard pid != anchor?.pid else { return }
        let wasAnchored = anchor != nil
        anchor?.stop()
        anchor = nil
        anchorFrame = nil
        if let pid {
            windowOffset = model.settings.pillWindowOffset
            // Reports the window's frame at once, which places the pill.
            anchor = WindowAnchor(pid: pid) { [weak self] frame in
                self?.anchorFrame = frame
                self?.place()
            }
        }
        if wasAnchored, anchor == nil {
            restoreSaved = true
            place()
        }
    }
}

/// Borderless panels cannot become key by default; with `becomesKeyOnlyIfNeeded` the pill still never takes focus
/// from the meeting app for a button click.
private final class PillPanel: NSPanel {
    override var canBecomeKey: Bool { true }
}

/// Reports hover with a tracking area that works while EchoDiar is inactive (the usual case: the user is in the
/// call), and takes the first click, so Pause or Stop act at once.
final class PillHostingView: NSHostingView<FloatingPillView> {
    var onHover: ((Bool) -> Void)?
    private var hoverArea: NSTrackingArea?

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    /// `.inVisibleRect` follows resizes, so one area is enough; re-added if it was cleared.
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let hoverArea, trackingAreas.contains(where: { $0 === hoverArea }) { return }
        let area = NSTrackingArea(
            rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self)
        addTrackingArea(area)
        hoverArea = area
    }

    override func mouseEntered(with event: NSEvent) {
        super.mouseEntered(with: event)
        onHover?(true)
    }

    override func mouseExited(with event: NSEvent) {
        super.mouseExited(with: event)
        onHover?(false)
    }
}

private extension CGRect {
    var area: CGFloat { isNull ? 0 : width * height }
}
