import AppKit
import EchoCore
import EchoTranscribe
import SwiftUI

/// A transcript laid out for `TranscriptTextView`: its `TranscriptDocument` and the styled text made from it. Built
/// off the main thread when a conversation opens; `@unchecked Sendable` because `string` is an immutable
/// `NSAttributedString` that nothing changes after `init`.
struct TranscriptText: @unchecked Sendable {
    /// The transcript this text was built from, to build it again only when it changed.
    let transcript: Transcript
    let document: TranscriptDocument
    let string: NSAttributedString
    /// Per entry: its speaker's color, for the speaking indicator.
    let colors: [NSColor]
    /// Per entry: the width of its timestamp, to center the speaking indicator under it.
    let timeWidths: [CGFloat]
    /// Entry index of each segment id.
    let indexByID: [UUID: Int]

    /// Per segment: the timestamp (secondary, plays from the segment start), the speaker dot in the speaker's color
    /// and the name (semibold, also plays), then the text below them, indented past the timestamp like the old rows.
    nonisolated init(_ transcript: Transcript) {
        let style = TranscriptTextStyle.self
        let document = TranscriptDocument(segments: transcript.segments) {
            TranscriptView.displayName(for: $0.speaker, in: transcript)
        }
        let paragraph = NSMutableParagraphStyle()
        paragraph.firstLineHeadIndent = 0
        paragraph.headIndent = style.gutter
        paragraph.paragraphSpacing = style.paragraphSpacing
        let string = NSMutableAttributedString(string: document.text, attributes: [
            .font: style.bodyFont, .foregroundColor: NSColor.labelColor, .paragraphStyle: paragraph,
        ])
        let playHelp = String(localized: "Play from here")
        var speakerColors: [String: NSColor] = [:]
        var colors: [NSColor] = []
        var timeWidths: [CGFloat] = []
        var indexByID: [UUID: Int] = [:]
        colors.reserveCapacity(document.entries.count)
        timeWidths.reserveCapacity(document.entries.count)
        for (entry, segment) in zip(document.entries, transcript.segments) {
            let color = speakerColors[segment.speaker] ?? NSColor(TranscriptView.color(for: segment.speaker))
            speakerColors[segment.speaker] = color
            let link = URL(string: "\(style.linkScheme)://\(entry.index)") as Any
            // The header and its line separator in the caption font, so the header line stays caption-high.
            string.addAttribute(.font, value: style.captionFont, range: NSRange(
                location: entry.headerRange.location, length: entry.bodyRange.location - entry.headerRange.location))
            string.addAttributes([
                .font: style.timeFont, .foregroundColor: NSColor.secondaryLabelColor, .link: link, .toolTip: playHelp,
            ], range: entry.timeRange)
            // "  ● ": the first space widens so the dot starts where the text below starts.
            let timeWidth = style.width(entry.time, font: style.timeFont)
            let gapStart = NSMaxRange(entry.timeRange)
            let spaces = style.width("  ", font: style.captionFont)
            string.addAttribute(.kern, value: max(0, style.gutter - timeWidth - spaces),
                                range: NSRange(location: gapStart, length: 1))
            string.addAttribute(.foregroundColor, value: color, range: NSRange(location: gapStart + 2, length: 1))
            string.addAttributes([.font: style.nameFont, .link: link, .toolTip: playHelp], range: entry.nameRange)
            string.addAttribute(.transcriptSegment, value: entry.index, range: NSRange(
                location: entry.headerRange.location,
                length: NSMaxRange(entry.bodyRange) - entry.headerRange.location))
            colors.append(color)
            timeWidths.append(timeWidth)
            indexByID[entry.id] = entry.index
        }
        self.transcript = transcript
        self.document = document
        self.string = string
        self.colors = colors
        self.timeWidths = timeWidths
        self.indexByID = indexByID
    }
}

extension NSAttributedString.Key {
    /// The entry index of the segment a paragraph shows, read by its layout fragment.
    fileprivate static let transcriptSegment = NSAttributedString.Key("EchoDiar.transcriptSegment")
}

/// Fonts and spacing of the transcript text, close to the old segment rows: body text, caption headers, 14 pt from
/// the card's sides (4 outside the row fill, 10 inside it), 14 pt between segments.
private enum TranscriptTextStyle {
    static let linkScheme = "echodiar-seg"
    // Fonts are immutable; the text is built off the main thread.
    nonisolated(unsafe) static let bodyFont = NSFont.preferredFont(forTextStyle: .body)
    nonisolated(unsafe) static let captionFont = NSFont.preferredFont(forTextStyle: .caption1)
    nonisolated(unsafe) static let timeFont = NSFont.monospacedDigitSystemFont(
        ofSize: captionFont.pointSize, weight: .regular)
    nonisolated(unsafe) static let nameFont = NSFont.systemFont(ofSize: captionFont.pointSize, weight: .semibold)
    /// Where the text starts: past a "00:00" timestamp and the old rows' 10 pt column spacing.
    static let gutter = width("00:00", font: timeFont) + 10
    static let paragraphSpacing: CGFloat = 14
    static let inset = NSSize(width: 14, height: 16)
    /// How far the active segment's fill reaches past its text.
    static let fillPadding = NSSize(width: 10, height: 6)
    static let fillRadius: CGFloat = 10

