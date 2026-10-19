import Foundation

/// Decides when a recording should end. Pure: the caller feeds timestamped inputs with `record(_:at:)` and asks
/// `evaluate(now:)` what to do; nothing here reads the clock.
///
/// Rules:
/// - Max duration (wall clock from `startedAt`) always stops, before any snooze is consulted, so a recording cannot
///   run forever unattended.
/// - System sleep and disk full stop at once.
/// - Source app quit, source input and output inactive, both channels silent, screen locked and input route lost
///   prompt with a countdown (default action Stop). "Keep recording" snoozes that trigger for `snoozeDuration`;
///   a condition that still holds afterwards prompts again.
/// - Source inactivity is ignored once the source app quit (its own prompt covers it).
/// - The recording starts as silent: the silence prompt fires unless `audible(true)` arrives within the threshold.
/// - `evaluate` returns the same prompt on every call while its condition holds; the caller dedupes, and dismisses
///   the prompt when the result drops back to `.none`.
public struct MeetingEndMonitor: Sendable {
    /// Thresholds, stored in the settings file. Every key is optional when read.
    public struct Configuration: Codable, Equatable, Sendable {
        /// Seconds the source app's input and output must both be inactive; clamped to `sourceInactiveRange`.
        public var sourceInactiveSeconds: Int
        /// Minutes both channels must be silent; clamped to `silenceRange`.
        public var silenceMinutes: Int
        /// Hard cap on the recording length, in minutes; clamped to `maxDurationRange`.
        public var maxDurationMinutes: Int

        public static let sourceInactiveRange = 20...60
        public static let silenceRange = 1...120
        public static let maxDurationRange = 1...1_440

        public init(sourceInactiveSeconds: Int = 25, silenceMinutes: Int = 10, maxDurationMinutes: Int = 240) {
            self.sourceInactiveSeconds = sourceInactiveSeconds
            self.silenceMinutes = silenceMinutes
            self.maxDurationMinutes = maxDurationMinutes
        }

        private enum CodingKeys: String, CodingKey {
            case sourceInactiveSeconds, silenceMinutes, maxDurationMinutes
        }

        public init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            sourceInactiveSeconds = Self.clamped(
                try container.decodeIfPresent(Int.self, forKey: .sourceInactiveSeconds) ?? 25, to: Self.sourceInactiveRange)
            silenceMinutes = Self.clamped(
                try container.decodeIfPresent(Int.self, forKey: .silenceMinutes) ?? 10, to: Self.silenceRange)
            maxDurationMinutes = Self.clamped(
                try container.decodeIfPresent(Int.self, forKey: .maxDurationMinutes) ?? 240, to: Self.maxDurationRange)
        }

        /// The values in force, clamped again at use: a configuration built in code is not clamped.
        public var effectiveSourceInactiveSeconds: Int { Self.clamped(sourceInactiveSeconds, to: Self.sourceInactiveRange) }
        public var effectiveSilenceMinutes: Int { Self.clamped(silenceMinutes, to: Self.silenceRange) }
        public var effectiveMaxDurationMinutes: Int { Self.clamped(maxDurationMinutes, to: Self.maxDurationRange) }

        private static func clamped(_ value: Int, to range: ClosedRange<Int>) -> Int {
            min(max(value, range.lowerBound), range.upperBound)
        }
    }

    public enum Input: Equatable, Sendable {
        /// The source (meeting) app's processes are gone.
        case sourceExited
        /// The source app's input or output is running (`false`: both stopped).
        case sourceActive(Bool)
        /// The mic or system channel is above the silence floor (`false`: both silent).
        case audible(Bool)
        case screenLocked(Bool)
        /// The input device went away (AirPods disconnect); fed only when the route-change setting asks to stop.
        case inputRouteLost
        case systemSleep
        case diskFull
    }

    public enum Reason: String, Codable, Sendable {
        case sourceExited, sourceInactive, silence, screenLocked, inputRouteLost, systemSleep, diskFull, maxDuration
    }

    public enum Action: Equatable, Sendable {
        case none
        /// Ask, then stop when `countdown` seconds pass without "Keep recording".
        case promptStop(reason: Reason, countdown: Int)
        case stopNow(reason: Reason)
    }

    public static let promptCountdown = 15
    public static let snoozeDuration: TimeInterval = 10 * 60

    public let configuration: Configuration
    public let startedAt: Date

    private var sourceExited = false
    private var sourceInactiveSince: Date?
    private var silentSince: Date?
    private var screenLocked = false
    private var inputRouteLost = false
    private var stopReason: Reason?
    private var snoozedUntil: [Reason: Date] = [:]

    public init(configuration: Configuration = Configuration(), startedAt: Date) {
        self.configuration = configuration
        self.startedAt = startedAt
        silentSince = startedAt
    }

    public mutating func record(_ input: Input, at time: Date) {
        switch input {
        case .sourceExited: sourceExited = true
        case .sourceActive(let active): sourceInactiveSince = active ? nil : (sourceInactiveSince ?? time)
        case .audible(let audible): silentSince = audible ? nil : (silentSince ?? time)
        case .screenLocked(let locked): screenLocked = locked
        case .inputRouteLost: if !isSnoozed(.inputRouteLost, at: time) { inputRouteLost = true }
        case .systemSleep: stopReason = stopReason ?? .systemSleep
        case .diskFull: stopReason = stopReason ?? .diskFull
        }
    }

    /// "Keep recording" on the prompt for `reason`: that trigger stays quiet for `snoozeDuration`.
    public mutating func keepRecording(_ reason: Reason, now: Date) {
        snoozedUntil[reason] = now.addingTimeInterval(Self.snoozeDuration)
        if reason == .inputRouteLost { inputRouteLost = false }
    }

    public func evaluate(now: Date) -> Action {
        if now.timeIntervalSince(startedAt) >= TimeInterval(configuration.effectiveMaxDurationMinutes) * 60 {
            return .stopNow(reason: .maxDuration)
        }
        if let stopReason { return .stopNow(reason: stopReason) }
        let inactiveThreshold = TimeInterval(configuration.effectiveSourceInactiveSeconds)
        let silenceThreshold = TimeInterval(configuration.effectiveSilenceMinutes) * 60
        let triggers: [(Reason, Bool)] = [
            (.sourceExited, sourceExited),
            (.inputRouteLost, inputRouteLost),
            (.screenLocked, screenLocked),
            (.sourceInactive, !sourceExited && Self.held(since: sourceInactiveSince, for: inactiveThreshold, at: now)),
            (.silence, Self.held(since: silentSince, for: silenceThreshold, at: now)),
        ]
        for (reason, fired) in triggers where fired && !isSnoozed(reason, at: now) {
            return .promptStop(reason: reason, countdown: Self.promptCountdown)
        }
        return .none
    }

    private func isSnoozed(_ reason: Reason, at time: Date) -> Bool {
        snoozedUntil[reason].map { time < $0 } ?? false
    }

    private static func held(since start: Date?, for threshold: TimeInterval, at now: Date) -> Bool {
        start.map { now.timeIntervalSince($0) >= threshold } ?? false
    }
}
