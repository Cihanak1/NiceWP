using System.Net;
using System.Net.Http.Headers;
using NiceWP.Core.Discover;

namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Handles Openverse rate limiting telemetry, HTTP 429 status codes, and backoff windows.
/// </summary>
public sealed class OpenverseRateLimitHandler
{
    private ProviderRateLimitState? _state;
    private readonly object _lock = new();

    public ProviderRateLimitState? CurrentState
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    public bool IsRateLimited(out DateTimeOffset? resetAt, out int? retryAfterSeconds)
    {
        lock (_lock)
        {
            resetAt = _state?.ResetAt;
            retryAfterSeconds = _state?.RetryAfterSeconds;

            if (_state?.IsLimited == true && _state.ResetAt.HasValue)
            {
                if (DateTimeOffset.UtcNow < _state.ResetAt.Value)
                {
                    return true;
                }

                // Window elapsed; reset
                _state = _state with { IsLimited = false };
            }

            return false;
        }
    }

    public void RecordResponse(HttpResponseMessage response)
    {
        lock (_lock)
        {
            int? limit = null;
            int? remaining = null;
            DateTimeOffset? resetAt = null;

            if (response.Headers.TryGetValues("X-RateLimit-Limit", out var limitVals) &&
                int.TryParse(limitVals.FirstOrDefault(), out var parsedLimit))
            {
                limit = parsedLimit;
            }

            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remVals) &&
                int.TryParse(remVals.FirstOrDefault(), out var parsedRem))
            {
                remaining = parsedRem;
            }

            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetVals) &&
                long.TryParse(resetVals.FirstOrDefault(), out var resetUnix))
            {
                resetAt = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = ExtractRetryAfter(response.Headers) ?? 60;
                resetAt = DateTimeOffset.UtcNow.AddSeconds(retryAfter);

                _state = new ProviderRateLimitState(
                    Limit: limit,
                    Remaining: 0,
                    ResetAt: resetAt,
                    IsLimited: true,
                    RetryAfterSeconds: retryAfter);
                return;
            }

            if (limit.HasValue || remaining.HasValue || resetAt.HasValue)
            {
                _state = new ProviderRateLimitState(
                    Limit: limit,
                    Remaining: remaining,
                    ResetAt: resetAt,
                    IsLimited: remaining == 0);
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _state = null;
        }
    }

    private static int? ExtractRetryAfter(HttpResponseHeaders headers)
    {
        if (headers.RetryAfter?.Delta.HasValue == true)
        {
            return (int)headers.RetryAfter.Delta.Value.TotalSeconds;
        }

        if (headers.RetryAfter?.Date.HasValue == true)
        {
            var diff = headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            return Math.Max(1, (int)diff.TotalSeconds);
        }

        return null;
    }
}