    /// The typographic width of `text` in `font` (Core Text, safe off the main thread).
    static func width(_ text: String, font: NSFont) -> CGFloat {
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: [.font: font]))
        return CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil))
    }
}

/// The saved transcript as one read-only text, so a drag selects across segments and ⌘C copies "Name [mm:ss]: text"
/// lines. A click on a timestamp or a name plays from the segment start, as does "Play from here" in the context
/// menu. Redrawn when the play head enters another segment, not at every tick. While playing, the text follows the
/// play head: Play scrolls to its segment, and so does each next segment, until the user scrolls away (or jumps to a
/// search result); the next Play, or a click on a timestamp, follows again.
struct TranscriptTextView: View {
    let text: TranscriptText
    let playback: PlaybackController
    @Binding var scrollTarget: UUID?

    var body: some View {
        TranscriptTextRepresentable(
            text: text, active: playback.activeSegmentID, playing: playback.isPlaying, scrollTarget: $scrollTarget
        ) { playback.play(from: $0) }
    }
}

/// The TextKit 2 text view in its scroll view; `TranscriptTextCoordinator` does the work.
private struct TranscriptTextRepresentable: NSViewRepresentable {
    let text: TranscriptText
    let active: UUID?
    let playing: Bool
    @Binding var scrollTarget: UUID?
    let play: (Double) -> Void

    func makeCoordinator() -> TranscriptTextCoordinator { TranscriptTextCoordinator() }

    func makeNSView(context: Context) -> NSScrollView { context.coordinator.makeScrollView() }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        let target = $scrollTarget
        context.coordinator.update(
            text: text, active: active, playing: playing, scrollTarget: scrollTarget, play: play
        ) {
            // Not during the view update; nil again, so the same citation scrolls the next time too.
            Task { @MainActor in target.wrappedValue = nil }
        }
    }

    static func dismantleNSView(_ scrollView: NSScrollView, coordinator: TranscriptTextCoordinator) {
        coordinator.stop()
    }
}

/// Builds the text view, applies each update (new text, active segment, playing, citation) and handles link clicks,
/// the context menu and following the play head.
@MainActor
private final class TranscriptTextCoordinator: NSObject, NSTextViewDelegate {
    private var textView: TranscriptNSTextView?
    private var scrollView: NSScrollView?
    private var text: TranscriptText?
    private let highlight = SegmentHighlight()
    private var playing = false
    /// The text scrolls to each next segment while playing; off once the user scrolls.
    private var following = false
    private var play: (Double) -> Void = { _ in }
    /// The waveform under the active segment's timestamp while it plays.
    private let indicator = NSImageView()
    private var observers: [any NSObjectProtocol] = []
    private var lastWidth: CGFloat = 0
    private var indicatorPending = false

