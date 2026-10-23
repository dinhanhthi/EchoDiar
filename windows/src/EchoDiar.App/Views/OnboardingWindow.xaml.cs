using System.Diagnostics;
using System.Runtime.InteropServices;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Settings;
using EchoDiar.Platform.Audio;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace EchoDiar.App.Views;

/// <summary>
/// The first-run window (macOS PermissionsOnboarding.swift <c>OnboardingWindow</c> and <c>OnboardingView</c>): the
/// consent notice, microphone access with a live level meter, and a system audio test. A new one is made for each show
/// and closed when done, so "Show onboarding again" starts from the first step; the main window stays usable meanwhile
/// (macOS). After <see cref="Window.Closed"/> nothing touches its controls; the meter's mic, the loopback and the tone
/// are let go off the UI thread.
/// </summary>
public sealed partial class OnboardingWindow : Window
{
    /// <summary>
    /// The client area in DIPs (macOS 520 × 460): taller, as Windows' text and buttons are, so step 1's card fits
    /// without scrolling.
    /// </summary>
    private const double ClientWidth = 520;
    private const double ClientHeight = 540;

    private const string CopyGlyph = "";
    private const string CopiedGlyph = "";

    /// <summary>15 Hz: the level meters (macOS SystemAudioTest ticks).</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(67);

    /// <summary>The test listens 3 s at 15 Hz and plays its tone at the fifth tick, inside the capture (macOS).</summary>
    private const int TestTicks = 45;
    private const int ToneTick = 4;
    private const float SilenceThreshold = 0.001f;

    private static readonly string[] Titles = ["Before you record", "Microphone", "System audio"];

    private readonly AppViewModel _model;
    private readonly AppCapability _microphone = AppCapability.Create("microphone");
    private readonly DispatcherQueueTimer _micTimer;
    private int _step;

    /// <summary>Set first in <see cref="Window.Closed"/>: no handler touches a control after it.</summary>
    private bool _closing;

    private AppCapabilityAccessStatus _micStatus = AppCapabilityAccessStatus.UserPromptRequired;

    /// <summary>The meter's mic should run (UI thread); the capture itself is opened and closed on <see cref="_micWork"/>.</summary>
    private bool _micWanted;

    /// <summary>The meter's mic opens and closes in order, off the UI thread: a close queued during a slow open waits for it.</summary>
    private Task _micWork = Task.CompletedTask;

    /// <summary>Touched only on <see cref="_micWork"/>.</summary>
    private MicCapture? _micCapture;

    /// <summary>The loudest sample since the last tick, written by the capture threads.</summary>
    private volatile float _micPeak;
    private volatile float _systemPeak;

    private TestPhase _testPhase;
    private string? _testError;
    private CancellationTokenSource? _testCancel;

    private enum TestPhase { Idle, Running, Heard, Silent, Failed }

