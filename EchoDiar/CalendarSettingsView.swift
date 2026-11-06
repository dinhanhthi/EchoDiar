import AppKit
import EchoCore
import SwiftUI

/// The Calendar settings page: calendar access, what happens when a meeting starts, the watched calendars and
/// stopping at the event's end.
struct CalendarSettingsView: View {
    let model: AppModel

    var body: some View {
        Form { CalendarSettingsSections(model: model) }
            .formStyle(.grouped)
            .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
                // Back from System Settings.
                model.refreshCalendarAccess()
            }
    }
}

/// The Calendar page's sections, also shown in Recording's Calendar tab until it leaves.
struct CalendarSettingsSections: View {
    let model: AppModel

    var body: some View {
        Section {
            calendarAccessRow
            Picker(selection: model.setting(\.calendar.mode)) {
                Text("Off").tag(CalendarScheduler.Mode.off)
                Text("Ask").tag(CalendarScheduler.Mode.ask)
                Text("Wait for the call").tag(CalendarScheduler.Mode.waitForCall)
                Text("Record at start").tag(CalendarScheduler.Mode.recordAtStart)
            } label: {
                Text("When a meeting starts")
                if model.settings.calendar.mode == .waitForCall && !model.settings.meetingDetection {
                    Text("Detect meetings is off, so EchoDiar asks instead.")
                }
            }
            if !model.settings.launchAtLogin {
                LabeledContent("EchoDiar reacts to meetings only while it runs. Turn on Launch at login in General.") {
                    Button("Open General") { model.openSettings(tab: .general) }
                }
                .foregroundStyle(.secondary)
            }
        }
        if model.calendarAuthorized {
            Section {
                ForEach(calendarSources, id: \.self) { source in
                    if !source.isEmpty {
                        Text(source)
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                    }
                    ForEach(model.calendars.filter { $0.sourceTitle == source }) { calendar in
                        Toggle(isOn: watched(calendar)) {
                            HStack(spacing: 6) {
                                Circle()
                                    .fill(Color(nsColor: calendar.color))
                                    .frame(width: 10, height: 10)
                                Text(calendar.title)
                            }
                        }
                        .toggleStyle(.checkbox)
                    }
                }
            } header: {
                SectionHeader(title: "Watched calendars", description: "New calendars are watched too.")
            }
        }
        Section {
            Toggle(isOn: meetingLinkOnly) {
                Text("Only events with a meeting link")
                Text("Zoom, Google Meet, Microsoft Teams or Webex link in the event.")
            }
        }
        Section {
            Toggle(isOn: model.setting(\.calendar.stopAtEventEnd)) {
                Text("Stop when the event ends")
                Text("The indicator asks before stopping, with a 15 s countdown, like the other auto-stops.")
            }
            NumberRow(
                title: "Minutes after the end", unit: String(localized: "min"),
                value: model.setting(\.calendar.stopAfterEndMinutes),
                range: CalendarScheduler.Configuration.stopAfterEndRange)
                .disabled(!model.settings.calendar.stopAtEventEnd)
        }
    }

    /// Nothing once full access is granted; else what is missing and how to grant it.
    @ViewBuilder private var calendarAccessRow: some View {
        switch model.calendarStatus {
        case .fullAccess:
            EmptyView()
        case .notDetermined:
            LabeledContent("EchoDiar has no calendar access yet.") {
                Button("Grant Access") { model.requestCalendarAccess() }
            }
            .foregroundStyle(.secondary)
        default:
            LabeledContent("Calendar access is off. Turn on EchoDiar in Privacy & Security › Calendars.") {
                Button("Open Privacy Settings") { PrivacySettings.openCalendars() }
            }
            .foregroundStyle(.secondary)
        }
    }

    /// Calendar sources ("iCloud", an account) in the list's order.
    private var calendarSources: [String] {
        var seen = Set<String>()
        return model.calendars.map(\.sourceTitle).filter { seen.insert($0).inserted }
    }

    /// Checked = watched, stored as not in `ignoredCalendarIDs`.
    private func watched(_ calendar: CalendarStore.CalendarInfo) -> Binding<Bool> {
        Binding(
            get: { !model.settings.calendar.ignoredCalendarIDs.contains(calendar.id) },
            set: { watched in
                model.updateSettings { settings in
                    settings.calendar.ignoredCalendarIDs.removeAll { $0 == calendar.id }
                    if !watched { settings.calendar.ignoredCalendarIDs.append(calendar.id) }
                }
            })
    }

    /// The inverse of `allTimedEvents`.
    private var meetingLinkOnly: Binding<Bool> {
        Binding(
            get: { !model.settings.calendar.allTimedEvents },
            set: { value in model.updateSettings { $0.calendar.allTimedEvents = !value } })
    }
}
