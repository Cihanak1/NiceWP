namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Evaluates NASA media metadata for third-party copyright claims or commercial restrictions.
/// Ensures only unrestricted, public domain US Government works are imported into NiceWP.
/// </summary>
public static class NasaRightsFilter
{
    /// <summary>
    /// Checks whether an item's metadata indicates it is free of third-party copyright restrictions.
    /// Returns true if the item is safe to display/download; false if restricted.
    /// </summary>
    public static bool IsSafeForDisplay(string? copyright, string? rights, string? description)
    {
        // 1. Explicit copyright field from third parties
        if (!string.IsNullOrWhiteSpace(copyright))
        {
            return false;
        }

        // 2. Rights field: if present, must contain public domain keywords
        if (!string.IsNullOrWhiteSpace(rights))
        {
            var r = rights.Trim().ToLowerInvariant();
            if (!r.Contains("public domain") && !r.Contains("no known copyright"))
            {
                return false;
            }
        }

        // 3. Description checks: reject explicit copyright notices
        if (!string.IsNullOrWhiteSpace(description))
        {
            var desc = description.ToLowerInvariant();
            if (desc.Contains("©") ||
                desc.Contains("copyright ") ||
                desc.Contains("(c) copyright") ||
                desc.Contains("all rights reserved"))
            {
                // Verify whether the copyright notice belongs to an external agency/person
                if (!desc.Contains("public domain"))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