    func makeScrollView() -> NSScrollView {
        let textView = TranscriptNSTextView(usingTextLayoutManager: true)
        textView.isEditable = false
        textView.isSelectable = true
        textView.isRichText = true
        textView.allowsUndo = false
        textView.importsGraphics = false
        textView.usesFontPanel = false
        textView.drawsBackground = false
        textView.displaysLinkToolTips = false
        // The storage's colors stay; no underline.
        textView.linkTextAttributes = [.cursor: NSCursor.pointingHand]
        textView.textContainerInset = TranscriptTextStyle.inset
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        textView.textContainer?.widthTracksTextView = true
        textView.textContainer?.lineFragmentPadding = 0
        textView.textContainer?.size.height = .greatestFiniteMagnitude
        textView.delegate = self
        #if DEBUG
        if textView.textLayoutManager?.delegate != nil {
            NSLog("EchoDiar: the transcript text view's layout manager already had a delegate")
        }
        #endif
        textView.textLayoutManager?.delegate = highlight
        textView.onUserScroll = { [weak self] in self?.following = false }
        textView.postsFrameChangedNotifications = true

        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.drawsBackground = false
        scrollView.contentView.drawsBackground = false
        scrollView.contentView.postsBoundsChangedNotifications = true
        scrollView.documentView = textView

        indicator.isHidden = true
        indicator.setAccessibilityElement(false)
        textView.addSubview(indicator)

        let center = NotificationCenter.default
        // The scroller dragged; wheels and trackpads go through `onUserScroll`. Scrolls made here post neither.
        observers.append(center.addObserver(
            forName: NSScrollView.willStartLiveScrollNotification, object: scrollView, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.following = false }
        })
        // A new width wraps the text again: the indicator moves with its segment.
        observers.append(center.addObserver(
            forName: NSView.frameDidChangeNotification, object: textView, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.widthMayHaveChanged() }
        })
        // Any scroll: the indicator shows only while its segment is in the viewport.
        observers.append(center.addObserver(
            forName: NSView.boundsDidChangeNotification, object: scrollView.contentView, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.playing else { return }
                self.updateIndicatorLater()
            }
        })
        #if DEBUG
        observers.append(center.addObserver(
            forName: NSTextView.didSwitchToNSLayoutManagerNotification, object: textView, queue: .main
        ) { _ in
            NSLog("EchoDiar: the transcript text view fell back to TextKit 1 (something read its layoutManager)")
        })
        #endif
        self.textView = textView
        self.scrollView = scrollView
        return scrollView
    }

    func stop() {
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
    }

    /// Same rules as the old segment list: Play (or a click on a segment) follows the play head, the user's own
    /// scrolling and a citation stop following, and a citation scrolls its segment to the middle.
    func update(text: TranscriptText, active: UUID?, playing: Bool, scrollTarget: UUID?,
                play: @escaping (Double) -> Void, clearTarget: () -> Void) {
        self.play = play
        let textChanged = apply(text)
        let index = active.flatMap { text.indexByID[$0] }
        let activeChanged = index != highlight.index
        if activeChanged || textChanged { setActive(index) }
        let playingChanged = playing != self.playing
        self.playing = playing
        if playingChanged { following = playing }
        if activeChanged || playingChanged { follow() }
        updateIndicator()
        if let scrollTarget {
            following = false
            clearTarget()
            // After this update's layout, so the first citation of a just-opened transcript lands too.
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated {
                    guard let self, let index = self.text?.indexByID[scrollTarget] else { return }
                    self.scroll(toCenter: index, animated: false)
                }
            }
        }
    }

    // MARK: - Text

    /// Puts `text` into the storage; true when the shown text changed. A rename changes only the names; anything else
    /// (a version switch, a new transcription) replaces the text, keeping the scroll position.
    private func apply(_ new: TranscriptText) -> Bool {
        guard let textView, let storage = textView.textStorage else { return false }
        defer {
            text = new
            textView.document = new.document
        }
        guard let old = text else {
            storage.setAttributedString(new.string)
            return true
        }
        if old.document == new.document { return false }
        let origin = scrollView?.contentView.bounds.origin
        if Self.onlyNamesDiffer(old.document, new.document) {
            let selection = textView.selectedRange()
            storage.beginEditing()
            for (before, after) in zip(old.document.entries, new.document.entries).reversed()
            where before.name != after.name {
                storage.replaceCharacters(in: before.nameRange,
                                          with: new.string.attributedSubstring(from: after.nameRange))
            }
            storage.endEditing()
            let start = Self.map(selection.location, from: old.document, to: new.document)
            let end = Self.map(NSMaxRange(selection), from: old.document, to: new.document)
            textView.setSelectedRange(NSRange(location: start, length: max(0, end - start)))
        } else {
            storage.setAttributedString(new.string)
            textView.setSelectedRange(NSRange(location: 0, length: 0))
        }
        if let origin, let scrollView {
            scrollView.contentView.scroll(to: origin)
            scrollView.reflectScrolledClipView(scrollView.contentView)
        }
        return true
    }

    /// Same segments with the same times and texts: only speaker names changed.
    private static func onlyNamesDiffer(_ old: TranscriptDocument, _ new: TranscriptDocument) -> Bool {
        guard old.entries.count == new.entries.count else { return false }
        let oldText = old.text as NSString, newText = new.text as NSString
        return zip(old.entries, new.entries).allSatisfy { before, after in
            before.id == after.id && before.time == after.time
                && oldText.substring(with: before.bodyRange) == newText.substring(with: after.bodyRange)
        }
    }

    /// An offset in `old` moved to the same place in `new` when only names differ: before its entry's name it keeps
    /// its distance from the header start, after it its distance from the name end, inside it is clamped to the name.
    private static func map(_ offset: Int, from old: TranscriptDocument, to new: TranscriptDocument) -> Int {
        guard let before = old.segment(at: offset), before.index < new.entries.count else {
            return min(offset, (new.text as NSString).length)
        }
        let after = new.entries[before.index]
        if offset >= NSMaxRange(before.nameRange) {
            return NSMaxRange(after.nameRange) + offset - NSMaxRange(before.nameRange)
        }
        if offset >= before.nameRange.location {
            return min(NSMaxRange(after.nameRange), after.nameRange.location + offset - before.nameRange.location)
        }
        return after.headerRange.location + offset - before.headerRange.location
    }

    // MARK: - Active segment

    /// Moves the fill: only the old and the new segment's fragments are laid out again.
    private func setActive(_ index: Int?) {
        let old = highlight.index
        highlight.index = index
        guard let layout = textView?.textLayoutManager else { return }
        for changed in Set([old, index]).compactMap({ $0 }) {
            if let range = textRange(of: changed) { layout.invalidateLayout(for: range) }
        }
        layout.textViewportLayoutController.layoutViewport()
    }

    /// The waveform under the playing segment's timestamp, in its speaker's color; still under Reduce Motion. Hidden
    /// while the segment is out of the viewport, so the text before it is never laid out for it.
    private func updateIndicator() {
        guard let text, let index = highlight.index, playing, let frame = viewportFrame(of: index) else {
            indicator.isHidden = true
            indicator.removeAllSymbolEffects()
            return
        }
        if indicator.image == nil {
            indicator.image = NSImage(systemSymbolName: "waveform", accessibilityDescription: nil)?
                .withSymbolConfiguration(.init(pointSize: TranscriptTextStyle.captionFont.pointSize, weight: .regular))
        }
        indicator.contentTintColor = text.colors[index]
        let size = indicator.image?.size ?? .zero
        // Under the header line, beside the text's first line.
        let lines = frame.lines
        let lineMidY = lines.count > 1
            ? lines[1].typographicBounds.midY
            : (lines.first?.typographicBounds.maxY ?? 0) + size.height / 2
        indicator.frame = NSRect(
            x: frame.rect.minX + text.timeWidths[index] / 2 - size.width / 2,
            y: frame.rect.minY + lineMidY - size.height / 2,
            width: size.width, height: size.height)
        if indicator.isHidden {
            indicator.isHidden = false
            indicator.removeAllSymbolEffects()
            if !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
                indicator.addSymbolEffect(.variableColor.iterative, options: .repeating)
            }
        }
    }

    private func widthMayHaveChanged() {
        guard let width = textView?.frame.width, width != lastWidth else { return }
        lastWidth = width
        updateIndicatorLater()
    }

    /// After the text view has wrapped its text for the new width or scrolled, once per run loop turn (a live resize
    /// or a scroll changes the frame or the bounds at every step).
    private func updateIndicatorLater() {
        guard !indicatorPending else { return }
        indicatorPending = true
        DispatchQueue.main.async { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.indicatorPending = false
                self.updateIndicator()
            }
        }
    }

    // MARK: - Scrolling

    /// While following, scrolls the playing segment to the middle when it is not fully in view. Out of the viewport it
    /// is not in view, so only a segment that has to be scrolled to gets the exact layout.
    private func follow() {
        guard playing, following, let index = highlight.index, let visible = scrollView?.contentView.bounds
        else { return }
        if let frame = viewportFrame(of: index)?.rect, visible.contains(frame) { return }
        scroll(toCenter: index, animated: !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
    }

    /// Scrolls the segment to the middle of the view (its top when taller than the view).
    private func scroll(toCenter index: Int, animated: Bool) {
        guard let textView, let scrollView, let frame = exactFrame(of: index)?.rect else { return }
        // The layout up to the segment is exact now; let the view's height include it.
        textView.sizeToFit()
        let clip = scrollView.contentView
        let visible = clip.bounds
        var y = frame.height > visible.height
            ? frame.minY - TranscriptTextStyle.fillPadding.height
            : frame.midY - visible.height / 2
        y = max(0, min(y, textView.frame.height - visible.height))
        let origin = NSPoint(x: visible.minX, y: y)
        if animated {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.25
                context.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
                clip.animator().setBoundsOrigin(origin)
            } completionHandler: { [weak self] in
                MainActor.assumeIsolated { self?.updateIndicatorLater() }
            }
        } else {
            clip.scroll(to: origin)
        }
        scrollView.reflectScrolledClipView(clip)
    }

    // MARK: - Geometry

    /// The segment's text range, from its header start to its body end.
    private func textRange(of index: Int) -> NSTextRange? {
        guard let text, index < text.document.entries.count, let storage = textView?.textContentStorage else {
            return nil
        }
        let entry = text.document.entries[index]
        let start = storage.documentRange.location
        guard let from = storage.location(start, offsetBy: entry.headerRange.location),
              let to = storage.location(start, offsetBy: NSMaxRange(entry.bodyRange))
        else { return nil }
        return NSTextRange(location: from, end: to)
    }

    /// The segment's fragment frame in the text view and its lines (in the fragment's coordinates), laid out from the
    /// start of the text so the frame is exact, not estimated. Costly far into a long text: only to scroll to it.
    private func exactFrame(of index: Int) -> (rect: CGRect, lines: [NSTextLineFragment])? {
        guard let layout = textView?.textLayoutManager, let range = textRange(of: index),
              let upTo = NSTextRange(location: layout.documentRange.location, end: range.endLocation)
        else { return nil }
        layout.ensureLayout(for: upTo)
        return fragmentFrame(at: range.location)
    }

    /// As `exactFrame(of:)`, but only when the segment is in the viewport (the visible rect and the bit around it the
    /// text view lays out), nil otherwise. Lays out the viewport only, never the text before it; the viewport's
    /// fragments are where they are drawn, so the frame is exact too.
    private func viewportFrame(of index: Int) -> (rect: CGRect, lines: [NSTextLineFragment])? {
        guard let controller = textView?.textLayoutManager?.textViewportLayoutController,
              let range = textRange(of: index)
        else { return nil }
        controller.layoutViewport()
        guard let viewport = controller.viewportRange, viewport.intersects(range) else { return nil }
        return fragmentFrame(at: range.location)
    }

    /// The frame, in the text view, and the lines of the fragment at `location`, as laid out now.
    private func fragmentFrame(at location: any NSTextLocation) -> (rect: CGRect, lines: [NSTextLineFragment])? {
        guard let textView, let fragment = textView.textLayoutManager?.textLayoutFragment(for: location) else {
            return nil
        }
        let origin = textView.textContainerOrigin
        return (fragment.layoutFragmentFrame.offsetBy(dx: origin.x, dy: origin.y), fragment.textLineFragments)
    }

    // MARK: - NSTextViewDelegate

    /// A timestamp or a name: plays from the segment start and follows the play head again.
    func textView(_ textView: NSTextView, clickedOnLink link: Any, at charIndex: Int) -> Bool {
        let url = (link as? URL) ?? (link as? String).flatMap(URL.init(string:))
        guard let url, url.scheme == TranscriptTextStyle.linkScheme, let index = url.host.flatMap(Int.init),
              let text, index < text.document.entries.count
        else { return false }
        playFrom(text.document.entries[index].start)
        return true
    }

    /// The standard menu (Copy, Look Up…) with "Play from here" first, for the segment under the click.
    func textView(_ view: NSTextView, menu: NSMenu, for event: NSEvent, at charIndex: Int) -> NSMenu? {
        guard let entry = text?.document.segment(at: charIndex) else { return menu }
        // A copy: the proposed menu may be shared between clicks.
        let menu = menu.copy() as? NSMenu ?? menu
        let item = NSMenuItem(title: String(localized: "Play from here"), action: #selector(playFromMenu(_:)),
                              keyEquivalent: "")
        item.target = self
        item.representedObject = entry.start
        menu.insertItem(item, at: 0)
        menu.insertItem(.separator(), at: 1)
        return menu
    }

    @objc private func playFromMenu(_ sender: NSMenuItem) {
        guard let start = sender.representedObject as? Double else { return }
        playFrom(start)
    }

    /// As a click on the old rows: follow again, then play.
    private func playFrom(_ start: Double) {
        following = true
        play(start)
    }
}

