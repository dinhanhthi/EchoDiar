import AppKit
import EchoCore
import SwiftUI

/// The app colors. The app is dark only, so each color has one value.
enum Palette {
    static let hoverFill = Color.white.opacity(0.1)
    static let pressedFill = Color.white.opacity(0.2)
    static let switchOffFill = Color.white.opacity(0.22)
    /// Cards and panels, a step above the window. #2C2C2E
    static let surface = Color(red: 44 / 255, green: 44 / 255, blue: 46 / 255)
    /// Text fields and input bars, a step above the cards. #3A3A3C
    static let inputSurface = Color(red: 58 / 255, green: 58 / 255, blue: 60 / 255)
    /// The 1 pt edge of cards and panels.
    static let surfaceBorder = Color.white.opacity(0.08)
    /// The edge of a raised card: bright at the top, dim on the sides, a faint highlight at the bottom.
    static let raisedRim = LinearGradient(
        stops: [
            .init(color: .white.opacity(0.24), location: 0),
            .init(color: .white.opacity(0.05), location: 0.45),
            .init(color: .white.opacity(0.10), location: 1),
        ],
        startPoint: .top, endPoint: .bottom)
}

/// The icon buttons of a row: a round highlight on hover, darker and a bit smaller while pressed. The highlight
/// reaches past the label without changing the row's layout.
struct RowIconButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Highlight(configuration: configuration)
    }

    private struct Highlight: View {
        let configuration: ButtonStyleConfiguration
        @State private var hovered = false
        @Environment(\.accessibilityReduceMotion) private var reduceMotion

        var body: some View {
            configuration.label
                .background {
                    Circle()
                        .fill(configuration.isPressed ? Palette.pressedFill : hovered ? Palette.hoverFill : .clear)
                        .padding(-5)
                }
                .contentShape(Rectangle())
                .scaleEffect(configuration.isPressed && !reduceMotion ? 0.88 : 1)
                .animation(reduceMotion ? nil : .snappy(duration: 0.12), value: configuration.isPressed)
                .animation(.easeOut(duration: 0.12), value: hovered)
                .onHover { hovered = $0 }
        }
    }
}

/// A small on/off switch; the system switch stays wide even at `.mini` on macOS 26.
struct CompactSwitch: ToggleStyle {
    static let height: CGFloat = 19

    func makeBody(configuration: Configuration) -> some View {
        Switch(configuration: configuration)
    }

    private struct Switch: View {
        let configuration: ToggleStyleConfiguration
        @Environment(\.accessibilityReduceMotion) private var reduceMotion

        var body: some View {
            Capsule()
                .fill(configuration.isOn ? Color.accentColor : Palette.switchOffFill)
                .frame(width: 34, height: CompactSwitch.height)
                .overlay(alignment: configuration.isOn ? .trailing : .leading) {
                    Circle()
                        .fill(.white)
                        .shadow(color: .black.opacity(0.2), radius: 0.5, y: 0.5)
                        .padding(1.5)
                }
                .animation(reduceMotion ? nil : .snappy(duration: 0.2), value: configuration.isOn)
                .contentShape(Capsule())
                .onTapGesture { configuration.isOn.toggle() }
                .accessibilityRepresentation {
                    Toggle(isOn: configuration.$isOn) { configuration.label }
                }
        }
    }
}

/// The translucent window background, blurring what is behind the window (toolbar included), under a dark tint so
/// only a little shows through.
struct WindowGlass: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .underWindowBackground
        view.blendingMode = .behindWindow
        view.state = .followsWindowActiveState
        let tint = NSView(frame: view.bounds)
        tint.wantsLayer = true
        // #1C1C1E
        tint.layer?.backgroundColor = NSColor(srgbRed: 28 / 255, green: 28 / 255, blue: 30 / 255, alpha: 0.6).cgColor
        tint.autoresizingMask = [.width, .height]
        view.addSubview(tint)
        return view
    }

    func updateNSView(_ view: NSVisualEffectView, context: Context) {}
}

