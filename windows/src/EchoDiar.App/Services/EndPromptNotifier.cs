using System.Diagnostics;
using System.Globalization;
using EchoDiar.Platform.Windowing;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace EchoDiar.App.Services;

/// <summary>
/// The Windows notification beside the pill's "meeting ended" prompt, while EchoDiar is not in front (macOS
/// EndPromptNotifier.swift): "Stop now" and "Keep recording" act like the pill's buttons, a click on the toast opens the
/// recording page. One toast at a time, under a fixed tag and group, so a new prompt replaces it and
/// <see cref="Dismiss"/> removes it. Every activation carries its prompt's deadline (<see cref="Activation.Deadline"/>), so
/// a leftover toast's buttons never act on a later prompt. Shows and removals run one after the other, in call order
/// (on the UI thread): a removal still under way never deletes the toast shown after it. No sound: a chime would land in the recording (speakers into the mic, or the
/// system audio capture).
/// <para>
/// <see cref="ParseAction"/> reads an activation's arguments (<c>AppNotificationManager.NotificationInvoked</c>, or a
/// cold start's <c>AppNotificationActivatedEventArgs</c>). Registration (<c>AppNotificationManager.Default.Register</c>)
/// is the App's: without it, as in an unpackaged dev run, every call here fails and is only logged.
/// </para>
/// </summary>
internal sealed class EndPromptNotifier
{
    /// <summary>What a toast activation asks.</summary>
    public enum Response
    {
        /// <summary>Not this toast's activation.</summary>
        None,

        /// <summary>A click on the toast itself: show the main window (the recording page while recording).</summary>
        Open,

        /// <summary>"Keep recording".</summary>
        Keep,

        /// <summary>"Stop now": only while the prompt is still "meeting ended" (a leftover toast never stops a recording).</summary>
        Stop,
    }

    private const string Tag = "endPrompt";
    private const string Group = "recording";
    private const string ActionKey = "endPromptAction";
    private const string OpenValue = "open";
    private const string KeepValue = "keep";
    private const string StopValue = "stop";
    private const string DeadlineKey = "endPromptDeadline";

    private readonly Func<bool> _enabled;

    /// <summary>The last show or removal queued; each waits for the one before it and never faults.</summary>
    private Task _last = Task.CompletedTask;

    /// <summary>A toast activation: what it asks, and its prompt's deadline (UTC ticks; null when it has none).</summary>
    public readonly record struct Activation(Response Response, long? Deadline);

    /// <param name="enabled">The <c>EndPromptNotification</c> setting, read at each <see cref="Show"/>.</param>
    public EndPromptNotifier(Func<bool> enabled)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// Shows (or replaces) the toast: <paramref name="question"/> as its title, "Stopping in n s" as its body; nothing
    /// when the setting is off, notifications are off for EchoDiar, or EchoDiar has the foreground window (macOS: the
    /// active app). Decided and built now; shown once the removals queued before it are done.
    /// </summary>
    public void Show(string question, DateTimeOffset deadline)
    {
        AppNotificationManager manager;
        AppNotification notification;
        try
        {
            if (!_enabled() || PillWindowStyle.ForegroundProcessId() == Environment.ProcessId)
            {
                StartupTrace.Write("end prompt toast skipped: setting off or EchoDiar in front");
                return;
            }
            manager = AppNotificationManager.Default;
            if (manager.Setting != AppNotificationSetting.Enabled)
            {
                StartupTrace.Write($"end prompt toast skipped: notifications {manager.Setting}");
                return;
            }
            var token = deadline.UtcTicks.ToString(CultureInfo.InvariantCulture);
            var left = Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.Now).TotalSeconds));
            var stop = new AppNotificationButton(Loc.T("Stop now"))
                .AddArgument(ActionKey, StopValue)
                .AddArgument(DeadlineKey, token);
            if (AppNotificationButton.IsButtonStyleSupported()) stop.SetButtonStyle(AppNotificationButtonStyle.Critical);
            notification = new AppNotificationBuilder()
                .AddArgument(ActionKey, OpenValue)
                .AddArgument(DeadlineKey, token)
                .AddText(question)
                .AddText(Loc.Format("Stopping in %lld s", left))
                .AddButton(stop)
                .AddButton(new AppNotificationButton(Loc.T("Keep recording"))
                    .AddArgument(ActionKey, KeepValue)
                    .AddArgument(DeadlineKey, token))
                .MuteAudio()
                .SetTag(Tag)
                .SetGroup(Group)
                .BuildNotification();
            // Gone from the notification center once the recording has stopped on its own.
            if (deadline > DateTimeOffset.Now) notification.Expiration = deadline;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not show the end prompt notification: {e}");
            return;
        }
        _last = After(_last, () =>
        {
            manager.Show(notification);
            StartupTrace.Write("end prompt toast shown");
            return Task.CompletedTask;
        }, "show");
    }

    /// <summary>
    /// Removes the toast, shown or in the notification center, once the shows queued before are done; nothing when there
    /// is none.
    /// </summary>
    public void Dismiss() =>
        _last = After(_last, async () => await AppNotificationManager.Default.RemoveByTagAndGroupAsync(Tag, Group), "remove");

    /// <summary><paramref name="step"/> once <paramref name="previous"/> is done; its error is only logged.</summary>
    private static async Task After(Task previous, Func<Task> step, string what)
    {
        await previous;
        try
        {
            await step();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not {what} the end prompt notification: {e}");
        }
    }

    /// <summary>
    /// Whether notifications are off for EchoDiar (in Windows Settings, by policy...), for the setting's note; false when
    /// it cannot be read, as in an unpackaged dev run.
    /// </summary>
    public static bool NotificationsOff()
    {
        try
        {
            return AppNotificationManager.Default.Setting != AppNotificationSetting.Enabled;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read the notification setting: {e}");
            return false;
        }
    }

    /// <summary>
    /// What an activation's arguments ask, and for which prompt; <see cref="Response.None"/> for any other toast's.
    /// </summary>
    public static Activation ParseAction(IDictionary<string, string> arguments)
    {
        var response = arguments.TryGetValue(ActionKey, out var value)
            ? value switch
            {
                OpenValue => Response.Open,
                KeepValue => Response.Keep,
                StopValue => Response.Stop,
                _ => Response.None,
            }
            : Response.None;
        long? deadline = arguments.TryGetValue(DeadlineKey, out var token)
            && long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                ? ticks
                : null;
        return new Activation(response, deadline);
    }

    /// <summary>Whether <paramref name="activation"/> came from the toast of the prompt with <paramref name="deadline"/>.</summary>
    public static bool IsFor(Activation activation, DateTimeOffset deadline) => activation.Deadline == deadline.UtcTicks;
}
