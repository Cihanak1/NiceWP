namespace NiceWP.Core;

/// <summary>
/// Specifies the user's preferred application language setting.
/// </summary>
public enum AppLanguage
{
    /// <summary>
    /// Follow the active Windows display / UI language.
    /// </summary>
    System,

    /// <summary>
    /// Force Turkish (Türkçe) interface.
    /// </summary>
    Turkish,

    /// <summary>
    /// Force English interface.
    /// </summary>
    English
}

/// <summary>
/// Extension methods for <see cref="AppLanguage"/>.
/// </summary>
public static class AppLanguageExtensions
{
    public const string LanguageSystem = "System";
    public const string LanguageTurkish = "Turkish";
    public const string LanguageEnglish = "English";

    public static string ToConfigString(this AppLanguage language) => language switch
    {
        AppLanguage.Turkish => LanguageTurkish,
        AppLanguage.English => LanguageEnglish,
        _ => LanguageSystem
    };

    public static string ToDisplayString(this AppLanguage language) => language switch
    {
        AppLanguage.Turkish => "Türkçe",
        AppLanguage.English => "English",
        _ => "System Default"
    };

    public static AppLanguage ParseLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AppLanguage.System;
        }

        var trimmed = value.Trim();

        if (string.Equals(trimmed, LanguageTurkish, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "tr", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "tr-TR", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Türkçe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Turkce", StringComparison.OrdinalIgnoreCase))
        {
            return AppLanguage.Turkish;
        }

        if (string.Equals(trimmed, LanguageEnglish, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "en", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "en-US", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "en-GB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "English", StringComparison.OrdinalIgnoreCase))
        {
            return AppLanguage.English;
        }

        return AppLanguage.System;
    }
}
