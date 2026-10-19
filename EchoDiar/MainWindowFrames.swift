import AppKit

/// The main window's two sizes: compact for the list, wide for a conversation, the recording and Settings. Each
/// remembers its own frame.
enum MainWindowLayout: Equatable {
    case compact, wide

    init(_ route: AppModel.Route) {
        self = route == .list ? .compact : .wide
    }

    var minSize: NSSize {
        switch self {
        case .compact: NSSize(width: 440, height: 480)
        case .wide: NSSize(width: 860, height: 560)
        }
    }

    var maxSize: NSSize {
        switch self {
        case .compact: NSSize(width: 640, height: 1200)
        case .wide: NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        }
    }

    /// When no frame was saved yet.
    var defaultSize: NSSize {
        switch self {
        case .compact: NSSize(width: 480, height: 640)
        case .wide: NSSize(width: 1080, height: 720)
        }
    }

    /// Whether a window of `size` is within the layout's bounds; a frame that is not is never saved or restored.
    func fits(_ size: NSSize) -> Bool {
        size.width >= minSize.width && size.width <= maxSize.width
            && size.height >= minSize.height && size.height <= maxSize.height
    }

    var defaultsKey: String {
        switch self {
        case .compact: "MainWindowFrame.compact"
        case .wide: "MainWindowFrame.wide"
        }
    }
}

/// Places the main window at its layout's saved frame and animates it between layouts, keeping its top-left corner.
/// Frames are saved after the user moves or resizes the window and after each switch, never while animating, in full
/// screen or outside the layout's bounds; a layout change in full screen waits for its exit.
@MainActor
final class MainWindowSizer {
    private(set) var layout = MainWindowLayout.compact
    private var animating = false
    /// Bumped by each switch, so an interrupted animation's completion does not apply its bounds.
    private var generation = 0
    /// The layout to apply once full screen ends.
    private var pending: MainWindowLayout?

    /// The window's first frame, without animation: the layout's saved frame, else its default size centered.
    func place(_ window: NSWindow, layout: MainWindowLayout) {
        self.layout = layout
        pending = nil
        animating = false
        let saved = Self.savedFrame(layout) ?? (layout == .compact ? Self.seedCompactFrame() : nil)
        let frame: NSRect
        if let saved {
            let screen = NSScreen.screens.first { $0.visibleFrame.intersects(saved) } ?? NSScreen.main
            frame = Self.clamp(saved, to: layout, in: screen?.visibleFrame)
        } else {
            let visible = NSScreen.main?.visibleFrame ?? .zero
            let size = layout.defaultSize
            let centered = NSRect(x: visible.midX - size.width / 2, y: visible.midY - size.height / 2,
                                  width: size.width, height: size.height)
            frame = Self.clamp(centered, to: layout, in: NSScreen.main?.visibleFrame)
        }
        apply(layout, to: window)
        window.setFrame(frame, display: false)
    }

    /// Animates the window to `layout`'s saved size (or default), keeping its top-left corner on screen.
    func switchTo(_ layout: MainWindowLayout, window: NSWindow) {
        if window.styleMask.contains(.fullScreen) {
            pending = layout == self.layout ? nil : layout
            return
        }
        guard layout != self.layout else { return }
        let from = self.layout
        self.layout = layout
        pending = nil
        let old = window.frame
        let size = Self.savedFrame(layout)?.size ?? layout.defaultSize
        let target = Self.clamp(
            NSRect(x: old.minX, y: old.maxY - size.height, width: size.width, height: size.height),
            to: layout, in: window.screen?.visibleFrame)
        generation += 1
        let current = generation
        guard !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion else {
            animating = false
            apply(layout, to: window)
            window.setFrame(target, display: true)
            save(window)
            return
        }
        // The loosest bounds of both layouts, so the frame does not snap against either while it moves.
        window.contentMinSize = NSSize(width: min(from.minSize.width, layout.minSize.width),
                                       height: min(from.minSize.height, layout.minSize.height))
        window.contentMaxSize = NSSize(width: max(from.maxSize.width, layout.maxSize.width),
                                       height: max(from.maxSize.height, layout.maxSize.height))
        animating = true
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.25
            window.animator().setFrame(target, display: true)
        } completionHandler: { [weak self, weak window] in
            MainActor.assumeIsolated {
                guard let self, current == self.generation else { return }
                self.animating = false
                guard let window else { return }
                // The animation may not have moved the window (ordered out, or a full-screen transition).
                if window.frame.size != target.size, !window.styleMask.contains(.fullScreen) {
                    window.setFrame(target, display: true)
                }
                self.apply(layout, to: window)
                self.save(window)
            }
        }
    }

    /// After the user moved or resized the window, and after a switch.
    func save(_ window: NSWindow) {
        guard !animating, !window.styleMask.contains(.fullScreen), layout.fits(window.frame.size) else { return }
        UserDefaults.standard.set(NSStringFromRect(window.frame), forKey: layout.defaultsKey)
    }

    /// Applies a layout change made in full screen.
    func exitedFullScreen(_ window: NSWindow) {
        guard let pending else { return }
        self.pending = nil
        switchTo(pending, window: window)
    }

    private func apply(_ layout: MainWindowLayout, to window: NSWindow) {
        window.contentMinSize = layout.minSize
        window.contentMaxSize = layout.maxSize
    }

    private static func savedFrame(_ layout: MainWindowLayout) -> NSRect? {
        guard let string = UserDefaults.standard.string(forKey: layout.defaultsKey) else { return nil }
        let frame = NSRectFromString(string)
        // A frame outside the bounds (saved under the wrong layout) falls back to the default size.
        return layout.fits(frame.size) ? frame : nil
    }

    /// The frame of the window before the two layouts ("x y w h …" of the old "Main" autosave), saved once as the
    /// compact frame so the user's size stays.
    private static func seedCompactFrame() -> NSRect? {
        guard let string = UserDefaults.standard.string(forKey: "NSWindow Frame Main") else { return nil }
        let numbers = string.split(separator: " ").compactMap { Double($0) }
        guard numbers.count >= 4, numbers[2] > 0, numbers[3] > 0 else { return nil }
        let frame = clamp(NSRect(x: numbers[0], y: numbers[1], width: numbers[2], height: numbers[3]),
                          to: .compact, in: nil)
        UserDefaults.standard.set(NSStringFromRect(frame), forKey: MainWindowLayout.compact.defaultsKey)
        return frame
    }

    /// Within the layout's bounds, then inside the screen's visible frame.
    private static func clamp(_ frame: NSRect, to layout: MainWindowLayout, in visible: NSRect?) -> NSRect {
        var frame = frame
        frame.size.width = min(max(frame.width, layout.minSize.width), layout.maxSize.width)
        frame.size.height = min(max(frame.height, layout.minSize.height), layout.maxSize.height)
        guard let visible, visible.width > 0, visible.height > 0 else { return frame }
        frame.size.width = min(frame.width, visible.width)
        frame.size.height = min(frame.height, visible.height)
        frame.origin.x = min(max(frame.minX, visible.minX), visible.maxX - frame.width)
        frame.origin.y = min(max(frame.minY, visible.minY), visible.maxY - frame.height)
        return frame
    }
}
