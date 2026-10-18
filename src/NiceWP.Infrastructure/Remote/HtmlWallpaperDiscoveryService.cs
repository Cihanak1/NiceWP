using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace NiceWP.Infrastructure.Remote;

/// <summary>
/// Represents a wallpaper candidate discovered from an HTML page.
/// </summary>
public sealed record DiscoveredWallpaper(
    Uri MediaUri,
    string? Title,
    string DiscoverySource,
    int Score);

/// <summary>
/// Performs lightweight, secure, static HTML analysis to discover wallpaper media URLs
/// without executing JavaScript or hosting browser engines.
/// </summary>
public static class HtmlWallpaperDiscoveryService
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex BaseHrefRegex = new(
        @"<base\s+[^>]*href\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex TitleRegex = new(
        @"<title(?:\s+[^>]*)?>(.*?)<\/title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex MetaTagRegex = new(
        @"<meta\s+([^>]+)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex VideoTagRegex = new(
        @"<video\s+([^>]+)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SourceTagRegex = new(
        @"<source\s+([^>]+)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex AnchorTagRegex = new(
        @"<a\s+([^>]+)>(.*?)<\/a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex ImgTagRegex = new(
        @"<img\s+([^>]+)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex AttributeRegex = new(
        @"(?<name>[a-zA-Z0-9_\-:]+)\s*=\s*[""'](?<value>[^""']*)[""']",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex DimensionRegex = new(
        @"(?<width>\d{3,5})\s*[xX×]\s*(?<height>\d{3,5})",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly string[] SupportedMediaExtensions = [".png", ".jpg", ".jpeg", ".mp4"];
    private static readonly string[] HtmlOrScriptExtensions = [".html", ".htm", ".xhtml", ".php", ".asp", ".aspx", ".js", ".css"];

    /// <summary>
    /// Analyzes static HTML content to discover the most appropriate high-resolution wallpaper media URL.
    /// </summary>
    public static DiscoveredWallpaper? DiscoverWallpaper(string? html, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var effectiveBaseUri = pageUri;
        var baseMatch = BaseHrefRegex.Match(html);
        if (baseMatch.Success)
        {
            var rawBaseHref = WebUtility.HtmlDecode(baseMatch.Groups[1].Value.Trim());
            if (Uri.TryCreate(pageUri, rawBaseHref, out var resolvedBase) &&
                (resolvedBase.Scheme == Uri.UriSchemeHttp || resolvedBase.Scheme == Uri.UriSchemeHttps))
            {
                effectiveBaseUri = resolvedBase;
            }
        }

        var pageTitle = ExtractCleanTitle(html);
        var candidates = new List<DiscoveredWallpaper>();

        // 1. OpenGraph / Twitter meta tags
        foreach (Match match in MetaTagRegex.Matches(html))
        {
            var tagContent = match.Groups[1].Value;
            var attributes = ExtractAttributes(tagContent);

            attributes.TryGetValue("property", out var property);
            attributes.TryGetValue("name", out var name);
            attributes.TryGetValue("content", out var content);

            var metaName = (property ?? name ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var isOgImage = metaName is "og:image" or "og:image:url" or "og:image:secure_url" or "twitter:image" or "twitter:image:src";
            var isOgVideo = metaName is "og:video" or "og:video:url" or "og:video:secure_url" or "twitter:player:stream";

            if (!isOgImage && !isOgVideo)
            {
                continue;
            }

            var decodedUrl = WebUtility.HtmlDecode(content.Trim());
            if (!TryResolveCandidateUri(effectiveBaseUri, decodedUrl, out var candidateUri))
            {
                continue;
            }

            if (IsNoiseUrl(candidateUri))
            {
                continue;
            }

            var baseScore = isOgVideo ? 450 : 350;
            var score = CalculateScore(candidateUri.AbsoluteUri, metaName, baseScore);
            candidates.Add(new DiscoveredWallpaper(candidateUri, pageTitle, isOgVideo ? "OpenGraphVideo" : "OpenGraphImage", score));
        }

        // 2. HTML5 video & source tags
        foreach (Match match in VideoTagRegex.Matches(html))
        {
            var attributes = ExtractAttributes(match.Groups[1].Value);
            if (attributes.TryGetValue("src", out var src) && !string.IsNullOrWhiteSpace(src))
            {
                var decodedUrl = WebUtility.HtmlDecode(src.Trim());
                if (TryResolveCandidateUri(effectiveBaseUri, decodedUrl, out var candidateUri) && !IsNoiseUrl(candidateUri))
                {
                    var score = CalculateScore(candidateUri.AbsoluteUri, "video", 400);
                    candidates.Add(new DiscoveredWallpaper(candidateUri, pageTitle, "VideoTag", score));
                }
            }
        }

        foreach (Match match in SourceTagRegex.Matches(html))
        {
            var attributes = ExtractAttributes(match.Groups[1].Value);
            if (attributes.TryGetValue("src", out var src) && !string.IsNullOrWhiteSpace(src))
            {
                var decodedUrl = WebUtility.HtmlDecode(src.Trim());
                if (TryResolveCandidateUri(effectiveBaseUri, decodedUrl, out var candidateUri) && !IsNoiseUrl(candidateUri))
                {
                    attributes.TryGetValue("type", out var mimeType);
                    var context = "source " + (mimeType ?? string.Empty);
                    var score = CalculateScore(candidateUri.AbsoluteUri, context, 400);
                    candidates.Add(new DiscoveredWallpaper(candidateUri, pageTitle, "SourceTag", score));
                }
            }
        }

        // 3. Anchor download links & high-resolution links
        foreach (Match match in AnchorTagRegex.Matches(html))
        {
            var tagAttributes = match.Groups[1].Value;
            var anchorText = match.Groups[2].Value;
            var attributes = ExtractAttributes(tagAttributes);

            if (!attributes.TryGetValue("href", out var href) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var decodedUrl = WebUtility.HtmlDecode(href.Trim());
            if (!TryResolveCandidateUri(effectiveBaseUri, decodedUrl, out var candidateUri))
            {
                continue;
            }

            // Exclude HTML/script pages masquerading as links
            if (HasHtmlOrScriptExtension(candidateUri))
            {
                continue;
            }

            if (IsNoiseUrl(candidateUri))
            {
                continue;
            }

            var hasDownloadAttr = attributes.ContainsKey("download");
            var combinedContext = $"{tagAttributes} {anchorText}".ToLowerInvariant();
            var hasKeywords = ContainsAnyKeyword(combinedContext, [
                "download", "original", "4k", "uhd", "5k", "8k", "1080p", "1440p", "2160p", "wallpaper", "full resolution", "full hd"
            ]);
            var hasMediaExt = HasSupportedMediaExtension(candidateUri);

            if (hasDownloadAttr || (hasKeywords && hasMediaExt))
            {
                var baseScore = 300;
                var score = CalculateScore(candidateUri.AbsoluteUri, combinedContext, baseScore);
                candidates.Add(new DiscoveredWallpaper(candidateUri, pageTitle, "DownloadLink", score));
            }
            else if (hasMediaExt)
            {
                // Direct media link within the page
                var baseScore = 150;
                var score = CalculateScore(candidateUri.AbsoluteUri, combinedContext, baseScore);
                candidates.Add(new DiscoveredWallpaper(candidateUri, pageTitle, "DirectLink", score));
            }
        }

        // 4. Prominent image tags (e.g. id="wallpaper", class="wallpaper", alt="...wallpaper...")
        foreach (Match match in ImgTagRegex.Matches(html))
        {
            var attributes = ExtractAttributes(match.Groups[1].Value);
            if (!attributes.TryGetValue("src", out var src) || string.IsNullOrWhiteSpace(src))
            {
                continue;
            }

            var decodedUrl = WebUtility.HtmlDecode(src.Trim());
            if (!TryResolveCandidateUri(effectiveBaseUri, decodedUrl, out var candidateUri))
            {
                continue;
            }

            if (!HasSupportedMediaExtension(candidateUri) || IsNoiseUrl(candidateUri))
            {
                continue;
            }

            attributes.TryGetValue("id", out var imgId);
            attributes.TryGetValue("class", out var imgClass);
            attributes.TryGetValue("alt", out var imgAlt);

            var imgContext = $"{imgId} {imgClass} {imgAlt}".ToLowerInvariant();
            if (ContainsAnyKeyword(imgContext, ["wallpaper", "original", "full", "download", "main", "hero"]))
            {
                var score = CalculateScore(candidateUri.AbsoluteUri, imgContext, 280);
                candidates.Add(new DiscoveredWallpaper(candidateUri, pageTitle, "ImgTag", score));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Return the highest scoring candidate (with preference for desktop high resolutions)
        return candidates
            .OrderByDescending(c => c.Score)
            .FirstOrDefault();
    }

    /// <summary>
    /// Extracts and cleans the page title for use as a wallpaper display name.
    /// </summary>
    public static string? ExtractCleanTitle(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = TitleRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var raw = WebUtility.HtmlDecode(match.Groups[1].Value);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Collapse whitespace and newlines
        var normalized = Regex.Replace(raw, @"\s+", " ").Trim();

        // Remove common branding suffixes e.g. "Car Wallpaper | 4K Wallpapers", "Sunset - Wallpaper Flare"
        string[] separators = [" | ", " - ", " — ", " – ", " » ", " :: "];
        foreach (var sep in separators)
        {
            var index = normalized.IndexOf(sep, StringComparison.Ordinal);
            if (index > 2)
            {
                var part = normalized[..index].Trim();
                if (part.Length >= 3)
                {
                    normalized = part;
                    break;
                }
            }
        }

        // Clean invalid file name characters
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(normalized.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();

        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80].Trim();
        }

        return cleaned.Length >= 2 ? cleaned : null;
    }

    private static Dictionary<string, string> ExtractAttributes(string tagContent)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(tagContent))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value;
            attributes[name] = value;
        }

        // Check for boolean download attribute without explicit value
        if (Regex.IsMatch(tagContent, @"\bdownload\b", RegexOptions.IgnoreCase, RegexTimeout) &&
            !attributes.ContainsKey("download"))
        {
            attributes["download"] = string.Empty;
        }

        return attributes;
    }

    private static bool TryResolveCandidateUri(Uri baseUri, string candidateUrl, out Uri resolvedUri)
    {
        resolvedUri = null!;

        if (string.IsNullOrWhiteSpace(candidateUrl))
        {
            return false;
        }

        if (candidateUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            candidateUrl.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            candidateUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(baseUri, candidateUrl, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        resolvedUri = uri;
        return true;
    }

    private static bool IsNoiseUrl(Uri uri)
    {
        var pathAndQuery = uri.PathAndQuery.ToLowerInvariant();

        string[] noiseMarkers = [
            "/thumb",
            "_thumb",
            "-thumb",
            "thumb.",
            "thumbs/",
            "thumbnail",
            "/favicon",
            "favicon.",
            "/avatar",
            "_avatar",
            "-avatar",
            "avatars/",
            "/logo",
            "_logo",
            "-logo",
            "logos/",
            "/badge",
            "_badge",
            "-badge",
            "/icon",
            "_icon",
            "-icon",
            "icon.",
            "icons/"
        ];

        foreach (var marker in noiseMarkers)
        {
            if (pathAndQuery.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasHtmlOrScriptExtension(Uri uri)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        foreach (var ext in HtmlOrScriptExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasSupportedMediaExtension(Uri uri)
    {
        var path = uri.AbsolutePath.ToLowerInvariant();
        foreach (var ext in SupportedMediaExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsAnyKeyword(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static int CalculateScore(string url, string contextText, int baseScore)
    {
        var score = baseScore;
        var combined = $"{url} {contextText}".ToLowerInvariant();

        // 1. Explicit pixel dimensions like 3840x2160
        var match = DimensionRegex.Match(combined);
        if (match.Success &&
            int.TryParse(match.Groups["width"].Value, out var width) &&
            int.TryParse(match.Groups["height"].Value, out var height))
        {
            if (width >= 640 && height >= 480)
            {
                var pixelScore = (int)Math.Min(1200, (long)width * height / 10000);
                score += pixelScore;
            }
        }

        // 2. High-resolution keyword bonuses
        if (combined.Contains("8k") || combined.Contains("4320p") || combined.Contains("7680x4320"))
        {
            score += 900;
        }
        else if (combined.Contains("5k") || combined.Contains("2880p") || combined.Contains("5120x2880"))
        {
            score += 800;
        }
        else if (combined.Contains("4k") || combined.Contains("2160p") || combined.Contains("uhd") || combined.Contains("3840x2160"))
        {
            score += 700;
        }
        else if (combined.Contains("2k") || combined.Contains("1440p") || combined.Contains("qhd") || combined.Contains("2560x1440"))
        {
            score += 500;
        }
        else if (combined.Contains("1080p") || combined.Contains("fhd") || combined.Contains("1920x1080"))
        {
            score += 400;
        }
        else if (combined.Contains("720p") || combined.Contains("1280x720"))
        {
            score += 200;
        }

        if (combined.Contains("original"))
        {
            score += 750;
        }

        if (combined.Contains("download"))
        {
            score += 100;
        }

        return score;
    }
}
