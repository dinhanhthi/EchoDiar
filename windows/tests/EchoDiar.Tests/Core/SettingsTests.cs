using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EchoDiar.Core;
using EchoDiar.Core.Calendar;
using EchoDiar.Core.Json;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;

namespace EchoDiar.Tests.Core;

// settings.json contract with macOS (SettingsStoreTests + ProviderConfigDecodingTests). Every test uses a temp root.
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsStore _store;

    public SettingsTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SettingsStore(new AppPaths(_dir));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTripKeepsUnknownKeys()
    {
        // The Carbon shortcuts are macOS-only and a newer build may add keys: a Windows write must keep both.
        const string unknown = """
            {"globalHotKey":{"keyCode":15,"modifiers":2304},"globalPauseHotKey":{"keyCode":35,"modifiers":2304},
             "futureKey":[1,{"a":null,"b":"ü"}]}
            """;
        File.WriteAllText(_store.FilePath, $$"""
            {"launchAtLogin":true,"defaultLanguage":"fr","defaultDiarizerModelID":"cloud:openai",
             "pillWindowOffset":{"x":-40,"y":-12},"perAppBehavior":{"zoom.exe":"always"},
             "globalHotKeyWindows":{"virtualKey":65,"modifiers":6},
             "globalHotKey":{"keyCode":15,"modifiers":2304},"globalPauseHotKey":{"keyCode":35,"modifiers":2304},
             "futureKey":[1,{"a":null,"b":"ü"}]}
            """);

        var loaded = _store.Load();
        loaded.ShowPill = false;
        _store.Save(loaded);

        var written = JsonNode.Parse(File.ReadAllBytes(_store.FilePath))!.AsObject();
        foreach (var (key, value) in JsonNode.Parse(unknown)!.AsObject())
        {
            Assert.True(JsonNode.DeepEquals(value, written[key]), key);
        }
        Assert.False(written["showPill"]!.GetValue<bool>());
        Assert.Equal(65u, loaded.GlobalHotKeyWindows.VirtualKey);
        Assert.Equal(Json(loaded), Json(_store.Load()));
    }

    [Fact]
    public void CalendarAndRememberedLanguageRoundTripAsMacOSWritesThem()
    {
        File.WriteAllText(_store.FilePath, """
            {"rememberRecordingLanguage":true,"lastRecordingLanguage":"fr",
             "calendar":{"mode":"someFutureMode","ignoredCalendarIDs":["work"],"allTimedEvents":true,
              "stopAtEventEnd":false,"stopAfterEndMinutes":999,"skippedEvents":{"evt|1":783000000.5},
              "ownCalendarID":"own"}}
            """);

        var loaded = _store.Load();
        Assert.True(loaded.RememberRecordingLanguage);
        Assert.Equal("fr", loaded.LastRecordingLanguage);
        Assert.Equal(CalendarScheduler.Mode.Ask, loaded.Calendar.Mode);
        Assert.Equal(120, loaded.Calendar.StopAfterEndMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(978_307_200).AddSeconds(783_000_000.5),
            loaded.Calendar.SkippedEvents["evt|1"]);
        Assert.Empty(loaded.Extra);

        _store.Save(loaded);
        var written = JsonNode.Parse(File.ReadAllBytes(_store.FilePath))!.AsObject();
        Assert.True(written["rememberRecordingLanguage"]!.GetValue<bool>());
        Assert.Equal("fr", written["lastRecordingLanguage"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {"mode":"ask","ignoredCalendarIDs":["work"],"allTimedEvents":true,"stopAtEventEnd":false,
             "stopAfterEndMinutes":120,"skippedEvents":{"evt|1":783000000.5},"ownCalendarID":"own"}
            """), written["calendar"]), written["calendar"]?.ToJsonString());
    }

    [Theory]
    [InlineData("""{"version":1}""")]
    [InlineData("{}")]
    public void EmptyJsonGivesTheDefaults(string json)
    {
        File.WriteAllText(_store.FilePath, json);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(DefaultsJson), JsonNode.Parse(Json(_store.Load()))));
        Assert.True(File.Exists(_store.FilePath));
    }

    [Theory]
    [InlineData("""{"modelIdleMinutes":0}""", "modelIdleMinutes", "1")]
    [InlineData("""{"modelIdleMinutes":500}""", "modelIdleMinutes", "60")]
    [InlineData("""{"autoDeleteAfterDays":0}""", "autoDeleteAfterDays", "1")]
    [InlineData("""{"autoDeleteAfterDays":99999}""", "autoDeleteAfterDays", "3650")]
    [InlineData("""{"speakerCountHint":0}""", "speakerCountHint", "1")]
    [InlineData("""{"speakerCountHint":21}""", "speakerCountHint", "20")]
    [InlineData("""{"endMonitor":{"sourceInactiveSeconds":1,"callEndedSeconds":1000}}""", "endMonitor",
        """{"sourceInactiveSeconds":20,"callEndedSeconds":300,"silenceMinutes":10,"maxDurationMinutes":240}""")]
    [InlineData("""{"endMonitor":{"silenceMinutes":0,"maxDurationMinutes":5000}}""", "endMonitor",
        """{"sourceInactiveSeconds":25,"callEndedSeconds":60,"silenceMinutes":1,"maxDurationMinutes":1440}""")]
    [InlineData("""{"playbackChannels":"surround"}""", "playbackChannels", "\"mixed\"")]
    [InlineData("""{"autoDeleteMode":"shred"}""", "autoDeleteMode", "\"audioOnly\"")]
    [InlineData("""{"pillSize":"huge"}""", "pillSize", "\"medium\"")]
    [InlineData("""{"perAppBehavior":null}""", "perAppBehavior", "{}")]
    [InlineData("""{"globalHotKeyWindows":{"virtualKey":65,"modifiers":4}}""", "globalHotKeyWindows",
        """{"virtualKey":82,"modifiers":3}""")]
    [InlineData("""{"globalPauseHotKeyWindows":{"virtualKey":65,"modifiers":8}}""", "globalPauseHotKeyWindows",
        """{"virtualKey":80,"modifiers":3}""")]
    [InlineData("""{"globalHotKeyWindows":{"virtualKey":75,"modifiers":2}}""", "globalHotKeyWindows",
        """{"virtualKey":75,"modifiers":2}""")]
    [InlineData("""{"globalHotKeyWindows":{"virtualKey":67,"modifiers":2}}""", "globalHotKeyWindows",
        """{"virtualKey":82,"modifiers":3}""")] // Ctrl+C
    [InlineData("""{"globalPauseHotKeyWindows":{"virtualKey":115,"modifiers":1}}""", "globalPauseHotKeyWindows",
        """{"virtualKey":80,"modifiers":3}""")] // Alt+F4
    [InlineData("""{"globalHotKeyWindows":{"virtualKey":0,"modifiers":3}}""", "globalHotKeyWindows",
        """{"virtualKey":82,"modifiers":3}""")]
    [InlineData("""{"globalHotKeyWindows":{"virtualKey":255,"modifiers":3}}""", "globalHotKeyWindows",
        """{"virtualKey":82,"modifiers":3}""")]
    [InlineData("""{"globalHotKeyWindows":{"virtualKey":75,"modifiers":16386}}""", "globalHotKeyWindows",
        """{"virtualKey":82,"modifiers":3}""")] // MOD_NOREPEAT | MOD_CONTROL
    public void ClampsAndEnumFallbacks(string json, string key, string expected)
    {
        var settings = SwiftJson.Deserialize<AppSettings>(Encoding.UTF8.GetBytes(json));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(Json(settings))![key]),
            $"{key}: {JsonNode.Parse(Json(settings))![key]?.ToJsonString()}");
    }

    [Fact]
    public void CorruptFileIsMovedAsideAndDefaultsAreReturned()
    {
        var now = new DateTimeOffset(2026, 10, 22, 9, 30, 15, TimeSpan.Zero).AddMilliseconds(7);
        var stamp = now.ToLocalTime().ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        byte[] corrupt = Encoding.UTF8.GetBytes("not json {");

        File.WriteAllBytes(_store.FilePath, corrupt);
        Assert.Equal(Json(new AppSettings()), Json(_store.Load(now)));
        File.WriteAllBytes(_store.FilePath, corrupt);
        Assert.Equal(Json(new AppSettings()), Json(_store.Load(now)));

        Assert.False(File.Exists(_store.FilePath));
        var aside = Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([$"settings.json.unreadable-{stamp}", $"settings.json.unreadable-{stamp}-1"], aside);
        Assert.Equal(corrupt, File.ReadAllBytes(Path.Combine(_dir, aside[0]!)));
    }

    [Fact]
    public void ProviderWithoutIdFailsTheDecode()
    {
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<ProviderConfig>("""{"kind":"local"}"""u8));
        Assert.Throws<JsonException>(() => SwiftJson.Deserialize<AppSettings>(
            """{"launchAtLogin":true,"providers":[{"kind":"local"}]}"""u8));
    }

    [Fact]
    public void MalformedProviderKeepsTheOtherSettings()
    {
        var settings = SwiftJson.Deserialize<AppSettings>("""
            {"launchAtLogin":true,"providers":[
              {"id":"x","kind":7},
              {"id":"future","kind":"quantum","authStyle":"oauth3","enabled":"yes","extraHeaders":[1,2],
               "displayName":42,"baseURL":"https://h/v1","defaultModel":"m","shownModels":["a",null],"newField":{"x":1}},
              {"id":"anthropic","kind":"anthropic","preset":"anthropic","displayName":"A","baseURL":"https://a",
               "authStyle":"xApiKey","extraHeaders":{"h":"v"},"defaultModel":"m","enabled":true}
            ]}
            """u8);

        Assert.True(settings.LaunchAtLogin);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            [
              {"id":"x","kind":"openAICompatible","displayName":"x","baseURL":"","authStyle":"none","extraHeaders":{},
               "defaultModel":"","shownModels":[],"transcriptionModel":"","diarizationModel":"","enabled":false},
              {"id":"future","kind":"openAICompatible","displayName":"future","baseURL":"https://h/v1","authStyle":"none",
               "extraHeaders":{},"defaultModel":"m","shownModels":[],"transcriptionModel":"","diarizationModel":"",
               "enabled":false},
              {"id":"anthropic","kind":"anthropic","preset":"anthropic","displayName":"A","baseURL":"https://a",
               "authStyle":"xApiKey","extraHeaders":{"h":"v"},"defaultModel":"m","shownModels":[],
               "transcriptionModel":"","diarizationModel":"","enabled":true}
            ]
            """), JsonNode.Parse(Json(settings))!["providers"]));
    }

    [Fact]
    public void ProviderIdsDifferingOnlyInCaseKeepTheFirst()
    {
        // One Credential Manager key per case-insensitive id: a later duplicate must not get the first one's key.
        var settings = SwiftJson.Deserialize<AppSettings>("""
            {"providers":[
              {"id":"openai","displayName":"First","enabled":true},
              {"id":"ollama","kind":"local","displayName":"Ollama"},
              {"id":"OpenAI","displayName":"Second","baseURL":"https://evil/v1"},
              {"id":"OPENAI","displayName":"Third"}
            ]}
            """u8);

        Assert.Equal(["openai", "ollama"], settings.Providers.Select(provider => provider.Id));
        Assert.Equal("First", settings.Providers[0].DisplayName);
        Assert.True(settings.Providers[0].Enabled);
        Assert.Equal("", settings.Providers[0].BaseURL);
    }

    // "Key saved = available" and its one-time migration: same rows as macOS ProviderAvailabilityTests.
    private const string CustomID = "3F2504E0-4F89-11D3-9A0C-0305E82C3301";

    /// <summary>A preset by id, or a custom OpenAI-compatible endpoint with <see cref="CustomID"/> for "custom".</summary>
    private static ProviderConfig Config(string id, bool enabled, AuthStyle? auth = null, string? baseURL = null)
    {
        var config = ProviderConfig.Presets().FirstOrDefault(p => p.Id == id) ?? ProviderConfig.Custom(baseURL: "https://h/v1");
        if (id is "custom" or CustomID) config.Id = CustomID;
        config.Enabled = enabled;
        if (auth is { } style) config.AuthStyle = style;
        if (baseURL is not null) config.BaseURL = baseURL;
        return config;
    }

    [Theory]
    // Cloud preset: the key decides, Enabled does not.
    [InlineData("openai", false, null, null, true, true, true)]
    [InlineData("openai", true, null, null, false, true, false)]
    [InlineData("openai", false, null, null, false, true, false)]
    [InlineData("anthropic", true, null, null, false, true, false)]
    [InlineData("deepgram", false, null, null, true, true, true)]
    [InlineData("deepgram", true, null, null, false, true, false)]
    // Custom endpoint with auth: the key decides.
    [InlineData("custom", false, null, null, true, true, true)]
    [InlineData("custom", true, null, null, false, true, false)]
    // Custom endpoint without auth: the base URL decides, the key does not.
    [InlineData("custom", false, AuthStyle.None, "https://h/v1", false, true, true)]
    [InlineData("custom", false, AuthStyle.None, "https://h/v1", true, true, true)]
    [InlineData("custom", true, AuthStyle.None, "", true, true, false)]
    [InlineData("custom", true, AuthStyle.None, "  \n", true, true, false)]
    // Local server and CLI: Enabled decides, the key does not.
    [InlineData("ollama", true, null, null, false, false, true)]
    [InlineData("ollama", false, null, null, true, false, false)]
    [InlineData("claude-cli", true, null, null, false, false, true)]
    [InlineData("claude-cli", false, null, null, true, false, false)]
    public void ProviderAvailability(string id, bool enabled, AuthStyle? auth, string? baseURL, bool hasKey, bool cloud,
        bool available)
    {
        var config = Config(id, enabled, auth, baseURL);

        Assert.Equal(cloud, config.IsCloud);
        Assert.Equal(available, config.IsAvailable(hasKey));
    }

    // Every reference points at id; savedEnabled null = the provider was never saved to Providers (its preset applies).
    [Theory]
    [InlineData("openai", false, true)]
    [InlineData("openai", true, false)]
    [InlineData("deepgram", null, true)]
    [InlineData("deepgram", false, true)]
    [InlineData("deepgram", true, false)]
    [InlineData(CustomID, false, true)]
    [InlineData(CustomID, true, false)]
    [InlineData("ollama", false, false)]
    [InlineData("claude-cli", false, false)]
    public void MigratingProviderAvailability(string id, bool? savedEnabled, bool cleared)
    {
        var settings = new AppSettings
        {
            DefaultASRModelID = "cloud:" + id, DefaultDiarizerModelID = "cloud:" + id,
            Providers = savedEnabled is { } enabled ? [Config(id, enabled)] : [],
            DefaultAIProviderID = id, CatchUpProviderID = id, EmbeddingModelID = ModelCatalog.ApiEmbedderPrefix + id,
            ProviderAvailabilityMigrated = false,
        };

        var migrated = settings.MigratingProviderAvailability();

        Assert.True(migrated.ProviderAvailabilityMigrated);
        Assert.Equal(cleared, migrated.AutoExcludedProviderIDs.Contains(id));
        Assert.Equal(cleared ? null : "cloud:" + id, migrated.DefaultASRModelID);
        Assert.Equal(cleared ? null : "cloud:" + id, migrated.DefaultDiarizerModelID);
        Assert.Equal(cleared ? null : id, migrated.DefaultAIProviderID);
        Assert.Equal(cleared ? "" : id, migrated.CatchUpProviderID);
        Assert.Equal(cleared ? AppSettings.DefaultEmbeddingModelID : ModelCatalog.ApiEmbedderPrefix + id,
            migrated.EmbeddingModelID);
        Assert.Equal(Json(settings.Providers), Json(migrated.Providers));
    }

    [Fact]
    public void ProviderAvailabilityMigrationRunsOnce()
    {
        var settings = new AppSettings
        {
            DefaultASRModelID = "cloud:openai", Providers = [Config("openai", enabled: false)],
            CatchUpProviderID = "openai", ProviderAvailabilityMigrated = false,
        };
        var once = settings.MigratingProviderAvailability();
        Assert.Same(once, once.MigratingProviderAvailability());

        // Already migrated (a fresh install): a disabled cloud config keeps its references.
        var fresh = settings with { ProviderAvailabilityMigrated = true };
        Assert.Same(fresh, fresh.MigratingProviderAvailability());
        Assert.Equal("cloud:openai", fresh.DefaultASRModelID);
        Assert.Empty(fresh.AutoExcludedProviderIDs);
    }

    [Fact]
    public void ProviderAvailabilityMigrationFlagAndExcludedIdsRoundTrip()
    {
        Assert.True(new AppSettings().ProviderAvailabilityMigrated);
        Assert.False(SwiftJson.Deserialize<AppSettings>("""{"version":1}"""u8).ProviderAvailabilityMigrated);
        Assert.True(SwiftJson.Deserialize<AppSettings>("""{"providerAvailabilityMigrated":true}"""u8)
            .ProviderAvailabilityMigrated);

        var migrated = new AppSettings { ProviderAvailabilityMigrated = false }.MigratingProviderAvailability();
        var read = SwiftJson.Deserialize<AppSettings>(SwiftJson.Serialize(migrated));
        Assert.True(read.ProviderAvailabilityMigrated);
        Assert.True(read.AutoExcludedProviderIDs.SetEquals(migrated.AutoExcludedProviderIDs));
        // Case-insensitive after a read, as Credential Manager names keys (the ISet overload asks the set itself).
        Assert.Contains("OpenAI", read.AutoExcludedProviderIDs);
        Assert.Contains("deepgram",
            SwiftJson.Deserialize<AppSettings>("""{"autoExcludedProviderIDs":["DeepGram"]}"""u8).AutoExcludedProviderIDs);
    }

    private static string Json<T>(T value) => Encoding.UTF8.GetString(SwiftJson.Serialize(value));

    // Every key Windows writes, with the macOS read defaults (Carbon shortcuts excluded, see AppSettings) except
    // embeddingModelID, the Windows catalog id. A file from an older build still has the availability migration to run.
    private const string DefaultsJson = """
        {
          "version": 1, "launchAtLogin": false, "defaultLanguage": "auto", "recentLanguages": [],
          "compressionBitrateKbps": 64, "keepUncompressedAudio": false, "rememberRecordingLanguage": false,
          "playbackChannels": "mixed",
          "onboardingCompleted": false, "modelIdleMinutes": 2, "providers": [], "cloudSTTConfirmed": [],
          "catchUpProviderID": "", "catchUpModel": "", "catchUpFromStart": false,
          "summaryLanguage": "auto", "chatContextMessages": 10, "ragEnabled": false,
          "embeddingModelID": "gguf-qwen3-embedding-0.6b", "embeddingAPIModel": "", "cloudEmbeddingConfirmed": [],
          "meetingDetection": true, "perAppBehavior": {}, "pillPositions": {}, "showPill": true,
          "hidePillWhenAppActive": false, "showPillInScreenCaptures": false, "attachPillToWindow": false,
          "pillSize": "medium",
          "endMonitor": {"sourceInactiveSeconds": 25, "callEndedSeconds": 60, "silenceMinutes": 10, "maxDurationMinutes": 240},
          "endPromptNotification": true, "routeChangeBehavior": "ask",
          "calendar": {"mode": "ask", "ignoredCalendarIDs": [], "allTimedEvents": false, "stopAtEventEnd": true,
                       "stopAfterEndMinutes": 10, "skippedEvents": {}},
          "autoTranscribe": true, "transcribeWhileRecording": true, "diarization": false,
          "deferTranscriptionWhenConstrained": true, "autoDeleteEnabled": false, "autoDeleteAfterDays": 90,
          "autoDeleteMode": "audioOnly", "deleteAudioAfterTranscription": false, "deleteWithoutConfirmation": false,
          "globalHotKeyWindows": {"virtualKey": 82, "modifiers": 3}, "globalHotKeyEnabled": true,
          "globalPauseHotKeyWindows": {"virtualKey": 80, "modifiers": 3}, "globalPauseHotKeyEnabled": true,
          "autoExcludedProviderIDs": [], "providerAvailabilityMigrated": false
        }
        """;
}