/// The active segment's index, read by the fragments as they draw, and the layout manager's delegate that makes
/// those fragments. Touched on the main thread only (TextKit 2 lays out a text view's text there).
private final class SegmentHighlight: NSObject, NSTextLayoutManagerDelegate, @unchecked Sendable {
    var index: Int?

    func textLayoutManager(_ textLayoutManager: NSTextLayoutManager,
                           textLayoutFragmentFor location: any NSTextLocation,
                           in textElement: NSTextElement) -> NSTextLayoutFragment {
        let fragment = SegmentFragment(textElement: textElement, range: textElement.elementRange)
        fragment.highlight = self
        if let paragraph = (textElement as? NSTextParagraph)?.attributedString, paragraph.length > 0 {
            fragment.index = paragraph.attribute(.transcriptSegment, at: 0, effectiveRange: nil) as? Int
        }
        return fragment
    }
}

/// A segment's paragraph; the active one draws a rounded fill (as `Palette.pressedFill`) behind its text, reaching
/// past it like the old row's background.
private final class SegmentFragment: NSTextLayoutFragment {
    weak var highlight: SegmentHighlight?
    var index: Int?

    override var renderingSurfaceBounds: CGRect {
        guard let fill = fillRect else { return super.renderingSurfaceBounds }
        return super.renderingSurfaceBounds.union(fill)
    }

