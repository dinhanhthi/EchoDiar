using System.Diagnostics;
using EchoDiar.AI;
using EchoDiar.App.Services;
using EchoDiar.Core.Model;
using EchoDiar.Core.Models;
using EchoDiar.Core.Settings;

namespace EchoDiar.App.ViewModels;

/// <summary>
/// What the library index (RAG) is doing (macOS <c>AppModel.RAGStatus</c>): conversations indexed out of those with a
/// transcript, a pass running or waiting (<see cref="Paused"/>: for a recording, compression, transcription or AI job to
/// end), why the last pass could not run or stopped, and whether a cloud embedder waits for its one-time confirmation
/// (nothing is sent meanwhile).
/// </summary>
public sealed record RAGStatus(
    int Indexed = 0, int Total = 0, bool Running = false, bool Paused = false, string? Message = null, bool NeedsConsent = false);

/// <summary>
/// A cloud embedder's one-time confirmation waiting for the window's dialog (macOS RAGSettingsView "Send transcripts to
/// …?"): its provider, the destination key it is remembered under, the host shown, and how many conversations would go.
/// <paramref name="PickProviderId"/>: a provider picked in Settings › RAG and not saved yet, which Understood makes the
/// embedder with <paramref name="PickModel"/> (macOS <c>pendingCloud</c>: the setting changes only on Understood); null
/// for the embedder in use.
/// </summary>
public sealed record PendingEmbeddingConsent(
    string ProviderName, string Destination, string Host, int Conversations, string? PickProviderId = null, string PickModel = "");

/// <summary>
/// The library index and the chat across the library (macOS LibraryChatModel.swift): the index follows the library (a
/// new transcript or summary, a deleted conversation, the settings), one pass at a time, waiting while a recording,
/// compression, transcription or AI job runs; the library chat's questions are answered from it, saved in
/// <c>GlobalChat</c> beside the conversations. Passes and questions run on the UI thread between awaits; file, key,
/// index and model work run on the thread pool. An in-app embedder holds <see cref="ModelLoadGate"/> (owner Embedder)
/// while it is loaded, one batch of about 20 s or one question at a time.
/// </summary>
public sealed partial class AppViewModel
{
    /// <summary>How often a waiting pass checks whether the jobs it waits for have ended.</summary>
    private static readonly TimeSpan IndexingPauseCheck = TimeSpan.FromSeconds(5);

    /// <summary>A batch keeps its loaded embedder at most this long, so recordings and AI jobs can take the gate between.</summary>
    private static readonly TimeSpan IndexBatchTime = TimeSpan.FromSeconds(20);

    /// <summary>The index's best passages a library question looks at.</summary>
    private const int LibrarySearchLimit = 40;

    private RAGStatus _ragStatus = new();
    private bool _globalChatOpen;
    private (Guid Id, double Seconds)? _pendingTranscriptTime;
    private PendingEmbeddingConsent? _pendingEmbeddingConsent;

    /// <summary>In-app embedders loaded now (a pass's batch, a question's query): their GGUF file is mapped.</summary>
    private int _embedderLeases;

    /// <summary>A pass runs; another asked for meanwhile runs once it ends (<see cref="_indexPending"/>).</summary>
    private bool _indexRunning;
    private bool _indexPending;
    private CancellationTokenSource? _indexCancel;

    /// <summary>The next pass deletes the current key's shards first (Rebuild).</summary>
    private bool _indexRebuild;

    /// <summary>The library was listed once: an empty list is then an empty library, not one still loading.</summary>
    private bool _libraryLoaded;

    /// <summary>What the library index is doing.</summary>
    public RAGStatus RAGStatus
    {
        get => _ragStatus;
        private set => SetProperty(ref _ragStatus, value);
    }

    /// <summary>
    /// The library chat is shown (macOS <c>globalChatOpen</c>): in place of the cards on the list, or of the sidebar beside
    /// a conversation or the recording.
    /// </summary>
    public bool GlobalChatOpen
    {
        get => _globalChatOpen;
        set => SetProperty(ref _globalChatOpen, value);
    }

