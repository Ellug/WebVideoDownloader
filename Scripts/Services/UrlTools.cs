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
