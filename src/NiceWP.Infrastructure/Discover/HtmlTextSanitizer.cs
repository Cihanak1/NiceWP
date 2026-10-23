using System.Net;
using System.Text.RegularExpressions;

namespace NiceWP.Infrastructure.Discover;

/// <summary>
/// Strips HTML tags and decodes entities to safely convert API metadata to plain text.
/// Prevents raw HTML rendering in the user interface.
/// </summary>
public static class HtmlTextSanitizer
{
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string Sanitize(string? rawHtml)
    {
        if (string.IsNullOrWhiteSpace(rawHtml))
        {
            return string.Empty;
        }

        // 1. Strip all HTML tags
        var stripped = HtmlTagRegex.Replace(rawHtml, " ");

        // 2. Decode HTML entities (&amp;, &lt;, &gt;, &quot;, &#39;, &nbsp;, etc.)
        var decoded = WebUtility.HtmlDecode(stripped);

        // 3. Normalize multiple spaces, tabs, and newlines
        var normalized = MultiWhitespaceRegex.Replace(decoded, " ").Trim();

        return normalized;
    }
}