    /// <summary>A library citation's conversation and time, shown once that conversation's transcript is read.</summary>
    public (Guid Id, double Seconds)? PendingTranscriptTime
    {
        get => _pendingTranscriptTime;
        private set => SetProperty(ref _pendingTranscriptTime, value);
    }

    /// <summary>The cloud embedding confirmation the window's dialog asks next; null when none waits.</summary>
    public PendingEmbeddingConsent? PendingEmbeddingConsent
    {
        get => _pendingEmbeddingConsent;
        private set => SetProperty(ref _pendingEmbeddingConsent, value);
    }

    /// <summary>An in-app embedder is loaded (part of <see cref="ModelsInUse"/>).</summary>
    public bool IsEmbedderLoaded => _embedderLeases > 0;

    /// <summary>The library chat's chat and archived chats, beside the conversations.</summary>
    public string GlobalChatFolder => _services.Paths.GlobalChat;

    /// <summary>
    /// Indexing waits while any of these runs, so it never competes with them for the model gate or the CPU (macOS
    /// <c>indexingPaused</c>): a recording, a compression or a recording's last steps, a transcription, an AI job.
    /// </summary>
    private bool IndexingPaused =>
        IsRecording || _finishing.Count > 0 || TranscriptionProgress.Keys.Any(IsTranscribing) || AIJob is not null;

    /// <summary>
    /// The recording transcriber holds the model gate for a whole recording, a transcription until it ends: a library
    /// question then skips an in-app embedder (keyword search only) instead of waiting for the gate (macOS
    /// <c>modelGateBusy</c>).
    /// </summary>
    private bool ModelGateBusy => IsRecording || RecordingCaptions is not null || TranscriptionProgress.Keys.Any(IsTranscribing);

    // Index

    /// <summary>
    /// Brings the index up to date with the library (macOS <c>syncLibraryIndex</c>): one pass at a time, a call during a
    /// pass runs another once it ends. <paramref name="restart"/> cancels the running pass first (the embedder changed).
    /// Turning RAG off removes the index, in a pass of its own once the running one stopped.
    /// </summary>
    public void SyncLibraryIndex(bool restart = false)
    {
        if (_closing) return;
        if (!Settings.RagEnabled && RAGStatus != new RAGStatus()) RAGStatus = new RAGStatus();
        if (restart || !Settings.RagEnabled) CancelIndexPass();
        if (_indexRunning)
        {
            _indexPending = true;
            return;
        }
        _indexRunning = true;
        var cancel = new CancellationTokenSource();
        _indexCancel = cancel;
        _ = RunIndexPassAsync(cancel);
    }

    /// <summary>Deletes the current embedder's shards, then indexes every conversation again (macOS <c>rebuildLibraryIndex</c>).</summary>
    public void RebuildLibraryIndex()
    {
        _indexRebuild = true;
        SyncLibraryIndex(restart: true);
    }

    /// <summary>The library was listed (launch, and after every change): its index follows.</summary>
    private void OnLibraryListed()
    {
        _libraryLoaded = true;
        SyncLibraryIndex();
    }

    /// <summary>A saved key changed: a running pass of an API embedder would keep sending with the old one (macOS <c>keychainChanged</c>).</summary>
    internal void EmbeddingKeyChanged()
    {
        if (Settings.EmbeddingModelID.StartsWith(ModelCatalog.ApiEmbedderPrefix, StringComparison.Ordinal)) SyncLibraryIndex(restart: true);
    }

    /// <summary>
    /// A change of these makes another embedder (macOS <c>updateSettings</c>' <c>ragChanged</c>): a pass still running
    /// would keep embedding with the old choice.
    /// </summary>
    private static bool RagChanged(AppSettings old, AppSettings updated) =>
        updated.RagEnabled != old.RagEnabled
        || updated.EmbeddingModelID != old.EmbeddingModelID
        || updated.EmbeddingAPIModel != old.EmbeddingAPIModel
        || (updated.EmbeddingModelID.StartsWith(ModelCatalog.ApiEmbedderPrefix, StringComparison.Ordinal)
            && !updated.Providers.SequenceEqual(old.Providers));

