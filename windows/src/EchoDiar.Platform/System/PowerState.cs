using Windows.System.Power;

// Not EchoDiar.Platform.System: that namespace would hide System for every System.-qualified name under EchoDiar.Platform.
namespace EchoDiar.Platform.SystemServices;

/// <summary>
/// Windows energy saver (<see cref="PowerManager.EnergySaverStatus"/>), the stand-in for the macOS Low Power Mode that
/// defers transcription. Follows <see cref="PowerManager.EnergySaverStatusChanged"/> until disposed.
/// </summary>
public sealed class PowerState : IDisposable
{
    public PowerState() => PowerManager.EnergySaverStatusChanged += OnStatusChanged;

    /// <summary>Energy saver is on (always on, or turned on by the battery level).</summary>
    public bool IsEnergySaverOn => PowerManager.EnergySaverStatus == EnergySaverStatus.On;

    /// <summary>Raised on a system thread when the energy saver status changes.</summary>
    public event Action? Changed;

    public void Dispose() => PowerManager.EnergySaverStatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, object e) => Changed?.Invoke();
}
