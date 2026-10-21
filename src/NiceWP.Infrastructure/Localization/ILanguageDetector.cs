namespace NiceWP.Infrastructure.Localization;

/// <summary>
/// Abstraction for detecting the host operating system's display / UI language.
/// </summary>
public interface ILanguageDetector
{
    /// <summary>
    /// Gets the primary display/UI language tag of the operating system (e.g. "tr-TR", "en-US", "de-DE").
    /// Never returns regional/locale formatting overrides (e.g. currency/date region).
    /// </summary>
    string GetSystemDisplayLanguage();
}
