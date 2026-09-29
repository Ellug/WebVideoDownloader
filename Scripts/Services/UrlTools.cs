using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace WebVideoDownloader.Services;

internal static class UrlTools
{
    public static string ResolveAgainst(string baseUrl, string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return "";
        }

        rawUrl = WebUtility.HtmlDecode(rawUrl.Trim().Trim('"', '\'', '`'));
        rawUrl = rawUrl.Replace("\\/", "/", StringComparison.Ordinal);

        if (rawUrl.StartsWith("//", StringComparison.Ordinal))
        {
            rawUrl = "https:" + rawUrl;
        }

        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.AbsoluteUri;
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, rawUrl, out var relativeUri))
        {
            return relativeUri.AbsoluteUri;
        }

        return rawUrl;
    }

    public static string GetDirectoryUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "";
        }

        var path = uri.AbsolutePath;
        var slashIndex = path.LastIndexOf('/');
        if (slashIndex < 0)
        {
            return uri.GetLeftPart(UriPartial.Authority) + "/";
        }

        var directoryPath = path[..(slashIndex + 1)];
        var builder = new UriBuilder(uri)
        {
            Path = directoryPath,
            Query = "",
            Fragment = ""
        };

        return builder.Uri.AbsoluteUri;
    }

    public static string NormalizeCandidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = ""
        };

        return builder.Uri.AbsoluteUri;
    }

    private static readonly char[] PathPatternChars =
        ['(', ')', '[', ']', '{', '}', '|', '^', '*', '\\', '<', '>', '"', '`', '$', ' ', '\t'];

    private static readonly char[] QueryPatternChars =
        ['(', ')', '{', '}', '^', '\\', '<', '>', '"', '`', ' ', '\t'];

    /// <summary>
    /// 스크립트에서 긁어온 문자열이 실제 미디어 URL인지 판별합니다.
    /// 정규식 리터럴(/keyid:\/\/([0-9a-zA-Z.]+)\.m3u8/)이나 템플릿 자리표시자(${id}.m3u8)가
    /// URL 후보로 올라가 ffmpeg 404를 만드는 것을 막습니다.
    /// </summary>
    public static bool IsPlausibleMediaUrl([NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !IsPlausibleHost(uri.Host))
        {
            return false;
        }

        var path = SafeUnescape(uri.AbsolutePath);
        if (path.IndexOfAny(PathPatternChars) >= 0 || path.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var query = SafeUnescape(uri.Query);
        return query.IndexOfAny(QueryPatternChars) < 0 &&
            !query.Contains("${", StringComparison.Ordinal);
    }

    /// <summary>
    /// 아직 해석되지 않은 원본 매치 문자열이 정규식/템플릿 조각인지 빠르게 걸러냅니다.
    /// </summary>
    public static bool LooksLikeUrlPattern(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return true;
        }

        if (rawUrl.IndexOfAny(PathPatternChars) >= 0)
        {
            return true;
        }

        var schemeIndex = rawUrl.IndexOf("://", StringComparison.Ordinal);
        return schemeIndex >= 0 &&
            rawUrl.IndexOf("://", schemeIndex + 3, StringComparison.Ordinal) >= 0;
    }

    private static bool IsPlausibleHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        foreach (var character in host)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '.' or '_' or ':' or '[' or ']'))
            {
                return false;
            }
        }

        return true;
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
    }

    public static bool TryNormalizePageUrl(string rawUrl, out string normalizedUrl)
    {
        normalizedUrl = "";

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        rawUrl = rawUrl.Trim();
        if (!rawUrl.Contains("://", StringComparison.Ordinal))
        {
            rawUrl = "https://" + rawUrl;
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }
}
