import AppKit
import Carbon.HIToolbox
import EchoCore

/// A global shortcut (record, pause), through Carbon's `RegisterEventHotKey`: works while other apps are in front and
/// needs no Accessibility or Input Monitoring permission. One shortcut per instance, told apart by `id`; Carbon
/// delivers it on the main thread.
@MainActor
final class GlobalHotKey {
    private let id: UInt32
    private let action: @MainActor () -> Void
    private var handler: EventHandlerRef?
    private var registered: EventHotKeyRef?

    init(id: UInt32, action: @escaping @MainActor () -> Void) {
        self.id = id
        self.action = action
    }

    /// Replaces the registered shortcut with `key` (nil only unregisters). Returns why it failed, nil on success.
    func register(_ key: HotKey?) -> String? {
        if let registered { UnregisterEventHotKey(registered) }
        registered = nil
        guard let key else { return nil }
        if handler == nil {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            let status = InstallEventHandler(
                GetApplicationEventTarget(), Self.callback, 1, &spec,
                Unmanaged.passUnretained(self).toOpaque(), &handler)
            guard status == noErr else { return "The shortcut could not be set up (error \(status))." }
        }
        var ref: EventHotKeyRef?
        let hotKeyID = EventHotKeyID(signature: Self.signature, id: id)
        let status = RegisterEventHotKey(key.keyCode, key.modifiers, hotKeyID, GetApplicationEventTarget(), 0, &ref)
        switch status {
        case noErr:
            registered = ref
            return nil
        case OSStatus(eventHotKeyExistsErr):
            return "\(key.displayString) is already used by another app. Pick another shortcut."
        default:
            return "\(key.displayString) could not be registered (error \(status))."
        }
    }

    private static let signature = OSType(0x4544_484B)  // "EDHK"

    /// Never released: the instance lives as long as the app (owned by `AppModel`). Every instance's handler sees
    /// every press: another instance's shortcut is passed on to the next handler.
    private static let callback: EventHandlerUPP = { _, event, userData in
        guard let event, let userData else { return OSStatus(eventNotHandledErr) }
        var pressed = EventHotKeyID()
        let status = GetEventParameter(
            event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
            MemoryLayout<EventHotKeyID>.size, nil, &pressed)
        guard status == noErr, pressed.signature == signature else { return OSStatus(eventNotHandledErr) }
        let hotKey = Unmanaged<GlobalHotKey>.fromOpaque(userData).takeUnretainedValue()
        return MainActor.assumeIsolated {
            guard pressed.id == hotKey.id else { return OSStatus(eventNotHandledErr) }
            hotKey.action()
            return noErr
        }
    }
}

extension HotKey {
    /// Carbon modifier bits from AppKit flags (⌃ ⌥ ⇧ ⌘ only).
    init(keyCode: UInt16, flags: NSEvent.ModifierFlags) {
        var modifiers: UInt32 = 0
        if flags.contains(.command) { modifiers |= UInt32(cmdKey) }
        if flags.contains(.shift) { modifiers |= UInt32(shiftKey) }
        if flags.contains(.option) { modifiers |= UInt32(optionKey) }
        if flags.contains(.control) { modifiers |= UInt32(controlKey) }
        self.init(keyCode: UInt32(keyCode), modifiers: modifiers)
    }

    /// "⌥⌘R".
    var displayString: String {
        modifierSymbols + Self.keyName(keyCode)
    }

    /// "⌥⌘", in the menu order ⌃ ⌥ ⇧ ⌘.
    var modifierSymbols: String {
        var text = ""
        if modifiers & UInt32(controlKey) != 0 { text += "⌃" }
        if modifiers & UInt32(optionKey) != 0 { text += "⌥" }
        if modifiers & UInt32(shiftKey) != 0 { text += "⇧" }
        if modifiers & UInt32(cmdKey) != 0 { text += "⌘" }
        return text
    }

    private static let specialKeys: [Int: String] = [
        kVK_Space: "Space", kVK_Return: "↩", kVK_Tab: "⇥", kVK_Delete: "⌫", kVK_ForwardDelete: "⌦",
        kVK_Escape: "⎋", kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_UpArrow: "↑", kVK_DownArrow: "↓",
        kVK_Home: "↖", kVK_End: "↘", kVK_PageUp: "⇞", kVK_PageDown: "⇟",
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6", kVK_F7: "F7",
        kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12", kVK_F13: "F13",
        kVK_F14: "F14", kVK_F15: "F15", kVK_F16: "F16", kVK_F17: "F17", kVK_F18: "F18", kVK_F19: "F19",
    ]

    /// The key's character on the current keyboard layout ("R" for `kVK_ANSI_R` on QWERTY).
    private static func keyName(_ keyCode: UInt32) -> String {
        if let special = specialKeys[Int(keyCode)] { return special }
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let layout = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData)
        else { return "Key \(keyCode)" }
        let data = Unmanaged<CFData>.fromOpaque(layout).takeUnretainedValue() as Data
        var deadKeyState: UInt32 = 0
        var length = 0
        var characters = [UniChar](repeating: 0, count: 4)
        let status = data.withUnsafeBytes { bytes -> OSStatus in
            guard let base = bytes.baseAddress else { return OSStatus(paramErr) }
            return UCKeyTranslate(
                base.assumingMemoryBound(to: UCKeyboardLayout.self), UInt16(keyCode), UInt16(kUCKeyActionDisplay), 0,
                UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysBit), &deadKeyState, characters.count,
                &length, &characters)
        }
        guard status == noErr, length > 0 else { return "Key \(keyCode)" }
        return String(utf16CodeUnits: characters, count: length).uppercased()
    }
}
