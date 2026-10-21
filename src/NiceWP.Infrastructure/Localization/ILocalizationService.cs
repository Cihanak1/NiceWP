using NiceWP.Core;

namespace NiceWP.Infrastructure.Localization;

/// <summary>
/// Service contract for providing localized string resources to views, view models, and services.
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// Gets the current concrete effective language in effect.
    /// </summary>
    EffectiveLanguage CurrentLanguage { get; }

    /// <summary>
    /// Event triggered when the current effective language changes at runtime.
    /// </summary>
    event Action<EffectiveLanguage>? LanguageChanged;

    /// <summary>
    /// Sets the active effective language, notifying all subscribers if it changed.
    /// </summary>
    void SetLanguage(EffectiveLanguage language);

    /// <summary>
    /// Retrieves the localized string corresponding to the given key.
    /// Falls back to English if the key is missing from the active language resource dictionary,
    /// or returns the key name itself if neither dictionary defines it.
    /// </summary>
    string GetString(string key);

    /// <summary>
    /// Formats a localized string using string.Format.
    /// </summary>
    string Format(string key, params object[] args);
}
