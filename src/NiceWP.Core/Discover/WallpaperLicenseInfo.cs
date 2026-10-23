namespace NiceWP.Core.Discover;

/// <summary>
/// Encapsulates normalized open license information and safety evaluation.
/// </summary>
public sealed record WallpaperLicenseInfo(
    string Code,
    string Name,
    string? Url,
    LicenseStatus Status,
    bool RequiresAttribution,
    bool CommercialUseAllowed,
    bool DerivativeUseAllowed,
    string? Notes = null);
