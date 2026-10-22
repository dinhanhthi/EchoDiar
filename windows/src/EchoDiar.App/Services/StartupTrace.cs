using System.Diagnostics;
using System.Globalization;

namespace EchoDiar.App.Services;

/// <summary>
/// DEBUG builds only (calls vanish elsewhere, arguments included): the launch's events, one per line, in
/// <c>Logs\startup.log</c> under the library root, truncated at each launch. A line is the milliseconds since the
/// process started, the managed thread id, its apartment (STA is the UI thread) and the event. The file stays open
/// for the process (shared for reading only, so a second instance's redirected launch leaves it alone), each line
/// flushed, capped at <see cref="MaxLines"/>. Never throws.
/// </summary>
internal static class StartupTrace
{
#if DEBUG
    private const int MaxLines = 5000;
    private static readonly Lock Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static StreamWriter? _writer;
    private static double _baseMs;
    private static int _lines;
#endif

    public static string PathIn(string root) => Path.Combine(root, "Logs", "startup.log");

    /// <summary>Opens the log, emptied; once, first thing in the App constructor.</summary>
    [Conditional("DEBUG")]
    public static void Start(string root)
    {
#if DEBUG
        try
        {
            using (var process = Process.GetCurrentProcess())
            {
                _baseMs = (DateTime.Now - process.StartTime).TotalMilliseconds - Clock.Elapsed.TotalMilliseconds;
            }
        }
        catch
        {
            // Without a start time the clock counts from this class's first use.
        }
        try
        {
            var path = PathIn(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            lock (Gate) _writer = new StreamWriter(stream) { AutoFlush = true };
            Write($"launch pid {Environment.ProcessId}, {DateTimeOffset.Now:O}, args [{string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}]");
        }
        catch
        {
            // Another instance holds the file, or the folder is not writable: no trace for this one.
        }
#endif
    }

    [Conditional("DEBUG")]
    public static void Write(string message)
    {
#if DEBUG
        try
        {
            var ms = _baseMs + Clock.Elapsed.TotalMilliseconds;
            var thread = Thread.CurrentThread;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{ms,9:0.0} t{thread.ManagedThreadId,-3} {thread.GetApartmentState(),-7} {message}");
            lock (Gate)
            {
                if (_writer is null || _lines >= MaxLines) return;
                _lines++;
                _writer.WriteLine(_lines == MaxLines ? "(trace capped)" : line);
            }
        }
        catch
        {
            // Tracing must not change what it traces.
        }
#endif
    }
}