    /// <summary>An embedding model was downloaded or deleted: indexing may have waited for it, or now says it is missing.</summary>
    private static bool EmbeddingInstallChanged(IReadOnlySet<string> old, IReadOnlySet<string> updated) =>
        ModelCatalog.Embedding.Any(e => old.Contains(e.Id) != updated.Contains(e.Id));

    /// <summary>Quit: the running pass stops; nothing waits for it.</summary>
    private void ShutdownRAG()
    {
        _indexPending = false;
        CancelIndexPass();
    }

    private void CancelIndexPass()
    {
        try
        {
            _indexCancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The pass ended meanwhile.
        }
    }

    private async Task RunIndexPassAsync(CancellationTokenSource cancel)
    {
        try
        {
            await IndexLibraryAsync(cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: indexing failed: {e.GetType().Name}");
            if (!_closing) RAGStatus = RAGStatus with { Message = Loc.Format("Indexing stopped: %@", ProvidersViewModel.ShortMessage(e)) };
        }
        finally
        {
            if (ReferenceEquals(_indexCancel, cancel)) _indexCancel = null;
            cancel.Dispose();
            _indexRunning = false;
        }
        if (_closing) return;
        RAGStatus = RAGStatus with { Running = false, Paused = false };
        if (!_indexPending) return;
        _indexPending = false;
        SyncLibraryIndex();
    }

    /// <summary>
    /// One pass (macOS <c>indexLibrary</c>): with RAG off or an empty library, deletes the whole index; else deletes the
    /// shards of conversations that are gone (even without a usable embedder), drops the other embedders' folders, loads
    /// the current one's shards when the embedder changed, then indexes the stale conversations in batches of one model
    /// load each, so recordings and AI jobs can take the gate between them. An error stops the pass; the next change
    /// retries. A cloud embedder whose destination is not confirmed sends nothing.
    /// </summary>
    private async Task IndexLibraryAsync(CancellationToken token)
    {
        // A missing or empty index folder makes this cheap on every call while RAG stays off.
        if (!Settings.RagEnabled)
        {
            await _services.Index.RemoveAllAsync();
            RAGStatus = new RAGStatus();
            return;
        }
        // Before the library's first listing: StaleAsync deletes the shards of every conversation not listed.
        if (!_libraryLoaded) return;
        var conversations = Conversations;
        RAGStatus = RAGStatus with { Message = null, NeedsConsent = false };
        // An empty library keeps no shard, whatever the embedder.
        if (conversations.Count == 0)
        {
            await _services.Index.RemoveAllAsync();
            RAGStatus = RAGStatus with { Total = 0, Indexed = 0 };
            return;
        }
        // Before the embedder checks: deleted conversations lose their shards even with no usable embedder.
        await _services.Index.PruneAsync(conversations, token);
        var settings = Settings;
        var configs = ProvidersModel.ProviderConfigs;
        var (embedder, problem) = await Task.Run(() => _services.MakeEmbedder(settings, configs), token);
        if (embedder is null)
        {
            RAGStatus = RAGStatus with { Message = problem };
            return;
        }
        if (!embedder.IsLocal && EmbedderNeedsConsent(settings, configs))
        {
            RAGStatus = RAGStatus with { NeedsConsent = true };
            return;
        }
        var key = embedder.Key;
        await _services.Index.RemoveAllExceptAsync(key);
        await _services.Index.LoadAsync(key, token);
        if (_indexRebuild)
        {
            _indexRebuild = false;
            await _services.Index.RemoveKeyAsync(key);
        }
        var stale = await _services.Index.StaleAsync(conversations, token);
        var transcribed = conversations.Where(c => c.HasTranscript).Select(c => c.Id).ToHashSet();
        var indexed = (await _services.Index.IndexedIdsAsync()).Count(transcribed.Contains);
        RAGStatus = RAGStatus with { Total = transcribed.Count, Indexed = indexed };
        if (stale.Count == 0 || token.IsCancellationRequested) return;
        RAGStatus = RAGStatus with { Running = true };
        // In-app models only: an API embedder (a loopback Ollama too) loads nothing here.
        var gated = embedder is not APITextEmbedder;
        var next = 0;
        while (next < stale.Count)
        {
            while (IndexingPaused)
            {
                RAGStatus = RAGStatus with { Paused = true };
                await Task.Delay(IndexingPauseCheck, token);
            }
            RAGStatus = RAGStatus with { Paused = false };
            token.ThrowIfCancellationRequested();
            var batch = stale.Skip(next).ToList();
            (int Handled, bool Stopped) result;
            try
            {
                result = await IndexWithEmbedderAsync(embedder, gated, batch, transcribed, token);
            }
            catch (Exception e) when (!token.IsCancellationRequested)
            {
                Debug.WriteLine($"EchoDiar: indexing failed to load the embedder: {e.GetType().Name}");
                RAGStatus = RAGStatus with { Message = Loc.Format("Indexing stopped: %@", ProvidersViewModel.ShortMessage(e)) };
                return;
            }
            if (result.Stopped || token.IsCancellationRequested) return;
            next += result.Handled;
        }
    }

    /// <summary>
    /// Loads <paramref name="embedder"/> once (inside the model gate when <paramref name="gated"/>) and indexes
    /// <paramref name="batch"/> with it on the thread pool (macOS <c>withExclusiveModel</c> around <c>indexBatch</c>).
    /// </summary>
    private async Task<(int Handled, bool Stopped)> IndexWithEmbedderAsync(
        ITextEmbedder embedder, bool gated, IReadOnlyList<Conversation> batch, IReadOnlySet<Guid> transcribed,
        CancellationToken token)
    {
        using var lease = gated ? await _services.Gate.AcquireAsync(ModelLoadGate.Owner.Embedder, token) : null;
        if (gated) LeaseEmbedder(1);
        try
        {
            return await Task.Run(
                () => embedder.WithLoadedAsync(loaded => IndexBatchAsync(batch, transcribed, loaded, token), token), token);
        }
        finally
        {
            if (gated) LeaseEmbedder(-1);
        }
    }

    /// <summary>
    /// Indexes <paramref name="batch"/> in order with one loaded embedder until about 20 s have passed, indexing pauses
    /// or the pass is cancelled; at least one conversation each time (macOS <c>indexBatch</c>). Returns how many it went
    /// through, and <c>Stopped</c> after an error (in <see cref="RAGStatus"/>). On the thread pool: the UI state is read
    /// through the dispatcher, the library through its lock-free list.
    /// </summary>
    private async Task<(int Handled, bool Stopped)> IndexBatchAsync(
        IReadOnlyList<Conversation> batch, IReadOnlySet<Guid> transcribed, ILoadedTextEmbedder embedder, CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        var handled = 0;
        foreach (var conversation in batch)
        {
            handled++;
            // Deleted during the pass.
            if (_services.Library.Conversations.Any(c => c.Id == conversation.Id))
            {
                var folder = conversation.Folder;
                try
                {
                    // LibraryIndex.IndexAsync takes the fingerprint after these reads (as macOS).
                    var transcript = Transcript.Read(folder);
                    var summary = Conversation.ReadSummary(folder);
                    await _services.Index.IndexAsync(conversation, transcript, summary, embedder, token);
                }
                catch (Exception e)
                {
                    if (token.IsCancellationRequested) return (handled, true);
                    Debug.WriteLine($"EchoDiar: indexing failed for {Path.GetFileName(folder)}: {e.GetType().Name}");
                    var message = Loc.Format("Indexing stopped at \"%@\": %@", conversation.Title, ProvidersViewModel.ShortMessage(e));
                    Post(() => RAGStatus = RAGStatus with { Message = message });
                    return (handled, true);
                }
                var indexed = (await _services.Index.IndexedIdsAsync()).Count(transcribed.Contains);
                Post(() => RAGStatus = RAGStatus with { Indexed = indexed });
            }
            if (token.IsCancellationRequested || started.Elapsed >= IndexBatchTime || await OnUIAsync(() => IndexingPaused)) break;
        }
        return (handled, false);
    }

    /// <summary>Counts an in-app embedder loaded or unloaded, on the UI thread.</summary>
    private void LeaseEmbedder(int change)
    {
        var was = IsEmbedderLoaded;
        _embedderLeases += change;
        if (IsEmbedderLoaded != was) OnPropertyChanged(nameof(IsEmbedderLoaded));
    }

    /// <summary>The value of <paramref name="read"/> on the UI thread, for work on the thread pool.</summary>
    private Task<T> OnUIAsync<T>(Func<T> read)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                result.TrySetResult(read());
            }
            catch (Exception e)
            {
                result.TrySetException(e);
            }
        });
        if (!queued) result.TrySetCanceled();
        return result.Task;
    }

    // Cloud embedding confirmation

    /// <summary>
    /// The API embedder sends transcripts off this PC and its destination was not confirmed yet (macOS
    /// <c>embedderNeedsConsent</c>). Loopback hosts (<see cref="ProviderInput.IsLoopback"/>, wider than
    /// <see cref="ITextEmbedder.IsLocal"/>) need no confirmation; a base URL with no destination is never confirmed.
    /// </summary>
    private static bool EmbedderNeedsConsent(AppSettings settings, IReadOnlyList<ProviderConfig> configs) =>
        AppServices.EmbeddingConfig(settings, configs) is { } config && NeedsEmbeddingConsent(settings, config);

    /// <summary>
    /// <paramref name="config"/> as an embedder would send transcripts off this PC to a destination not confirmed yet
    /// (macOS RAGSettingsView <c>needsConsent</c>); a base URL with no destination is never confirmed.
    /// </summary>
    public static bool NeedsEmbeddingConsent(AppSettings settings, ProviderConfig config) =>
        !ProviderInput.IsLoopback(config.BaseURL)
        && (LibraryChat.EmbeddingDestination(config) is not { } destination || !settings.CloudEmbeddingConfirmed.Contains(destination));

    /// <summary>
    /// Asks the window to confirm the current API embedder's destination (Settings › RAG's Confirm…); nothing when it
    /// needs no confirmation.
    /// </summary>
    public void RequestCloudEmbeddingConsent()
    {
        var configs = ProvidersModel.ProviderConfigs;
        if (_closing || !EmbedderNeedsConsent(Settings, configs) || AppServices.EmbeddingConfig(Settings, configs) is not { } config
            || LibraryChat.EmbeddingDestination(config) is not { } destination) return;
        PendingEmbeddingConsent = EmbeddingConsent(config, destination);
    }

    /// <summary>
    /// Makes <paramref name="config"/> the embedder with <paramref name="model"/> (Settings › RAG's pick of an API
    /// embedder, macOS <c>selectAPI</c>): a destination off this PC not confirmed yet asks the window first, and the
    /// setting changes only on Understood. A base URL with no destination is picked without asking: nothing can be sent
    /// to it, and the index says why. Nothing when it is the embedder already.
    /// </summary>
    public void PickAPIEmbedder(ProviderConfig config, string model)
    {
        var id = ModelCatalog.ApiEmbedderPrefix + config.Id;
        if (_closing || Settings.EmbeddingModelID == id) return;
        if (NeedsEmbeddingConsent(Settings, config) && LibraryChat.EmbeddingDestination(config) is { } destination)
        {
            PendingEmbeddingConsent = EmbeddingConsent(config, destination) with { PickProviderId = config.Id, PickModel = model };
            return;
        }
        ApplySettings(Settings with { EmbeddingModelID = id, EmbeddingAPIModel = model });
    }

    private PendingEmbeddingConsent EmbeddingConsent(ProviderConfig config, string destination)
    {
        var host = Uri.TryCreate(config.BaseURL.Trim(), UriKind.Absolute, out var url) ? url.Host : config.BaseURL;
        return new PendingEmbeddingConsent(config.DisplayName, destination, host, Conversations.Count(c => c.HasTranscript));
    }

    /// <summary>
    /// The window's answer: Understood makes a picked provider the embedder, remembers the destination in
    /// <c>cloudEmbeddingConfirmed</c> and indexes (macOS <c>confirm</c> and <c>confirmCloudEmbedding</c>), in one change;
    /// Cancel changes nothing and sends nothing.
    /// </summary>
    public void ResolveCloudEmbeddingConsent(bool allowed)
    {
        if (PendingEmbeddingConsent is not { } pending) return;
        PendingEmbeddingConsent = null;
        if (!allowed || _closing) return;
        var settings = Settings;
        if (pending.PickProviderId is { } pick)
            settings = settings with { EmbeddingModelID = ModelCatalog.ApiEmbedderPrefix + pick, EmbeddingAPIModel = pending.PickModel };
        if (!settings.CloudEmbeddingConfirmed.Contains(pending.Destination))
            settings = settings with { CloudEmbeddingConfirmed = [.. settings.CloudEmbeddingConfirmed, pending.Destination] };
        ApplySettings(settings);
        SyncLibraryIndex();
    }

    // Library chat

    /// <summary>
    /// Asks <paramref name="question"/> across the library as the AI job (no conversation) with the current AI provider
    /// and model (macOS <c>askLibrary</c>): searches the index, then streams the answer, saved with its references in
    /// <see cref="GlobalChatFolder"/>'s <c>chat.json</c>. The job is set before the search so indexing yields. With no
    /// source found the question is still asked: the prompt has the model say the sources hold no answer.
    /// </summary>
    public void AskLibrary(string question)
    {
        question = question.Trim();
        if (_closing || AIJob is not null || ChatChanging || question.Length == 0 || !Settings.RagEnabled
            || ProvidersModel.CurrentAIProvider is not { } provider) return;
        if (Updater.IsInstalling)
        {
            // Windows closes the app once the update is downloaded: the answer would be cut off.
            AIError = new AIErrorInfo(null, InstallingNotice);
            return;
        }
        var model = ProvidersModel.AIModelFor(provider).Trim();
        AIError = null;
        _aiStopRequested = false;
        _aiPendingText.Clear();
        var cancel = new CancellationTokenSource();
        _aiCancel = cancel;
        AIJob = new AIJob(null, AIJobKind.Question, question, Loc.T("Searching your conversations…"),
            UsesLocalModel: provider.Kind == AIProviderKind.InApp);
        // Read on the UI thread: the settings and the provider configs belong to it.
        var config = provider.Kind == AIProviderKind.InApp ? null : ProvidersModel.Config(provider.Id);
        _aiTask = RunLibraryJobAsync(question, provider, config, model, Settings, ProvidersModel.ProviderConfigs, cancel);
    }

    private async Task RunLibraryJobAsync(
        string question, AIProviderInfo provider, ProviderConfig? config, string model, AppSettings settings,
        IReadOnlyList<ProviderConfig> configs, CancellationTokenSource cancel)
    {
        try
        {
            if (await MakeJobClientAsync(provider, config, null, cancel.Token) is not { } client) return;
            var token = cancel.Token;
            var vector = await QueryVectorAsync(question, settings, configs, token);
            var context = await Task.Run(() => client.ContextWindowAsync(model, token).AsTask(), token);
            var folder = GlobalChatFolder;
            // An earlier answer's numbers never point at this question's sources.
            var history = await Task.Run(() => ChatLog.Read(folder).Messages
                .Select(m => m.Role == ChatRole.Assistant ? m with { Text = LibraryChat.WithoutCitations(m.Text) } : m)
                .ToList(), token);
            var limit = settings.ChatContextMessages;
            var hits = await _services.Index.SearchAsync(question, vector, LibrarySearchLimit, token);
            var budget = TranscriptChunker.Budget(context, LibraryChat.PromptTokens(question, history, limit));
            var (sources, references) = LibraryChat.Sources(hits, Conversations, budget);
            if (token.IsCancellationRequested || _closing) return;
            if (AIJob is { } job) AIJob = job with { Status = null };
            await StreamAIJobAsync(_services.AI.AskLibrary(
                question, sources, references, history, limit, client, provider.Id, model, settings.SummaryLanguage, folder,
                token), token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Stopped during the search, before any text: nothing was saved.
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: the library question failed: {e.GetType().Name}");
            if (!_closing) AIError = new AIErrorInfo(null, e.Message);
        }
        finally
        {
            if (ReferenceEquals(_aiCancel, cancel)) _aiCancel = null;
            cancel.Dispose();
            if (!_closing)
            {
                FlushAIText();
                AIJob = null;
                AIJobsEnded++;
            }
        }
        SyncLibraryIndex();
    }

    /// <summary>
    /// The question's embedding (macOS <c>queryVector</c>); null (keyword search only) without a usable embedder, while the
    /// API embedder's destination is not confirmed (the confirmation covers questions too), while the model gate is busy
    /// for an in-app one, or on error. Loads the embedder's index when another one is loaded. An in-app model loads inside
    /// the model gate, for this one query.
    /// </summary>
    private async Task<float[]?> QueryVectorAsync(
        string question, AppSettings settings, IReadOnlyList<ProviderConfig> configs, CancellationToken token)
    {
        var (embedder, _) = await Task.Run(() => _services.MakeEmbedder(settings, configs), token);
        if (embedder is null || (!embedder.IsLocal && EmbedderNeedsConsent(settings, configs))) return null;
        var key = embedder.Key;
        await _services.Index.LoadAsync(key, token);
        var gated = embedder is not APITextEmbedder;
        if (gated && ModelGateBusy) return null;
        try
        {
            using var lease = gated ? await _services.Gate.AcquireAsync(ModelLoadGate.Owner.Embedder, token) : null;
            if (gated) LeaseEmbedder(1);
            try
            {
                var vectors = await Task.Run(
                    () => embedder.WithLoadedAsync(loaded => loaded.EmbedAsync([question], true, token), token), token);
                return vectors.Count > 0 ? vectors[0] : null;
            }
            finally
            {
                if (gated) LeaseEmbedder(-1);
            }
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            Debug.WriteLine($"EchoDiar: could not embed the question: {e.GetType().Name}");
            return null;
        }
    }

    /// <summary>Archives the library chat so the next question starts without history, titled as <see cref="StartFreshChat"/>.</summary>
    public void StartFreshLibraryChat() => _ = ChangeChatAsync(null, GlobalChatFolder, ChatArchive.Archive);

    /// <summary>Makes the archived library chat <paramref name="entryId"/> the open one, as <see cref="ResumeChat"/>.</summary>
    public void ResumeLibraryChat(Guid entryId) =>
        _ = ChangeChatAsync(null, GlobalChatFolder, folder => ChatArchive.Resume(entryId, folder));

    /// <summary>Deletes the archived library chat <paramref name="entryId"/>, as <see cref="DeleteChat"/>.</summary>
    public void DeleteLibraryChat(Guid entryId) =>
        _ = ChangeChatAsync(null, GlobalChatFolder, folder =>
        {
            ChatArchive.Delete(entryId, folder);
            return null;
        });

    /// <summary>Deletes every archived library chat; the open one stays, as <see cref="ClearChats"/>.</summary>
    public void ClearLibraryChats() =>
        _ = ChangeChatAsync(null, GlobalChatFolder, folder =>
        {
            ChatArchive.DeleteAll(folder);
            return null;
        });

    /// <summary>
    /// A reference of a library answer (macOS <c>openReference</c>): the conversation at <paramref name="seconds"/> in its
    /// transcript, or its summary; the library chat stays open beside it. Nothing when the conversation is gone.
    /// </summary>
    public void OpenReference(Guid id, double? seconds)
    {
        if (_closing || Conversations.All(c => c.Id != id)) return;
        GlobalChatOpen = true;
        if (seconds is { } time)
        {
            PendingTranscriptTime = (id, time);
            Open(id, DetailTab.Transcript);
        }
        else
        {
            Open(id, DetailTab.Summary);
        }
    }

    /// <summary>The pending citation time of conversation <paramref name="id"/>, taken once (its page shows it).</summary>
    internal double? TakePendingTranscriptTime(Guid id)
    {
        if (PendingTranscriptTime is not { } pending || pending.Id != id) return null;
        PendingTranscriptTime = null;
        return pending.Seconds;
    }
}
