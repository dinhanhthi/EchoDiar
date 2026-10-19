import AppKit
import CoreAudio
import EchoCore
import SwiftUI

/// The Recording tab, one inner tab per section: the microphone, meeting detection and per-app behavior with the
/// floating pill's anchor, auto-stop, and what happens after a recording stops.
struct RecordingSettingsView: View {
    let model: AppModel
    @State private var microphones: [InputDevice] = []
    @State private var accessibilityTrusted = WindowAnchor.isTrusted

    /// One row per app the user sees (FaceTime's daemon is listed as FaceTime).
    private static let meetingApps: [MeetingApp] = {
        var seen = Set<String>()
        return MeetingApps.known.filter { seen.insert($0.appBundleID).inserted }
    }()

    @State private var tab = Tab.microphone

    private enum Tab: String, CaseIterable {
        case microphone, meetings, autoStop, afterRecording

        var title: LocalizedStringKey {
            switch self {
            case .microphone: "Microphone"
            case .meetings: "Meetings"
            case .autoStop: "Auto-stop"
            case .afterRecording: "After recording"
            }
        }
    }

    var body: some View {
        SettingsSectionTabs(selection: $tab, title: \.title) { tab in
            switch tab {
            case .microphone: microphoneSection
            case .meetings: meetingsSection
            case .autoStop: autoStopSection
            case .afterRecording: afterRecordingSection
            }
        }
        .task {
            microphones = await Task.detached(priority: .utility) { InputDevice.all() }.value
        }
        .task { model.refreshInstalledModels() }
        .onChange(of: model.pendingSettingsSubTab, initial: true) { showPendingTab() }
        .onChange(of: model.settingsTab) { showPendingTab() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            // Back from System Settings.
            accessibilityTrusted = WindowAnchor.isTrusted
        }
    }

    /// Shows the inner tab a deep link asked for (`AppModel.openSettings(tab:subTab:)`) once this section is selected.
    private func showPendingTab() {
        guard model.settingsTab == .recording, let id = model.pendingSettingsSubTab, let pending = Tab(rawValue: id) else { return }
        tab = pending
        model.pendingSettingsSubTab = nil
    }

    private var microphoneSection: some View {
        Section {
            Picker("Microphone", selection: model.setting(\.micDeviceUID)) {
                Text("System default").tag(String?.none)
                ForEach(microphones) { Text($0.name).tag(String?.some($0.id)) }
                if let uid = model.settings.micDeviceUID, !microphones.contains(where: { $0.id == uid }) {
                    Text("Unavailable device").tag(String?.some(uid))
                }
            }
            Picker(selection: model.setting(\.routeChangeBehavior)) {
                Text("Ask to stop").tag(RouteChangeBehavior.ask)
                Text("Keep recording").tag(RouteChangeBehavior.continue)
                Text("Stop recording").tag(RouteChangeBehavior.stop)
            } label: {
                Text("When the microphone disconnects")
                Text("Recording continues with the default input until then.")
            }
        }
    }

    @ViewBuilder private var meetingsSection: some View {
        Section {
            Toggle(isOn: model.setting(\.attachPillToWindow)) {
                Text("Attach the indicator to the meeting window")
                Text("Keeps it at the window's top-right corner. Needs Accessibility access.")
            }
            if model.settings.attachPillToWindow && !accessibilityTrusted {
                LabeledContent("EchoDiar has no Accessibility access.") {
                    Button("Grant Accessibility…", action: WindowAnchor.openAccessibilitySettings)
                }
                .foregroundStyle(.secondary)
            }
        }
        Section {
            Toggle(isOn: model.setting(\.meetingDetection)) {
                Text("Detect meetings")
                Text("Offers to record when a meeting app starts using the microphone.")
            }
            ForEach(Self.meetingApps, id: \.appBundleID) { app in
                Picker(app.name, selection: behavior(app)) {
                    Text("Ask").tag(MeetingBehavior.ask)
                    if !app.isBrowser { Text("Always record").tag(MeetingBehavior.always) }
                    Text("Ignore").tag(MeetingBehavior.ignore)
                }
            }
            .disabled(!model.settings.meetingDetection)
        }
    }

