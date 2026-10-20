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

    /// Ids in `SettingsTab.recording.subTabs`; picked in the toolbar.
    private enum Tab: String {
        case microphone, meetings, autoStop, afterRecording
    }

    private var tab: Tab { model.settingsSubTab(.recording).flatMap(Tab.init) ?? .microphone }

    var body: some View {
        Form {
            switch tab {
            case .microphone: microphoneSection
            case .meetings: meetingsSection
            case .autoStop: autoStopSection
            case .afterRecording: afterRecordingSection
            }
        }
        .formStyle(.grouped)
        .task {
            for await _ in InputDevice.changes() {
                microphones = await Task.detached(priority: .utility) { InputDevice.all() }.value
            }
        }
        .task { model.refreshInstalledModels() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            // Back from System Settings.
            accessibilityTrusted = WindowAnchor.isTrusted
        }
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
                switch model.settings.routeChangeBehavior {
                case .ask: Text("Recording continues with the default input until then.")
                case .continue: Text("Recording switches to the default input.")
                case .stop: Text("Recording stops at once.")
                }
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
            Text("The indicator asks before stopping, with a 15 s countdown. Reaching the length limit stops at once.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var afterRecordingSection: some View {
        Section {
            Toggle("Transcribe automatically", isOn: model.setting(\.autoTranscribe))
            SpeakerCountPicker(model: model)
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

/// The speaker count hint, shown in Recording › After recording and in Transcription › Diarization model.
struct SpeakerCountPicker: View {
    let model: AppModel

    var body: some View {
        Picker(selection: model.setting(\.speakerCountHint)) {
            Text("Auto").tag(Int?.none)
            ForEach(AppSettings.speakerCountHintRange, id: \.self) { Text("\($0)").tag(Int?.some($0)) }
        } label: {
            Text("Number of other speakers")
            if model.installedModelIDs.contains(ModelCatalog.defaultDiarizer.id) {
                Text("A hint for telling the system audio's speakers apart.")
            } else {
                Text("Needs the speaker model: download it in Transcription › Diarization model.")
            }
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

    private static let system = AudioObjectID(kAudioObjectSystemObject)
    private static let devicesAddress = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
    /// Core Audio's per-process aggregate for the default devices ("CADefaultDeviceAggregate-<pid>-<n>").
    private static let defaultAggregatePrefix = "CADefaultDeviceAggregate-"

    /// Read again on each `changes()` value. Private aggregates (another app's or EchoDiar's own tap) are skipped;
    /// aggregates the user made in Audio MIDI Setup stay.
    static func all() -> [InputDevice] {
        var address = devicesAddress
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            guard hasInput(id), !isPrivateAggregate(id), let uid = string(id, kAudioDevicePropertyDeviceUID),
                  !uid.hasPrefix(defaultAggregatePrefix),
                  let name = string(id, kAudioObjectPropertyName) else { return nil }
            return InputDevice(id: uid, name: name)
        }
        .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// Yields once at start, then each time the device list changes (plugged, unplugged, aggregate made or removed).
    /// The listener is removed when the stream ends (the consuming task is cancelled).
    static func changes() -> AsyncStream<Void> {
        AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            continuation.yield()
            // `@Sendable` so `onTermination` can capture it; the same block must be passed to remove it.
            let listener: @convention(block) @Sendable (UInt32, UnsafePointer<AudioObjectPropertyAddress>) -> Void = {
                _, _ in continuation.yield()
            }
            var address = devicesAddress
            guard AudioObjectAddPropertyListenerBlock(system, &address, nil, listener) == noErr else {
                continuation.finish()
                return
            }
            continuation.onTermination = { _ in
                var address = devicesAddress
                AudioObjectRemovePropertyListenerBlock(system, &address, nil, listener)
            }
        }
    }

    /// `kAudioAggregateDeviceIsPrivateKey` in the composition, which only aggregate devices have.
    private static func isPrivateAggregate(_ device: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioAggregateDevicePropertyComposition, mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectHasProperty(device, &address) else { return false }
        var value: Unmanaged<CFDictionary>?
        var size = UInt32(MemoryLayout<Unmanaged<CFDictionary>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr,
              let composition = value?.takeRetainedValue() as? [String: Any] else { return false }
        return (composition[kAudioAggregateDeviceIsPrivateKey] as? NSNumber)?.boolValue == true
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
