using System.Net;
using System.Text.RegularExpressions;
using WebVideoDownloader.Models;

namespace WebVideoDownloader.Services;

internal static class CandidateDisplayService
{
    public static readonly TimeSpan PlaybackSignalLifetime = TimeSpan.FromSeconds(20);

    private static readonly Regex HeightLabelRegex = new(
        @"(?<!\d)(?<height>2160|1440|1080|960|854|848|720|640|576|540|480|432|360|320|240|216|144)p(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DimensionRegex = new(
        @"(?<!\d)(?<width>\d{3,4})[xX](?<height>\d{3,4})(?!\d)",
        RegexOptions.Compiled);

    private static readonly Regex HeightQueryRegex = new(
        @"(?:^|[?&;,_-])(?:quality|height|res|resolution|q|label|profile)=(?<height>\d{3,4})p?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CandidateDisplayInfo GetDisplayInfo(VideoCandidate candidate)
    {
        var host = GetHostLabel(candidate.Url);
        var shortUrl = GetShortUrlLabel(candidate.Url);
        var height = ExtractVideoHeight(candidate.Url);
        var qualityLabel = height is > 0
            ? $"{height}p"
            : candidate.Kind is VideoKind.Hls or VideoKind.Level5Hls
                ? "HLS"
                : "확인 필요";

        var priority = candidate.Kind switch
        {
            VideoKind.Level5Hls => 220,
            VideoKind.Hls => 215,
            VideoKind.DirectFile => 120,
            _ => 0
        };

        if (IsCurrentlyPlaying(candidate))
        {
            priority += 10_000;
        }

        if (height is > 0)
        {
            priority += Math.Min(height.Value / 5, 320);
        }

        if (candidate.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            priority += 24;
        }
        else if (candidate.ContentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                 candidate.ContentType.Contains("application/vnd.apple", StringComparison.OrdinalIgnoreCase))
        {
            priority += 20;
        }

        priority += candidate.Source switch
        {
            "CDP Media" => 35,
            "CDP 응답" => 24,
            "네트워크" => 20,
            "태그" => 16,
            "응답바디" => 12,
            "HTML" => 8,
            "요청URL" or "응답URL" => 4,
            _ => 0
        };

        if (IsLikelyAuxiliaryVideoUrl(candidate.Url))
        {
            priority -= 90;
        }

        var recommendation = IsCurrentlyPlaying(candidate)
            ? "재생중"
            : priority switch
            {
                >= 360 => "고화질 후보",
                >= 260 => "메인 후보",
                >= 160 => "영상 후보",
                _ => "보조 후보"
            };

        return new CandidateDisplayInfo(priority, height, recommendation, qualityLabel, host, shortUrl);
    }

    public static bool IsCurrentlyPlaying(VideoCandidate candidate)
    {
        return candidate.LastPlaybackSignalAt is DateTime signalAt &&
            DateTime.Now - signalAt <= PlaybackSignalLifetime;
    }

    public static string ShortenContentType(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return "-";
        }

        var semicolonIndex = contentType.IndexOf(';', StringComparison.Ordinal);
        return semicolonIndex >= 0 ? contentType[..semicolonIndex] : contentType;
    }

    private static string GetHostLabel(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "-";
        }

        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
    }

    private static string GetShortUrlLabel(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var tail = parts.Length switch
        {
            0 => uri.AbsolutePath,
            1 => parts[0],
            _ => string.Join('/', parts.TakeLast(Math.Min(3, parts.Length)))
        };

        if (!string.IsNullOrWhiteSpace(uri.Query))
        {
            var relevantQuery = BuildRelevantQueryLabel(uri.Query);
            if (!string.IsNullOrWhiteSpace(relevantQuery))
            {
                tail += "?" + relevantQuery;
            }
        }

        const int maxLength = 92;
        return tail.Length <= maxLength ? tail : "..." + tail[^maxLength..];
    }

    private static string BuildRelevantQueryLabel(string query)
    {
        var trimmedQuery = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            return "";
        }

        var relevantKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "q", "quality", "height", "res", "resolution", "profile", "label", "type"
        };

        var parts = trimmedQuery
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && relevantKeys.Contains(WebUtility.UrlDecode(pair[0])))
            .Select(pair => $"{WebUtility.UrlDecode(pair[0])}={WebUtility.UrlDecode(pair[1])}")
            .ToList();

        return parts.Count == 0 ? "" : string.Join('&', parts.Take(3));
    }

    private static int? ExtractVideoHeight(string url)
    {
        foreach (Match match in DimensionRegex.Matches(url))
        {
            if (int.TryParse(match.Groups["height"].Value, out var height))
            {
                return height;
            }
        }

        foreach (Match match in HeightLabelRegex.Matches(url))
        {
            if (int.TryParse(match.Groups["height"].Value, out var height))
            {
                return height;
            }
        }

        foreach (Match match in HeightQueryRegex.Matches(url))
        {
            if (int.TryParse(match.Groups["height"].Value, out var height))
            {
                return height;
            }
        }

        return null;
    }

    private static bool IsLikelyAuxiliaryVideoUrl(string url)
    {
        var lowerUrl = url.ToLowerInvariant();
        return lowerUrl.Contains("preview", StringComparison.Ordinal) ||
            lowerUrl.Contains("trailer", StringComparison.Ordinal) ||
            lowerUrl.Contains("thumb", StringComparison.Ordinal) ||
            lowerUrl.Contains("sprite", StringComparison.Ordinal) ||
            lowerUrl.Contains("sample", StringComparison.Ordinal) ||
            lowerUrl.Contains("teaser", StringComparison.Ordinal) ||
            lowerUrl.Contains("/ad/", StringComparison.Ordinal) ||
            lowerUrl.Contains("/ads/", StringComparison.Ordinal) ||
            lowerUrl.Contains("vast", StringComparison.Ordinal);
    }
}
