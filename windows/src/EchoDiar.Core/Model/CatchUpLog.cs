using System.Text.Json;
using System.Text.Json.Serialization;
using EchoDiar.Core.Json;

namespace EchoDiar.Core.Model;

/// <summary>
/// One catch-up summary made while recording: the meeting so far, up to the live line <see cref="ThroughLineID"/>
/// (Swift <c>CatchUp</c>). Property defaults are the read defaults of Swift's <c>init(from:)</c> (<see cref="CreatedAt"/>
/// is the Unix epoch), so set <see cref="CreatedAt"/> when creating one.
/// </summary>
public sealed record CatchUp
{
    /// <summary>The longest <see cref="Elapsed"/> and <see cref="ThroughTime"/> read from a file: 7 days.</summary>
    public const double MaxSeconds = 7 * 24 * 3600;

    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("createdAt"), JsonConverter(typeof(Iso8601SecondsConverter))]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UnixEpoch;
    /// <summary>Seconds of recording when it was asked for.</summary>
    [JsonPropertyName("elapsed")] public double Elapsed { get; set; }
    /// <summary>The newest live line it covers; the next incremental catch-up sends the lines after it. -1 = none.</summary>
    [JsonPropertyName("throughLineID")] public int ThroughLineID { get; set; } = -1;
    /// <summary>Seconds from the recording start of that line.</summary>
    [JsonPropertyName("throughTime")] public double ThroughTime { get; set; }
    /// <summary>Made from all the lines, without a previous catch-up (the first one, or the "from the start" setting).</summary>
    [JsonPropertyName("fullPass")] public bool FullPass { get; set; }
    /// <summary>Markdown.</summary>
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    /// <summary>The provider id (provider config id) and model that wrote it.</summary>
    [JsonPropertyName("provider")] public string Provider { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    /// <summary>Stopped before the end: <see cref="Text"/> is partial, and the next catch-up does not build on it.</summary>
    [JsonPropertyName("stopped")] public bool Stopped { get; set; }
}

/// <summary>
/// The <c>catchups.json</c> file format, version 1: a recording's catch-ups, oldest first. Dates are ISO 8601 (whole
/// seconds). Every key is optional when read, an entry that does not decode is skipped, and one whose id repeats an
/// earlier entry's is dropped (the first is kept), so ids are unique.
/// </summary>
public sealed record CatchUpLog
{
    public const string FileName = "catchups.json";

    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("catchUps"), JsonConverter(typeof(LossyCatchUpsConverter))]
    public List<CatchUp> CatchUps { get; set; } = [];

    /// <summary>The newest catch-up that was not stopped: the base of an incremental catch-up.</summary>
    [JsonIgnore] public CatchUp? Base => CatchUps.LastOrDefault(c => !c.Stopped);

    /// <summary>Empty when the file is missing; throws when it is unreadable, so an append never drops the catch-ups it holds.</summary>
    public static CatchUpLog Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : new CatchUpLog();
    }

    public void Append(CatchUp catchUp) => CatchUps.Add(catchUp);

    public void Save(string folder) => AtomicFile.Write(Path.Combine(folder, FileName), Encoded());

    public byte[] Encoded() => SwiftJson.Serialize(this);

    public static CatchUpLog Decode(ReadOnlySpan<byte> utf8) => SwiftJson.Deserialize<CatchUpLog>(utf8);

    /// <summary>
    /// Swift's lossy <c>catchUps</c> decode: an entry that does not decode is skipped, so one bad entry does not lose the
    /// others; a value that is not an array still throws. The file is untrusted: an entry whose id repeats an earlier
    /// one's is dropped, the times are finite within [0, <see cref="CatchUp.MaxSeconds"/>] (a huge one throws in
    /// <see cref="TimeSpan"/>) and a line id is from -1, as Swift's <c>CatchUp.init(from:)</c>.
    /// </summary>
    internal sealed class LossyCatchUpsConverter : JsonConverter<List<CatchUp>>
    {
        public override List<CatchUp> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Expected an array of catch-ups.");
            var catchUps = new List<CatchUp>();
            var seen = new HashSet<Guid>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                try
                {
                    if (element.Deserialize<CatchUp>(options) is not { } catchUp || !seen.Add(catchUp.Id)) continue;
                    catchUp.Elapsed = Seconds(catchUp.Elapsed);
                    catchUp.ThroughTime = Seconds(catchUp.ThroughTime);
                    catchUp.ThroughLineID = Math.Max(catchUp.ThroughLineID, -1);
                    catchUps.Add(catchUp);
                }
                catch (JsonException)
                {
                    // Skipped, as Swift's `try?`.
                }
            }
            return catchUps;
        }

        public override void Write(Utf8JsonWriter writer, List<CatchUp> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, options);

        /// <summary><paramref name="value"/> within [0, <see cref="CatchUp.MaxSeconds"/>]; 0 when it is not finite.</summary>
        private static double Seconds(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, 0, CatchUp.MaxSeconds) : 0;
    }
}

/// <summary>Who spoke a <see cref="CatchUpLine"/>: <c>ToString()</c> is the macOS raw value ("Me", "Others").</summary>
public enum CatchUpSpeaker { Me, Others }

/// <summary>One finalized live line, the input of a catch-up (not stored; Swift <c>CatchUpLine</c>).</summary>
/// <param name="Id">The live line id: increases by one, never reused within a recording.</param>
/// <param name="Start">Seconds from the recording start.</param>
public sealed record CatchUpLine(int Id, double Start, CatchUpSpeaker Speaker, string Text);
