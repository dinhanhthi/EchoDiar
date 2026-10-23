using System.Runtime.CompilerServices;
using System.Text;
using EchoDiar.AI;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.Text;
using Windows.ApplicationModel;

namespace EchoDiar.Platform.AI;

/// <summary>A Limited Access Feature unlock (Microsoft issues all three strings with the token).</summary>
public sealed record LimitedAccessFeatureToken(string FeatureId, string Token, string Attestation)
{
    // ToString (logs, exceptions, the debugger) never shows the token or the attestation.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("FeatureId = ").Append(FeatureId).Append(", Token = [redacted], Attestation = [redacted]");
        return true;
    }
}

/// <summary>
/// Phi Silica through Windows AI (Copilot+ PCs), the Windows counterpart of Swift <c>AppleFoundationModelProvider</c>.
/// Nothing here touches the Windows AI API until <see cref="ReadyState"/> or a job calls it, so creating the provider
/// at app start is free. Every job gets a new <see cref="LanguageModel"/> and context.
/// </summary>
/// <param name="unlock">Unlocks the feature before the first ready-state check; null skips the unlock (Q8: no token).</param>
public sealed class WindowsAIProvider(LimitedAccessFeatureToken? unlock = null) : ILocalLLMProvider
{
    /// <summary>The only model; matches the catalog entry id.</summary>
    public const string ModelID = "windows-phi-silica";
    /// <summary>Windows AI has no context-size API; Phi Silica's window.</summary>
    internal const int FallbackContext = 4096;

    private readonly object _lock = new();
    private bool? _unlocked;

    /// <summary>
    /// The Windows AI ready state; any failure of the API (also its projection assembly missing, as in Platform.Tests)
    /// reads as <see cref="WindowsAIReadyState.ApiUnavailable"/>.
    /// </summary>
    public WindowsAIReadyState ReadyState()
    {
        try
        {
            return Unlocked() ? QueryReadyState() : WindowsAIReadyState.Locked;
        }
        catch (Exception)
        {
            return WindowsAIReadyState.ApiUnavailable;
        }
    }

    // Not inlined: loading the Windows AI projection fails inside ReadyState's try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WindowsAIReadyState QueryReadyState() => (WindowsAIReadyState)(int)LanguageModel.GetReadyState();

    /// <summary>Why Phi Silica cannot run now, and whether pickers hide it (<see cref="WindowsAIAvailability.Describe"/>).</summary>
    public (string? Reason, bool Hidden) Availability() => WindowsAIAvailability.Describe(ReadyState());

    /// <summary>The system loads and unloads the model; a not-ready model is installed first (<c>EnsureReadyAsync</c>).</summary>
    public Task<T> WithLoadedModelAsync<T>(
        string model, Func<ILocalLLMGenerator, Task<T>> body, CancellationToken cancellationToken = default)
    {
        var state = ReadyState();
        if (WindowsAIAvailability.Describe(state).Reason is { } reason) return Task.FromException<T>(LLMException.Provider(reason));
        return RunAsync(state == WindowsAIReadyState.NotReady, body, cancellationToken);
    }

    private static async Task<T> RunAsync<T>(
        bool ensureReady, Func<ILocalLLMGenerator, Task<T>> body, CancellationToken cancellationToken)
    {
        if (ensureReady)
        {
            var ready = await LanguageModel.EnsureReadyAsync().AsTask(cancellationToken);
            if (ready.Status != AIFeatureReadyResultState.Success)
            {
                throw LLMException.Provider(string.IsNullOrWhiteSpace(ready.ErrorDisplayText)
                    ? "Windows AI could not prepare its model."
                    : ready.ErrorDisplayText);
            }
        }
        using var languageModel = await LanguageModel.CreateAsync().AsTask(cancellationToken);
        return await body(new Generator(languageModel));
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([ModelID]);

    public ValueTask<int> ContextWindowAsync(string model, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(FallbackContext);

    private bool Unlocked()
    {
        if (unlock is null) return true;
        lock (_lock)
        {
            if (_unlocked is { } done) return done;
            try
            {
                var status = LimitedAccessFeatures.TryUnlockFeature(unlock.FeatureId, unlock.Token, unlock.Attestation).Status;
                _unlocked = status is LimitedAccessFeatureStatus.Available or LimitedAccessFeatureStatus.AvailableWithoutToken;
            }
            catch (Exception)
            {
                _unlocked = false;
            }
            return _unlocked.Value;
        }
    }

    /// <summary>
    /// The system messages as the context and the earlier turns flattened ahead of the last message, which is the
    /// prompt: Windows AI has no conversation API.
    /// </summary>
    internal static (string Context, string Prompt) Split(IReadOnlyList<LLMMessage> messages)
    {
        var context = string.Join("\n\n", messages.Where(m => m.Role == LLMRole.System).Select(m => m.Content));
        var turns = messages.Where(m => m.Role != LLMRole.System).ToList();
        if (turns.Count == 0) return (context, "");
        var prompt = new StringBuilder();
        foreach (var turn in turns.Take(turns.Count - 1))
        {
            prompt.Append(turn.Role == LLMRole.User ? "User: " : "Assistant: ").Append(turn.Content).Append("\n\n");
        }
        if (prompt.Length > 0) prompt.Append("User: ");
        prompt.Append(turns[^1].Content);
        return (context, prompt.ToString());
    }

    private static string ResponseError(LanguageModelResponseStatus status) => status switch
    {
        LanguageModelResponseStatus.PromptLargerThanContext => "The text is too long for Windows AI.",
        LanguageModelResponseStatus.PromptBlockedByContentModeration => "Windows AI's content filter blocked the request.",
        LanguageModelResponseStatus.ResponseBlockedByContentModeration => "Windows AI's content filter blocked the answer.",
        LanguageModelResponseStatus.BlockedByPolicy => "A policy on this PC blocks Windows AI.",
        LanguageModelResponseStatus.UnsupportedLanguage or LanguageModelResponseStatus.LanguageMismatch =>
            "Windows AI does not support this language.",
        _ => "Windows AI could not answer.",
    };

    private sealed class Generator(LanguageModel model) : ILocalLLMGenerator
    {
        /// <summary>Progress strings are the new text; on cancel the text so far is returned (22.3 contract).</summary>
        public async Task<string> GenerateAsync(
            IReadOnlyList<LLMMessage> messages, int maxTokens, Action<string> onText,
            CancellationToken cancellationToken = default)
        {
            // LanguageModelOptions has no output-token limit, so maxTokens is not passed on.
            var (instructions, prompt) = Split(messages);
            using var context = model.CreateContext(instructions);
            var sent = new StringBuilder();
            var gate = new object();
            var stopped = false;
            var operation = model.GenerateResponseAsync(context, prompt, new LanguageModelOptions());
            operation.Progress = (_, delta) =>
            {
                lock (gate)
                {
                    if (stopped || string.IsNullOrEmpty(delta)) return;
                    sent.Append(delta);
                    onText(delta);
                }
            };
            LanguageModelResponseResult result;
            try
            {
                result = await operation.AsTask(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (gate)
                {
                    stopped = true;
                    return sent.ToString();
                }
            }
            lock (gate) stopped = true;
            if (result.Status != LanguageModelResponseStatus.Complete) throw LLMException.Provider(ResponseError(result.Status));
            return result.Text;
        }
    }
}
