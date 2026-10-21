using NiceWP.Core;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Normalizes and deduplicates user-defined <see cref="AppRule"/> configurations.
/// Enforces case-insensitivity, filename-only resolution, and consistent '.exe' suffixing.
/// </summary>
public static class AppRuleNormalizer
{
    /// <summary>
    /// Cleans and normalizes an application process name.
    /// Strips leading/trailing whitespace, directory paths, and guarantees a normalized .exe suffix.
    /// Returns empty string if invalid.
    /// </summary>
    public static string NormalizeProcessName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return string.Empty;
        }

        var trimmed = rawName.Trim();

        // Extract filename only if a full path was provided
        try
        {
            trimmed = Path.GetFileName(trimmed);
        }
        catch
        {
            // Ignore path extraction errors and use trimmed string
        }

        trimmed = trimmed.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += ".exe";
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Filters, normalizes, and deduplicates a collection of application rules.
    /// Ignores invalid entries and ensures only the first rule per process is retained.
    /// </summary>
    public static IReadOnlyList<AppRule> NormalizeRules(IEnumerable<AppRule>? rules)
    {
        if (rules == null)
        {
            return Array.Empty<AppRule>();
        }

        var normalizedList = new List<AppRule>();
        var seenProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule == null) continue;

            var normalizedProc = NormalizeProcessName(rule.ProcessName);
            if (string.IsNullOrEmpty(normalizedProc)) continue;

            if (seenProcesses.Add(normalizedProc))
            {
                normalizedList.Add(new AppRule
                {
                    ProcessName = normalizedProc,
                    Behavior = rule.Behavior,
                    OptionalWallpaperId = string.IsNullOrWhiteSpace(rule.OptionalWallpaperId) ? null : rule.OptionalWallpaperId.Trim()
                });
            }
        }

        return normalizedList;
    }
}
