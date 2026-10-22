using System.Diagnostics;
using System.Text.Json;
using EchoDiar.Core.Model;

namespace EchoDiar.Core.Audio;

/// <summary>A session that was still recording or paused when its owner died, after recovery.</summary>
/// <param name="State"><see cref="SessionState.Recovered"/>, or <see cref="SessionState.Failed"/> when the audio file is missing or empty.</param>
/// <param name="Duration">Seconds of audio in the file (0 when failed).</param>
public sealed record RecoveredResult(string Folder, SessionState State, double Duration);

/// <summary>
/// Marks sessions interrupted by a crash or a killed process as recovered (or failed) at launch, before any capture.
/// Only <c>session.json</c> changes (its state), as on macOS.
/// </summary>
public static class SessionRecovery
{
    /// <summary>
    /// Scans <paramref name="root"/> (the conversations folder); never touches a session whose owner is alive or that
    /// this process started, and skips folders it cannot read or write.
    /// </summary>
    /// <param name="isOwnerAlive">Whether the process with this pid, started no later than the session, still runs.</param>
    /// <param name="ownPid">This process's pid.</param>
    /// <param name="ownStartedAt">When this process started; a session with <paramref name="ownPid"/> started at or
    /// after it is this process's own. Null reads the current process's start time when <paramref name="ownPid"/>
    /// is this process; without a start time every session with <paramref name="ownPid"/> counts as its own.</param>
    public static List<RecoveredResult> Recover(
        string root, Func<int, DateTimeOffset, bool> isOwnerAlive, int ownPid, DateTimeOffset? ownStartedAt = null)
    {
        ownStartedAt ??= ownPid == Environment.ProcessId ? CurrentProcessStartTime() : null;
        var results = new List<RecoveredResult>();
        foreach (var folder in Folders(root))
        {
            RecordingSession session;
            try
            {
                session = RecordingSession.Read(folder);
            }
            catch (Exception e) when (IsFileError(e) || e is JsonException)
            {
                continue;
            }
            if (session.State is not (SessionState.Recording or SessionState.Paused)
                || IsStartedByThisProcess(session, ownPid, ownStartedAt)
                || isOwnerAlive(session.Pid, session.StartedAt))
            {
                continue;
            }

            var duration = AudioDuration(folder, session.AudioFileName);
            session.State = duration > 0 ? SessionState.Recovered : SessionState.Failed;
            try
            {
                session.Write(folder);
            }
            catch (Exception e) when (IsFileError(e))
            {
                continue;
            }
            results.Add(new RecoveredResult(folder, session.State, duration));
        }
        return results;
    }

    // Seconds of audio in the session's file; 0 when its name is not a plain name in the folder (session.json is
    // untrusted) or the file is missing, not a regular file, empty or unreadable.
    private static double AudioDuration(string folder, string fileName)
    {
        try
        {
            if (ConversationFiles.AudioPath(folder, fileName) is not { } path) return 0;
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return 0;
            using var reader = new CafFile.Reader(path);
            return reader.Frames > 0 ? reader.Frames / reader.SampleRate : 0;
        }
        // ArgumentException: a path FileStream rejects, so one bad session.json cannot stop the others' recovery.
        catch (Exception e) when (IsFileError(e) || e is InvalidDataException or NotSupportedException or ArgumentException)
        {
            return 0;
        }
    }

    // Started by this process (same pid, at or after its start), so maybe still recording.
    private static bool IsStartedByThisProcess(RecordingSession session, int ownPid, DateTimeOffset? ownStartedAt) =>
        session.Pid == ownPid && (ownStartedAt is not { } start || session.StartedAt >= start);

    // Visible real directories only (no hidden entries, no symlinks or junctions); none when the root is unreadable.
    private static IEnumerable<string> Folders(string root)
    {
        try
        {
            return new DirectoryInfo(root).GetDirectories()
                .Where(d => !d.Name.StartsWith('.')
                    && (d.Attributes & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) == 0)
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception e) when (IsFileError(e))
        {
            return [];
        }
    }

    private static DateTimeOffset? CurrentProcessStartTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime;
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool IsFileError(Exception e) => e is IOException or UnauthorizedAccessException;
}
