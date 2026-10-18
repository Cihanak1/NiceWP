using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NiceWP.Infrastructure.Remote;

/// <summary>
/// Validates network inputs, protects against Server-Side Request Forgery (SSRF) and private network probing,
/// and sanitizes remote wallpaper URLs for persistence.
/// </summary>
public static class NetworkSafetyValidator
{
    public const string ErrorInvalidUrl = "Invalid URL.";
    public const string ErrorLocalPrivateNotAllowed = "Local/private network addresses are not allowed.";
    public const string ErrorCouldNotConnect = "Could not connect to server.";

    /// <summary>
    /// Validates basic URI syntax, scheme (http/https only), absence of credentials, and non-empty host.
    /// </summary>
    public static bool ValidateUrl(string? url, out Uri validatedUri, out string errorMessage)
    {
        validatedUri = null!;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            errorMessage = ErrorInvalidUrl;
            return false;
        }

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            errorMessage = ErrorInvalidUrl;
            return false;
        }

        // Scheme check: only http and https are permitted
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = ErrorInvalidUrl;
            return false;
        }

        // Reject embedded username/password credentials
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errorMessage = ErrorInvalidUrl;
            return false;
        }

        // Reject empty host
        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            errorMessage = ErrorInvalidUrl;
            return false;
        }

        // Reject known localhost / private names directly
        var host = uri.Host.Trim();
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = ErrorLocalPrivateNotAllowed;
            return false;
        }

        // If host is a literal IP address, inspect it immediately
        if (IPAddress.TryParse(host, out var literalIp))
        {
            if (IsPrivateOrLocal(literalIp))
            {
                errorMessage = ErrorLocalPrivateNotAllowed;
                return false;
            }
        }

        validatedUri = uri;
        return true;
    }

    /// <summary>
    /// Checks whether an IP address is loopback, RFC1918 private, link-local, unspecified, or multicast.
    /// </summary>
    public static bool IsPrivateOrLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();

            // 0.0.0.0/8
            if (bytes[0] == 0) return true;

            // 127.0.0.0/8 (Loopback)
            if (bytes[0] == 127) return true;

            // 10.0.0.0/8 (RFC1918 Private)
            if (bytes[0] == 10) return true;

            // 172.16.0.0/12 (RFC1918 Private: 172.16.0.0 - 172.31.255.255)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.168.0.0/16 (RFC1918 Private)
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // 169.254.0.0/16 (Link-Local / APIPA / Cloud Metadata)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // 100.64.0.0/10 (Carrier-Grade NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;

            // 224.0.0.0/4 (Multicast)
            if (bytes[0] >= 224 && bytes[0] <= 239) return true;

            // 255.255.255.255 (Broadcast)
            if (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255) return true;

            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Loopback) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast)
            {
                return true;
            }

            var bytes = ip.GetAddressBytes();

            // Unique Local Address (ULA) fc00::/7 (fc00... or fd00...)
            if ((bytes[0] & 0xFE) == 0xFC)
            {
                return true;
            }

            // Discard prefix 100::/64
            if (bytes[0] == 0x01 && bytes[1] == 0x00)
            {
                return true;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates that the URI format is safe and resolves its DNS to ensure no private/local IP addresses are targeted.
    /// Supports a pluggable DNS resolver for deterministic offline unit testing.
    /// </summary>
    public static async Task<(bool Success, string ErrorMessage)> ValidateSafetyAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>>? dnsResolver = null,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateUrl(uri.ToString(), out var validatedUri, out var error))
        {
            return (false, error);
        }

        var host = validatedUri.Host;
        if (IPAddress.TryParse(host, out var parsedIp))
        {
            if (IsPrivateOrLocal(parsedIp))
            {
                return (false, ErrorLocalPrivateNotAllowed);
            }
            return (true, string.Empty);
        }

        try
        {
            IPAddress[] addresses;
            if (dnsResolver != null)
            {
                addresses = await dnsResolver(host, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }

            if (addresses == null || addresses.Length == 0)
            {
                return (false, ErrorCouldNotConnect);
            }

            foreach (var addr in addresses)
            {
                if (IsPrivateOrLocal(addr))
                {
                    return (false, ErrorLocalPrivateNotAllowed);
                }
            }

            return (true, string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"DNS resolution failed for '{host}': {ex.Message}");
            return (false, ErrorCouldNotConnect);
        }
    }

    /// <summary>
    /// Sanitizes the URI for persistent storage by stripping query string, fragment, and user info.
    /// Returns scheme + host[:port] + path.
    /// </summary>
    public static string SanitizeUrl(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Path);
    }
}
