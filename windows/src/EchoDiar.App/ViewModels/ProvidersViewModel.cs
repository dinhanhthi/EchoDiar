using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoDiar.AI;
using EchoDiar.AI.Cloud;
using EchoDiar.App.Services;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.SystemServices;

namespace EchoDiar.App.ViewModels;

/// <summary>Where a provider of the AI pickers runs (macOS <c>AppModel.AIProvider.Kind</c>).</summary>
public enum AIProviderKind { InApp, LocalHost, Cloud, Cli }

/// <summary>
/// A provider the AI pickers offer (macOS <c>AppModel.AIProvider</c>): an in-app model (its catalog id) or a configured
/// <see cref="ProviderConfig"/>. <see cref="Models"/> is what the model menu offers: the config's default model, then its
/// shown models ("" is a CLI's own default). <see cref="UnavailableReason"/> says why it cannot run now (Windows AI).
/// </summary>
public sealed record AIProviderInfo(
    string Id,
    string Name,
    AIProviderKind Kind,
    string DefaultModel,
    IReadOnlyList<string> Models,
    string? UnavailableReason = null,
    CLIPreset? CliPreset = null,
    bool RemoteHost = false)
{
    /// <summary>What it is sent leaves this PC: a cloud API, a CLI, or a local host on another machine.</summary>
    public bool LeavesPC => Kind is AIProviderKind.Cloud or AIProviderKind.Cli || RemoteHost;
}

public enum CLIState { Checking, NotInstalled, Found }

/// <summary>
/// A subscription CLI's lookup (macOS <c>CLIStatus</c>): still looking, not found, or found at <see cref="Path"/> with
/// its <c>--version</c>. <see cref="InvalidOverride"/>: a path override is set but is not a full path to a <c>.exe</c> or
/// an npm <c>.cmd</c> shim that exists (the same rules as every run).
/// </summary>
public sealed record CLIStatus(CLIState State, string? Path = null, string? Version = null, bool InvalidOverride = false);

/// <summary>
/// The AI providers of Settings › AI and the AI pickers (macOS ProvidersSettingsView.swift state and AppModel's provider
/// functions): the provider configs, their API keys (Credential Manager), Windows AI's availability, the local hosts
/// found by a scan, the subscription CLIs found on PATH, the GGUF models of "On this PC" (macOS <c>mlxEntries</c>: download,
/// cancel, delete), the providers the pickers offer and the default one. Registry,
/// disk, process, credential and HTTP work runs off the UI thread; results come back on it. Lives as long as the app
/// (<see cref="AppViewModel.ProvidersModel"/>); on the UI thread.
/// </summary>
public sealed partial class ProvidersViewModel : ObservableObject
{
    /// <summary>The error and the result texts are cut here (macOS <c>shortMessage</c>).</summary>
    private const int MessageLimit = 160;

    private readonly AppViewModel _app;
    private readonly AppServices _services;

    /// <summary>Whether each provider has a saved key (macOS <c>savedKeys</c>); read again after every key change.</summary>
    private IReadOnlyDictionary<string, bool> _keys = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private int _keysGeneration;

    private (string? Reason, bool Hidden)? _windowsAI;
    private int _windowsAIGeneration;

    private IReadOnlyList<DiscoveredHost>? _hosts;
    private bool _scanning;

    private readonly Dictionary<CLIPreset, CLIStatus> _cliStatus = [];
    private readonly Dictionary<CLIPreset, int> _cliGeneration = [];

    /// <summary>Models whose Delete is under way: their Delete is off.</summary>
    private readonly HashSet<string> _deleting = [];

    internal ProvidersViewModel(AppViewModel app, AppServices services)
    {
        _app = app;
        _services = services;
        app.PropertyChanged += OnAppChanged;
        // The pickers need both from the start.
        _ = RefreshKeysAsync();
        _ = RefreshWindowsAIAsync();
    }

    /// <summary>The configs changed (settings), or a key was saved or removed: rows and pickers show them again.</summary>
    public event Action? Changed;

    /// <summary>A CLI's lookup changed.</summary>
    public event Action? CliStatusChanged;

    /// <summary>A GGUF model row's state may have changed (installs, downloads, errors, jobs).</summary>
    public event Action? LocalModelStatesChanged;

