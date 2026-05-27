using WebVideoDownloader.Models;

namespace WebVideoDownloader.Services;

internal static class MediaClassifier
{
    public static VideoKind DetermineVideoKind(string url, string? contentType)
    {
        var lowerUrl = url.ToLowerInvariant();
        var lowerContentType = contentType?.ToLowerInvariant() ?? "";

        if (lowerUrl.Contains(".m3u8", StringComparison.Ordinal) ||
            lowerContentType.Contains("mpegurl", StringComparison.Ordinal) ||
            lowerContentType.Contains("application/vnd.apple", StringComparison.Ordinal))
        {
            if (lowerUrl.Contains("/v.html", StringComparison.Ordinal))
            {
                return VideoKind.Level5Hls;
            }

            return VideoKind.Hls;
        }

        if (lowerUrl.Contains("/v.html?", StringComparison.Ordinal))
        {
            return VideoKind.Level5Hls;
        }

        if (lowerUrl.Contains(".mp4", StringComparison.Ordinal) ||
            lowerUrl.Contains(".webm", StringComparison.Ordinal) ||
            lowerUrl.Contains(".m4v", StringComparison.Ordinal) ||
            lowerUrl.Contains(".mov", StringComparison.Ordinal))
        {
            return VideoKind.DirectFile;
        }

        if (lowerContentType.StartsWith("video/", StringComparison.Ordinal) &&
            !lowerContentType.Contains("mp2t", StringComparison.Ordinal))
        {
            return VideoKind.DirectFile;
        }

        return VideoKind.Unknown;
    }

    public static bool IsLikelyDirectMediaResponse(string url, string resourceType, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            IsLikelySegmentUrl(url, contentType))
        {
            return false;
        }

        if (!resourceType.Equals("Media", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lowerContentType = contentType?.ToLowerInvariant() ?? "";
        return string.IsNullOrWhiteSpace(lowerContentType) ||
            lowerContentType.StartsWith("video/", StringComparison.Ordinal) ||
            lowerContentType.Contains("octet-stream", StringComparison.Ordinal);
    }

    public static bool ShouldInspectResponseBody(NetworkRequestInfo requestInfo)
    {
        if (string.IsNullOrWhiteSpace(requestInfo.Url) ||
            IsLikelySegmentUrl(requestInfo.Url, requestInfo.MimeType))
        {
            return false;
        }

        if (requestInfo.ContentLength is > 2_000_000)
        {
            return false;
        }

        var resourceType = requestInfo.ResourceType.ToLowerInvariant();
        if (resourceType is not ("document" or "script" or "xhr" or "fetch"))
        {
            return false;
        }

        var mimeType = requestInfo.MimeType.ToLowerInvariant();
        return string.IsNullOrWhiteSpace(mimeType) ||
            mimeType.Contains("mpegurl", StringComparison.Ordinal) ||
            mimeType.Contains("application/vnd.apple", StringComparison.Ordinal) ||
            mimeType.Contains("application/octet-stream", StringComparison.Ordinal) ||
            mimeType.Contains("binary/octet-stream", StringComparison.Ordinal) ||
            mimeType.Contains("html", StringComparison.Ordinal) ||
            mimeType.Contains("javascript", StringComparison.Ordinal) ||
            mimeType.Contains("json", StringComparison.Ordinal) ||
            mimeType.Contains("text", StringComparison.Ordinal);
    }

    public static bool IsLikelySegmentUrl(string url, string? contentType)
    {
        var lowerUrl = url.ToLowerInvariant();
        var lowerContentType = contentType?.ToLowerInvariant() ?? "";

        return lowerUrl.Contains(".ts", StringComparison.Ordinal) ||
            lowerUrl.Contains(".m4s", StringComparison.Ordinal) ||
            lowerUrl.Contains(".cmfv", StringComparison.Ordinal) ||
            lowerUrl.Contains("/segment", StringComparison.Ordinal) ||
            lowerUrl.Contains("/segments/", StringComparison.Ordinal) ||
            lowerUrl.Contains("/frag", StringComparison.Ordinal) ||
            lowerContentType.Contains("mp2t", StringComparison.Ordinal);
    }
}
