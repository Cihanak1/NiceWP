using NiceWP.Core;

namespace NiceWP.Infrastructure.Localization;

/// <summary>
/// Centralized implementation of <see cref="ILocalizationService"/> managing localized string dictionaries
/// for Turkish and English. Thread-safe, event-driven, and zero periodic polling overhead.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private static readonly Lazy<LocalizationService> _shared = new(() => new LocalizationService());
    public static LocalizationService Instance => _shared.Value;

    private readonly object _syncLock = new();
    private EffectiveLanguage _currentLanguage = EffectiveLanguage.English;

    private readonly Dictionary<string, string> _englishStrings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _turkishStrings = new(StringComparer.OrdinalIgnoreCase);

    public event Action<EffectiveLanguage>? LanguageChanged;

    public LocalizationService()
    {
        RegisterDefaultResources();
    }

    public EffectiveLanguage CurrentLanguage
    {
        get
        {
            lock (_syncLock)
            {
                return _currentLanguage;
            }
        }
    }

    public void SetLanguage(EffectiveLanguage language)
    {
        bool changed = false;
        lock (_syncLock)
        {
            if (_currentLanguage != language)
            {
                _currentLanguage = language;
                changed = true;
            }
        }

        if (changed)
        {
            LanguageChanged?.Invoke(language);
        }
    }

    public string GetString(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        lock (_syncLock)
        {
            if (_currentLanguage == EffectiveLanguage.Turkish)
            {
                if (_turkishStrings.TryGetValue(key, out var trValue))
                {
                    return trValue;
                }
            }

            // Fallback to English
            if (_englishStrings.TryGetValue(key, out var enValue))
            {
                return enValue;
            }

            return key;
        }
    }

    public string Format(string key, params object[] args)
    {
        var template = GetString(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }

    public void RegisterEnglish(string key, string value)
    {
        lock (_syncLock)
        {
            _englishStrings[key] = value;
        }
    }

    public void RegisterTurkish(string key, string value)
    {
        lock (_syncLock)
        {
            _turkishStrings[key] = value;
        }
    }

    private void RegisterDefaultResources()
    {
        // Default entries are registered via EnglishResources and TurkishResources
        EnglishResources.Register(this);
        TurkishResources.Register(this);
    }
}
