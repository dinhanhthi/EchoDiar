import AppKit
import EchoCore
import SwiftUI

/// The app colors. The app is dark only, so each color has one value.
enum Palette {
    static let hoverFill = Color.white.opacity(0.1)
    static let pressedFill = Color.white.opacity(0.2)
    static let switchOffFill = Color.white.opacity(0.22)
    /// The window, under the toolbar too. #1C1C1E
    static let windowBackground = Color(red: 28 / 255, green: 28 / 255, blue: 30 / 255)
    /// Cards and panels, a step above the window. #2C2C2E
    static let surface = Color(red: 44 / 255, green: 44 / 255, blue: 46 / 255)
    /// Text fields and input bars, a step above the cards. #3A3A3C
    static let inputSurface = Color(red: 58 / 255, green: 58 / 255, blue: 60 / 255)
    /// The 1 pt edge of cards and panels.
    static let surfaceBorder = Color.white.opacity(0.08)
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

/// The translucent window background, blurring what is behind the window (toolbar included).
struct WindowGlass: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .underWindowBackground
        view.blendingMode = .behindWindow
        view.state = .followsWindowActiveState
        return view
    }

    func updateNSView(_ view: NSVisualEffectView, context: Context) {}
}

/// A card or panel: solid `Palette.surface` with a 1 pt `Palette.surfaceBorder` edge, on every macOS version, so it
/// stands out from the window background.
struct GlassCard: ViewModifier {
    var cornerRadius: CGFloat = 14

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        content
            .background(shape.fill(Palette.surface))
            .overlay(shape.strokeBorder(Palette.surfaceBorder, lineWidth: 1).allowsHitTesting(false))
    }
}

/// Tabs in a small surface capsule, the toolbar's selected-toggle look: the selected tab is an accent capsule that
/// slides between the tabs (no slide under Reduce Motion).
struct CapsuleTabs<Tab: Hashable>: View {
    @Binding var selection: Tab
    let tabs: [Tab]
    let title: (Tab) -> LocalizedStringKey
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
        .padding(3)
        // Also animates a selection set from code (menu, shortcuts, card buttons).
        .animation(animation, value: selection)
        .background(Capsule().fill(Palette.surface))
        .overlay(Capsule().strokeBorder(Palette.surfaceBorder, lineWidth: 1).allowsHitTesting(false))
    }

    private var animation: Animation? { reduceMotion ? nil : .snappy(duration: 0.25) }

    private struct Item: View {
        let tab: Tab
        let title: LocalizedStringKey
        let selected: Bool
        let namespace: Namespace.ID
        let action: () -> Void
        @State private var hovered = false

        var body: some View {
            Button(action: action) {
                Text(title)
                    .font(.callout)
                    .lineLimit(1)
                    .foregroundStyle(selected ? Color.white : Color.secondary)
                    .padding(.horizontal, 12)
                    .frame(height: 24)
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
