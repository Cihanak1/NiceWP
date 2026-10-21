using NiceWP.Core;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Service responsible for evaluating the active foreground application against user-configured rules.
/// Thread-safe and privacy-first: matches strictly on normalized executable filenames.
/// </summary>
public sealed class AppRuleMatchingService
{
    private readonly object gate = new();
    private Dictionary<string, AppRule> ruleLookup = new(StringComparer.OrdinalIgnoreCase);
    private List<AppRule> rulesList = new();

    public IReadOnlyList<AppRule> CurrentRules
    {
        get
        {
            lock (gate)
            {
                return rulesList.ToArray();
            }
        }
    }

    public AppRuleMatchingService(IEnumerable<AppRule>? initialRules = null)
    {
        UpdateRules(initialRules);
    }

    /// <summary>
    /// Replaces the active rules with a freshly normalized and deduplicated rule collection.
    /// </summary>
    public void UpdateRules(IEnumerable<AppRule>? rules)
    {
        var normalized = AppRuleNormalizer.NormalizeRules(rules);
        var lookup = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in normalized)
        {
            lookup[rule.ProcessName] = rule;
        }

        lock (gate)
        {
            ruleLookup = lookup;
            rulesList = normalized.ToList();
        }
    }

    /// <summary>
    /// Finds the configured rule for the given executable name, or null if no rule applies.
    /// </summary>
    public AppRule? FindMatch(string? rawProcessName)
    {
        var normalized = AppRuleNormalizer.NormalizeProcessName(rawProcessName);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        lock (gate)
        {
            return ruleLookup.TryGetValue(normalized, out var rule) ? rule : null;
        }
    }
}
