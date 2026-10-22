using System.Text.Json;
using System.Text.Json.Serialization;
using EchoDiar.Core.Json;

namespace EchoDiar.Core.Settings;

/// <summary>Swift <c>ProviderConfig.Kind</c>. An unknown value reads as <see cref="OpenAICompatible"/>.</summary>
[JsonConverter(typeof(ProviderKindConverter))]
public enum ProviderKind
{
    OpenAICompatible, Anthropic, Cli, Local,
    /// <summary>A speech-to-text-only API (ElevenLabs, Deepgram, AssemblyAI): <c>baseURL</c> and <c>authStyle</c> are unused.</summary>
    SpeechToText,
}

public sealed class ProviderKindConverter() : TolerantEnumConverter<ProviderKind>(ProviderKind.OpenAICompatible);

/// <summary>Swift <c>ProviderConfig.AuthStyle</c>. An unknown value reads as <see cref="None"/>: never put the key in a guessed header.</summary>
[JsonConverter(typeof(AuthStyleConverter))]
public enum AuthStyle
{
    /// <summary><c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    Bearer,
    /// <summary>Azure OpenAI <c>api-key: &lt;key&gt;</c>.</summary>
    ApiKeyHeader,
    /// <summary>Anthropic <c>x-api-key: &lt;key&gt;</c>.</summary>
    XApiKey,
    None,
}

public sealed class AuthStyleConverter() : TolerantEnumConverter<AuthStyle>(AuthStyle.None);

/// <summary>
/// One configured AI provider in <see cref="AppSettings.Providers"/>. Its API key is never part of this value.
/// Read tolerantly: only <c>id</c> is required, and a missing, malformed or unknown value of any other key falls back
/// to its default, so one bad entry cannot fail the whole settings read.
/// </summary>
[JsonConverter(typeof(ProviderConfigConverter))]
public sealed record ProviderConfig
{
    /// <summary>The preset slug (<c>openai</c>, <c>ollama</c>, <c>claude-cli</c>, ...) for presets; a UUID string for custom endpoints.</summary>
    public required string Id { get; set; }
    public ProviderKind Kind { get; set; } = ProviderKind.OpenAICompatible;
    /// <summary>The preset slug this config came from; null for custom endpoints.</summary>
    public string? Preset { get; set; }
    public required string DisplayName { get; set; }
    /// <summary>
    /// For CLIs: the executable path override (empty = search PATH). Anthropic and Ollama bases are the host root;
    /// every other base already ends where <c>/chat/completions</c> and <c>/models</c> are appended.
    /// </summary>
    public string BaseURL { get; set; } = "";
    public AuthStyle AuthStyle { get; set; } = AuthStyle.None;
    public Dictionary<string, string> ExtraHeaders { get; set; } = [];
    /// <summary>Fallback until the model picker is filled; empty = pick one (or the CLI's default).</summary>
    public string DefaultModel { get; set; } = "";
    /// <summary>More models offered in the AI pane's model menu, next to <see cref="DefaultModel"/>.</summary>
    public List<string> ShownModels { get; set; } = [];
    /// <summary>Cloud transcription model; empty = the vendor default.</summary>
    public string TranscriptionModel { get; set; } = "";
    /// <summary>Diarization model id when this is the default cloud diarizer; empty = the provider default.</summary>
    public string DiarizationModel { get; set; } = "";
    public bool Enabled { get; set; }

    /// <summary>A new custom OpenAI-compatible endpoint with its own UUID id.</summary>
    public static ProviderConfig Custom(string displayName = "Custom", string baseURL = "") => new()
    {
        Id = Guid.NewGuid().ToString("D").ToUpperInvariant(), Kind = ProviderKind.OpenAICompatible,
        DisplayName = displayName, BaseURL = baseURL, AuthStyle = AuthStyle.Bearer,
    };

    /// <summary>Built-in presets, all disabled; a new list of new values on every call. Model ids are research fallbacks.</summary>
    public static List<ProviderConfig> Presets() =>
    [
        Cloud("openai", "OpenAI", "https://api.openai.com/v1", "gpt-6.1-sol"),
        new()
        {
            Id = "anthropic", Kind = ProviderKind.Anthropic, Preset = "anthropic", DisplayName = "Anthropic",
            BaseURL = "https://api.anthropic.com", AuthStyle = AuthStyle.XApiKey, DefaultModel = "claude-sonnet-5-5",
        },
        Cloud("xai", "xAI", "https://api.x.ai/v1", "grok-4.3"),
        Cloud("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.8-flash"),
        Cloud("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral-medium-latest"),
        // Base is https://<resource>.openai.azure.com/openai/v1 (set by the user); model = deployment name.
        Cloud("azure", "Azure OpenAI", "", "", AuthStyle.ApiKeyHeader),
        Cloud("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", ""),
        Cloud("groq", "Groq", "https://api.groq.com/openai/v1", "openai/gpt-oss-120b"),
        Cloud("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-v4-pro"),
        Local("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1"),
        // Native API (/api/chat, /api/tags) so the context size can be set.
        Local("ollama", "Ollama", "http://127.0.0.1:11434"),
        Local("jan", "Jan", "http://127.0.0.1:1337/v1"),
        // Older builds and Docker listen on :8080 instead.
        Local("llamacpp", "llama.cpp server", "http://127.0.0.1:9931/v1"),
        Local("vllm", "vLLM", "http://127.0.0.1:8000/v1"),
        Other("claude-cli", "Claude Code", ProviderKind.Cli),
        Other("codex-cli", "Codex", ProviderKind.Cli),
        Other("opencode-cli", "OpenCode", ProviderKind.Cli),
        Other("gemini-cli", "Gemini CLI", ProviderKind.Cli),
        // Speech to text only (OpenAI, Groq, Mistral, Gemini and custom endpoints transcribe through the configs above).
        Other("elevenlabs", "ElevenLabs", ProviderKind.SpeechToText),
        Other("deepgram", "Deepgram", ProviderKind.SpeechToText),
        Other("assemblyai", "AssemblyAI", ProviderKind.SpeechToText),
    ];

