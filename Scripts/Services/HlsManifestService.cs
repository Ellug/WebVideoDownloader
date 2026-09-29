using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using WebVideoDownloader.Models;

namespace WebVideoDownloader.Services;

internal static class HlsManifestService
{
    private static readonly Regex DimensionRegex = new(
        @"(?<!\d)(?<width>\d{3,4})[xX](?<height>\d{3,4})(?!\d)",
        RegexOptions.Compiled);

    public static bool LooksLikeManifest(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var text = body.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return text.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> ExtractPlaylistUrls(string manifestText, string manifestUrl)
    {
        var urls = new List<string>();
        var expectsVariantUri = false;

        foreach (var rawLine in manifestText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                var uri = ExtractAttribute(line, "URI");
                if (!string.IsNullOrWhiteSpace(uri))
                {
                    urls.Add(UrlTools.ResolveAgainst(manifestUrl, uri));
                }

                expectsVariantUri = false;
                continue;
            }

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                expectsVariantUri = true;
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (expectsVariantUri)
            {
                urls.Add(UrlTools.ResolveAgainst(manifestUrl, line));
                expectsVariantUri = false;
            }
        }

        return urls;
    }

    public static string Normalize(string manifestText, string manifestUrl)
    {
        var builder = new StringBuilder();

        foreach (var rawLine in manifestText.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                builder.AppendLine();
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine(RewriteUriAttribute(line, manifestUrl));
                continue;
            }

            if (line.StartsWith('#'))
            {
                builder.AppendLine(line);
                continue;
            }

            builder.AppendLine(UrlTools.ResolveAgainst(manifestUrl, line));
        }

