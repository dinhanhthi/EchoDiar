import AppKit
import EchoCore
import SwiftUI

/// The Calendar page of the main window: calendar access while it is missing, then the upcoming meetings of the next
/// 7 days by day, each in a card with Join, Record and a Skip switch. One scrolling pane of solid cards on the window's
/// glass. Event titles come from invites: shown as plain text only.
struct CalendarView: View {
    let model: AppModel

    /// Record is offered this long before an event starts.
    private static let recordLead: TimeInterval = 15 * 60

    @State private var editor: EditorState?

    /// The event editor: a new event or one of EchoDiar's own.
    private enum EditorState: Identifiable {
        case new
        case edit(CalendarEvent)

        var id: String {
            switch self {
            case .new: "new"
            case .edit(let event): event.id
            }
        }

        var event: CalendarEvent? {
            if case .edit(let event) = self { event } else { nil }
        }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                header
                if !model.calendarAuthorized { accessCard }
                if !model.settings.launchAtLogin { launchAtLoginCard }
                if model.calendarAuthorized {
                    // Each minute, so Record turns on 15 min before an event without a calendar change.
                    TimelineView(.periodic(from: .now, by: 60)) { context in
                        events(now: context.date)
                    }
                }
            }
            .frame(maxWidth: 720)
            .padding(12)
            .frame(maxWidth: .infinity)
        }
        // Back from System Settings, where access may have been granted.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            model.refreshCalendarAccess()
        }
        .sheet(item: $editor) { EventEditorSheet(model: model, editing: $0.event) }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("Calendar")
                    .font(.title2.weight(.semibold))
                    .accessibilityAddTraits(.isHeader)
                Spacer()
                Button("New Event…") { editor = .new }
                    .disabled(!model.calendarAuthorized)
                Button("Calendar settings…") { model.openSettings(tab: .recording, subTab: "calendar") }
            }
            Text("Showing meetings in the next 7 days. The list updates automatically.")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
    }

    /// What is missing and how to grant it, with the Settings tab's wording.
    @ViewBuilder private var accessCard: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Upcoming meetings show here once EchoDiar can read your calendars.")
            if model.calendarStatus == .notDetermined {
                Text("EchoDiar has no calendar access yet.")
                    .foregroundStyle(.secondary)
                Button("Grant Access") { model.requestCalendarAccess() }
            } else {
                Text("Calendar access is off. Turn on EchoDiar in Privacy & Security › Calendars.")
                    .foregroundStyle(.secondary)
                Button("Open Privacy Settings") { PrivacySettings.openCalendars() }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(12)
        .modifier(GlassCard())
    }

    private var launchAtLoginCard: some View {
        HStack(spacing: 12) {
            Text("EchoDiar reacts to meetings only while it runs. Turn on Launch at login in General.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Button("Open General") { model.openSettings(tab: .general) }
        }
        .padding(12)
        .modifier(GlassCard())
    }

    /// The events by day (one in progress under today), or why there are none.
    @ViewBuilder private func events(now: Date) -> some View {
        let days = Self.days(model.upcomingEvents, now: now)
        if days.isEmpty {
            VStack(spacing: 6) {
                Text("No upcoming meetings in the next 7 days.")
                if !model.settings.calendar.allTimedEvents {
                    Text("Only events with a meeting link, or in the EchoDiar calendar, are shown.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                }
            }
            .multilineTextAlignment(.center)
            .frame(maxWidth: .infinity)
            .padding(20)
            .modifier(GlassCard())
        } else {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(days, id: \.day) { group in
                    Text(Self.dayTitle(group.day))
                        .font(.caption.weight(.semibold))
                        .foregroundStyle(.secondary)
                        .accessibilityAddTraits(.isHeader)
                        .frame(maxWidth: .infinity)
                        .padding(.top, 4)
                    ForEach(group.events) { event in
                        row(event, now: now)
                    }
                }
            }
        }
    }

    private func row(_ event: CalendarEvent, now: Date) -> some View {
        let skipped = model.settings.calendar.skippedEvents[event.id] != nil
        return HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(verbatim: "\(event.start.formatted(date: .omitted, time: .shortened))–\(event.end.formatted(date: .omitted, time: .shortened))")
                    .font(.callout)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                Text(verbatim: event.title)
                    .font(.headline)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .help(event.title)
                if let service = event.link?.serviceName {
                    Text(verbatim: service)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .opacity(skipped ? 0.5 : 1)
            Spacer(minLength: 8)
            if model.isOwnEvent(event) {
                Button("Edit…") { editor = .edit(event) }
            }
            if event.link != nil {
                Button("Join") { model.openMeetingLink(event) }
                    .help("Open the meeting link")
            }
            Button("Record") { model.startRecording(target: .global, event: event) }
                .disabled(!canRecord(event, now: now))
                .help("Record all system audio, from 15 min before the start")
            HStack(spacing: 6) {
                Text("Skip")
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
                Toggle(isOn: Binding(
                    get: { skipped },
                    set: { model.setEventSkipped(event, $0) }
                )) {
                    Text("Skip")
                }
                .toggleStyle(CompactSwitch())
            }
            .help("Skip this meeting: no prompt and no recording")
        }
        .padding(12)
        .modifier(GlassCard())
    }

    /// Idle, and the event is in progress or starts within 15 min.
    private func canRecord(_ event: CalendarEvent, now: Date) -> Bool {
        !model.isRecording && !model.isStarting
            && event.start.timeIntervalSince(now) <= Self.recordLead && event.end > now
    }

    private struct Day {
        let day: Date
        let events: [CalendarEvent]
    }

    /// Events not over at `now`, by the day they start (an event in progress counts as today's), in order.
    private static func days(_ events: [CalendarEvent], now: Date) -> [Day] {
        let calendar = Calendar.current
        let current = events.filter { $0.end > now }
        let grouped = Dictionary(grouping: current) { calendar.startOfDay(for: max($0.start, now)) }
        return grouped.keys.sorted().map { Day(day: $0, events: grouped[$0] ?? []) }
    }

    private static func dayTitle(_ day: Date) -> String {
        let calendar = Calendar.current
        if calendar.isDateInToday(day) { return String(localized: "Today") }
        if calendar.isDateInTomorrow(day) { return String(localized: "Tomorrow") }
        return day.formatted(date: .long, time: .omitted)
    }
}
