#if DEBUG
import AppKit
import EchoAudio
import EchoTranscribe
import Foundation

/// What one `--measure` run records for `result.json` (see `AppModel.runMeasureHarness`). Times are seconds after
/// Stop. Phases: `recording` until Stop (the feed with `--incremental on`), `finishing` while the recording
/// transcriber's `finish()` runs (incremental only), then in `finishRecording`'s order: compress first (`compressing`
/// until the pipeline's first event, then one per pipeline step), or transcribe first (`submitting` until the
/// pipeline's first event, one per pipeline step, `compressing` from `finished` until the `compressed` milestone, then
/// `done`). A step seen twice adds up; each phase keeps its max `phys_footprint` (1 s sampler plus a sample at every
/// change) and this process's CPU time from `getrusage` (work done in other processes, such as CoreML's, is not
/// counted).
@MainActor
final class MeasureRun {
    private struct Phase {
        let name: String
        var maxFootprint: UInt64 = 0
        var cpuSeconds: Double = 0
    }

    let out: URL
    /// Every field written to `result.json`, besides those this class computes.
    var fields: [String: Any] = [:]
    private(set) var stop: Date?
    private var phases: [Phase] = [Phase(name: "recording")]
    private var current = 0
    private var cpuMark = MeasureRun.cpuSeconds()
    private var steps: [[String: Any]] = []
    private var step: JobProgress.Step?
    private var stepFraction = 0.0
    private var firstFraction: [String: Any]?
    private var milestones: [[String: Any]] = []
    private var sampler: Task<Void, Never>?
    /// The `failed` or `needsModel` step's detail.
    private(set) var failureDetail: String?
    private(set) var stopToCompressed: Double?
    private(set) var stopToTranscript: Double?
    /// Stop until the recording transcriber's `finish()` returned (incremental only).
    private(set) var stopToPrior: Double?

    init(out: URL) {
        self.out = out
        fields["system"] = ["hwModel": Self.hardwareModel(), "macOS": ProcessInfo.processInfo.operatingSystemVersionString]
        fields["start"] = Self.conditions()
        sampler = Task { [weak self] in
            while !Task.isCancelled {
                self?.sample()
                try? await Task.sleep(for: .seconds(1))
            }
        }
    }

    /// Seconds since Stop; 0 before it.
    private var elapsed: Double { stop.map { Date().timeIntervalSince($0) } ?? 0 }

    /// `transcribesFirst`: `finishRecording` transcribes the CAF before compressing it.
    func markStop(transcribesFirst: Bool) {
        stop = Date()
        enter(transcribesFirst ? "submitting" : "compressing")
    }

    /// Stop with a recording transcriber: `finishing` until `markPriorReady(transcribesFirst:)`.
    func markStopFinishing() {
        stop = Date()
        enter("finishing")
    }

    /// The recording transcriber's `finish()` returned; `transcribesFirst` as in `markStop(transcribesFirst:)`.
    func markPriorReady(transcribesFirst: Bool) {
        stopToPrior = elapsed
        enter(transcribesFirst ? "submitting" : "compressing")
    }

    func progress(_ progress: JobProgress) {
        if progress.step != step {
            step = progress.step
            stepFraction = progress.fraction
            steps.append(["step": progress.step.rawValue, "t": elapsed, "fraction": progress.fraction])
            // Transcribe first: the compression follows the transcription.
            enter(progress.step == .finished && stopToCompressed == nil ? "compressing" : progress.step.rawValue)
            switch progress.step {
            case .finished: stopToTranscript = elapsed
            case .failed, .needsModel: failureDetail = progress.detail ?? progress.step.rawValue
            default: break
            }
        } else if firstFraction == nil, progress.fraction > stepFraction,
                  progress.step == .transcribingMic || progress.step == .transcribingSystem {
            // "Load + first chunk": the engine's first progress report after the model load.
            firstFraction = ["step": progress.step.rawValue, "t": elapsed, "fraction": progress.fraction]
        }
    }