    private static ProviderConfig Cloud(string id, string name, string baseURL, string model, AuthStyle auth = AuthStyle.Bearer) => new()
    {
        Id = id, Kind = ProviderKind.OpenAICompatible, Preset = id, DisplayName = name, BaseURL = baseURL,
        AuthStyle = auth, DefaultModel = model,
    };

    private static ProviderConfig Local(string id, string name, string baseURL) => new()
    {
        Id = id, Kind = ProviderKind.Local, Preset = id, DisplayName = name, BaseURL = baseURL, AuthStyle = AuthStyle.None,
    };

    private static ProviderConfig Other(string id, string name, ProviderKind kind) => new()
    {
        Id = id, Kind = kind, Preset = id, DisplayName = name, BaseURL = "", AuthStyle = AuthStyle.None,
    };
}

/// <summary>
/// Swift's tolerant <c>ProviderConfig.init(from:)</c> (a non-object or a missing or non-string <c>id</c> throws) and
/// its synthesized encode (every key, <c>preset</c> omitted when null). Unknown keys are dropped, as in Swift.
/// </summary>
public sealed class ProviderConfigConverter : JsonConverter<ProviderConfig>
{
    public override ProviderConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var obj = document.RootElement;
        if (obj.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a provider object.");
        if (!obj.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            throw new JsonException("A provider needs a string id.");
        var id = idElement.GetString()!;
        return new ProviderConfig
        {
            Id = id,
            Kind = Value(obj, "kind", ProviderKind.OpenAICompatible, options),
            Preset = Value<string?>(obj, "preset", null, options),
            DisplayName = Value(obj, "displayName", id, options),
            BaseURL = Value(obj, "baseURL", "", options),
            AuthStyle = Value(obj, "authStyle", AuthStyle.None, options),
            ExtraHeaders = Value<Dictionary<string, string>>(obj, "extraHeaders", [], options),
            DefaultModel = Value(obj, "defaultModel", "", options),
            ShownModels = Value<List<string>>(obj, "shownModels", [], options),
            TranscriptionModel = Value(obj, "transcriptionModel", "", options),
            DiarizationModel = Value(obj, "diarizationModel", "", options),
            Enabled = Value(obj, "enabled", false, options),
        };
    }

    /// <summary>Swift's <c>(try? decodeIfPresent(T.self)) ?? nil</c>, then the default: missing, null or malformed falls back.</summary>
    private static T Value<T>(JsonElement obj, string key, T fallback, JsonSerializerOptions options)
    {
        if (!obj.TryGetProperty(key, out var element) || element.ValueKind == JsonValueKind.Null) return fallback;
        try
        {
            var value = element.Deserialize<T>(options);
            // A null element in a list or dictionary fails the Swift decode of the whole value.
            if (value is null || value is Dictionary<string, string> d && d.ContainsValue(null!)
                              || value is List<string> l && l.Contains(null!))
                return fallback;
            return value;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public override void Write(Utf8JsonWriter writer, ProviderConfig value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WritePropertyName("kind");
        JsonSerializer.Serialize(writer, value.Kind, options);
        if (value.Preset is { } preset) writer.WriteString("preset", preset);
        writer.WriteString("displayName", value.DisplayName);
        writer.WriteString("baseURL", value.BaseURL);
        writer.WritePropertyName("authStyle");
        JsonSerializer.Serialize(writer, value.AuthStyle, options);
        writer.WritePropertyName("extraHeaders");
        JsonSerializer.Serialize(writer, value.ExtraHeaders, options);
        writer.WriteString("defaultModel", value.DefaultModel);
        writer.WritePropertyName("shownModels");
        JsonSerializer.Serialize(writer, value.ShownModels, options);
        writer.WriteString("transcriptionModel", value.TranscriptionModel);
        writer.WriteString("diarizationModel", value.DiarizationModel);
        writer.WriteBoolean("enabled", value.Enabled);
        writer.WriteEndObject();
    }
}
