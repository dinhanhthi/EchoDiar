using EchoDiar.Platform.SystemServices;

namespace EchoDiar.App.Services;

/// <summary>
/// Stand-in for the startup task until task 14.3 adds Platform's <c>LaunchAtLogin</c> (then <see cref="AppServices"/>
/// creates that instead): it registers nothing and remembers the choice for this run only.
/// </summary>
internal sealed class LaunchAtLoginStub : ILaunchAtLogin
{
    private LaunchAtLoginState _state = LaunchAtLoginState.Disabled;

    public Task<LaunchAtLoginState> GetStateAsync() => Task.FromResult(_state);

    public Task<LaunchAtLoginState> RequestEnableAsync()
    {
        _state = LaunchAtLoginState.Enabled;
        return Task.FromResult(_state);
    }

    public Task DisableAsync()
    {
        _state = LaunchAtLoginState.Disabled;
        return Task.CompletedTask;
    }
}
