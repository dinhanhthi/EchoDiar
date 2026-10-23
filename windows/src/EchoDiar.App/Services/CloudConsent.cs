using System.Globalization;
using EchoDiar.AI.Cloud;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Services;

/// <summary>
/// The one-time "audio leaves this PC" confirmation of cloud transcription (macOS MainView.swift's "Send audio to …?"
/// alert and AppModel's <c>cloudSTTConfirmed</c> check): asked once per provider and destination origin
/// (<see cref="CloudDestination.ConsentKey"/>), before anything is uploaded, and remembered in the settings'
/// <c>cloudSTTConfirmed</c>. A changed base URL is a new destination, so it asks again; an invalid one is never
/// confirmed. <see cref="AppViewModel.PendingCloudRuns"/> holds the runs that wait; the main window shows the dialog.
/// </summary>
internal static class CloudConsent
{
    /// <summary>Whether <paramref name="config"/>'s destination was confirmed.</summary>
    public static bool IsConfirmed(AppSettings settings, ProviderConfig config) =>
        CloudTranscription.Destination(config) is { } destination && settings.CloudSTTConfirmed.Contains(destination.ConsentKey);

    /// <summary>
    /// The dialog for <paramref name="run"/>: Send (allowed, remembered) or Cancel (nothing is uploaded), Cancel the
    /// default. The macOS text, with "this PC" for "this Mac" (as Settings' other Windows texts).
    /// </summary>
    public static ContentDialog Dialog(XamlRoot root, PendingCloudRun run) => new()
    {
        XamlRoot = root,
        Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
        Title = Loc.Format("Send audio to %@?", run.ProviderName),
        Content = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = string.Format(
                CultureInfo.CurrentCulture,
                "Audio leaves this PC: the recording's mic and system channels are uploaded to {0} ({1}) for transcription "
                + "and billed by them. The provider may keep the audio under its own retention policy. You are asked once "
                + "per provider and destination.",
                run.ProviderName, run.Host),
        },
        PrimaryButtonText = Loc.T("Send"),
        CloseButtonText = Loc.T("Cancel"),
        DefaultButton = ContentDialogButton.Close,
    };
}
