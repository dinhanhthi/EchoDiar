using System.Globalization;
using System.Text.Json;
using EchoDiar.Core.Json;

namespace EchoDiar.Core.Settings;

/// <summary>Reads and writes <c>settings.json</c>; every file access runs under one lock.</summary>
public sealed class SettingsStore(AppPaths paths)
{
    private readonly Lock _gate = new();

    public string FilePath => paths.SettingsFile;

    /// <summary>
    /// The saved settings, or defaults when the file is missing. An unreadable file is moved aside to
    /// <c>settings.json.unreadable-&lt;now&gt;</c> (never overwritten), then defaults are returned.
    /// </summary>
    public AppSettings Load() => Load(DateTimeOffset.Now);

    public AppSettings Load(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            try
            {
                return SwiftJson.Read<AppSettings>(FilePath);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                try { File.Move(FilePath, AsidePath(now)); }
                catch (Exception m) when (m is IOException or UnauthorizedAccessException) { }
                return new AppSettings();
            }
        }
    }

    /// <summary>Writes atomically, creating the folder.</summary>
    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var data = SwiftJson.Serialize(settings);
            Directory.CreateDirectory(paths.Root);
            AtomicFile.Write(FilePath, data);
        }
    }

    /// <summary><c>settings.json.unreadable-&lt;yyyyMMdd-HHmmss-fff, local time&gt;</c>, with <c>-1</c>, <c>-2</c>, ... when taken.</summary>
    private string AsidePath(DateTimeOffset now)
    {
        var stamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var basePath = $"{FilePath}.unreadable-{stamp}";
        var path = basePath;
        for (var n = 1; File.Exists(path) || Directory.Exists(path); n++) path = $"{basePath}-{n}";
        return path;
    }
}
