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
/// A cloud embedder of the library index asks the same way (<see cref="EmbeddingDialog"/>, <c>cloudEmbeddingConfirmed</c>).
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
            Text = Loc.Format(
                "Audio leaves this PC: the recording's mic and system channels are uploaded to %@ (%@) for transcription and billed by them. The provider may keep the audio under its own retention policy. You are asked once per provider and destination.",
                run.ProviderName, run.Host),
        },
        PrimaryButtonText = Loc.T("Send"),
        CloseButtonText = Loc.T("Cancel"),
        DefaultButton = ContentDialogButton.Close,
    };

    /// <summary>
    /// The one-time "transcripts leave this PC" confirmation of a cloud embedder (macOS RAGSettingsView.swift's "Send
    /// transcripts to …?" alert and its <c>consentMessage</c>): Understood (allowed, remembered in
    /// <c>cloudEmbeddingConfirmed</c>) or Cancel (nothing is sent), Cancel the default.
    /// </summary>
    public static ContentDialog EmbeddingDialog(XamlRoot root, PendingEmbeddingConsent consent) => new()
    {
        XamlRoot = root,
        Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
        Title = Loc.Format("Send transcripts to %@?", consent.ProviderName),
        Content = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.Plural(
                "Transcripts and summaries of %lld conversations, and your questions, will be sent to %@ to build and search the index, and billed by %@. The provider may keep them under its own retention policy. You are asked once per provider and destination.",
                consent.Conversations, consent.Conversations, consent.Host, consent.ProviderName),
        },
        PrimaryButtonText = Loc.T("Understood"),
        CloseButtonText = Loc.T("Cancel"),
        DefaultButton = ContentDialogButton.Close,
    };
}
