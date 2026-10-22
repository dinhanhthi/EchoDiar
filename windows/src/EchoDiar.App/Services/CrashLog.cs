using System.Globalization;

namespace EchoDiar.App.Services;

/// <summary>
/// Unhandled exceptions, appended with a timestamp to <c>Logs\crash.log</c> under the library root. Past about 1 MB
/// the file is renamed <c>crash.log.1</c> (replacing the older one) and a new one starts. Never throws.
/// </summary>
internal static class CrashLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    public static string PathIn(string root) => Path.Combine(root, "Logs", "crash.log");

    public static void Write(string root, string source, object? error)
    {
        try
        {
            var path = PathIn(root);
            var entry = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:O} {source}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var file = new FileInfo(path);
                if (file.Exists && file.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, entry);
            }
        }
        catch
        {
            // Logging must not add a failure of its own while the app is already failing.
        }
    }
}
