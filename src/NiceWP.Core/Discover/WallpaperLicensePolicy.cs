namespace NiceWP.Core.Discover;

/// <summary>
/// Evaluates and standardizes open licenses into safety categories.
/// Only Public Domain / PDM, CC0, CC BY, and CC BY-SA are displayed in default Discover results.
/// </summary>
public static class WallpaperLicensePolicy
{
    public const string CodePublicDomain = "PDM";
    public const string CodeCC0 = "CC0";
    public const string CodeCCBY = "CC BY";
    public const string CodeCCBYSA = "CC BY-SA";
    public const string CodeRestricted = "Restricted";
    public const string CodeUnknown = "Unknown";

    /// <summary>
    /// Evaluates a raw license code and URL to produce a standardized <see cref="WallpaperLicenseInfo"/>.
    /// </summary>
    public static WallpaperLicenseInfo Evaluate(
        string? rawCode,
        string? licenseUrl = null,
        bool isExplicitPublicDomain = false)
    {
        if (isExplicitPublicDomain)
        {
            return new WallpaperLicenseInfo(
                Code: CodePublicDomain,
                Name: "Public Domain",
                Url: licenseUrl ?? "https://creativecommons.org/publicdomain/mark/1.0/",
                Status: LicenseStatus.Allowed,
                RequiresAttribution: false,
                CommercialUseAllowed: true,
                DerivativeUseAllowed: true);
        }

        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return new WallpaperLicenseInfo(
                Code: CodeUnknown,
                Name: "Unknown License",
                Url: licenseUrl,
                Status: LicenseStatus.Unknown,
                RequiresAttribution: false,
                CommercialUseAllowed: false,
                DerivativeUseAllowed: false,
                Notes: "Provider did not supply a recognized license code.");
        }

        var normalized = rawCode.Trim().ToLowerInvariant()
            .Replace("-", " ")
            .Replace("_", " ");

        // Public Domain / PDM variants
        if (normalized.Contains("public domain") || normalized == "pdm" || normalized.StartsWith("pdm ") || normalized.Contains("pd"))
        {
            return new WallpaperLicenseInfo(
                Code: CodePublicDomain,
                Name: "Public Domain",
                Url: licenseUrl ?? "https://creativecommons.org/publicdomain/mark/1.0/",
                Status: LicenseStatus.Allowed,
                RequiresAttribution: false,
                CommercialUseAllowed: true,
                DerivativeUseAllowed: true);
        }

        // CC0 / Zero
        if (normalized.Contains("cc0") || normalized.Contains("zero"))
        {
            return new WallpaperLicenseInfo(
                Code: CodeCC0,
                Name: "Creative Commons Zero (CC0)",
                Url: licenseUrl ?? "https://creativecommons.org/publicdomain/zero/1.0/",
                Status: LicenseStatus.Allowed,
                RequiresAttribution: false,
                CommercialUseAllowed: true,
                DerivativeUseAllowed: true);
        }

        // Restricted variants: NonCommercial (NC) or NoDerivatives (ND)
        var isNc = normalized.Contains("nc") || normalized.Contains("non commercial") || normalized.Contains("noncommercial");
        var isNd = normalized.Contains("nd") || normalized.Contains("no derivatives") || normalized.Contains("noderivatives");

        if (isNc || isNd)
        {
            var code = (isNc && isNd) ? "CC BY-NC-ND" : isNc ? "CC BY-NC" : "CC BY-ND";
            return new WallpaperLicenseInfo(
                Code: code,
                Name: $"Restricted ({rawCode.Trim()})",
                Url: licenseUrl,
                Status: LicenseStatus.Restricted,
                RequiresAttribution: true,
                CommercialUseAllowed: !isNc,
                DerivativeUseAllowed: !isNd,
                Notes: "Excluded from default results due to NonCommercial or NoDerivatives restrictions.");
        }

        // CC BY-SA variants
        if (normalized.Contains("by sa") || normalized.Contains("sa"))
        {
            return new WallpaperLicenseInfo(
                Code: CodeCCBYSA,
                Name: "Creative Commons Attribution-ShareAlike (CC BY-SA)",
                Url: licenseUrl ?? "https://creativecommons.org/licenses/by-sa/4.0/",
                Status: LicenseStatus.AllowedWithAttribution,
                RequiresAttribution: true,
                CommercialUseAllowed: true,
                DerivativeUseAllowed: true);
        }

        // CC BY variants
        if (normalized.Contains("by") || normalized.StartsWith("cc by") || normalized == "by")
        {
            return new WallpaperLicenseInfo(
                Code: CodeCCBY,
                Name: "Creative Commons Attribution (CC BY)",
                Url: licenseUrl ?? "https://creativecommons.org/licenses/by/4.0/",
                Status: LicenseStatus.AllowedWithAttribution,
                RequiresAttribution: true,
                CommercialUseAllowed: true,
                DerivativeUseAllowed: true);
        }

        return new WallpaperLicenseInfo(
            Code: CodeUnknown,
            Name: rawCode.Trim(),
            Url: licenseUrl,
            Status: LicenseStatus.Unknown,
            RequiresAttribution: false,
            CommercialUseAllowed: false,
            DerivativeUseAllowed: false,
            Notes: "Unrecognized license code; excluded from default Discover view.");
    }

    /// <summary>
    /// Determines whether the license status is permitted for standard Discover display.
    /// </summary>
    public static bool IsDefaultDiscoverAllowed(WallpaperLicenseInfo license) =>
        license.Status is LicenseStatus.Allowed or LicenseStatus.AllowedWithAttribution;
}