    override func draw(at point: CGPoint, in context: CGContext) {
        if let fill = fillRect {
            let radius = TranscriptTextStyle.fillRadius
            context.saveGState()
            context.addPath(CGPath(roundedRect: fill.offsetBy(dx: point.x, dy: point.y),
                                   cornerWidth: radius, cornerHeight: radius, transform: nil))
            context.setFillColor(NSColor.white.withAlphaComponent(0.2).cgColor)
            context.fillPath()
            context.restoreGState()
        }
        super.draw(at: point, in: context)
    }

    /// In the fragment's coordinates: the container's width plus the padding, the lines' height plus the padding.
    private var fillRect: CGRect? {
        guard let index, index == highlight?.index else { return nil }
        let lines = textLineFragments.reduce(CGRect.null) { $0.union($1.typographicBounds) }
        guard !lines.isNull else { return nil }
        let padding = TranscriptTextStyle.fillPadding
        let width = textLayoutManager?.textContainer?.size.width ?? layoutFragmentFrame.width
        return CGRect(x: -layoutFragmentFrame.minX - padding.width, y: lines.minY - padding.height,
                      width: width + 2 * padding.width, height: lines.height + 2 * padding.height)
    }
}

/// Copies the selection as "Name [mm:ss]: text" lines (⌘C, Edit › Copy, the context menu, drags) and tells the
/// coordinator about the user's own wheel and trackpad scrolling.
private final class TranscriptNSTextView: NSTextView {
    var document: TranscriptDocument?
    var onUserScroll: (() -> Void)?

