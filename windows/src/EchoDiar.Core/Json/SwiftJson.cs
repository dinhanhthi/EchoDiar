using System.Collections;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace EchoDiar.Core.Json;

/// <summary>
/// JSON as the macOS app's <c>JSONEncoder</c> (<c>[.prettyPrinted, .sortedKeys]</c>) and <c>JSONDecoder</c> use it:
/// non-ASCII written as UTF-8, nil optionals omitted, keys sorted, case-sensitive keys, unknown keys ignored.
/// Identity is semantic, not byte for byte (Swift writes <c>"k" : v</c> and escapes <c>/</c>).
/// </summary>
public static class SwiftJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            WriteIndented = true,
            RespectNullableAnnotations = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ReadNullsAsSwift } },
            Converters = { new UpperGuidConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// <c>null</c> as Swift decodes it: on a key that is optional but not nullable here (<c>decodeIfPresent ?? default</c>)
    /// it keeps the default; in an array or dictionary of non-optionals it throws. A required key that is not nullable
    /// already throws (<see cref="JsonSerializerOptions.RespectNullableAnnotations"/>), as Swift's <c>decode</c> does.
    /// </summary>
    private static void ReadNullsAsSwift(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object) return;
        foreach (var property in info.Properties)
        {
            if (property.Set is not { } set || property.PropertyType.IsValueType) continue;
            var keepsDefault = !property.IsSetNullable && !property.IsRequired;
            if (keepsDefault) property.IsSetNullable = true;
            property.Set = (target, value) =>
            {
                if (value is null && keepsDefault) return;
                if (value is not null) RejectNullItems(value);
                set(target, value);
            };
        }
    }

    private static void RejectNullItems(object value)
    {
        var items = value switch
        {
            IDictionary dictionary => dictionary.Values,
            string => null,
            IEnumerable enumerable => enumerable,
            _ => null,
        };
        if (items is null) return;
        foreach (var item in items)
        {
            if (item is null) throw new JsonException("Null array element or dictionary value.");
        }
    }

    /// <summary>UTF-8 JSON, indented, with object keys sorted (ordinal) at every level.</summary>
    public static byte[] Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteSorted(writer, node);
        }
        return buffer.ToArray();
    }

    /// <summary>Throws <see cref="JsonException"/> when the JSON does not decode (or is <c>null</c>).</summary>
    public static T Deserialize<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options) ?? throw new JsonException($"null {typeof(T).Name}");

    /// <summary>Writes <paramref name="value"/> to <paramref name="path"/> atomically.</summary>
    public static void Write<T>(string path, T value) => AtomicFile.Write(path, Serialize(value));

    public static T Read<T>(string path) => Deserialize<T>(File.ReadAllBytes(path));

    private static void WriteSorted(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, child) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteSorted(writer, child);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var child in array) WriteSorted(writer, child);
                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}

/// <summary>
/// Swift's default <c>Date</c> coding: a JSON number of seconds since 2001-01-01T00:00:00Z (may be fractional).
/// Computed from ticks, so a macOS-written number decodes and re-encodes to the same double.
/// </summary>
public sealed class AppleDateConverter : JsonConverter<DateTimeOffset>
{
    private static readonly long ReferenceTicks = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected a number of seconds since 2001.");
        var seconds = reader.GetDouble();
        // DateTimeOffset spans about ±3.2e11 s around 2001; the bound keeps the tick math below in range of a long.
        if (!(Math.Abs(seconds) < 4e11)) throw new JsonException("Date out of range.");
        // Whole and fractional seconds apart: seconds * 1e7 at today's magnitude (~8e15) can be a tick off.
        var whole = Math.Floor(seconds);
        var utcTicks = ReferenceTicks + (long)whole * TimeSpan.TicksPerSecond
            + (long)Math.Round((seconds - whole) * TimeSpan.TicksPerSecond);
        if (utcTicks < DateTimeOffset.MinValue.UtcTicks || utcTicks > DateTimeOffset.MaxValue.UtcTicks)
            throw new JsonException("Date out of range.");
        return new DateTimeOffset(utcTicks, TimeSpan.Zero);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value.UtcTicks - ReferenceTicks) / (double)TimeSpan.TicksPerSecond);
}

/// <summary>Swift's <c>[String: Date]</c> with the default date coding: an object of <see cref="AppleDateConverter"/> values.</summary>
public sealed class AppleDateDictionaryConverter : JsonConverter<Dictionary<string, DateTimeOffset>>
{
    private static readonly AppleDateConverter Date = new();

    public override Dictionary<string, DateTimeOffset> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected an object of dates.");
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            reader.Read();
            result[key] = Date.Read(ref reader, typeof(DateTimeOffset), options);
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, DateTimeOffset> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, date) in value)
        {
            writer.WritePropertyName(key);
            Date.Write(writer, date, options);
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// Swift's <c>.iso8601</c> date strategy: writes <c>yyyy-MM-ddTHH:mm:ssZ</c> in UTC, fractions truncated; reads
/// with or without fractions, with <c>Z</c> or an offset. A date without a zone is rejected.
/// </summary>
public sealed class Iso8601SecondsConverter : JsonConverter<DateTimeOffset>
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected an ISO 8601 string.");
        if (DateTimeOffset.TryParseExact(reader.GetString(), Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return date;
        throw new JsonException("Invalid ISO 8601 date.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        var utc = value.UtcDateTime;
        var seconds = new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        writer.WriteStringValue(seconds.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}

/// <summary>Swift's <c>UUID.uuidString</c>: writes uppercase 8-4-4-4-12; reads that form in any case.</summary>
public sealed class UpperGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Guid.TryParseExact(reader.GetString(), "D", out var id)) return id;
        throw new JsonException("Expected a UUID string.");
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());

    public override Guid ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString("D").ToUpperInvariant());
}

/// <summary>
/// A Swift <c>String</c>-backed enum with a custom <c>init(from:)</c> that maps an unrecognized raw value to
/// <paramref name="fallback"/>. Raw values are the camelCase member names (<c>CompressionFailed</c> is
/// <c>"compressionFailed"</c>). A non-string value still throws, as in Swift. Subclass it per enum (attributes need
/// a parameterless converter).
/// </summary>
public class TolerantEnumConverter<T>(T fallback) : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<T, string> Names =
        Enum.GetValues<T>().ToDictionary(v => v, v => JsonNamingPolicy.CamelCase.ConvertName(v.ToString()));
    private static readonly Dictionary<string, T> Values = Names.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"Expected a {typeof(T).Name} string.");
        return Values.TryGetValue(reader.GetString()!, out var value) ? value : fallback;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Names.TryGetValue(value, out var name) ? name : Names[fallback]);
}

/// <summary>Swift's <c>Data.write(to:options: .atomic)</c>: a temp file in the same folder, then a rename over the target.</summary>
public static class AtomicFile
{
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full) ?? throw new ArgumentException("No parent folder.", nameof(path));
        var temp = Path.Combine(folder, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
