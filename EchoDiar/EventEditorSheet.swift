import EchoCore
import EventKit
import SwiftUI

/// Creates, edits or deletes an event of EchoDiar's own calendar; `editing` nil = a new event.
struct EventEditorSheet: View {
    let model: AppModel
    let editing: CalendarEvent?
    @Environment(\.dismiss) private var dismiss

    @State private var draft = CalendarEventDraft(title: "", start: .now, end: .now)
    /// The values as loaded, to tell whether Repeat or End changed; nil until the sheet appears.
    @State private var loaded: CalendarEventDraft?
    @State private var isRecurring = false
    @State private var loadFailed = false
    @State private var error: String?
    @State private var pending: PendingAction?

    private enum PendingAction { case save, delete }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(editing == nil ? "New Event" : "Edit Event").font(.title3.weight(.semibold))
            if loadFailed {
                Text("This event can't be edited here.").foregroundStyle(.secondary)
                Spacer()
            } else {
                form
            }
            if let error {
                Text(error).font(.callout).foregroundStyle(.red)
            }
            buttons
        }
        .padding(20)
        .frame(width: 460, height: 480)
        .buttonBorderShape(.capsule)
        .onAppear(perform: load)
        .alert(pendingTitle, isPresented: Binding(
            get: { pending != nil },
            set: { if !$0 { pending = nil } }
        ), presenting: pending) { action in
            switch action {
            case .save:
                if repeatUnchanged {
                    Button("This Event Only") { save(span: .thisEvent) }
                }
                Button("This and Future Events") { save(span: .futureEvents) }
            case .delete:
                if isRecurring {
                    Button("This Event Only", role: .destructive) { delete(span: .thisEvent) }
                    Button("This and Future Events", role: .destructive) { delete(span: .futureEvents) }
                } else {
                    Button("Delete", role: .destructive) { delete(span: .thisEvent) }
                }
            }
            Button("Cancel", role: .cancel) {}
        }
    }

    private var form: some View {
        Form {
            Section {
                TextField("Title", text: $draft.title)
                DatePicker("Date", selection: day, displayedComponents: .date)
                DatePicker("Start", selection: time(\.start), displayedComponents: .hourAndMinute)
                DatePicker("End", selection: time(\.end), displayedComponents: .hourAndMinute)
                if draft.end <= draft.start {
                    Text("The end must be after the start.").font(.callout).foregroundStyle(.secondary)
                }
            }
            Section {
                TextField("Meeting link", text: $draft.urlText, prompt: Text(verbatim: "https://…"))
                if !draft.urlText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    if draft.url == nil {
                        Text("Use an https link.").font(.callout).foregroundStyle(.secondary)
                    } else if MeetingLink.find(url: draft.url, location: nil, notes: nil) == nil {
                        Text("Not a recognized meeting link: Join won't be offered.")
                            .font(.callout).foregroundStyle(.secondary)
                    }
                }
            }
            Section {
                Picker("Repeat", selection: repeatRule) {
                    Text("None").tag(CalendarEventDraft.Repeat.none)
                    Text("Daily").tag(CalendarEventDraft.Repeat.daily)
                    Text("Weekdays (Mon–Fri)").tag(CalendarEventDraft.Repeat.weekdays)
                    Text("Weekly").tag(CalendarEventDraft.Repeat.weekly)
                    Text("Monthly").tag(CalendarEventDraft.Repeat.monthly)
                    // Only a rule read back from another app can be custom; it is kept unless changed.
                    if loaded?.repeatRule == .custom {
                        Text("Custom").tag(CalendarEventDraft.Repeat.custom)
                    }
                }
                if draft.repeatRule != .none && draft.repeatRule != .custom {
                    Picker("End Repeat", selection: endsOnDate) {
                        Text("Never").tag(false)
                        Text("On date").tag(true)
                    }
                    if let repeatEnd = draft.repeatEnd {
                        DatePicker(
                            "End date",
                            selection: Binding(get: { repeatEnd }, set: { draft.repeatEnd = $0 }),
                            in: Calendar.current.startOfDay(for: draft.start)...,
                            displayedComponents: .date)
                        if repeatEnd < Calendar.current.startOfDay(for: draft.start) {
                            Text("The repeat ends before this date.").font(.callout).foregroundStyle(.secondary)
                        }
                    }
                }
            }
        }
        .formStyle(.grouped)
        .disabled(loaded == nil)
    }

    private var buttons: some View {
        HStack {
            if editing != nil && !loadFailed {
                Button("Delete Event…", role: .destructive) {
                    pending = .delete
                }
            }
            Spacer()
            if loadFailed {
                Button("Close") { dismiss() }
                    .keyboardShortcut(.defaultAction)
            } else {
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Save") {
                    if editing != nil && isRecurring { pending = .save } else { save(span: .thisEvent) }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(!draft.isValid || loaded == nil)
            }
        }
    }

    private var pendingTitle: LocalizedStringKey {
        switch pending {
        case .save: "Save changes to a repeating event?"
        case .delete: isRecurring ? "Delete a repeating event?" : "Delete this event?"
        case nil: ""
        }
    }

    /// Whether Repeat and End are as loaded, so a single occurrence can still be changed.
    private var repeatUnchanged: Bool {
        draft.repeatRule == loaded?.repeatRule && draft.repeatEnd == loaded?.repeatEnd
    }

    // MARK: - Bindings

    /// The event's day: changing it moves start and end to that day, keeping their times.
    private var day: Binding<Date> {
        Binding(get: { draft.start }, set: { newDay in
            let calendar = Calendar.current
            let days = calendar.dateComponents(
                [.day], from: calendar.startOfDay(for: draft.start), to: calendar.startOfDay(for: newDay)
            ).day ?? 0
            guard days != 0 else { return }
            draft.start = calendar.date(byAdding: .day, value: days, to: draft.start) ?? draft.start
            draft.end = calendar.date(byAdding: .day, value: days, to: draft.end) ?? draft.end
        })
    }

    /// A time of day on the start's day, so the event never runs overnight.
    private func time(_ keyPath: WritableKeyPath<CalendarEventDraft, Date>) -> Binding<Date> {
        Binding(get: { draft[keyPath: keyPath] }, set: { newTime in
            let calendar = Calendar.current
            let parts = calendar.dateComponents([.hour, .minute], from: newTime)
            draft[keyPath: keyPath] = calendar.date(
                bySettingHour: parts.hour ?? 0, minute: parts.minute ?? 0, second: 0, of: draft.start) ?? newTime
        })
    }

    /// Repeat; None and Custom clear the series end so a hidden date can't block Save.
    private var repeatRule: Binding<CalendarEventDraft.Repeat> {
        Binding(get: { draft.repeatRule }, set: { rule in
            draft.repeatRule = rule
            if rule == .none || rule == .custom { draft.repeatEnd = nil }
        })
    }

    private var endsOnDate: Binding<Bool> {
        Binding(get: { draft.repeatEnd != nil }, set: { onDate in
            draft.repeatEnd = onDate
                ? Calendar.current.date(byAdding: .month, value: 1, to: draft.start)
                : nil
        })
    }

    // MARK: - Actions

    private func load() {
        guard loaded == nil, !loadFailed else { return }
        if let editing {
            guard let (draft, isRecurring) = model.calendarDraft(for: editing) else {
                loadFailed = true
                return
            }
            self.draft = draft
            self.isRecurring = isRecurring
        } else {
            let start = Self.nextHalfHour(after: .now)
            draft = CalendarEventDraft(title: "", start: start, end: Self.end(after: start))
        }
        loaded = draft
    }

    private func save(span: EKSpan) {
        error = model.saveCalendarEvent(draft, editing: editing, span: span)
        if error == nil { dismiss() }
    }

    private func delete(span: EKSpan) {
        guard let editing else { return }
        error = model.deleteCalendarEvent(editing, span: span)
        if error == nil { dismiss() }
    }

    /// The next :00 or :30 after `date` (seconds dropped).
    private static func nextHalfHour(after date: Date) -> Date {
        let calendar = Calendar.current
        let minute = calendar.component(.minute, from: date)
        let parts = calendar.dateComponents([.year, .month, .day, .hour, .minute], from: date)
        let trimmed = calendar.date(from: parts) ?? date
        return calendar.date(byAdding: .minute, value: minute < 30 ? 30 - minute : 60 - minute, to: trimmed) ?? date
    }

    /// Start + 30 min, kept on the start's day (23:59 at the latest).
    private static func end(after start: Date) -> Date {
        let calendar = Calendar.current
        let end = start.addingTimeInterval(30 * 60)
        guard !calendar.isDate(end, inSameDayAs: start) else { return end }
        return calendar.date(bySettingHour: 23, minute: 59, second: 0, of: start) ?? end
    }
}
