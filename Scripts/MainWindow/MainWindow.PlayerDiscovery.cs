#pragma warning disable CS8600

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebVideoDownloader.Models;
using WebVideoDownloader.Services;

namespace WebVideoDownloader;

public partial class MainWindow
{
    private async Task AddLevel5CandidatesFromPlayerAsync(string rawPlayerUrl)
    {
        string playerUrl = ResolveUrl(rawPlayerUrl);
        if (string.IsNullOrWhiteSpace(playerUrl) || !_playerUrls.Add(playerUrl))
        {
            return;
        }
        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(25.0));
            string playerHtml = await FetchStringAsync(playerUrl, _currentPageUrl, cts.Token);
            string manifestUrl = ExtractJsStringProperty(playerHtml, "url");
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                Log("플레이어에서 HLS URL을 찾지 못했습니다: " + playerUrl);
                return;
            }
            manifestUrl = UrlTools.ResolveAgainst(playerUrl, manifestUrl);
            string wasmJs = UrlTools.ResolveAgainst(playerUrl, ExtractJsStringProperty(playerHtml, "wasmJs"));
            string wasmBin = UrlTools.ResolveAgainst(playerUrl, ExtractJsStringProperty(playerHtml, "wasmBin"));
            string wasmHash = ExtractJsStringProperty(playerHtml, "wasmSha384Hex");
            if (string.IsNullOrWhiteSpace(wasmJs) || string.IsNullOrWhiteSpace(wasmBin))
            {
                AddCandidate(manifestUrl, "플레이어", "application/vnd.apple.mpegurl", VideoKind.Hls);
                return;
            }
            AddCandidate(manifestUrl, "Level5", "application/vnd.apple.mpegurl", VideoKind.Level5Hls, playerUrl, wasmJs, wasmBin, wasmHash);
        }
        catch (Exception ex)
        {
            Log("플레이어 분석 실패: " + ex.Message);
        }
    }

    private void AddExtractedCandidateUrls(string? text, string source)
    {
        foreach (string item in _mediaUrlExtractor.ExtractMediaUrls(text))
        {
            AddCandidate(item, source, "");
        }
    }

    private bool IsPlayerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }
        string text = ResolveUrl(url) ?? url;
        return text.Contains("/player.php?", StringComparison.OrdinalIgnoreCase) && text.Contains("k=", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }
        rawUrl = WebUtility.HtmlDecode(rawUrl.Trim().Trim('"', '\'', '`'));
        rawUrl = rawUrl.Replace("\\/", "/", StringComparison.Ordinal);
        if (rawUrl.StartsWith("//", StringComparison.Ordinal))
        {
            Uri result;
            string text = (Uri.TryCreate(_currentPageUrl, UriKind.Absolute, out result) ? result.Scheme : Uri.UriSchemeHttps);
            rawUrl = text + ":" + rawUrl;
        }
        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri result2))
        {
            return result2.AbsoluteUri;
        }
        if (Uri.TryCreate(_currentPageUrl, UriKind.Absolute, out Uri result3) && Uri.TryCreate(result3, rawUrl, out Uri result4))
        {
            return result4.AbsoluteUri;
        }
        return null;
    }

    private static string ExtractJsStringProperty(string html, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }
        Match match = Regex.Match(html, "\\b" + Regex.Escape(propertyName) + "\\s*:\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return "";
        }
        return DecodeJavaScriptString(match.Groups["value"].Value);
    }

    private static string DecodeJavaScriptString(string escapedValue)
    {
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + escapedValue + "\"") ?? "";
        }
        catch
        {
            return Regex.Unescape(escapedValue).Replace("\\/", "/", StringComparison.Ordinal);
        }
    }
}





