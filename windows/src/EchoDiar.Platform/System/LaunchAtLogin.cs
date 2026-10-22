using Windows.ApplicationModel;

// Not EchoDiar.Platform.System: that namespace would hide System for every System.-qualified name under EchoDiar.Platform.
namespace EchoDiar.Platform.SystemServices;

/// <summary>
/// The package's startup task <see cref="TaskId"/> (Package.appxmanifest, <c>uap5:StartupTask</c>). Needs package
/// identity: every call throws without it, and Settings shows the message.
/// </summary>
public sealed class LaunchAtLogin : ILaunchAtLogin
{
    /// <summary>The manifest's <c>uap5:StartupTask TaskId</c>.</summary>
    public const string TaskId = "EchoDiarStartup";

    public async Task<LaunchAtLoginState> GetStateAsync() => ToState((await GetTaskAsync()).State);

    public async Task<LaunchAtLoginState> RequestEnableAsync() =>
        ToState(await (await GetTaskAsync()).RequestEnableAsync());

    public async Task DisableAsync() => (await GetTaskAsync()).Disable();

    private static async Task<StartupTask> GetTaskAsync() => await StartupTask.GetAsync(TaskId);

    // LaunchAtLoginState lists StartupTaskState's values in its order.
    private static LaunchAtLoginState ToState(StartupTaskState state) => (LaunchAtLoginState)(int)state;
}