        return builder.ToString();
    }

    public static IReadOnlyList<string> ExtractAes128KeyUrls(string manifestText, string manifestUrl)
    {
        return manifestText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            .Where(line => ExtractAttribute(line, "METHOD").Equals("AES-128", StringComparison.OrdinalIgnoreCase))
            .Select(line => ExtractAttribute(line, "URI"))
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .Select(uri => UrlTools.ResolveAgainst(manifestUrl, uri))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 매니페스트가 선언한 암호화 방식(METHOD)을 모읍니다. NONE은 제외합니다.
    /// AES-128 이외의 값이 있으면 직접 복호화 대신 ffmpeg에 맡겨야 합니다.
    /// </summary>
    public static IReadOnlyList<string> ExtractEncryptionMethods(string manifestText)
    {
        return manifestText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            .Select(line => ExtractAttribute(line, "METHOD").Trim())
            .Where(method => !string.IsNullOrWhiteSpace(method) &&
                !method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 키 서버 응답을 16바이트 키로 해석합니다.
    /// 원시 16바이트 외에 hex 문자열(0x… 포함)이나 base64로 내려주는 사이트가 있습니다.
    /// </summary>
    public static byte[]? ParseKeyMaterial(byte[] responseBytes)
    {
        if (responseBytes.Length == 16)
        {
            return responseBytes;
        }

        if (responseBytes.Length is 0 or > 64)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(responseBytes).Trim().Trim('"', '\'');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (text.Length == 32 && text.All(Uri.IsHexDigit))
        {
            try
            {
                return Convert.FromHexString(text);
            }
            catch (FormatException)
            {
                // base64 해석으로 넘어갑니다.
            }
        }

        return TryDecodeBase64Key(text);
    }

    private static byte[]? TryDecodeBase64Key(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');

        try
        {
            var decoded = Convert.FromBase64String(padded);
            return decoded.Length == 16 ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool HasMediaSegments(string manifestText)
    {
        return manifestText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.TrimStart().StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase));
    }

    public static bool UsesFragmentedMp4(string manifestText)
    {
        var lowerManifest = manifestText.ToLowerInvariant();
        return lowerManifest.Contains("#ext-x-map:", StringComparison.Ordinal) ||
            lowerManifest.Contains(".m4s", StringComparison.Ordinal) ||
            lowerManifest.Contains(".mp4", StringComparison.Ordinal) ||
            lowerManifest.Contains("mp4a.", StringComparison.Ordinal);
    }

    public static IReadOnlyList<HlsVariantPlaylist> ExtractVariants(string manifestText, string manifestUrl)
    {
        var variants = new List<HlsVariantPlaylist>();
        string? currentStreamInfo = null;

        foreach (var rawLine in manifestText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                currentStreamInfo = line;
                continue;
            }

            if (line.StartsWith('#') || currentStreamInfo is null)
            {
                continue;
            }

            var bandwidth = TryParseLong(ExtractAttribute(currentStreamInfo, "AVERAGE-BANDWIDTH"));
            bandwidth ??= TryParseLong(ExtractAttribute(currentStreamInfo, "BANDWIDTH"));

            int? height = null;
            var resolution = ExtractAttribute(currentStreamInfo, "RESOLUTION");
            var resolutionMatch = DimensionRegex.Match(resolution);
            if (resolutionMatch.Success && int.TryParse(resolutionMatch.Groups["height"].Value, out var parsedHeight))
            {
                height = parsedHeight;
            }

            var url = UrlTools.ResolveAgainst(manifestUrl, line);
            var label = height is > 0
                ? $"{height}p"
                : bandwidth is > 0
                    ? $"{bandwidth.Value / 1000}kbps"
                    : "variant";

            variants.Add(new HlsVariantPlaylist(url, height, bandwidth, label));
            currentStreamInfo = null;
        }

        return variants;
    }

    public static IReadOnlyList<HlsSegment> ParseSegments(
        string manifestText,
        string manifestUrl,
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>> decodedKeyByUrl)
    {
        var segments = new List<HlsSegment>();
        IReadOnlyList<byte[]> currentKeyCandidates = [];
        byte[]? currentManifestIv = null;
        long mediaSequence = 0;

        foreach (var rawLine in manifestText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                _ = long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), out mediaSequence);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                var method = ExtractAttribute(line, "METHOD");
                if (!method.Equals("AES-128", StringComparison.OrdinalIgnoreCase))
                {
                    currentKeyCandidates = [];
                    currentManifestIv = null;
                    continue;
                }

                var keyUri = ExtractAttribute(line, "URI");
                var absoluteKeyUrl = UrlTools.ResolveAgainst(manifestUrl, keyUri);
                currentKeyCandidates = decodedKeyByUrl.TryGetValue(absoluteKeyUrl, out var keyBytes) ? keyBytes : [];

                var ivText = ExtractAttribute(line, "IV");
                currentManifestIv = string.IsNullOrWhiteSpace(ivText) ? null : ParseIv(ivText);
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            var segmentUrl = UrlTools.ResolveAgainst(manifestUrl, line);
            var sequenceNumber = mediaSequence + segments.Count;
            segments.Add(new HlsSegment(
                segments.Count,
                sequenceNumber,
                segmentUrl,
                currentKeyCandidates,
                currentManifestIv?.ToArray()));
        }

        return segments;
    }

    public static byte[] BuildSequenceIv(long sequenceNumber)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequenceNumber);
        return iv;
    }

    public static string ExtractAttribute(string line, string name)
    {
        // 태그의 첫 속성은 쉼표가 아니라 콜론 뒤에 옵니다(#EXT-X-KEY:METHOD=AES-128,URI="...").
        // 콜론을 구분자로 인정하지 않으면 METHOD/BANDWIDTH 같은 선두 속성이 항상 빈 값으로 나옵니다.
        var match = Regex.Match(
            line,
            @"(?:^|[,:])\s*" + Regex.Escape(name) + @"\s*=\s*(?:""(?<quoted>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^,]*))",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return "";
        }

        if (match.Groups["quoted"].Success)
        {
            return match.Groups["quoted"].Value;
        }

        return match.Groups["single"].Success ? match.Groups["single"].Value : match.Groups["bare"].Value;
    }

    private static byte[] ParseIv(string ivText)
    {
        var hex = ivText.Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (hex.Length < 32)
        {
            hex = hex.PadLeft(32, '0');
        }
        else if (hex.Length > 32)
        {
            hex = hex[^32..];
        }

        return Convert.FromHexString(hex);
    }

    private static string RewriteUriAttribute(string line, string manifestUrl)
    {
        return Regex.Replace(
            line,
            @"\bURI\s*=\s*(?:""(?<quoted>[^""]*)""|'(?<single>[^']*)'|(?<bare>[^,]*))",
            match =>
            {
                var originalValue = match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["single"].Success
                        ? match.Groups["single"].Value
                        : match.Groups["bare"].Value;

                var resolvedValue = UrlTools.ResolveAgainst(manifestUrl, originalValue);
                return "URI=\"" + resolvedValue.Replace("\"", "%22", StringComparison.Ordinal) + "\"";
            },
            RegexOptions.IgnoreCase);
    }

    private static long? TryParseLong(string value)
    {
        return long.TryParse(value, out var parsed) ? parsed : null;
    }
}
