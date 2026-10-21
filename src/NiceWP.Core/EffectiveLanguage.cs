namespace NiceWP.Core;

/// <summary>
/// Represents the resolved concrete language applied to the UI after taking
/// into account the user preference and the detected system display language.
/// </summary>
public enum EffectiveLanguage
{
    /// <summary>
    /// English language (en-US fallback).
    /// </summary>
    English,

    /// <summary>
    /// Turkish language (tr-TR).
    /// </summary>
    Turkish
}

/// <summary>
/// Extension methods for <see cref="EffectiveLanguage"/>.
/// </summary>
public static class EffectiveLanguageExtensions
{
    public const string CodeEnglish = "en-US";
    public const string CodeTurkish = "tr-TR";

    public static string ToCultureCode(this EffectiveLanguage language) => language switch
    {
        EffectiveLanguage.Turkish => CodeTurkish,
        _ => CodeEnglish
    };

    public static string ToNativeName(this EffectiveLanguage language) => language switch
    {
        EffectiveLanguage.Turkish => "Türkçe",
        _ => "English"
    };

    public static string ToIsoCode(this EffectiveLanguage language) => language switch
    {
        EffectiveLanguage.Turkish => "tr",
        _ => "en"
    };
}
