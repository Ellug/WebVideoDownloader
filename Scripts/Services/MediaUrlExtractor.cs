using System.Net;
using System.Text.RegularExpressions;

namespace WebVideoDownloader.Services;

internal sealed class MediaUrlExtractor(Func<string, string?> resolveUrl)
{
    private static readonly Regex MediaUrlRegex = new(
        @"(?<url>(?:(?:https?:)?//|/)[^'""<>\s\\]+?(?:(?:\.(?:m3u8|mp4|webm|m4v|mov)(?:\?[^'""<>\s\\]*)?)|(?:/v\.html\?[^'""<>\s\\]*)))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlayerUrlRegex = new(
        @"(?<url>(?:(?:https?:)?//|/)[^'""<>\s\\]+?/player\.php\?k=[^'""<>\s\\]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IEnumerable<string> ExtractMediaUrls(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in BuildMediaSearchTexts(html))
        {
            foreach (Match match in MediaUrlRegex.Matches(source))
            {
                var resolvedUrl = resolveUrl(match.Groups["url"].Value);
                if (!string.IsNullOrWhiteSpace(resolvedUrl) && seen.Add(resolvedUrl))
                {
                    yield return resolvedUrl;
                }
            }
        }
    }

    public IEnumerable<string> ExtractPlayerUrls(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in BuildMediaSearchTexts(html))
        {
            foreach (Match match in PlayerUrlRegex.Matches(source))
            {
                var resolvedUrl = resolveUrl(match.Groups["url"].Value);
                if (!string.IsNullOrWhiteSpace(resolvedUrl) && seen.Add(resolvedUrl))
                {
                    yield return resolvedUrl;
                }
            }
        }
    }

    private static IEnumerable<string> BuildMediaSearchTexts(string value)
    {
        var htmlDecoded = WebUtility.HtmlDecode(value).Replace("\\/", "/", StringComparison.Ordinal);
        yield return htmlDecoded;

        var urlDecoded = WebUtility.UrlDecode(htmlDecoded);
        if (!string.IsNullOrWhiteSpace(urlDecoded))
        {
            yield return urlDecoded.Replace("\\/", "/", StringComparison.Ordinal);
        }

        var regexUnescaped = TryRegexUnescape(htmlDecoded);
        if (!string.IsNullOrWhiteSpace(regexUnescaped))
        {
            yield return regexUnescaped.Replace("\\/", "/", StringComparison.Ordinal);
        }
    }

    private static string TryRegexUnescape(string value)
    {
        try
        {
            return Regex.Unescape(value);
        }
        catch
        {
            return "";
        }
    }
}