    override var writablePasteboardTypes: [NSPasteboard.PasteboardType] { [.string] }

    override func writeSelection(to pboard: NSPasteboard, type: NSPasteboard.PasteboardType) -> Bool {
        guard type == .string, let document else { return super.writeSelection(to: pboard, type: type) }
        return pboard.setString(document.copyText(for: selectedRange()), forType: .string)
    }

    override func scrollWheel(with event: NSEvent) {
        onUserScroll?()
        super.scrollWheel(with: event)
    }
}

// MARK: - Live captions

/// The live captions as one read-only text, so a drag selects across lines and ⌘C copies them as plain text. Each
/// line is a paragraph "Name: text" (the name bold in the speaker's color), then the open chunks' tentative text in
/// the secondary color. Updates edit the text in place (new lines appended, dropped lines removed from the front, the
/// tentative tail replaced), so a selection survives the stream. The text follows its end until the user scrolls up;
/// scrolling back to the bottom follows again.
struct LiveCaptionsTextView: View {
    let lines: [RecordingTranscriber.Line]
    let tentative: [RecordingTranscriber.Tentative]
    let name: (RecordingTranscriber.Speaker) -> String
    let color: (RecordingTranscriber.Speaker) -> NSColor

    var body: some View {
        LiveCaptionsRepresentable(lines: lines, tentative: tentative, name: name, color: color)
    }
}

/// The TextKit 2 text view in its scroll view; `LiveCaptionsCoordinator` does the work.
private struct LiveCaptionsRepresentable: NSViewRepresentable {
    let lines: [RecordingTranscriber.Line]
    let tentative: [RecordingTranscriber.Tentative]
    let name: (RecordingTranscriber.Speaker) -> String
    let color: (RecordingTranscriber.Speaker) -> NSColor

    func makeCoordinator() -> LiveCaptionsCoordinator { LiveCaptionsCoordinator() }

    func makeNSView(context: Context) -> NSScrollView { context.coordinator.makeScrollView() }

    func updateNSView(_ scrollView: NSScrollView, context: Context) {
        context.coordinator.update(lines: lines, tentative: tentative, name: name, color: color)
    }

    static func dismantleNSView(_ scrollView: NSScrollView, coordinator: LiveCaptionsCoordinator) {
        coordinator.stop()
    }
}

/// Builds the captions' text view, applies each snapshot as small edits and keeps the end in view while following.
/// The text's paragraphs are joined by "\n" and it never ends with one: the shown lines first, then the tail.
@MainActor
private final class LiveCaptionsCoordinator: NSObject {
    private var textView: LiveCaptionsNSTextView?
    private var scrollView: NSScrollView?
    /// The lines in the text, oldest first, and each one's UTF-16 length without its separator.
    private var shown: [RecordingTranscriber.Line] = []
    private var lengths: [Int] = []
    /// The tentative text in the tail and the tail's UTF-16 length, its leading "\n" included.
    private var shownTentative: [RecordingTranscriber.Tentative] = []
    private var tailLength = 0
    /// The text scrolls to its end on each update; off while the user is scrolled away from the bottom.
    private var following = true
    private var scrollPending = false
    private var observers: [any NSObjectProtocol] = []

    private static let nameFont = NSFont.boldSystemFont(ofSize: TranscriptTextStyle.bodyFont.pointSize)
    private static let paragraphStyle: NSParagraphStyle = {
        let paragraph = NSMutableParagraphStyle()
        paragraph.paragraphSpacing = 4
        return paragraph
    }()