    private AppSettings Settings => _app.Settings;

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppViewModel.Settings):
                RaiseChanged();
                break;
            case nameof(AppViewModel.InstalledModelIds):
                // An installed GGUF model joins the pickers; a deleted one leaves them.
                RaiseChanged();
                LocalModelStatesChanged?.Invoke();
                break;
            case nameof(AppViewModel.Downloads):
            case nameof(AppViewModel.DownloadErrors):
            case nameof(AppViewModel.TranscriptionProgress):
            case nameof(AppViewModel.RecordingCaptions):
            case nameof(AppViewModel.AIJob):
            case nameof(AppViewModel.IsEmbedderLoaded):
                LocalModelStatesChanged?.Invoke();
                break;
        }
    }

    private void RaiseChanged()
    {
        if (_app.IsClosing) return;
        OnPropertyChanged(nameof(AIProviders));
        OnPropertyChanged(nameof(CurrentAIProvider));
        Changed?.Invoke();
    }

    // Configs

    /// <summary>
    /// Every preset (with its saved changes), then the custom endpoints (macOS <c>providerConfigs</c>). Ids compare
    /// case-insensitively, as at settings load: Credential Manager names keys that way, so two ids differing only in case
    /// would share one key.
    /// </summary>
    public IReadOnlyList<ProviderConfig> ProviderConfigs
    {
        get
        {
            var saved = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase);
            foreach (var config in Settings.Providers) saved.TryAdd(config.Id, config);
            var presets = ProviderConfig.Presets();
            var presetIds = presets.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return
            [
                .. presets.Select(p => saved.GetValueOrDefault(p.Id) ?? p),
                .. Settings.Providers.Where(p => p.Preset is null && !presetIds.Contains(p.Id)),
            ];
        }
    }

    /// <summary>The config shown for <paramref name="id"/> (saved, else its preset); null when there is none.</summary>
    public ProviderConfig? Config(string id) => ProviderConfigs.FirstOrDefault(c => SameId(c.Id, id));

    /// <summary>The saved local hosts (macOS <c>settings.providers</c> of kind local).</summary>
    public IReadOnlyList<ProviderConfig> SavedLocalHosts => [.. Settings.Providers.Where(p => p.Kind == ProviderKind.Local)];

    public static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A copy that shares no list or dictionary with <paramref name="config"/> (clients keep the one they got).</summary>
    public static ProviderConfig Copy(ProviderConfig config) =>
        config with { ExtraHeaders = new(config.ExtraHeaders), ShownModels = [.. config.ShownModels] };

    /// <summary>
    /// Applies <paramref name="change"/> to a copy of the saved config <paramref name="id"/> (or of its preset, which is
    /// then saved whole) and saves the settings (macOS <c>updateProvider</c>). The config is found case-insensitively and
    /// replaced in place, so no second id differing only in case is ever added.
    /// </summary>
    public void UpdateProvider(string id, Action<ProviderConfig> change)
    {
        var providers = Settings.Providers;
        var index = providers.FindIndex(p => SameId(p.Id, id));
        var source = index >= 0 ? providers[index] : ProviderConfig.Presets().FirstOrDefault(p => SameId(p.Id, id));
        if (source is null) return;
        var config = Copy(source);
        change(config);
        List<ProviderConfig> list = [.. providers];
        if (index >= 0) list[index] = config;
        else list.Add(config);
        Save(list);
    }

    /// <summary>Adds <paramref name="config"/> unless a provider with its id (in any case) is already saved.</summary>
    public bool AddProvider(ProviderConfig config)
    {
        if (Settings.Providers.Any(p => SameId(p.Id, config.Id))) return false;
        if (config.Preset is null && ProviderConfig.Presets().Any(p => SameId(p.Id, config.Id))) return false;
        Save([.. Settings.Providers, config]);
        return true;
    }

    /// <summary>A new custom OpenAI-compatible endpoint (macOS "Add Endpoint").</summary>
    public void AddEndpoint() => AddProvider(ProviderConfig.Custom());

    /// <summary>
    /// Removes the saved config (a preset goes back to its defaults) and its key (macOS <c>removeProvider</c>); the key
    /// is deleted off the UI thread.
    /// </summary>
    public async Task RemoveProviderAsync(string id)
    {
        Save([.. Settings.Providers.Where(p => !SameId(p.Id, id))]);
        await DeleteApiKeyAsync(id);
    }

    /// <summary>The last guard against case-variant duplicates, then the settings (saved after their delay).</summary>
    private void Save(List<ProviderConfig> providers) =>
        _app.ApplySettings(Settings with { Providers = [.. providers.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)] });

    // Keys

    /// <summary>Whether <paramref name="id"/> has a saved key, as last read (false until read).</summary>
    public bool HasApiKey(string id) => _keys.TryGetValue(id, out var saved) && saved;

    /// <summary>Reads, off the UI thread, whether each provider that can have a key has one (macOS <c>hasSavedKey</c>).</summary>
    public async Task RefreshKeysAsync()
    {
        var generation = ++_keysGeneration;
        var ids = ProviderConfigs.Where(c => c.Kind != ProviderKind.Cli).Select(c => c.Id).ToList();
        var secrets = _services.Secrets;
        var keys = await Task.Run(() =>
        {
            var found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                try
                {
                    found[id] = secrets.ApiKey(id) is not null;
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"EchoDiar: could not read the key of {id}: {e.GetType().Name}");
                    found[id] = false;
                }
            }
            return found;
        });
        if (generation != _keysGeneration || _app.IsClosing) return;
        _keys = keys;
        RaiseChanged();
    }

    /// <summary>
    /// Saves a typed key, its spaces and line breaks dropped anywhere (macOS <c>saveAPIKey</c>: a key copied across a
    /// wrapped line has one inside). Nothing happens for a blank text. Returns the error to show, which never contains key
    /// text, or null once saved.
    /// </summary>
    public async Task<string?> SaveApiKeyAsync(string id, string text)
    {
        var cleaned = new string([.. text.Where(c => !char.IsWhiteSpace(c))]);
        if (cleaned.Length == 0) return null;
        if (Encoding.UTF8.GetByteCount(cleaned) > CredentialStore.MaxKeyBytes)
            return Loc.Format("The API key is longer than %lld bytes, the most Credential Manager keeps.", CredentialStore.MaxKeyBytes);
        var secrets = _services.Secrets;
        string? error = null;
        try
        {
            await Task.Run(() => secrets.SetApiKey(id, cleaned));
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not save the key of {id}: {e.GetType().Name}");
            error = ShortMessage(e);
        }
        _app.EmbeddingKeyChanged();
        await RefreshKeysAsync();
        return error;
    }

    /// <summary>Deletes the key of <paramref name="id"/>; returns the error to show, or null.</summary>
    public async Task<string?> DeleteApiKeyAsync(string id)
    {
        var secrets = _services.Secrets;
        string? error = null;
        try
        {
            await Task.Run(() => secrets.DeleteApiKey(id));
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not delete the key of {id}: {e.GetType().Name}");
            error = ShortMessage(e);
        }
        _app.EmbeddingKeyChanged();
        await RefreshKeysAsync();
        return error;
    }

    /// <summary>The first line of an error, at most 160 characters (macOS <c>shortMessage</c>); no parameter names.</summary>
    public static string ShortMessage(Exception error)
    {
        var message = error is ArgumentException { ParamName: { } name } argument
            ? argument.Message.Replace($" (Parameter '{name}')", "", StringComparison.Ordinal)
            : error.Message;
        var line = message.Split('\n', '\r').FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > MessageLimit ? line[..MessageLimit] + "..." : line;
    }

    // Test and models

    /// <summary>The models <paramref name="config"/> lists, sorted; the client is made and asked off the UI thread.</summary>
    public Task<IReadOnlyList<string>> ListModelsAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        var copy = Copy(config);
        return Task.Run(async () =>
        {
            var provider = _services.MakeProvider(copy);
            if (provider is null) return (IReadOnlyList<string>)[];
            var models = await provider.ListModelsAsync(cancellationToken);
            return [.. models.Order(StringComparer.Ordinal)];
        }, cancellationToken);
    }

    /// <summary>
    /// Test: an API or local host lists its models (a request that needs a valid key and sends no transcript); a CLI
    /// answers a one-word prompt (it uses the subscription). Throws the error to show.
    /// </summary>
    public Task TestAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        var copy = Copy(config);
        return Task.Run(async () =>
        {
            if (_services.MakeProvider(copy) is not { } provider) return;
            if (copy.Kind == ProviderKind.Cli)
            {
                var stream = provider.StreamAsync([new LLMMessage(LLMRole.User, "ping")], copy.DefaultModel.Trim(), 1, cancellationToken);
                await foreach (var _ in stream.WithCancellation(cancellationToken)) break;
            }
            else
            {
                await provider.ListModelsAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);
    }

    /// <summary>
    /// Test of a speech-to-text API (macOS <c>SpeechProviderRow</c>): one free body-less GET that needs a valid key and
    /// uploads no audio, with the saved key read off the UI thread. Throws the error to show: "Save a key first.", or a
    /// short, key-free reason (<see cref="CloudTranscription.FailureReason"/>).
    /// </summary>
    public Task TestSpeechKeyAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        var copy = Copy(config);
        var secrets = _services.Secrets;
        return Task.Run(async () =>
        {
            var key = secrets.ApiKey(copy.Id) ?? throw LLMException.Provider(Loc.T("Save a key first."));
            try
            {
                await CloudTranscription.CheckKeyAsync(copy, key, cancellationToken: cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                throw LLMException.Provider(CloudTranscription.FailureReason(e));
            }
        }, cancellationToken);
    }

    // Windows AI

    /// <summary>
    /// Windows AI's state for Settings and the pickers: null until read, else why it cannot run (null when it can) and
    /// whether this PC can never run it (then it is not shown at all).
    /// </summary>
    public (string? Reason, bool Hidden)? WindowsAI => _windowsAI;

    /// <summary>Asks Windows AI again, off the UI thread (macOS "Check Again").</summary>
    public async Task RefreshWindowsAIAsync()
    {
        var generation = ++_windowsAIGeneration;
        var services = _services;
        (string? Reason, bool Hidden) availability;
        try
        {
            availability = await Task.Run(() => services.WindowsAI.Availability());
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read Windows AI's state: {e}");
            availability = WindowsAIAvailability.Describe(WindowsAIReadyState.ApiUnavailable);
        }
        if (generation != _windowsAIGeneration || _app.IsClosing) return;
        _windowsAI = availability;
        OnPropertyChanged(nameof(WindowsAI));
        RaiseChanged();
    }

    // Local hosts

    /// <summary>The hosts the last scan found; null before the first scan.</summary>
    public IReadOnlyList<DiscoveredHost>? Hosts
    {
        get => _hosts;
        private set => SetProperty(ref _hosts, value);
    }

    public bool Scanning
    {
        get => _scanning;
        private set => SetProperty(ref _scanning, value);
    }

    /// <summary>Probes the local hosts and Foundry Local off the UI thread (macOS <c>scan</c>); one scan at a time.</summary>
    public async Task ScanAsync()
    {
        if (Scanning) return;
        Scanning = true;
        IReadOnlyList<DiscoveredHost> hosts;
        try
        {
            hosts = await AppServices.ScanLocalHostsAsync();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: the local host scan failed: {e.GetType().Name}");
            hosts = [];
        }
        if (_app.IsClosing) return;
        Hosts = hosts;
        Scanning = false;
    }

    /// <summary>A found host is saved and on (macOS: the Added label instead of Add).</summary>
    public bool IsAdded(DiscoveredHost host) =>
        Settings.Providers.Any(p => SameId(p.Id, host.Preset) && p.BaseURL == host.BaseURL && p.Enabled);

    /// <summary>
    /// Saves a found host on its preset (macOS <c>add</c>): its base URL, on, and its first model when none is set.
    /// </summary>
    public void AddHost(DiscoveredHost host) => UpdateProvider(host.Preset, config =>
    {
        config.BaseURL = host.BaseURL;
        config.Enabled = true;
        if (config.DefaultModel.Length == 0) config.DefaultModel = host.Models.FirstOrDefault() ?? "";
    });

    /// <summary>Saves a local host typed by URL (macOS <c>addCustomLocal</c>); false when the text is not an http(s) URL.</summary>
    public bool AddCustomLocal(string text)
    {
        var url = text.Trim();
        if (ProviderInput.HostAndPort(url) is not { } name) return false;
        return AddProvider(new ProviderConfig
        {
            Id = Guid.NewGuid().ToString("D").ToUpperInvariant(), Kind = ProviderKind.Local, Preset = null,
            DisplayName = name, BaseURL = url, AuthStyle = AuthStyle.None, Enabled = true,
        });
    }

    // CLIs

    /// <summary>The lookup of <paramref name="preset"/>; null before it started.</summary>
    public CLIStatus? CliStatus(CLIPreset preset) => _cliStatus.GetValueOrDefault(preset);

    /// <summary>Looks every CLI up once per app run (macOS: once per window; each check spawns <c>--version</c>).</summary>
    public void DetectAllOnce()
    {
        if (_cliStatus.Count > 0) return;
        foreach (var preset in CLIPresets.All) _ = DetectAsync(preset);
    }

    /// <summary>
    /// Finds <paramref name="preset"/> on PATH or at its path override, then reads its version, off the UI thread (macOS
    /// <c>detect</c>). A newer lookup of the same CLI wins.
    /// </summary>
    public async Task DetectAsync(CLIPreset preset)
    {
        var generation = _cliGeneration[preset] = _cliGeneration.GetValueOrDefault(preset) + 1;
        _cliStatus[preset] = new CLIStatus(CLIState.Checking);
        CliStatusChanged?.Invoke();
        var @override = Config(preset.Slug())?.BaseURL.Trim() ?? "";
        CLIStatus status;
        try
        {
            status = await Task.Run(async () =>
            {
                var shell = ShellEnvironment.Shared;
                if (shell.Executable(preset.ExecutableName(), @override) is not { } executable)
                    return new CLIStatus(CLIState.NotInstalled, InvalidOverride: @override.Length > 0);
                return new CLIStatus(CLIState.Found, executable.DisplayPath, await shell.VersionAsync(executable));
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not look for {preset.ExecutableName()}: {e.GetType().Name}");
            status = new CLIStatus(CLIState.NotInstalled, InvalidOverride: @override.Length > 0);
        }
        if (_app.IsClosing || _cliGeneration[preset] != generation) return;
        _cliStatus[preset] = status;
        CliStatusChanged?.Invoke();
    }

    // In-app models

    /// <summary>
    /// The GGUF chat models "On this PC" lists, in catalog order (macOS <c>mlxEntries</c>); the embedding models are
    /// Phase 27's (macOS lists them in RAG, not here).
    /// </summary>
    public static IReadOnlyList<ModelEntry> LocalModelEntries { get; } =
        [.. ModelCatalog.Llm.Where(e => e.Runtime == ModelRuntime.Gguf)];

    /// <summary>
    /// The row of <paramref name="entry"/> as it is now (macOS <c>ModelRow</c> for an LLM): the Default badge on the
    /// catalog default (the model menu picks the one used), the download's fraction with Cancel, Installed with Delete, or
    /// Download. Delete is off while a transcription or the recording's transcriber runs (macOS), and while an in-app AI job
    /// runs (the menu may have moved on from its model): llama.cpp maps the file, which Windows then refuses to move or delete.
    /// </summary>
    public ModelRowState LocalModelState(ModelEntry entry)
    {
        var installed = _app.InstalledModelIds.Contains(entry.Id);
        double? download = _app.Downloads.TryGetValue(entry.Id, out var fraction) ? fraction : null;
        var busy = _app.ModelsInUse;
        return new ModelRowState(
            entry.IsDefault,
            installed,
            download,
            _app.DownloadErrors.GetValueOrDefault(entry.Id),
            UnsupportedNote: null,
            ShowSetDefault: false,
            CanSetDefault: false,
            CanDownload: true,
            CanDelete: !busy && !_deleting.Contains(entry.Id));
    }

    /// <summary>The installed models read again (macOS <c>refreshInstalledModels</c> when the tab shows).</summary>
    public Task RefreshLocalModelsAsync() => _app.RefreshModelsAsync();

    /// <summary>Downloads the model off the UI thread, SHA-256 checked, its progress on its row.</summary>
    public void DownloadLocalModel(ModelEntry entry) => _app.DownloadModel(entry.Id);

    public void CancelLocalModelDownload(ModelEntry entry) => _app.CancelDownload(entry.Id);

    /// <summary>Removes the model's files, without confirmation (macOS); its Delete is off meanwhile.</summary>
    public async Task DeleteLocalModelAsync(ModelEntry entry)
    {
        if (!_deleting.Add(entry.Id)) return;
        LocalModelStatesChanged?.Invoke();
        try
        {
            await _app.DeleteModelAsync(entry.Id);
        }
        finally
        {
            _deleting.Remove(entry.Id);
            if (!_app.IsClosing) LocalModelStatesChanged?.Invoke();
        }
    }

    // The pickers

    /// <summary>
    /// Windows AI (unless this PC can never run it; with why it cannot run now), the installed GGUF models, then the
    /// enabled providers with what they need: a base URL and, when they send one, a saved key (macOS <c>aiProviders</c>).
    /// </summary>
    public IReadOnlyList<AIProviderInfo> AIProviders
    {
        get
        {
            List<AIProviderInfo> providers = [];
            if (_windowsAI is { Hidden: false } windowsAI
                && ModelCatalog.Llm.FirstOrDefault(e => e.Runtime == ModelRuntime.WindowsAI) is { } entry)
            {
                providers.Add(new AIProviderInfo(
                    entry.Id, entry.DisplayName, AIProviderKind.InApp, entry.Id, [entry.Id],
                    windowsAI.Reason is { } reason ? Loc.T(reason) : null));
            }
            foreach (var model in LocalModelEntries.Where(e => _app.InstalledModelIds.Contains(e.Id)))
            {
                providers.Add(new AIProviderInfo(model.Id, model.DisplayName, AIProviderKind.InApp, model.Id, [model.Id]));
            }
            foreach (var config in ProviderConfigs)
            {
                if (!config.Enabled || config.Kind == ProviderKind.SpeechToText) continue;
                var kind = config.Kind switch
                {
                    ProviderKind.Cli => AIProviderKind.Cli,
                    ProviderKind.Local => AIProviderKind.LocalHost,
                    _ => AIProviderKind.Cloud,
                };
                if (kind != AIProviderKind.Cli
                    && (config.BaseURL.Trim().Length == 0 || config.AuthStyle != AuthStyle.None && !HasApiKey(config.Id))) continue;
                List<string> models = [];
                foreach (var id in config.ShownModels.Prepend(config.DefaultModel).Select(m => m.Trim()))
                {
                    if (id.Length > 0 && !models.Contains(id)) models.Add(id);
                }
                if (kind == AIProviderKind.Cli && config.DefaultModel.Trim().Length == 0) models.Insert(0, "");
                // The picked model stays listed, also once it is unchecked in Settings.
                if (config.Id == Settings.DefaultAIProviderID && Settings.DefaultAIModel is { Length: > 0 } picked
                    && !models.Contains(picked)) models.Add(picked);
                providers.Add(new AIProviderInfo(
                    config.Id, config.DisplayName, kind, config.DefaultModel, models,
                    CliPreset: kind == AIProviderKind.Cli ? CLIPresets.FromSlug(config.Preset) : null,
                    RemoteHost: kind == AIProviderKind.LocalHost && !ProviderInput.IsLoopback(config.BaseURL)));
            }
            return providers;
        }
    }

    /// <summary>The default provider when it can run, else the first one that can (macOS <c>currentAIProvider</c>).</summary>
    public AIProviderInfo? CurrentAIProvider
    {
        get
        {
            var usable = AIProviders.Where(p => p.UnavailableReason is null).ToList();
            return usable.FirstOrDefault(p => p.Id == Settings.DefaultAIProviderID) ?? usable.FirstOrDefault();
        }
    }

    /// <summary>The saved model for the default provider, else the provider's own (macOS <c>aiModel(for:)</c>).</summary>
    public string AIModelFor(AIProviderInfo provider) =>
        provider.Id == Settings.DefaultAIProviderID && Settings.DefaultAIModel is { Length: > 0 } model ? model : provider.DefaultModel;

    /// <summary>The model picked for the next summaries and questions (macOS <c>setAIModel</c>).</summary>
    public void SetAIModel(string model, AIProviderInfo provider) => _app.ApplySettings(Settings with
    {
        DefaultAIProviderID = provider.Id,
        DefaultAIModel = model == provider.DefaultModel ? null : model,
    });

    /// <summary>The language of summaries and answers: "auto" or a language code.</summary>
    public string SummaryLanguage
    {
        get => Settings.SummaryLanguage;
        set => _app.ApplySettings(Settings with { SummaryLanguage = value });
    }

    /// <summary>Earlier chat messages sent with each question; 0 = the whole chat.</summary>
    public int ChatContextMessages
    {
        get => Settings.ChatContextMessages;
        set => _app.ApplySettings(Settings with { ChatContextMessages = value });
    }
}
