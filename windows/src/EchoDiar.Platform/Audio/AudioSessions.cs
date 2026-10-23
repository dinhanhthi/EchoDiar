using System.Runtime.InteropServices;
using EchoDiar.Core.Audio;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Diagnostics.ToolHelp;
using Windows.Win32.System.Threading;

namespace EchoDiar.Platform.Audio;

/// <summary>
/// The processes with audio sessions on the active render endpoints (the Windows counterpart of macOS
/// AudioProcessList). Every call enumerates afresh and releases each COM object before it returns; nothing is cached
/// across calls or threads. Blocking (a few milliseconds per endpoint): call off the UI thread.
/// <para>
/// A process has a render session (<see cref="ProcessRow.HasRenderSession"/>) when it has any session that has not
/// expired, active or inactive: like macOS, which offers every process with an audio client, an app can be picked
/// before its call audio starts. Playing (<see cref="IsPlayingOutput"/>) means a session in the active state.
/// </para>
/// </summary>
public static class AudioSessions
{
    /// <summary>
    /// The apps with a render session, grouped by <see cref="AudioProcessGrouping.Group"/>, without EchoDiar and its
    /// child processes. Endpoints or sessions that cannot be read (removed meanwhile) are skipped; no audio service
    /// gives an empty list. Paths are empty for processes that deny PROCESS_QUERY_LIMITED_INFORMATION.
    /// </summary>
    public static IReadOnlyList<AudioProcessGroup> Groups()
    {
        var sessions = SessionStates();
        var processes = Snapshot();
        var own = Tree(Environment.ProcessId, processes);
        var rows = processes.Values
            .Where(p => !own.Contains(p.Pid))
            .Select(p => new ProcessRow(p.Pid, p.ParentPid, p.Exe, ImagePath(p.Pid), sessions.ContainsKey(p.Pid)))
            .ToList();
        return AudioProcessGrouping.Group(rows);
    }

    /// <summary>
    /// Whether a process in the tree rooted at <paramref name="rootPid"/> has an active render session
    /// (RecordingEngine.IsTargetPlayingOutput, which calls it inside the writer's lock). Never throws: false when
    /// the sessions or processes cannot be read.
    /// </summary>
    public static bool IsPlayingOutput(int rootPid)
    {
        if (rootPid <= 0) return false;
        try
        {
            var active = SessionStates().Where(s => s.Value).Select(s => s.Key).ToHashSet();
            return active.Count > 0 && Tree(rootPid, Snapshot()).Overlaps(active);
        }
        catch (Exception)
        {
            // Total by contract: an error here must not end the writer's drain.
            return false;
        }
    }

    /// <summary><paramref name="rootPid"/> and its descendants now (WindowFollower); just the root when the snapshot fails.</summary>
    internal static HashSet<int> ProcessTree(int rootPid) => Tree(rootPid, Snapshot());

    internal readonly record struct ProcessEntry(int Pid, int ParentPid, string Exe);

    /// <summary>Pid → whether one of its sessions is active, for every pid with a session that has not expired.</summary>
    private static Dictionary<int, bool> SessionStates()
    {
        var states = new Dictionary<int, bool>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE.DEVICE_STATE_ACTIVE, out devices);
            devices.GetCount(out var count);
            for (uint i = 0; i < count; i++) AddEndpointSessions(devices, i, states);
        }
        catch (COMException)
        {
            // No audio service or endpoint list: what was read so far.
        }
        finally
        {
            if (devices is not null) Marshal.ReleaseComObject(devices);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
        return states;
    }

    private static void AddEndpointSessions(IMMDeviceCollection devices, uint index, Dictionary<int, bool> states)
    {
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            devices.Item(index, out device);
            device.Activate(typeof(IAudioSessionManager2).GUID, CLSCTX.CLSCTX_ALL, null, out var activated);
            manager = (IAudioSessionManager2)activated;
            sessions = manager.GetSessionEnumerator();
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++) AddSession(sessions, i, states);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // The endpoint went away meanwhile: skip it.
        }
        finally
        {
            if (sessions is not null) Marshal.ReleaseComObject(sessions);
            if (manager is not null) Marshal.ReleaseComObject(manager);
            if (device is not null) Marshal.ReleaseComObject(device);
        }
    }

    private static void AddSession(IAudioSessionEnumerator sessions, int index, Dictionary<int, bool> states)
    {
        IAudioSessionControl? session = null;
        try
        {
            sessions.GetSession(index, out session);
            var control = (IAudioSessionControl2)session; // the same wrapper: released once below
            if (control.IsSystemSoundsSession().Value == 0) return; // S_OK: system sounds, no app
            control.GetProcessId(out var pid);
            control.GetState(out var state);
            if (pid == 0 || pid > int.MaxValue || state == AudioSessionState.AudioSessionStateExpired) return;
            var key = (int)pid;
            states[key] = state == AudioSessionState.AudioSessionStateActive || states.GetValueOrDefault(key);
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // The session expired meanwhile: skip it.
        }
        finally
        {
            if (session is not null) Marshal.ReleaseComObject(session);
        }
    }

    /// <summary>Every process (Toolhelp32 snapshot) by pid, with its parent pid and lowercase exe name; empty on failure.</summary>
    internal static unsafe Dictionary<int, ProcessEntry> Snapshot()
    {
        var processes = new Dictionary<int, ProcessEntry>();
        using var snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot.IsInvalid) return processes;
        var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
        for (var more = PInvoke.Process32FirstW(snapshot, ref entry); more; more = PInvoke.Process32NextW(snapshot, ref entry))
        {
            if (entry.th32ProcessID > int.MaxValue || entry.th32ParentProcessID > int.MaxValue) continue;
            var pid = (int)entry.th32ProcessID;
            processes.TryAdd(pid, new ProcessEntry(pid, (int)entry.th32ParentProcessID, entry.szExeFile.ToString().ToLowerInvariant()));
        }
        return processes;
    }

    /// <summary>
    /// <paramref name="rootPid"/> and its descendants by parent pid. The snapshot has no start times, so a process
    /// whose dead parent's pid was reused by a member counts as a child. Pid reuse can make cycles; each pid is visited once.
    /// </summary>
    internal static HashSet<int> Tree(int rootPid, Dictionary<int, ProcessEntry> processes)
    {
        var children = processes.Values.ToLookup(p => p.ParentPid, p => p.Pid);
        var tree = new HashSet<int> { rootPid };
        var pending = new Queue<int>([rootPid]);
        while (pending.TryDequeue(out var pid))
        {
            foreach (var child in children[pid])
            {
                if (tree.Add(child)) pending.Enqueue(child);
            }
        }
        return tree;
    }

    /// <summary>The full image path, or empty when the process denies PROCESS_QUERY_LIMITED_INFORMATION or has exited.</summary>
    private static string ImagePath(int pid)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (process.IsInvalid) return "";
        Span<char> buffer = stackalloc char[1024];
        var size = (uint)buffer.Length;
        return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size)
            ? buffer[..(int)size].ToString()
            : "";
    }
}