/// A card or panel: solid `Palette.surface` with a 1 pt `Palette.surfaceBorder` edge, on every macOS version, so it
/// stands out from the window background. Raised (the library's entry cards) looks like the toolbar's capsules: a
/// rim lit from above, a faint sheen at the top and a soft shadow under it.
struct GlassCard: ViewModifier {
    var cornerRadius: CGFloat = 14
    var raised = false

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        if raised {
            content
                .background {
                    shape.fill(Palette.surface)
                        .overlay(shape.fill(LinearGradient(
                            colors: [.white.opacity(0.05), .clear], startPoint: .top, endPoint: .center)))
                        .shadow(color: .black.opacity(0.35), radius: 6, y: 3)
                }
                .overlay(shape.strokeBorder(Palette.raisedRim, lineWidth: 1).allowsHitTesting(false))
        } else {
            content
                .background(shape.fill(Palette.surface))
                .overlay(shape.strokeBorder(Palette.surfaceBorder, lineWidth: 1).allowsHitTesting(false))
        }
    }
}

/// Tabs for a toolbar item, as tall as its buttons' highlights, in the toolbar's capsule on macOS 26 (a surface capsule
/// before): the selected tab is an accent capsule that slides between the tabs (no slide under Reduce Motion).
struct CapsuleTabs<Tab: Hashable>: View {
    @Binding var selection: Tab
    let tabs: [Tab]
    let title: (Tab) -> Text
    @Namespace private var namespace
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 2) {
            ForEach(tabs, id: \.self) { tab in
                Item(tab: tab, title: title(tab), selected: tab == selection, namespace: namespace) {
                    withAnimation(animation) { selection = tab }
                }
            }
        }
        // One pill that follows the selected item's frame, so it slides instead of fading between items.
        .background {
            Capsule()
                .fill(Color.accentColor)
                .matchedGeometryEffect(id: selection, in: namespace, isSource: false)
        }
        // Also animates a selection set from code (menu, shortcuts, card buttons).
        .animation(animation, value: selection)
        .modifier(SurfaceTrack())
    }

    private var animation: Animation? { reduceMotion ? nil : .snappy(duration: 0.25) }

    /// Before macOS 26 the toolbar draws no capsule around its items.
    private struct SurfaceTrack: ViewModifier {
        func body(content: Content) -> some View {
            if #available(macOS 26, *) {
                content
            } else {
                content
                    .padding(3)
                    .background(Capsule().fill(Palette.surface))
                    .overlay(Capsule().strokeBorder(Palette.surfaceBorder, lineWidth: 1).allowsHitTesting(false))
            }
        }
    }

    private struct Item: View {
        let tab: Tab
        let title: Text
        let selected: Bool
        let namespace: Namespace.ID
        let action: () -> Void
        @State private var hovered = false
        /// A toolbar button's highlight.
        static var height: CGFloat { 28 }

        var body: some View {
            Button(action: action) {
                title
                    .font(.callout)
                    .lineLimit(1)
                    .foregroundStyle(selected ? Color.white : Color.secondary)
                    .padding(.horizontal, 12)
                    .frame(height: Self.height)
                    .background {
                        if hovered && !selected { Capsule().fill(Palette.hoverFill) }
                    }
                    .matchedGeometryEffect(id: tab, in: namespace)
                    .contentShape(Capsule())
            }
            .buttonStyle(.plain)
            .onHover { hovered = $0 }
            .accessibilityAddTraits(selected ? .isSelected : [])
        }
    }
}

/// A disclosure row whose chevron sits on the title's line, not halfway down a title with a subtitle. A click anywhere
/// on the header row toggles it, not only on the chevron; the row's own controls (its switch) keep their clicks.
struct TitleChevronDisclosureStyle: DisclosureGroupStyle {
    /// The chevron's width: content inset by it lines up with the title.
    static let indent: CGFloat = 18

    func makeBody(configuration: Configuration) -> some View {
        TitleChevronDisclosure(configuration: configuration)
    }
}

private struct TitleChevronDisclosure: View {
    let configuration: DisclosureGroupStyleConfiguration
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline, spacing: 0) {
                Button(action: toggle) {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 10, weight: .bold))
                        .rotationEffect(.degrees(configuration.isExpanded ? 90 : 0))
                        .frame(width: TitleChevronDisclosureStyle.indent, alignment: .leading)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(configuration.isExpanded ? "Collapse" : "Expand")
                configuration.label
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
            .onTapGesture(perform: toggle)
            if configuration.isExpanded { configuration.content }
        }
    }

    private func toggle() {
        withAnimation(reduceMotion ? nil : .snappy(duration: 0.2)) {
            configuration.$isExpanded.wrappedValue.toggle()
        }
    }
}