    func makeScrollView() -> NSScrollView {
        let textView = LiveCaptionsNSTextView(usingTextLayoutManager: true)
        textView.isEditable = false
        textView.isSelectable = true
        textView.isRichText = true
        textView.allowsUndo = false
        textView.importsGraphics = false
        textView.usesFontPanel = false
        textView.drawsBackground = false
        textView.textContainerInset = .zero
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: .greatestFiniteMagnitude)
        textView.textContainer?.widthTracksTextView = true
        textView.textContainer?.lineFragmentPadding = 0
        textView.textContainer?.size.height = .greatestFiniteMagnitude
        textView.onUserScroll = { [weak self] in self?.userScrolled() }
        textView.postsFrameChangedNotifications = true

        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.drawsBackground = false
        scrollView.contentView.drawsBackground = false
        scrollView.documentView = textView

        let center = NotificationCenter.default
        // The scroller dragged: no following while it moves, then following again when let go at the bottom. Wheels
        // and trackpads go through `onUserScroll`. Scrolls made here post neither.
        observers.append(center.addObserver(
            forName: NSScrollView.willStartLiveScrollNotification, object: scrollView, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.following = false }
        })
        observers.append(center.addObserver(
            forName: NSScrollView.didEndLiveScrollNotification, object: scrollView, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.userScrolled() }
        })
        // A new width wraps the text again: keep its end in view.
        observers.append(center.addObserver(
            forName: NSView.frameDidChangeNotification, object: textView, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.scrollToBottomLater() }
        })
        #if DEBUG
        observers.append(center.addObserver(
            forName: NSTextView.didSwitchToNSLayoutManagerNotification, object: textView, queue: .main
        ) { _ in
            NSLog("EchoDiar: the live captions text view fell back to TextKit 1 (something read its layoutManager)")
        })
        #endif
        self.textView = textView
        self.scrollView = scrollView
        return scrollView
    }

    func stop() {
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
    }

    /// Applies a snapshot: rebuilds the text when its lines don't continue the shown ones (another transcriber), else
    /// drops the lines that left the snapshot from the front, appends the new ones and replaces the tentative tail,
    /// keeping the selection where it was.
    func update(lines: [RecordingTranscriber.Line], tentative: [RecordingTranscriber.Tentative],
                name: (RecordingTranscriber.Speaker) -> String, color: (RecordingTranscriber.Speaker) -> NSColor) {
        guard let textView, let storage = textView.textStorage else { return }
        guard Self.continues(shown, with: lines) else {
            rebuild(lines: lines, tentative: tentative, name: name, color: color)
            return
        }
        let dropped = shown.prefix { $0.id < (lines.first?.id ?? .max) }.count
        let added = lines.drop { $0.id <= (shown.last?.id ?? .min) }
        guard dropped > 0 || !added.isEmpty || tentative != shownTentative else { return }
        var selection = textView.selectedRange()
        storage.beginEditing()
        if dropped > 0 {
            // Each dropped line and the separator after it; all of the lines when every one is dropped.
            let removed = dropped < shown.count
                ? lengths.prefix(dropped).reduce(0, +) + dropped
                : linesLength
            storage.deleteCharacters(in: NSRange(location: 0, length: removed))
            selection = Self.shift(selection, removing: removed)
            shown.removeFirst(dropped)
            lengths.removeFirst(dropped)
        }
        for line in added {
            let paragraph = Self.paragraph(line.speaker, line.text, secondary: false, name: name, color: color,
                                           separated: !shown.isEmpty)
            storage.insert(paragraph, at: linesLength)
            shown.append(line)
            lengths.append(paragraph.length - (shown.count > 1 ? 1 : 0))
        }
        let tail = Self.tail(tentative, separated: !shown.isEmpty, name: name, color: color)
        storage.replaceCharacters(in: NSRange(location: linesLength, length: tailLength), with: tail)
        shownTentative = tentative
        tailLength = tail.length
        storage.endEditing()
        let length = storage.length
        let location = min(selection.location, length)
        textView.setSelectedRange(NSRange(location: location, length: min(selection.length, length - location)))
        scrollToBottomLater()
    }

    /// The UTF-16 length of the shown lines and the separators between them.
    private var linesLength: Int {
        lengths.reduce(0, +) + max(0, lengths.count - 1)
    }

    /// The whole text again, scrolled to its end, without a selection.
    private func rebuild(lines: [RecordingTranscriber.Line], tentative: [RecordingTranscriber.Tentative],
                         name: (RecordingTranscriber.Speaker) -> String,
                         color: (RecordingTranscriber.Speaker) -> NSColor) {
        guard let textView, let storage = textView.textStorage else { return }
        let text = NSMutableAttributedString()
        lengths = []
        for line in lines {
            let paragraph = Self.paragraph(line.speaker, line.text, secondary: false, name: name, color: color,
                                           separated: text.length > 0)
            lengths.append(paragraph.length - (text.length > 0 ? 1 : 0))
            text.append(paragraph)
        }
        let tail = Self.tail(tentative, separated: !lines.isEmpty, name: name, color: color)
        text.append(tail)
        storage.setAttributedString(text)
        textView.setSelectedRange(NSRange(location: 0, length: 0))
        shown = lines
        shownTentative = tentative
        tailLength = tail.length
        following = true
        scrollToBottomLater()
    }

    /// True when `lines` go on from `shown`: they start inside it or just after it, and the lines both have are the
    /// same. Ids restart at 0 with each transcriber, so the texts are compared too.
    private static func continues(_ shown: [RecordingTranscriber.Line], with lines: [RecordingTranscriber.Line])
        -> Bool {
        guard let firstShown = shown.first, let lastShown = shown.last else { return true }
        guard let first = lines.first, first.id >= firstShown.id, first.id <= lastShown.id + 1 else { return false }
        let common = shown.drop { $0.id < first.id }
        return lines.count >= common.count && lines.prefix(common.count).elementsEqual(common)
    }

    /// `selection` once `removed` characters left the front: shifted back, clamped to the start, cleared when it
    /// was all in the removed part.
    private static func shift(_ selection: NSRange, removing removed: Int) -> NSRange {
        if selection.location >= removed {
            return NSRange(location: selection.location - removed, length: selection.length)
        }
        let end = NSMaxRange(selection)
        return end > removed ? NSRange(location: 0, length: end - removed) : NSRange(location: 0, length: 0)
    }

    /// The tentative lines, joined by "\n", after a "\n" when lines come before them.
    private static func tail(_ tentative: [RecordingTranscriber.Tentative], separated: Bool,
                             name: (RecordingTranscriber.Speaker) -> String,
                             color: (RecordingTranscriber.Speaker) -> NSColor) -> NSAttributedString {
        let tail = NSMutableAttributedString()
        for (index, item) in tentative.enumerated() {
            tail.append(paragraph(item.speaker, item.text, secondary: true, name: name, color: color,
                                  separated: separated || index > 0))
        }
        return tail
    }

    /// "Name: text": the name bold in the speaker's color, the text in the label (or secondary) color; after a "\n"
    /// when `separated`.
    private static func paragraph(_ speaker: RecordingTranscriber.Speaker, _ text: String, secondary: Bool,
                                  name: (RecordingTranscriber.Speaker) -> String,
                                  color: (RecordingTranscriber.Speaker) -> NSColor,
                                  separated: Bool) -> NSAttributedString {
        let body: [NSAttributedString.Key: Any] = [
            .font: TranscriptTextStyle.bodyFont, .foregroundColor: NSColor.labelColor,
            .paragraphStyle: paragraphStyle,
        ]
        let string = NSMutableAttributedString(string: separated ? "\n" : "", attributes: body)
        string.append(NSAttributedString(string: name(speaker), attributes: body.merging([
            .font: nameFont, .foregroundColor: color(speaker),
        ]) { $1 }))
        string.append(NSAttributedString(string: ": ", attributes: body))
        string.append(NSAttributedString(string: text, attributes: body.merging([
            .foregroundColor: secondary ? NSColor.secondaryLabelColor : NSColor.labelColor,
        ]) { $1 }))
        return string
    }

    // MARK: - Scrolling

    /// A wheel, trackpad or scroller scroll: away from the bottom stops following, back at the bottom follows again.
    private func userScrolled() {
        guard let textView, let clip = scrollView?.contentView else { return }
        following = clip.bounds.maxY >= textView.frame.height - 2
    }

    /// After this update's layout (the view has no size yet when it first appears), once per run loop turn.
    private func scrollToBottomLater() {
        guard following, !scrollPending else { return }
        scrollPending = true
        DispatchQueue.main.async { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.scrollToBottom()
                self.scrollPending = false
            }
        }
    }

    /// Lays out the whole text (at most 200 lines) so its height is exact, then scrolls to its end.
    private func scrollToBottom() {
        guard following, let textView, let scrollView, let layout = textView.textLayoutManager else { return }
        layout.ensureLayout(for: layout.documentRange)
        textView.sizeToFit()
        let clip = scrollView.contentView
        let y = max(0, textView.frame.height - clip.bounds.height)
        guard clip.bounds.minY != y else { return }
        clip.scroll(to: NSPoint(x: clip.bounds.minX, y: y))
        scrollView.reflectScrolledClipView(clip)
    }
}

/// Copies the selection as plain text only (⌘C, Edit › Copy, the context menu, drags) and tells the coordinator about
/// the user's own wheel and trackpad scrolling.
private final class LiveCaptionsNSTextView: NSTextView {
    var onUserScroll: (() -> Void)?

    override var writablePasteboardTypes: [NSPasteboard.PasteboardType] { [.string] }

    override func scrollWheel(with event: NSEvent) {
        super.scrollWheel(with: event)
        onUserScroll?()
    }
}
