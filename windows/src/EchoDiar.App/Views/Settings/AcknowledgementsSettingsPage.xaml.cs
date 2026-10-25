using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using EchoDiar.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.System;

namespace EchoDiar.App.Views;

/// <summary>
/// A component EchoDiar includes or downloads (macOS <c>Acknowledgement</c>): its name (also its section's first line in
/// ThirdPartyNotices.txt), version, what EchoDiar uses it for, license, home page, and a note on the license.
/// </summary>
public sealed record Acknowledgement(string Name, string? Version, string Purpose, string License, string Url, string? Note = null);

/// <summary>Settings › Acknowledgements (macOS AcknowledgementsSettingsView.swift).</summary>
public sealed partial class AcknowledgementsSettingsPage : UserControl
{
    /// <summary>The software, versions as in windows/Directory.Packages.props (native libraries: the package that ships them).</summary>
    public static IReadOnlyList<Acknowledgement> Software { get; } =
    [
        new("Whisper.net", "1.9.1", "Whisper transcription", "MIT", "https://github.com/sandrohanea/whisper.net"),
        new("whisper.cpp", "Whisper.net.Runtime 1.9.1", "Used by Whisper.net", "MIT", "https://github.com/ggml-org/whisper.cpp"),
        new("sherpa-onnx", "1.13.8", "Parakeet transcription and speaker diarization", "Apache-2.0", "https://github.com/k2-fsa/sherpa-onnx"),
        new("ONNX Runtime", "sherpa-onnx 1.13.8", "Used by sherpa-onnx", "MIT", "https://github.com/microsoft/onnxruntime"),
        new("Silero VAD", "6.2.1", "Speech detection for Whisper transcription", "MIT", "https://github.com/snakers4/silero-vad"),
        new("LLamaSharp", "0.30.0", "Local language and embedding models", "MIT", "https://github.com/SciSharp/LLamaSharp"),
        new("llama.cpp", "LLamaSharp.Backend.Cpu 0.30.0", "Used by LLamaSharp", "MIT", "https://github.com/ggml-org/llama.cpp"),
        new("NAudio", "3.3.0", "Audio capture and playback", "MIT", "https://github.com/naudio/NAudio"),
        new(".NET Community Toolkit", "8.4.2", "App structure (MVVM)", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new("Windows Community Toolkit", "8.3.260402-preview2", "Settings cards", "MIT", "https://github.com/CommunityToolkit/Windows"),
        new("Windows App SDK", "2.5.1", "WinUI 3 and the app platform", "MIT", "https://github.com/microsoft/WindowsAppSDK",
            "Its runtime files come under the Microsoft Software License Terms."),
    ];

    /// <summary>The models EchoDiar downloads (Core ModelCatalog).</summary>
    public static IReadOnlyList<Acknowledgement> Models { get; } =
    [
        new("Whisper models", null, "Whisper transcription", "MIT", "https://github.com/openai/whisper"),
        new("Parakeet TDT 0.6B v3", null, "Parakeet transcription", "CC-BY-4.0", "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3"),
        new("pyannote segmentation 3.0", null, "Speaker diarization", "MIT", "https://huggingface.co/pyannote/segmentation-3.0"),
        new("3D-Speaker CAM++", null, "Speaker diarization", "Apache-2.0", "https://github.com/modelscope/3D-Speaker"),
        new("PhoWhisper", null, "Vietnamese transcription", "BSD-3-Clause", "https://github.com/VinAIResearch/PhoWhisper",
            "The ggml conversion's model card says Apache-2.0; the original weights by VinAI are BSD-3-Clause."),
    ];

    /// <summary>The searchable rows (macOS SettingsSearch.swift: one row, the package names as keywords).</summary>
    public static IReadOnlyList<SettingsEntry> SearchEntries { get; } =
    [
        new("Open-source software",
            ["acknowledgements", "credits", "licenses", "libraries", "models", "third-party notices", .. Software.Select(a => a.Name), .. Models.Select(a => a.Name)]),
    ];

    private static readonly string NoticesPath = Path.Combine(AppContext.BaseDirectory, "Licenses", "ThirdPartyNotices.txt");

    /// <summary>Each component's section of the notices, by its name; empty when the file is missing.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Notices = new(ReadNotices);

    public AcknowledgementsSettingsPage()
    {
        InitializeComponent();
        foreach (var item in Software) SoftwareList.Children.Add(Row(item));
        foreach (var item in Models) ModelList.Children.Add(Row(item));
        OpenNoticesButton.Click += (_, _) => _ = OpenNoticesAsync();
    }

    /// <summary>Name; version, purpose and license; the home page link; the license text when expanded.</summary>
    private static SettingsExpander Row(Acknowledgement item)
    {
        var description = string.Join(" · ", new[] { item.Version, Loc.T(item.Purpose), item.License }.OfType<string>());
        if (item.Note is { } note) description += "\n" + Loc.T(note);
        var link = new HyperlinkButton
        {
            NavigateUri = new Uri(item.Url),
            Content = new FontIcon { FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"], FontSize = 14, Glyph = "" },
        };
        ToolTipService.SetToolTip(link, item.Url);
        AutomationProperties.SetName(link, item.Url);

        var text = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        var expander = new SettingsExpander
        {
            Header = item.Name,
            Description = description,
            Content = link,
        };
        expander.Items.Add(new SettingsCard { ContentAlignment = ContentAlignment.Left, Content = text });
        // Read when first expanded: most rows are never opened.
        expander.Expanded += (_, _) =>
        {
            if (text.Text.Length == 0) text.Text = Notices.Value.GetValueOrDefault(item.Name) ?? Loc.T("The license text is missing from this build.");
        };
        return expander;
    }

    /// <summary>
    /// The notices' sections by name: each starts after a line of '=' with the component's name, then its version, home
    /// page, license and the license text.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ReadNotices()
    {
        var sections = new Dictionary<string, string>();
        string text;
        try
        {
            text = File.ReadAllText(NoticesPath).Replace("\r\n", "\n");
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not read {NoticesPath}: {e}");
            return sections;
        }
        foreach (var block in text.Split(new string('=', 80) + "\n").Skip(1))
        {
            var newline = block.IndexOf('\n');
            if (newline <= 0) continue;
            sections[block[..newline].Trim()] = block[(newline + 1)..].TrimEnd();
        }
        return sections;
    }

    /// <summary>The whole notices file in the app for text files.</summary>
    private static async Task OpenNoticesAsync()
    {
        try
        {
            await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(NoticesPath));
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar: could not open {NoticesPath}: {e}");
        }
    }
}
