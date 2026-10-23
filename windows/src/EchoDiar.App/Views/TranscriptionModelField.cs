using CommunityToolkit.WinUI.Controls;
using EchoDiar.AI.Cloud;
using EchoDiar.App.ViewModels;
using EchoDiar.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// A cloud provider's transcription model (macOS ProvidersSettingsView.swift <c>TranscriptionModelField</c>): free text,
/// empty = the vendor default shown as the placeholder, and a Models menu of the preset's known models when it has more
/// than one. Shared by the API rows of Settings › AI and the speech-to-text rows of Settings › Transcription; made in
/// code and updated in place, so typing never rebuilds it.
/// </summary>
internal sealed class TranscriptionModelField
{
    /// <summary>Below this width the card puts the field under its label (the provider rows' fields do the same).</summary>
    private const double WrapWidth = 640;

    private readonly ProvidersViewModel _providers;
    private readonly string _id;
    private readonly Func<bool> _ignoreChanges;
    private readonly TextBox _box = new();
    private readonly DropDownButton _models = new() { Visibility = Visibility.Collapsed };
    private readonly MenuFlyout _menu = new();
    private IReadOnlyList<string> _known = [];

    /// <param name="ignoreChanges">True while the page sets its controls, or once it closes.</param>
    public TranscriptionModelField(ProvidersViewModel providers, string id, Func<bool> ignoreChanges)
    {
        _providers = providers;
        _id = id;
        _ignoreChanges = ignoreChanges;
        AutomationProperties.SetName(_box, Loc.T("Transcription model"));
        _box.TextChanged += (_, _) => Set(_box.Text);
        _models.Content = Loc.T("Models");
        _models.Flyout = _menu;
        Card = new SettingsCard { Header = Loc.T("Transcription model"), Content = new FieldPanel { InputWidth = 260, Children = { _box, _models } } };
        Card.Resources["SettingsCardWrapThreshold"] = WrapWidth;
    }

    public SettingsCard Card { get; }

    /// <summary>Shows <paramref name="config"/>; the text the user is typing stays as it is.</summary>
    public void Update(ProviderConfig config)
    {
        if (_box.Text != config.TranscriptionModel) _box.Text = config.TranscriptionModel;
        var fallback = CloudTranscription.DefaultModel(config);
        _box.PlaceholderText = fallback.Length == 0 ? Loc.T("Model id, e.g. whisper-1") : fallback;
        IReadOnlyList<string> known = OpenAICompatibleTranscriber.PresetModels.TryGetValue(config.Preset ?? "", out var models) ? models : [];
        _models.Visibility = known.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (known.SequenceEqual(_known)) return;
        _known = known;
        _menu.Items.Clear();
        foreach (var model in known)
        {
            var item = new MenuFlyoutItem { Text = model };
            item.Click += (_, _) => Set(model);
            _menu.Items.Add(item);
        }
    }

    private void Set(string model)
    {
        if (_ignoreChanges() || _providers.Config(_id) is not { } config || config.TranscriptionModel == model) return;
        _providers.UpdateProvider(_id, c => c.TranscriptionModel = model);
    }
}
