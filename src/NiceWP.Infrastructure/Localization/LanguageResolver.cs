using NiceWP.Core;

namespace NiceWP.Infrastructure.Localization;

/// <summary>
/// Resolves the concrete <see cref="EffectiveLanguage"/> from the user's saved preference
/// and the detected Windows operating system UI language.
/// </summary>
public sealed class LanguageResolver
{
    private readonly ILanguageDetector _detector;

    public LanguageResolver(ILanguageDetector? detector = null)
    {
        _detector = detector ?? WindowsLanguageDetector.Instance;
    }

    /// <summary>
    /// Resolves the effective language using the provided user preference and detected OS language tag.
    /// Manual overrides (Turkish or English) always take absolute precedence.
    /// When System is chosen, Turkish is selected for any Turkish language dialect ("tr*"),
    /// and all other languages fallback to English ("en-US").
    /// </summary>
    public static EffectiveLanguage Resolve(AppLanguage preference, string? detectedLanguageTag)
    {
        return preference switch
        {
            AppLanguage.Turkish => EffectiveLanguage.Turkish,
            AppLanguage.English => EffectiveLanguage.English,
            _ => ResolveFromSystemTag(detectedLanguageTag)
        };
    }

    /// <summary>
    /// Resolves the effective language using the configured detector for system language lookup.
    /// </summary>
    public EffectiveLanguage Resolve(AppLanguage preference)
    {
        if (preference == AppLanguage.Turkish) return EffectiveLanguage.Turkish;
        if (preference == AppLanguage.English) return EffectiveLanguage.English;

        var systemTag = _detector.GetSystemDisplayLanguage();
        return ResolveFromSystemTag(systemTag);
    }

    /// <summary>
    /// Evaluates a raw BCP-47 or culture tag (e.g. "tr-TR", "en-US", "de-DE") to determine if it maps
    /// to Turkish or falls back to English. Region settings are ignored.
    /// </summary>
    public static EffectiveLanguage ResolveFromSystemTag(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return EffectiveLanguage.English;
        }

        var trimmed = languageTag.Trim().Replace('_', '-');

        // Check if primary language subtag is Turkish ("tr", "tr-TR", "tr-CY", etc.)
        if (string.Equals(trimmed, "tr", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("tr-", StringComparison.OrdinalIgnoreCase))
        {
            return EffectiveLanguage.Turkish;
        }

        // All other languages (including English "en*", and unsupported languages like German, French, etc.)
        // safely fallback to English.
        return EffectiveLanguage.English;
    }

    /// <summary>
    /// Produces a user-facing label for the language selection option.
    /// If preference is System, appends the resolved effective language in parentheses
    /// (e.g. "System Default (Türkçe)" or "System Default (English)").
    /// </summary>
    public static string FormatSelectionLabel(AppLanguage preference, EffectiveLanguage effectiveLanguage, EffectiveLanguage uiLanguage)
    {
        if (preference == AppLanguage.Turkish)
        {
            return "Türkçe";
        }

        if (preference == AppLanguage.English)
        {
            return "English";
        }

        // Preference is System Default
        var systemPrefix = uiLanguage == EffectiveLanguage.Turkish ? "Sistem Varsayılanı" : "System Default";
        var effectiveName = effectiveLanguage.ToNativeName();
        return $"{systemPrefix} ({effectiveName})";
    }
}
