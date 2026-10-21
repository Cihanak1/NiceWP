using System.Globalization;

namespace NiceWP.Infrastructure.Localization;

/// <summary>
/// Detects the Windows display / UI language using standard Windows globalization preferences
/// and .NET UI culture information. Excludes regional formats (currency/date format regions).
/// </summary>
public sealed class WindowsLanguageDetector : ILanguageDetector
{
    public static WindowsLanguageDetector Instance { get; } = new();

    public string GetSystemDisplayLanguage()
    {
        try
        {
            // First attempt WinRT GlobalizationPreferences if available on modern Windows
            var winRtLanguages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            if (winRtLanguages != null && winRtLanguages.Count > 0 && !string.IsNullOrWhiteSpace(winRtLanguages[0]))
            {
                return winRtLanguages[0].Trim();
            }
        }
        catch
        {
            // Fallback for non-packaged, unit test, or older runtime contexts
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture?.Name))
            {
                return CultureInfo.CurrentUICulture.Name.Trim();
            }
        }
        catch
        {
            // Ignore culture lookup issues
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(CultureInfo.InstalledUICulture?.Name))
            {
                return CultureInfo.InstalledUICulture.Name.Trim();
            }
        }
        catch
        {
            // Ignore culture lookup issues
        }

        return "en-US";
    }
}
