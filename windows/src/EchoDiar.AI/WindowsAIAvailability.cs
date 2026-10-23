namespace EchoDiar.AI;

/// <summary>
/// <c>Microsoft.Windows.AI.AIFeatureReadyState</c> (Windows App SDK AI 2.5.5) with the same values, so the Platform
/// provider casts the real state by number; plus the states the API cannot report itself.
/// </summary>
public enum WindowsAIReadyState
{
    Ready = 0,
    NotReady = 1,
    NotSupportedOnCurrentSystem = 2,
    DisabledByUser = 3,
    CapabilityMissing = 4,
    NotCompatibleWithSystemHardware = 5,
    OSUpdateNeeded = 6,
    /// <summary>The Windows AI API threw (an unpackaged process, an older Windows, no runtime): treated as not supported.</summary>
    ApiUnavailable = 100,
    /// <summary>A Limited Access Feature token was configured, but unlocking it failed.</summary>
    Locked = 101,
}

/// <summary>What the Windows AI (Phi Silica) ready state means for the AI pickers (Swift <c>AppleFoundationModelProvider.unavailableReason</c>).</summary>
public static class WindowsAIAvailability
{
    /// <summary>
    /// Why Phi Silica cannot run now (nil when it can), and whether pickers hide it because this PC can never run it.
    /// <see cref="WindowsAIReadyState.NotReady"/> is usable: the first job runs <c>EnsureReadyAsync</c>, which installs the model.
    /// </summary>
    public static (string? Reason, bool Hidden) Describe(WindowsAIReadyState state) => state switch
    {
        WindowsAIReadyState.Ready or WindowsAIReadyState.NotReady => (null, false),
        WindowsAIReadyState.NotSupportedOnCurrentSystem or WindowsAIReadyState.ApiUnavailable =>
            ("Windows AI is not supported on this PC (it needs a Copilot+ PC).", true),
        WindowsAIReadyState.NotCompatibleWithSystemHardware =>
            ("This PC's hardware does not support Windows AI (it needs a Copilot+ PC).", true),
        WindowsAIReadyState.CapabilityMissing =>
            ("This build of EchoDiar cannot use Windows AI (the systemAIModels capability is missing).", true),
        WindowsAIReadyState.Locked => ("Windows AI is not unlocked for EchoDiar.", true),
        WindowsAIReadyState.DisabledByUser =>
            ("Windows AI is turned off (Settings › Privacy & security › Text and image generation).", false),
        WindowsAIReadyState.OSUpdateNeeded => ("Windows AI needs a Windows update (Settings › Windows Update).", false),
        _ => ("Windows AI is not available on this PC.", false),
    };
}
