namespace NiceWP.Core.Discover;

/// <summary>
/// Encapsulates provider-agnostic HTTP rate limit state and backoff metrics.
/// </summary>
public sealed record ProviderRateLimitState(
    int? Limit = null,
    int? Remaining = null,
    DateTimeOffset? ResetAt = null,
    bool IsLimited = false,
    int? RetryAfterSeconds = null);