    private var autoStopSection: some View {
        Section {
            NumberRow(
                title: "Stop when the app is quiet for", unit: "s",
                value: model.setting(\.endMonitor.sourceInactiveSeconds),
                range: MeetingEndMonitor.Configuration.sourceInactiveRange)
            NumberRow(
                title: "Stop when the call seems over for", unit: "s",
                value: model.setting(\.endMonitor.callEndedSeconds),
                range: MeetingEndMonitor.Configuration.callEndedRange)
            NumberRow(
                title: "Stop after silence of", unit: "min",
                value: model.setting(\.endMonitor.silenceMinutes),
                range: MeetingEndMonitor.Configuration.silenceRange)
            NumberRow(
                title: "Stop after a recording length of", unit: "min",
                value: model.setting(\.endMonitor.maxDurationMinutes),
                range: MeetingEndMonitor.Configuration.maxDurationRange)
        } footer: {
            Text("The indicator asks before stopping, with a 15 s countdown.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var afterRecordingSection: some View {
        Section {
            Toggle("Transcribe automatically", isOn: model.setting(\.autoTranscribe))
            Picker(selection: model.setting(\.speakerCountHint)) {
                Text("Auto").tag(Int?.none)
                ForEach(AppSettings.speakerCountHintRange, id: \.self) { Text("\($0)").tag(Int?.some($0)) }
            } label: {
                Text("Number of other speakers")
                if model.installedModelIDs.contains(ModelCatalog.defaultDiarizer.id) {
                    Text("A hint for telling the system audio's speakers apart.")
                } else {
                    Text("Needs the speaker model: download it in Transcription › Balanced.")
                }
            }
            Toggle(isOn: model.setting(\.deferTranscriptionWhenConstrained)) {
                Text("Wait when the Mac is hot or in Low Power Mode")
                Text("Transcription starts once the Mac is ready.")
            }
        }
    }

    /// "Ask" is stored as no entry, the default.
    private func behavior(_ app: MeetingApp) -> Binding<MeetingBehavior> {
        Binding(
            get: { model.settings.perAppBehavior[app.appBundleID] ?? .ask },
            set: { value in model.updateSettings { $0.perAppBehavior[app.appBundleID] = value == .ask ? nil : value } })
    }
}

/// The sections of a Settings page as tabs: capsule tabs at the top, the selected section's form below.
struct SettingsSectionTabs<Tab: Hashable & CaseIterable, Content: View>: View where Tab.AllCases: RandomAccessCollection {
    @Binding var selection: Tab
    let title: (Tab) -> LocalizedStringKey
    @ViewBuilder let content: (Tab) -> Content

    var body: some View {
        VStack(spacing: 0) {
            // A menu when the tabs are wider than the pane, so the pane never grows past the window.
            ViewThatFits(in: .horizontal) {
                CapsuleTabs(selection: $selection, tabs: Array(Tab.allCases), title: title).fixedSize()
                picker.pickerStyle(.menu).fixedSize()
            }
            .labelsHidden()
            .padding(.horizontal, 20)
            .padding(.vertical, 12)
            Form { content(selection) }
                .formStyle(.grouped)
        }
    }

    private var picker: some View {
        Picker("Section", selection: $selection) {
            ForEach(Tab.allCases, id: \.self) { Text(title($0)).tag($0) }
        }
    }
}

/// A whole number typed or stepped within `range`.
struct NumberRow: View {
    let title: String
    let unit: String
    @Binding var value: Int
    let range: ClosedRange<Int>

    var body: some View {
        LabeledContent(title) {
            HStack(spacing: 6) {
                TextField(title, value: clamped, format: .number)
                    .labelsHidden()
                    .multilineTextAlignment(.trailing)
                    .frame(width: 60)
                Stepper(title, value: clamped, in: range)
                    .labelsHidden()
                Text(unit)
                    .foregroundStyle(.secondary)
                    .frame(minWidth: 26, alignment: .leading)
            }
        }
        .help("\(range.lowerBound)–\(range.upperBound) \(unit)")
    }

    private var clamped: Binding<Int> {
        Binding(get: { value }, set: { value = min(max($0, range.lowerBound), range.upperBound) })
    }
}

/// A Core Audio device with input channels.
private struct InputDevice: Identifiable, Sendable {
    /// The device UID, as stored in `AppSettings.micDeviceUID`.
    let id: String
    let name: String

    /// Read once when the tab opens; nothing listens for changes.
    static func all() -> [InputDevice] {
        let system = AudioObjectID(kAudioObjectSystemObject)
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            guard hasInput(id), let uid = string(id, kAudioDevicePropertyDeviceUID),
                  let name = string(id, kAudioObjectPropertyName) else { return nil }
            return InputDevice(id: uid, name: name)
        }
        .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    private static func hasInput(_ device: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreams, mScope: kAudioObjectPropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func string(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr,
              let value else { return nil }
        return value.takeRetainedValue() as String
    }
}
