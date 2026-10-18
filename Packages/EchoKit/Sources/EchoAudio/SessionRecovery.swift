import AppKit
import AVFoundation
import EchoCore
import Foundation

/// A session that was still `recording` or `paused` when its owner died, after recovery.
public struct RecoveredResult: Sendable, Equatable {
    public let folder: URL
    /// `.recovered`, or `.failed` when the audio file is missing or empty.
    public let state: RecordingSession.State
    /// Seconds of audio in the file (0 when failed).
    public let duration: Double
}

/// Marks sessions interrupted by a crash or `kill -9` as `recovered` (or `failed`) at launch, before any capture.
public enum SessionRecovery {
    public static let appBundleID = "dinh.thi.EchoDiar"

    /// A live EchoDiar process other than this one. A session with this pid comes from an earlier process
    /// that had the same pid, unless this process started it, which `recover` checks on its own.
    public static let defaultIsOwnerAlive: @Sendable (pid_t) -> Bool = { pid in
        pid != getpid() && NSRunningApplication(processIdentifier: pid)?.bundleIdentifier == appBundleID
    }

    /// When this process started, from the kernel (`sysctl` `KERN_PROC_PID`, `kp_proc.p_starttime`), so reading
    /// it late is fine: recovery runs in a task nothing awaits. Nil if `sysctl` fails.
    static let processStartDate: Date? = {
        var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, getpid()]
        var info = kinfo_proc()
        var size = MemoryLayout<kinfo_proc>.stride
        guard sysctl(&mib, u_int(mib.count), &info, &size, nil, 0) == 0, size > 0 else { return nil }
        let start = info.kp_proc.p_starttime
        return Date(timeIntervalSince1970: Double(start.tv_sec) + Double(start.tv_usec) / 1_000_000)
    }()

    /// Started by this process (same pid, at or after its start), so maybe still recording.
    /// Without a start time every session with this pid counts.
    static func isStartedByThisProcess(_ session: RecordingSession) -> Bool {
        guard session.pid == getpid() else { return false }
        guard let start = processStartDate else { return true }
        return session.startedAt >= start
    }

    /// Scans `root`; never touches a session whose owner is alive or that this process started,
    /// and skips folders it cannot read or write.
    @discardableResult
    public static func recover(
        root: URL = RecordingSession.conversationsRoot,
        isOwnerAlive: (pid_t) -> Bool = defaultIsOwnerAlive
    ) -> [RecoveredResult] {
        let folders = (try? FileManager.default.contentsOfDirectory(
            at: root, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
        return folders.compactMap { folder in
            guard (try? folder.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true,
                  var session = try? RecordingSession.read(from: folder),
                  session.state == .recording || session.state == .paused,
                  !isStartedByThisProcess(session),
                  !isOwnerAlive(session.pid) else { return nil }
            let duration = audioDuration(of: folder.appendingPathComponent(session.audioFileName))
            session.state = duration > 0 ? .recovered : .failed
            do {
                try session.write(to: folder)
            } catch {
                return nil
            }
            return RecoveredResult(folder: folder, state: session.state, duration: duration)
        }
    }

    /// Seconds of audio in the file; 0 when it is missing, empty or unreadable.
    static func audioDuration(of url: URL) -> Double {
        guard let file = try? AVAudioFile(forReading: url), file.length > 0, file.fileFormat.sampleRate > 0 else { return 0 }
        return Double(file.length) / file.fileFormat.sampleRate
    }
}
