import Foundation
import SwiftUI

/// The order of every language menu: Auto, the first-class content languages, the recent ones, then every other
/// language the engine supports. Plain data, so the AppKit menu bar menu uses it too.
enum LanguageOptions {
    static let auto = "auto"
    static let pinned = ["en", "fr", "vi"]
    static let maxRecent = 5

    /// Each code once; `selection` joins the recent group when no other group has it.
    static func groups(selection: String, recent: [String], supported: [String]) -> [[String]] {
        var seen = Set([auto] + pinned)
        var recent = recent.filter { seen.insert($0).inserted }
        let others = supported.filter { seen.insert($0).inserted }
        if !selection.isEmpty, !seen.contains(selection) { recent.append(selection) }
        return [[auto], pinned, recent, others].filter { !$0.isEmpty }
    }

    /// English name (the UI is English only); the code itself when the system has no name for it.
    static func name(_ code: String) -> String {
        code == auto ? "Auto" : Locale(identifier: "en").localizedString(forLanguageCode: code) ?? code
    }
}

/// A language picker for menus (a submenu) and forms (a pop-up button), in `LanguageOptions` order.
/// `asRecorded` adds a first option with the tag "" for "keep the recording's language markers".
struct LanguagePicker: View {
    let title: String
    @Binding var selection: String
    let model: AppModel
    var engineID: String?
    var asRecorded = false

    var body: some View {
        Picker(title, selection: $selection) {
            if asRecorded {
                Section { Text("As recorded").tag("") }
            }
            let groups = LanguageOptions.groups(
                selection: selection, recent: model.settings.recentLanguages,
                supported: model.supportedLanguages(engineID: engineID))
            ForEach(groups, id: \.self) { group in
                Section {
                    ForEach(group, id: \.self) { Text(LanguageOptions.name($0)).tag($0) }
                }
            }
        }
    }
}
