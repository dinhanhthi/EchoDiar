using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Markup;
using Microsoft.Windows.ApplicationModel.Resources;

namespace EchoDiar.App;

/// <summary>
/// UI strings from Strings/en-US/Resources.resw, generated from the macOS EchoDiar/Localizable.xcstrings by
/// windows/tools/xcstrings_to_resw.py. Pass the macOS source text (the xcstrings key, printf placeholders and
/// all): <c>Loc.T("Delete conversation")</c>, <c>Loc.Format("Step %lld of %lld", step, total)</c>.
/// <para>
/// The resource name is <see cref="Slug"/> of that text. When two source texts share a slug the generator
/// names the later ones (sorted by source text) <c>_2</c>, <c>_3</c>…, and writes every member of such a group,
/// by exact source text, to <c>CollisionKeys</c> in the generated Strings/LocKeys.g.cs, which <see cref="T"/>
/// and <see cref="Format"/> look up before the slug: callers never need the suffixes. <see cref="Key"/> stays
/// for an explicit resource name. Each resw entry carries its source text as the comment.
/// </para>
/// </summary>
public static partial class Loc
{
    private const int MaxSlug = 60;
    private const string Prefix = "S_";

    // Same pattern as PLACEHOLDER in xcstrings_to_resw.py.
    private static readonly Regex Placeholder = new(
        @"%(?:(\d+)\$)?[-+ #0]*\d*(?:\.\d+)?(?:hh|h|ll|l|q|z|t|j|L)?([@dDiuUxXoOfFeEgGcCsSpaA%])",
        RegexOptions.CultureInvariant);

    private static readonly Lazy<ResourceLoader?> Loader = new(() =>
    {
        try
        {
            return new ResourceLoader();
        }
        catch
        {
            return null; // No resources.pri (unpackaged run): every lookup falls back to its text.
        }
    });

    /// <summary>The localized string for a macOS source text, or the text itself when it is not found.</summary>
    public static string T(string text) => Lookup(KeyOf(text)) ?? text;

    /// <summary>The localized string for an explicit resource name, or the name itself when it is not found.</summary>
    public static string Key(string key) => Lookup(key) ?? key;

    /// <summary>
    /// <see cref="T"/> formatted with <paramref name="args"/>. The resw value uses {0}, {1}…; when the text is not
    /// found its printf placeholders are converted the same way, so the fallback is formatted too.
    /// </summary>
    public static string Format(string text, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Lookup(KeyOf(text)) ?? ToNetFormat(text), args);

    /// <summary>The resource name of a source text: its entry in the generated CollisionKeys, else its <see cref="Slug"/>.</summary>
    internal static string KeyOf(string text) => CollisionKeys.TryGetValue(text, out var key) ? key : Slug(text);

    /// <summary>
    /// The resource name of a source text: PascalCase of its ASCII alphanumeric runs, cut to 60 characters, with
    /// the prefix "S_" when that is empty or starts with a digit. Identical to slug() in xcstrings_to_resw.py.
    /// </summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsAsciiLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }
            sb.Append(char.ToUpperInvariant(text[i++]));
            while (i < text.Length && char.IsAsciiLetterOrDigit(text[i]))
            {
                sb.Append(text[i++]);
            }
        }
        var slug = sb.Length > MaxSlug ? sb.ToString(0, MaxSlug) : sb.ToString();
        return slug.Length == 0 || char.IsAsciiDigit(slug[0]) ? Prefix + slug : slug;
    }

    /// <summary>printf placeholders as .NET format items, like to_net_format() in xcstrings_to_resw.py.</summary>
    internal static string ToNetFormat(string text)
    {
        var sb = new StringBuilder();
        var pos = 0;
        var sequential = 0;
        foreach (Match m in Placeholder.Matches(text))
        {
            sb.Append(EscapeBraces(text[pos..m.Index]));
            if (m.Groups[2].Value == "%")
            {
                sb.Append('%');
            }
            else if (m.Groups[1].Success)
            {
                sb.Append('{').Append(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1).Append('}');
            }
            else
            {
                sb.Append('{').Append(sequential++).Append('}');
            }
            pos = m.Index + m.Length;
        }
        sb.Append(EscapeBraces(text[pos..]));
        return sb.ToString();
    }

    private static string EscapeBraces(string s) => s.Replace("{", "{{").Replace("}", "}}");

    private static string? Lookup(string key)
    {
        var loader = Loader.Value;
        if (loader is null)
        {
            return null;
        }
        try
        {
            var value = loader.GetString(key);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            return null; // Missing resource.
        }
    }
}

/// <summary>
/// <c>{local:T Text='Delete conversation'}</c> (with <c>xmlns:local="using:EchoDiar.App"</c>): <see cref="Loc.T"/>
/// of <see cref="Text"/>.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class T : MarkupExtension
{
    public string Text { get; set; } = "";

    protected override object ProvideValue() => Loc.T(Text);
}
