import EchoCore
import Foundation
import Testing

/// Guards when a recording is asked to stop or stopped, and that nothing keeps it running past the max duration.
struct MeetingEndMonitorTests {
    private let start = Date(timeIntervalSinceReferenceDate: 0)

    private func at(_ seconds: TimeInterval) -> Date { start.addingTimeInterval(seconds) }

    private func monitor() -> MeetingEndMonitor {
        var monitor = MeetingEndMonitor(startedAt: start)
        monitor.record(.audible(true), at: start)
        return monitor
    }

    @Test func meetingAppQuittingPromptsWithCountdownAndKeepRecordingSnoozesTenMinutes() {
        var monitor = monitor()
        monitor.record(.sourceExited, at: at(60))
        #expect(monitor.evaluate(now: at(60)) == .promptStop(reason: .sourceExited, countdown: 15))

        monitor.keepRecording(.sourceExited, now: at(70))
        #expect(monitor.evaluate(now: at(70 + 599)) == MeetingEndMonitor.Action.none)
        #expect(monitor.evaluate(now: at(70 + 600)) == .promptStop(reason: .sourceExited, countdown: 15))
    }

    @Test func silencePastThresholdPrompts() {
        var monitor = monitor()
        monitor.record(.audible(false), at: at(100))
        #expect(monitor.evaluate(now: at(100 + 599)) == MeetingEndMonitor.Action.none)
        #expect(monitor.evaluate(now: at(100 + 600)) == .promptStop(reason: .silence, countdown: 15))
    }

    @Test func systemQuietAfterSoundWithTheMeetingMicOffPromptsCallEnded() {
        var monitor = monitor()
        monitor.record(.sourceInputActive(true), at: start)
        monitor.record(.systemAudible(true), at: at(10))
        monitor.record(.systemAudible(false), at: at(100))
        monitor.record(.sourceInputActive(false), at: at(110))
        #expect(monitor.evaluate(now: at(110 + 59)) == MeetingEndMonitor.Action.none)
        #expect(monitor.evaluate(now: at(110 + 60)) == .promptStop(reason: .callEnded, countdown: 15))
    }

    @Test func pausedTimeIsNotCallEndedSilence() {
        var monitor = monitor()
        monitor.record(.sourceInputActive(true), at: start)
        monitor.record(.systemAudible(true), at: at(10))
        monitor.record(.systemAudible(false), at: at(100))
        monitor.record(.sourceInputActive(false), at: at(110))
        monitor.record(.paused(true), at: at(120))
        #expect(monitor.evaluate(now: at(250)) == MeetingEndMonitor.Action.none)
        monitor.record(.paused(false), at: at(300))
        #expect(monitor.evaluate(now: at(300 + 59)) == MeetingEndMonitor.Action.none)
        #expect(monitor.evaluate(now: at(300 + 60)) == .promptStop(reason: .callEnded, countdown: 15))
    }

    @Test func callEndedNeedsSoundFirstAndTheMeetingMicOff() {
        var neverHeard = monitor()
        neverHeard.record(.systemAudible(false), at: at(10))
        neverHeard.record(.sourceInputActive(false), at: at(10))
        #expect(neverHeard.evaluate(now: at(300)) == MeetingEndMonitor.Action.none)

        var micStillOn = monitor()
        micStillOn.record(.sourceInputActive(true), at: start)
        micStillOn.record(.systemAudible(true), at: at(10))
        micStillOn.record(.systemAudible(false), at: at(20))
        #expect(micStillOn.evaluate(now: at(300)) == MeetingEndMonitor.Action.none)
    }

    @Test func sleepStopsNow() {
        var monitor = monitor()
        monitor.record(.systemSleep, at: at(30))
        #expect(monitor.evaluate(now: at(30)) == .stopNow(reason: .systemSleep))
    }

    @Test func maxDurationStopsNowEvenWhenSnoozed() {
        var monitor = monitor()
        let snoozedAt: TimeInterval = 4 * 3600 - 300
        monitor.record(.sourceExited, at: at(snoozedAt))
        monitor.keepRecording(.sourceExited, now: at(snoozedAt))
        #expect(monitor.evaluate(now: at(4 * 3600 - 1)) == MeetingEndMonitor.Action.none)
        #expect(monitor.evaluate(now: at(4 * 3600)) == .stopNow(reason: .maxDuration))
    }

    @Test func absurdStoredConfigurationIsClampedAndMaxDurationStillStops() throws {
        let json = #"{"sourceInactiveSeconds": -1, "callEndedSeconds": 1, "silenceMinutes": 0, "maxDurationMinutes": 9223372036854775807}"#
        let configuration = try JSONDecoder().decode(MeetingEndMonitor.Configuration.self, from: Data(json.utf8))
        #expect(configuration == MeetingEndMonitor.Configuration(
            sourceInactiveSeconds: 20, callEndedSeconds: 30, silenceMinutes: 1, maxDurationMinutes: 1_440))

        var monitor = MeetingEndMonitor(configuration: configuration, startedAt: start)
        monitor.record(.audible(true), at: start)
        #expect(monitor.evaluate(now: at(1_440 * 60 - 1)) == MeetingEndMonitor.Action.none)
        #expect(monitor.evaluate(now: at(1_440 * 60)) == .stopNow(reason: .maxDuration))
    }
}
