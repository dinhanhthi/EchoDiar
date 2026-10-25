import AppKit

/// Places the main window at its saved frame. The frame is saved after the user moves or resizes the window, never in
/// full screen or below the minimum size.
@MainActor
final class MainWindowSizer {
    private static let minSize = NSSize(width: 860, height: 560)
    /// When no frame was saved yet.
    private static let defaultSize = NSSize(width: 1080, height: 720)
    /// The key of the wide layout from before the window had one size, so the user's frame stays.
    private static let defaultsKey = "MainWindowFrame.wide"

    /// The window's first frame: the saved one, else the default size centered.
    func place(_ window: NSWindow) {
        let frame: NSRect
        if let saved = Self.savedFrame() {
            let screen = NSScreen.screens.first { $0.visibleFrame.intersects(saved) } ?? NSScreen.main
            frame = Self.clamp(saved, in: screen?.visibleFrame)
        } else {
            let visible = NSScreen.main?.visibleFrame ?? .zero
            let size = Self.defaultSize
            let centered = NSRect(x: visible.midX - size.width / 2, y: visible.midY - size.height / 2,
                                  width: size.width, height: size.height)
            frame = Self.clamp(centered, in: NSScreen.main?.visibleFrame)
        }
        window.contentMinSize = Self.minSize
        window.setFrame(frame, display: false)
    }

    /// After the user moved or resized the window.
    func save(_ window: NSWindow) {
        guard !window.styleMask.contains(.fullScreen), Self.fits(window.frame.size) else { return }
        UserDefaults.standard.set(NSStringFromRect(window.frame), forKey: Self.defaultsKey)
    }

    /// Whether a window of `size` is at least the minimum size; a frame that is not is never saved or restored.
    private static func fits(_ size: NSSize) -> Bool {
        size.width >= minSize.width && size.height >= minSize.height
    }

    private static func savedFrame() -> NSRect? {
        guard let string = UserDefaults.standard.string(forKey: defaultsKey) else { return nil }
        let frame = NSRectFromString(string)
        // A frame below the minimum size falls back to the default size.
        return fits(frame.size) ? frame : nil
    }

    /// At least the minimum size, then inside the screen's visible frame.
    private static func clamp(_ frame: NSRect, in visible: NSRect?) -> NSRect {
        var frame = frame
        frame.size.width = max(frame.width, minSize.width)
        frame.size.height = max(frame.height, minSize.height)
        guard let visible, visible.width > 0, visible.height > 0 else { return frame }
        frame.size.width = min(frame.width, visible.width)
        frame.size.height = min(frame.height, visible.height)
        frame.origin.x = min(max(frame.minX, visible.minX), visible.maxX - frame.width)
        frame.origin.y = min(max(frame.minY, visible.minY), visible.maxY - frame.height)
        return frame
    }
}
