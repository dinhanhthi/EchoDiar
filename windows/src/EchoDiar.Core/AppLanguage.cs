namespace EchoDiar.Core;

/// <summary>Swift <c>AppLanguage</c>: the app's interface language; <see cref="System"/> follows Windows.</summary>
public enum AppLanguage { System, En, Vi, Fr, Es, ZhHans }

/// <summary>
/// Swift <c>AppLanguage</c>'s mapping, without WinRT: the App writes <see cref="OverrideTag"/> to
/// <c>ApplicationLanguages.PrimaryLanguageOverride</c> and reads it back with <see cref="Matching"/>.
/// </summary>
public static class AppLanguages
{
    /// <summary>The picker's order, as on macOS.</summary>
    public static IReadOnlyList<AppLanguage> All { get; } =
        [AppLanguage.System, AppLanguage.En, AppLanguage.Vi, AppLanguage.Fr, AppLanguage.Es, AppLanguage.ZhHans];

    /// <summary>
    /// The language of <paramref name="tag"/> by prefix, ignoring case ("zh-Hans-CN" is ZhHans, "fr-FR" is Fr); none
    /// or another one ("de-DE", "zh-Hant-TW") is System.
    /// </summary>
    public static AppLanguage Matching(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return AppLanguage.System;
        foreach (var language in All)
        {
            if (language == AppLanguage.System) continue;
            var code = Code(language);
            if (tag.Equals(code, StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase))
            {
                return language;
            }
        }
        return AppLanguage.System;
    }

    /// <summary>The <c>PrimaryLanguageOverride</c> value; "" clears the override (System).</summary>
    public static string OverrideTag(this AppLanguage language) => language switch
    {
        AppLanguage.En => "en-US",
        AppLanguage.System => "",
        _ => Code(language),
    };

    /// <summary>
    /// The endonym, the same in every language; only System is translated, so the caller passes
    /// <paramref name="systemName"/> (Core has no localization).
    /// </summary>
    public static string DisplayName(this AppLanguage language, string systemName) => language switch
    {
        AppLanguage.En => "English",
        AppLanguage.Vi => "Tiếng Việt",
        AppLanguage.Fr => "Français",
        AppLanguage.Es => "Español",
        AppLanguage.ZhHans => "简体中文",
        _ => systemName,
    };

    /// <summary>
    /// Whether <paramref name="count"/> takes the CLDR "one" plural form in <paramref name="language"/> (Loc.Plural):
    /// French for 0 and 1, Vietnamese and Chinese never (they have only "other"), English, Spanish and System (MRT
    /// falls back to en-US) for 1.
    /// </summary>
    public static bool IsPluralOne(this AppLanguage language, long count) => language switch
    {
        AppLanguage.Fr => count is 0 or 1,
        AppLanguage.Vi or AppLanguage.ZhHans => false,
        _ => count == 1,
    };

    // Swift's raw values: the prefix a tag of the language starts with.
    private static string Code(AppLanguage language) => language switch
    {
        AppLanguage.En => "en",
        AppLanguage.Vi => "vi",
        AppLanguage.Fr => "fr",
        AppLanguage.Es => "es",
        AppLanguage.ZhHans => "zh-Hans",
        _ => "",
    };
}
