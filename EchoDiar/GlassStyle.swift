import AppKit
import EchoCore
import SwiftUI

/// The app colors. The app is dark only, so each color has one value.
enum Palette {
    static let hoverFill = Color.white.opacity(0.1)
    static let pressedFill = Color.white.opacity(0.2)
    static let switchOffFill = Color.white.opacity(0.22)
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

/// Liquid Glass on macOS 26+ (unless `--force-fallbacks`), a material card otherwise.
struct GlassCard: ViewModifier {
    var cornerRadius: CGFloat = 14

    func body(content: Content) -> some View {
        if #available(macOS 26, *), PlatformFeatures.liquidGlass {
            content.glassEffect(.regular, in: .rect(cornerRadius: cornerRadius))
        } else {
            content.background(.regularMaterial, in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
        }
    }
}

/// Groups glass cards in a `GlassEffectContainer` when Liquid Glass is on.
struct GlassCards<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        if #available(macOS 26, *), PlatformFeatures.liquidGlass {
            GlassEffectContainer { content }
        } else {
            content
        }
    }
}