    func milestone(_ milestone: AppModel.FinishMilestone) {
        switch milestone {
        case .compressed(let result):
            stopToCompressed = elapsed
            if stopToTranscript != nil { enter("done") }
            milestones.append([
                "milestone": "compressed", "t": elapsed, "outputFileNames": result.outputFileNames,
                "failure": result.failure.map { String(describing: $0) } ?? NSNull(),
            ])
        case .transcribed(let succeeded):
            milestones.append(["milestone": "transcribed", "t": elapsed, "succeeded": succeeded])
        }
    }

    /// Writes `result.json` (with `error` when non-nil) to `out`.
    func write(error: String?) throws {
        sampler?.cancel()
        sample()
        account()
        var result = fields
        result["end"] = Self.conditions()
        result["stopAt"] = stop.map { ISO8601DateFormatter().string(from: $0) } ?? NSNull()
        result["steps"] = steps
        result["loadAndFirstChunk"] = firstFraction ?? NSNull()
        result["milestones"] = milestones
        result["stopToCompressed"] = stopToCompressed ?? NSNull()
        result["stopToTranscript"] = stopToTranscript ?? NSNull()
        result["phases"] = phases.map {
            ["phase": $0.name, "maxFootprintMB": Self.megabytes($0.maxFootprint), "cpuSeconds": $0.cpuSeconds]
        }
        result["maxFootprintMB"] = Self.megabytes(phases.map(\.maxFootprint).max() ?? 0)
        result["error"] = error ?? NSNull()
        let data = try JSONSerialization.data(withJSONObject: result, options: [.prettyPrinted, .sortedKeys])
        try data.write(to: out.appendingPathComponent("result.json"), options: .atomic)
    }

    private func enter(_ name: String) {
        sample()
        account()
        if let index = phases.firstIndex(where: { $0.name == name }) {
            current = index
        } else {
            phases.append(Phase(name: name))
            current = phases.count - 1
        }
    }

    private func sample() {
        phases[current].maxFootprint = max(phases[current].maxFootprint, Self.physFootprint())
    }

    /// Adds the CPU time since the last change to the current phase.
    private func account() {
        let now = Self.cpuSeconds()
        phases[current].cpuSeconds += now - cpuMark
        cpuMark = now
    }

    // MARK: - System

    private static func megabytes(_ bytes: UInt64) -> Double { (Double(bytes) / 1_048_576 * 10).rounded() / 10 }

    private static func conditions() -> [String: Any] {
        let thermal = switch ProcessInfo.processInfo.thermalState {
        case .nominal: "nominal"
        case .fair: "fair"
        case .serious: "serious"
        case .critical: "critical"
        @unknown default: "unknown"
        }
        return ["thermalState": thermal, "lowPowerMode": ProcessInfo.processInfo.isLowPowerModeEnabled]
    }

    private static func hardwareModel() -> String {
        var size = 0
        guard sysctlbyname("hw.model", nil, &size, nil, 0) == 0, size > 0 else { return "unknown" }
        var bytes = [CChar](repeating: 0, count: size)
        guard sysctlbyname("hw.model", &bytes, &size, nil, 0) == 0 else { return "unknown" }
        return bytes.withUnsafeBufferPointer { String(cString: $0.baseAddress!) }
    }

    /// This process's user + system CPU time.
    private static func cpuSeconds() -> Double {
        var usage = rusage()
        getrusage(RUSAGE_SELF, &usage)
        let user = Double(usage.ru_utime.tv_sec) + Double(usage.ru_utime.tv_usec) / 1_000_000
        let system = Double(usage.ru_stime.tv_sec) + Double(usage.ru_stime.tv_usec) / 1_000_000
        return user + system
    }

    /// `phys_footprint` (Activity Monitor's Memory), 0 when `task_info` fails.
    private static func physFootprint() -> UInt64 {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let status = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        return status == KERN_SUCCESS ? info.phys_footprint : 0
    }

    /// Other running EchoDiar processes (any build): a concurrent job would skew the run.
    static func otherInstances() -> Int {
        NSRunningApplication.runningApplications(withBundleIdentifier: SessionRecovery.appBundleID)
            .filter { $0.processIdentifier != getpid() }.count
    }
}
#endif
