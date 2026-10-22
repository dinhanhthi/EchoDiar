using System.ComponentModel;
using System.Globalization;
using EchoDiar.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace EchoDiar.App.Views;

/// <summary>
/// The order of every language menu (macOS LanguagePicker.swift LanguageOptions): Auto, the first-class content
/// languages, the recent ones, then every other language the engine supports.
/// </summary>
public static class LanguageOptions
{
    public const string Auto = "auto";
    public static readonly IReadOnlyList<string> Pinned = ["en", "fr", "vi"];
    public const int MaxRecent = 5;

    /// <summary>Each code once; <paramref name="selection"/> joins the recent group when no other group has it.</summary>
    public static List<List<string>> Groups(string selection, IEnumerable<string> recent, IEnumerable<string> supported)
    {
        var seen = new HashSet<string>([Auto, .. Pinned]);
        var recentGroup = recent.Where(seen.Add).ToList();
        var others = supported.Where(seen.Add).ToList();
        if (selection.Length > 0 && !seen.Contains(selection)) recentGroup.Add(selection);
        List<List<string>> groups = [[Auto], [.. Pinned], recentGroup, others];
        return [.. groups.Where(g => g.Count > 0)];
    }

    /// <summary>English name (the UI is English only); the code itself when the system has no name for it.</summary>
    public static string Name(string code)
    {
        if (code == Auto) return Loc.T("Auto");
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>Compact label ("VI", "ZH" for "zh-Hans"); lists keep <see cref="Name"/>.</summary>
    public static string ShortCode(string code) =>
        code == Auto ? Loc.T("Auto") : (code.Split('-', '_')[0] is { Length: > 0 } primary ? primary : code).ToUpperInvariant();

    /// <summary>
    /// Fills <paramref name="items"/> with the language groups between separators, the current one checked; a pick sets
    /// the recording language (a marker while recording).
    /// </summary>
    public static void Fill(IList<MenuFlyoutItemBase> items, AppViewModel model)
    {
        items.Clear();
        var selection = model.RecordingLanguage;
        foreach (var group in Groups(selection, model.Settings.RecentLanguages, model.SupportedLanguages()))
        {
            if (items.Count > 0) items.Add(new MenuFlyoutSeparator());
            foreach (var code in group)
            {
                var item = new RadioMenuFlyoutItem { Text = Name(code), GroupName = "RecordingLanguage", IsChecked = code == selection };
                item.Click += (_, _) => model.SetRecordingLanguage(code);
                items.Add(item);
            }
        }
    }
}

/// <summary>
/// A globe and the recording language, opening the language menu (macOS RecordingViews.swift languageMenu): the short
/// code on the recording's card, the name on the recording page.
/// </summary>
public sealed partial class LanguagePicker : UserControl
{
    private readonly AppViewModel _model;
    private readonly bool _shortLabel;

    public LanguagePicker(AppViewModel model, bool shortLabel)
    {
        _model = model;
        _shortLabel = shortLabel;
        InitializeComponent();
        if (shortLabel)
        {
            LabelText.FontSize = 12;
            GlobeIcon.FontSize = 12;
        }
        LanguageMenu.Opening += (_, _) => LanguageOptions.Fill(LanguageMenu.Items, _model);
        Loaded += (_, _) =>
        {
            _model.PropertyChanged += OnModelChanged;
            UpdateLabel();
        };
        Unloaded += (_, _) => _model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppViewModel.RecordingLanguage)) UpdateLabel();
    }

    private void UpdateLabel()
    {
        var language = _model.RecordingLanguage;
        LabelText.Text = _shortLabel ? LanguageOptions.ShortCode(language) : LanguageOptions.Name(language);
    }
}
