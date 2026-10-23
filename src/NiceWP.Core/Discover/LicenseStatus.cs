namespace NiceWP.Core.Discover;

/// <summary>
/// Categorizes the safety and redistribution permission level of a wallpaper license.
/// </summary>
public enum LicenseStatus
{
    /// <summary>
    /// License permits unconditional personal and commercial use (e.g., Public Domain, CC0).
    /// </summary>
    Allowed,

    /// <summary>
    /// License permits use subject to proper attribution credit (e.g., CC BY, CC BY-SA).
    /// </summary>
    AllowedWithAttribution,

    /// <summary>
    /// License has restrictive conditions (e.g., NonCommercial, NoDerivatives) excluded from default Discover view.
    /// </summary>
    Restricted,

    /// <summary>
    /// License could not be reliably verified; excluded from default Discover view.
    /// </summary>
    Unknown
}
