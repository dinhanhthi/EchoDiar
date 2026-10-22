// Not EchoDiar.Platform.System: that namespace would hide System for every System.-qualified name under EchoDiar.Platform.
namespace EchoDiar.Platform.SystemServices;

/// <summary>
/// Whether EchoDiar starts when the user signs in: the states of <c>Windows.ApplicationModel.StartupTaskState</c>,
/// in its order (the macOS counterpart is <c>SMAppService.Status</c>).
/// </summary>
public enum LaunchAtLoginState
{
    /// <summary>Off; turning it on asks the user once.</summary>
    Disabled,

    /// <summary>Turned off by the user (Settings › Apps › Startup, Task Manager); only they can turn it back on there.</summary>
    DisabledByUser,

    /// <summary>On.</summary>
    Enabled,

    /// <summary>Turned off by an administrator's policy; it cannot be turned on.</summary>
    DisabledByPolicy,

    /// <summary>Turned on by an administrator's policy; it cannot be turned off.</summary>
    EnabledByPolicy,
}

/// <summary>
/// The app's startup task (macOS GeneralSettingsView.swift: <c>SMAppService.mainApp</c>), implemented by
/// <see cref="LaunchAtLogin"/> over <c>StartupTask</c> (TaskId <c>EchoDiarStartup</c>).
/// Every call may throw (no package identity, no startup task in the manifest); Settings shows the message.
/// </summary>
public interface ILaunchAtLogin
{
    /// <summary>The current state (<c>StartupTask.GetAsync</c>, <c>State</c>).</summary>
    Task<LaunchAtLoginState> GetStateAsync();

    /// <summary>
    /// Asks to turn it on (<c>RequestEnableAsync</c>): the first time Windows asks the user. Returns the state after
    /// the request: <see cref="LaunchAtLoginState.DisabledByUser"/> when the user refused or turned it off earlier.
    /// </summary>
    Task<LaunchAtLoginState> RequestEnableAsync();

    /// <summary>Turns it off (<c>Disable</c>); nothing happens under a policy.</summary>
    Task DisableAsync();
}