    public OnboardingWindow(AppViewModel model, WindowId near)
    {
        _model = model;
        InitializeComponent();
        Title = Loc.T("Welcome to EchoDiar");
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "EchoDiar.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        // Standard Mica, else Desktop Acrylic; with neither, RootGrid keeps the theme's solid background (as MainWindow).
        SystemBackdrop? backdrop = MicaController.IsSupported() ? new MicaBackdrop { Kind = MicaKind.Base }
            : DesktopAcrylicController.IsSupported() ? new DesktopAcrylicBackdrop()
            : null;
        if (backdrop is not null)
        {
            SystemBackdrop = backdrop;
            RootGrid.Background = null;
        }
        // The standard title bar, in the app's Light or Dark; only Close (macOS: titled and closable).
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        Place(near);

        ConsentText.Text = $"\"{Loc.T(GeneralSettingsPage.ConsentMessage)}\"";
        MicDeniedText.Text = Loc.T("Turn on EchoDiar in Windows Settings › Privacy & security › Microphone.");
        SystemAudioText.Text = Loc.T(
            "EchoDiar records the other people on the call from your PC's audio. The test plays a short sound and listens for it.");

        _micTimer = DispatcherQueue.CreateTimer();
        _micTimer.Interval = TickInterval;
        _micTimer.Tick += (_, _) => ShowMicLevel();

        CopyConsentButton.Click += (_, _) => CopyConsent();
        OpenMicSettingsButton.Click += (_, _) =>
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));
        AllowMicButton.Click += (_, _) => _ = AllowMicrophoneAsync();
        TestButton.Click += (_, _) => _ = RunTestAsync();
        TestAgainButton.Click += (_, _) => _ = RunTestAsync();
        BackButton.Click += (_, _) => ShowStep(_step - 1);
        ContinueButton.Click += (_, _) => ShowStep(_step + 1);
        DoneButton.Click += (_, _) => Finish();

        Activated += OnActivated;
        Closed += OnClosed;
        ShowStep(0);
        ShowTest();
    }

    /// <summary>Centered on <paramref name="near"/>'s display, at its scale.</summary>
    private void Place(WindowId near)
    {
        var display = DisplayArea.GetFromWindowId(near, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        AppWindow.Move(new PointInt32(work.X, work.Y));
        var scale = Scale(display);
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(ClientWidth * scale), (int)Math.Round(ClientHeight * scale)));
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
    }

    // Steps

    private void ShowStep(int step)
    {
        if (_closing) return;
        _step = Math.Clamp(step, 0, Titles.Length - 1);
        StepTitleText.Text = Loc.T(Titles[_step]);
        StepCountText.Text = Loc.Format("Step %lld of %lld", _step + 1, Titles.Length);
        ConsentPanel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        MicrophonePanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        SystemAudioPanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        var last = _step == Titles.Length - 1;
        ContinueButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        DoneButton.Visibility = last ? Visibility.Visible : Visibility.Collapsed;
        if (_step == 1) RefreshMicrophone();
        else SyncMicMeter();
    }

    /// <summary>Done: the onboarding is completed (saved through the settings queue), and the window closes.</summary>
    private void Finish()
    {
        if (_closing || _testPhase == TestPhase.Running) return;
        _model.CompleteOnboarding();
        Close();
    }

    /// <summary>The window comes back to the front (from Windows' settings, the consent dialog): access is read again (macOS didBecomeActive).</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_closing || args.WindowActivationState == WindowActivationState.Deactivated) return;
        if (_step == 1) RefreshMicrophone();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        Activated -= OnActivated;
        _micTimer.Stop();
        _testCancel?.Cancel();
        _micWanted = false;
        _micWork = _micWork.ContinueWith(_ => StopMic(), TaskScheduler.Default);
    }

    // Before you record

    /// <summary>The message on the clipboard; the button says "Copied" from then on (macOS).</summary>
    private void CopyConsent()
    {
        if (!GeneralSettingsPage.CopyConsentMessage()) return;
        CopyConsentText.Text = Loc.T("Copied");
        CopyConsentIcon.Glyph = CopiedGlyph;
    }

    // Microphone

    /// <summary>Reads the access state (no prompt), shows it, and runs the meter while access is on.</summary>
    private void RefreshMicrophone()
    {
        if (_closing) return;
        _micStatus = MicrophoneAccess();
        var on = _micStatus == AppCapabilityAccessStatus.Allowed;
        var off = _micStatus is AppCapabilityAccessStatus.DeniedByUser or AppCapabilityAccessStatus.DeniedBySystem
            or AppCapabilityAccessStatus.NotDeclaredByApp;
        MicStatusText.Text = Loc.T(_micStatus switch
        {
            AppCapabilityAccessStatus.Allowed => "Microphone access is on.",
            AppCapabilityAccessStatus.DeniedByUser => "Microphone access is off.",
            AppCapabilityAccessStatus.DeniedBySystem or AppCapabilityAccessStatus.NotDeclaredByApp => "Microphone access is blocked on this PC.",
            _ => "Microphone access has not been requested yet.",
        });
        MicOnIcon.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MicOffIcon.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        MicUnknownIcon.Visibility = !on && !off ? Visibility.Visible : Visibility.Collapsed;
        MicTrack.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        MicDeniedText.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        OpenMicSettingsButton.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        AllowMicButton.Visibility = !on && !off ? Visibility.Visible : Visibility.Collapsed;
        SyncMicMeter();
    }

    private AppCapabilityAccessStatus MicrophoneAccess()
    {
        try
        {
            return _microphone.CheckAccess();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read the microphone access: {e}");
            return AppCapabilityAccessStatus.UserPromptRequired;
        }
    }

    /// <summary>
    /// "Allow Microphone": asks Windows. A full-trust app may get no prompt from the request; opening the mic then shows
    /// the system's consent dialog, and the window's next activation reads the answer.
    /// </summary>
    private async Task AllowMicrophoneAsync()
    {
        AllowMicButton.IsEnabled = false;
        try
        {
            await _microphone.RequestAccessAsync();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: the microphone access request failed: {e}");
        }
        if (_closing) return;
        AllowMicButton.IsEnabled = true;
        RefreshMicrophone();
        if (_micStatus != AppCapabilityAccessStatus.UserPromptRequired) return;
        var endpointId = _model.Settings.MicDeviceUID;
        _micWork = _micWork.ContinueWith(_ =>
        {
            StartMic(endpointId);
            StopMic();
        }, TaskScheduler.Default);
    }

    /// <summary>The meter runs on the Microphone step while access is on and the window is open.</summary>
    private void SyncMicMeter()
    {
        var wanted = !_closing && _step == 1 && _micStatus == AppCapabilityAccessStatus.Allowed;
        if (wanted == _micWanted) return;
        _micWanted = wanted;
        if (wanted)
        {
            // The mic a recording would use.
            var endpointId = _model.Settings.MicDeviceUID;
            _micPeak = 0;
            _micWork = _micWork.ContinueWith(_ => StartMic(endpointId), TaskScheduler.Default);
            _micTimer.Start();
        }
        else
        {
            _micWork = _micWork.ContinueWith(_ => StopMic(), TaskScheduler.Default);
            _micTimer.Stop();
            if (!_closing) MicFill.Width = 0;
        }
    }

    /// <summary>Opens the picked mic, else the default one (the picked one may be gone). On <see cref="_micWork"/>.</summary>
    private void StartMic(string? endpointId)
    {
        if (_micCapture is not null) return;
        string?[] ids = endpointId is null ? [null] : [endpointId, null];
        foreach (var id in ids)
        {
            MicCapture? capture = null;
            try
            {
                capture = new MicCapture(id);
                // Unhandled, a capture error would end the app.
                capture.Failed += e => Debug.WriteLine($"EchoDiar: the onboarding mic failed: {e}");
                capture.Start(OnMicSamples);
                _micCapture = capture;
                return;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"EchoDiar: could not open the mic for the onboarding meter: {e}");
                capture?.Dispose();
            }
        }
    }

    /// <summary>On <see cref="_micWork"/>.</summary>
    private void StopMic()
    {
        var capture = _micCapture;
        _micCapture = null;
        try
        {
            capture?.Dispose();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not close the onboarding mic: {e}");
        }
    }

    private void OnMicSamples(ReadOnlySpan<float> samples, long time, int rate) => _micPeak = Math.Max(_micPeak, Peak(samples));

    private void ShowMicLevel()
    {
        if (_closing) return;
        var peak = _micPeak;
        _micPeak = 0;
        MicFill.Width = MicTrack.ActualWidth * Fraction(peak);
    }

    // System audio

    /// <summary>
    /// macOS SystemAudioTest <c>run</c>: listens 3 s on the default output's loopback (which, unlike the recording's
    /// process loopback, hears EchoDiar's own sound) while a short tone plays at the fifth tick; heard when the loudest
    /// sample passes -60 dBFS.
    /// </summary>
    private async Task RunTestAsync()
    {
        if (_closing || _testPhase == TestPhase.Running) return;
        var cancel = new CancellationTokenSource();
        _testCancel = cancel;
        _testPhase = TestPhase.Running;
        _testError = null;
        _systemPeak = 0;
        ShowTest();

        LoopbackCapture? loopback = null;
        AudioPlayer? player = null;
        string? tone = null;
        var result = TestPhase.Idle;
        try
        {
            var capture = LoopbackCapture.Endpoint();
            loopback = capture;
            // Unhandled, a capture error would end the app.
            capture.Failed += e => Debug.WriteLine($"EchoDiar: the onboarding loopback failed: {e}");
            await Task.Run(() => capture.Start(OnSystemSamples));
            tone = await Task.Run(WriteTone);
            var loudest = 0f;
            for (var tick = 0; tick < TestTicks; tick++)
            {
                await Task.Delay(TickInterval, cancel.Token);
                if (tick == ToneTick) player = PlayTone(tone);
                var peak = _systemPeak;
                _systemPeak = 0;
                loudest = Math.Max(loudest, peak);
                if (!_closing) SystemFill.Width = SystemTrack.ActualWidth * Fraction(peak);
            }
            result = loudest > SilenceThreshold ? TestPhase.Heard : TestPhase.Silent;
        }
        catch (OperationCanceledException)
        {
            result = TestPhase.Idle;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: the system audio test could not start: {e}");
            result = TestPhase.Failed;
            _testError = e.Message;
        }
        finally
        {
            player?.Dispose();
            var path = tone;
            var capture = loopback;
            _ = Task.Run(() =>
            {
                try
                {
                    capture?.Dispose();
                }
                catch (Exception e)
                {
                    Debug.WriteLine($"EchoDiar: could not close the onboarding loopback: {e}");
                }
                DeleteTone(path);
            });
            cancel.Dispose();
            if (_testCancel == cancel) _testCancel = null;
        }
        if (_closing) return;
        _testPhase = result;
        ShowTest();
    }

    private void OnSystemSamples(ReadOnlySpan<float> samples, long time, int rate) =>
        _systemPeak = Math.Max(_systemPeak, Peak(samples));

    /// <summary>Plays the tone on the default output (macOS: afplay); a failure leaves the test to hear silence (macOS).</summary>
    private static AudioPlayer? PlayTone(string path)
    {
        AudioPlayer? player = null;
        try
        {
            player = new AudioPlayer(path, PlaybackChannels.Mixed);
            player.Play();
            return player;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not play the test sound: {e}");
            player?.Dispose();
            return null;
        }
    }

    /// <summary>A 0.5 s 880 Hz tone with 20 ms fades, as a mono 48 kHz CAF in the temp folder.</summary>
    private static string WriteTone()
    {
        const int rate = 48_000;
        const int frames = rate / 2;
        const int fade = rate / 50;
        var samples = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            var envelope = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (double)fade);
            samples[i] = (float)(0.25 * envelope * Math.Sin(2 * Math.PI * 880 * i / rate));
        }
        var path = Path.Combine(Path.GetTempPath(), $"EchoDiar-test-tone-{Guid.NewGuid():N}.caf");
        try
        {
            using var writer = new CafFile.Writer(path, rate, 1, CafFile.SampleFormat.Float32);
            writer.Append(samples);
            writer.Finish();
        }
        catch
        {
            // A half-written file is not left in the temp folder; closed first by the using.
            DeleteTone(path);
            throw;
        }
        return path;
    }

    private static void DeleteTone(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"EchoDiar: could not delete the test sound: {e}");
        }
    }

    /// <summary>The step's controls for the test's phase (macOS SystemAudioStep).</summary>
    private void ShowTest()
    {
        if (_closing) return;
        var phase = _testPhase;
        TestButton.Visibility = phase == TestPhase.Idle ? Visibility.Visible : Visibility.Collapsed;
        ListeningPanel.Visibility = phase == TestPhase.Running ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = phase is TestPhase.Heard or TestPhase.Silent or TestPhase.Failed ? Visibility.Visible : Visibility.Collapsed;
        HeardIcon.Visibility = phase == TestPhase.Heard ? Visibility.Visible : Visibility.Collapsed;
        SilentIcon.Visibility = phase == TestPhase.Silent ? Visibility.Visible : Visibility.Collapsed;
        FailedIcon.Visibility = phase == TestPhase.Failed ? Visibility.Visible : Visibility.Collapsed;
        ResultText.Text = phase switch
        {
            TestPhase.Heard => Loc.T("System audio works."),
            TestPhase.Silent => Loc.T("EchoDiar heard only silence."),
            TestPhase.Failed => Loc.T("The test could not start."),
            _ => "",
        };
        ResultDetailText.Text = phase switch
        {
            TestPhase.Silent => Loc.T("Check that your speakers or headphones are on and not muted, then test again."),
            TestPhase.Failed => _testError ?? "",
            _ => "",
        };
        ResultDetailText.Visibility = ResultDetailText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (phase != TestPhase.Running) SystemFill.Width = 0;
        // macOS: Done waits for a test under way.
        DoneButton.IsEnabled = phase != TestPhase.Running;
    }

    // Levels

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    /// <summary>A peak level (0...1) on a -60...0 dBFS scale (macOS LevelMeter).</summary>
    private static double Fraction(float level) =>
        level > 0 ? Math.Clamp((20 * Math.Log10(level) + 60) / 60, 0, 1) : 0;

    private static double Scale(DisplayArea display)
    {
        var monitor = Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        return GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpi, out _) >= 0 && dpi > 0 ? dpi / 96.0 : 1;
    }

    private const int MdtEffectiveDpi = 0;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
