using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebVideoDownloader.Models;
using WebVideoDownloader.Services;

namespace WebVideoDownloader;

public partial class MainWindow
{
    private async Task DownloadDirectFileAsync(VideoCandidate candidate, string outputPath, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
        await AddCommonRequestHeadersAsync(request, candidate);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);

        var buffer = new byte[128 * 1024];
        long downloadedBytes = 0;
        int read;

        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloadedBytes += read;

            if (totalBytes is > 0)
            {
                var percent = (int)Math.Clamp(downloadedBytes * 100D / totalBytes.Value, 0, 100);
                SetProgress(percent, indeterminate: false);
                SetStatus($"다운로드 중... {percent}% ({FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes.Value)})");
            }
            else
            {
                SetStatus($"다운로드 중... {FormatBytes(downloadedBytes)}");
            }
        }
    }

    private async Task DownloadHlsAsync(VideoCandidate candidate, string outputPath, CancellationToken cancellationToken)
    {
        var headerLines = await BuildFfmpegHeaderLinesAsync(candidate);
        var stderrTail = new Queue<string>();

        if (string.IsNullOrWhiteSpace(candidate.CapturedManifestText))
        {
            await _ffmpegRunner.DownloadHlsAsync(candidate.Url, outputPath, headerLines, stderrTail, cancellationToken);
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "WebVideoDownloader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            Log($"캡처한 HLS 매니페스트 사용: {candidate.Url}");
            await DownloadCapturedHlsAsync(candidate, outputPath, tempRoot, headerLines, stderrTail, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task DownloadCapturedHlsAsync(
        VideoCandidate candidate,
        string outputPath,
        string tempRoot,
        string headerLines,
        Queue<string> stderrTail,
        CancellationToken cancellationToken)
    {
        var manifestUrl = candidate.Url;
        var manifestText = candidate.CapturedManifestText ?? "";

        if (!HlsManifestService.HasMediaSegments(manifestText))
        {
            var variants = HlsManifestService.ExtractVariants(manifestText, manifestUrl)
                .OrderByDescending(variant => variant.Height ?? 0)
                .ThenByDescending(variant => variant.Bandwidth ?? 0)
                .ToList();

            if (variants.Count == 0)
            {
                throw new InvalidOperationException("캡처한 HLS 매니페스트에서 세그먼트나 화질 목록을 찾지 못했습니다.");
            }

            var selectedVariant = variants[0];
            SetStatus($"HLS 화질 목록 선택 중... {selectedVariant.Label}");
            manifestUrl = selectedVariant.Url;
            manifestText = await FetchStringAsync(manifestUrl, candidate.Referer, cancellationToken);
        }

        if (HlsManifestService.UsesFragmentedMp4(manifestText))
        {
            SetStatus("캡처한 HLS 매니페스트를 로컬 플레이리스트로 변환 중...");
            var localManifestPath = Path.Combine(tempRoot, "playlist.m3u8");
            await File.WriteAllTextAsync(
                localManifestPath,
                HlsManifestService.Normalize(manifestText, manifestUrl),
                new UTF8Encoding(false),
                cancellationToken);

            await _ffmpegRunner.DownloadHlsAsync(localManifestPath, outputPath, headerLines, stderrTail, cancellationToken);
            return;
        }

        SetStatus("HLS 세그먼트를 직접 다운로드 중...");
        var decodedKeyByUrl = await FetchStandardHlsKeysAsync(manifestText, manifestUrl, candidate.Referer, cancellationToken);
        var segments = HlsManifestService.ParseSegments(manifestText, manifestUrl, decodedKeyByUrl);
        if (segments.Count == 0)
        {
            throw new InvalidOperationException("HLS 세그먼트를 찾지 못했습니다.");
        }

        var transportStreamPath = Path.Combine(tempRoot, "merged.ts");
        await DownloadAndDecryptLevel5SegmentsAsync(segments, candidate.Referer, transportStreamPath, cancellationToken);
        await ValidateTransportStreamFileAsync(transportStreamPath, cancellationToken);

        SetStatus("TS를 MP4로 변환 중...");
        await _ffmpegRunner.RemuxTransportStreamAsync(transportStreamPath, outputPath, cancellationToken);
    }

    private async Task DownloadLevel5HlsAsync(VideoCandidate candidate, string outputPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.WasmJsUrl) || string.IsNullOrWhiteSpace(candidate.WasmBinUrl))
        {
            throw new InvalidOperationException("Level5 플레이어 런타임 정보를 찾지 못했습니다. 페이지를 다시 열어 후보를 새로 탐색하세요.");
        }

        var nodePath = FindNodeExecutable();
        if (nodePath is null)
        {
            throw new InvalidOperationException("이 사이트의 Level5 HLS 키를 처리하려면 Node.js가 필요합니다. node.exe를 PATH에 추가한 뒤 다시 실행하세요.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "WebVideoDownloader", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            SetStatus("Level5 HLS 플레이리스트 분석 중...");

            var manifestText = await FetchStringAsync(candidate.Url, candidate.Referer, cancellationToken);
            var uniqueKeyUrls = HlsManifestService.ExtractAes128KeyUrls(manifestText, candidate.Url);
            if (uniqueKeyUrls.Count == 0)
            {
                await _ffmpegRunner.DownloadHlsAsync(candidate.Url, outputPath, await BuildFfmpegHeaderLinesAsync(candidate), new Queue<string>(), cancellationToken);
                return;
            }

            var keyJsonByUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < uniqueKeyUrls.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetStatus($"Level5 키 요청 중... {index + 1}/{uniqueKeyUrls.Count}");
                keyJsonByUrl[uniqueKeyUrls[index]] = await FetchStringAsync(uniqueKeyUrls[index], candidate.Referer, cancellationToken);
            }

            var decodedKeyCandidates = await DecodeLevel5KeysAsync(
                nodePath,
                candidate,
                uniqueKeyUrls.Select(url => keyJsonByUrl[url]).ToList(),
                tempRoot,
                cancellationToken);

            var decodedKeyByUrl = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < uniqueKeyUrls.Count; index++)
            {
                decodedKeyByUrl[uniqueKeyUrls[index]] = decodedKeyCandidates[index];
            }

            var segments = HlsManifestService.ParseSegments(manifestText, candidate.Url, decodedKeyByUrl);
            if (segments.Count == 0)
            {
                throw new InvalidOperationException("HLS 세그먼트를 찾지 못했습니다.");
            }

            var transportStreamPath = Path.Combine(tempRoot, "merged.ts");
            await DownloadAndDecryptLevel5SegmentsAsync(segments, candidate.Referer, transportStreamPath, cancellationToken);
            await ValidateTransportStreamFileAsync(transportStreamPath, cancellationToken);

            SetStatus("TS를 MP4로 변환 중...");
            await _ffmpegRunner.RemuxTransportStreamAsync(transportStreamPath, outputPath, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task AddCommonRequestHeadersAsync(HttpRequestMessage request, VideoCandidate candidate)
    {
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Accept.ParseAdd("*/*");

        if (Uri.TryCreate(candidate.Referer, UriKind.Absolute, out var referer))
        {
            request.Headers.Referrer = referer;
            request.Headers.TryAddWithoutValidation("Origin", GetOrigin(referer));
        }

        var cookieHeader = await GetCookieHeaderAsync(candidate.Url);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }
    }

    private async Task<string> FetchStringAsync(string url, string referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await AddRequestHeadersAsync(request, referer, url);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await NetworkResponseReader.ReadBytesAsync(response, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    private async Task<byte[]> FetchBytesAsync(string url, string referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await AddRequestHeadersAsync(request, referer, url);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await NetworkResponseReader.ReadBytesAsync(response, cancellationToken);
    }

    private async Task AddRequestHeadersAsync(HttpRequestMessage request, string refererUrl, string targetUrl)
    {
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Accept.ParseAdd("*/*");

        if (Uri.TryCreate(refererUrl, UriKind.Absolute, out var referer))
        {
            request.Headers.Referrer = referer;
            request.Headers.TryAddWithoutValidation("Origin", GetOrigin(referer));
        }

        var cookieHeader = await GetCookieHeaderAsync(targetUrl);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }
    }

    private async Task<IReadOnlyList<IReadOnlyList<byte[]>>> DecodeLevel5KeysAsync(
        string nodePath,
        VideoCandidate candidate,
        IReadOnlyList<string> keyJsonBodies,
        string tempRoot,
        CancellationToken cancellationToken)
    {
        var runtimePath = Path.Combine(tempRoot, "runtime.mjs");
        var wasmPath = Path.Combine(tempRoot, "core.wasm");
        var keysPath = Path.Combine(tempRoot, "keys.json");
        var decoderPath = Path.Combine(tempRoot, "decode-level5.mjs");

        SetStatus("Level5 WASM 런타임 준비 중...");
        var runtimeJs = await FetchStringAsync(candidate.WasmJsUrl!, candidate.Referer, cancellationToken);
        var wasmBytes = await FetchBytesAsync(candidate.WasmBinUrl!, candidate.Referer, cancellationToken);

        if (!string.IsNullOrWhiteSpace(candidate.ExpectedWasmSha384Hex))
        {
            var actualHash = Convert.ToHexString(SHA384.HashData(wasmBytes)).ToLowerInvariant();
            if (!actualHash.Equals(candidate.ExpectedWasmSha384Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Level5 WASM 해시가 플레이어 정보와 일치하지 않습니다.");
            }
        }

        await File.WriteAllTextAsync(runtimePath, runtimeJs, new UTF8Encoding(false), cancellationToken);
        await File.WriteAllBytesAsync(wasmPath, wasmBytes, cancellationToken);
        await File.WriteAllTextAsync(keysPath, JsonSerializer.Serialize(keyJsonBodies), new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(decoderPath, VideoProbeScripts.Level5Decoder, new UTF8Encoding(false), cancellationToken);

        using var process = new Process();
        process.StartInfo.FileName = nodePath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WorkingDirectory = tempRoot;
        process.StartInfo.ArgumentList.Add(decoderPath);
        process.StartInfo.ArgumentList.Add(wasmPath);
        process.StartInfo.ArgumentList.Add(keysPath);

        if (!process.Start())
        {
            throw new InvalidOperationException("Node.js 키 디코더를 시작하지 못했습니다.");
        }

        try
        {
            SetStatus("Level5 키 디코딩 중...");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Level5 키 디코딩 실패. 종료 코드: {process.ExitCode}{Environment.NewLine}{stderr}");
            }

            var base64KeyCandidates = JsonSerializer.Deserialize<List<List<string>>>(stdout) ?? [];
            if (base64KeyCandidates.Count != keyJsonBodies.Count)
            {
                throw new InvalidOperationException("Level5 키 디코더 결과 개수가 맞지 않습니다.");
            }

            var decodedKeyCandidates = new List<IReadOnlyList<byte[]>>(base64KeyCandidates.Count);
            foreach (var keyCandidates in base64KeyCandidates)
            {
                var decodedKeys = keyCandidates
                    .Select(Convert.FromBase64String)
                    .Select(bytes => bytes.Length == 16
                        ? bytes
                        : throw new InvalidOperationException("Level5 키 길이가 16바이트가 아닙니다."))
                    .DistinctBy(Convert.ToHexString)
                    .ToList();

                if (decodedKeys.Count == 0)
                {
                    throw new InvalidOperationException("Level5 키를 디코딩하지 못했습니다.");
                }

                decodedKeyCandidates.Add(decodedKeys);
            }

            return decodedKeyCandidates;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private async Task DownloadAndDecryptLevel5SegmentsAsync(
        IReadOnlyList<HlsSegment> segments,
        string referer,
        string transportStreamPath,
        CancellationToken cancellationToken)
    {
        const int concurrency = 8;
        var inFlight = new Dictionary<int, Task<byte[]>>();
        var nextToStart = 0;
        var nextToWrite = 0;

        await using var output = new FileStream(
            transportStreamPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            useAsync: true);

        while (nextToWrite < segments.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (nextToStart < segments.Count && inFlight.Count < concurrency)
            {
                var segmentIndex = nextToStart;
                inFlight[segmentIndex] = DownloadAndDecryptSegmentAsync(segments[segmentIndex], referer, cancellationToken);
                nextToStart++;
            }

            var segmentBytes = await inFlight[nextToWrite];
            inFlight.Remove(nextToWrite);
            await output.WriteAsync(segmentBytes, cancellationToken);

            nextToWrite++;
            var percent = (int)Math.Clamp(nextToWrite * 100D / segments.Count, 0, 100);
            SetProgress(percent, indeterminate: false);
            SetStatus($"세그먼트 다운로드/복호화 중... {nextToWrite}/{segments.Count}");
        }
    }

    private async Task<byte[]> DownloadAndDecryptSegmentAsync(HlsSegment segment, string referer, CancellationToken cancellationToken)
    {
        var encryptedBytes = await FetchBytesAsync(segment.Url, referer, cancellationToken);
        var decodeResult = TransportStreamService.DecodeSegment(encryptedBytes, segment, cancellationToken);

        if (decodeResult.IsSuccess)
        {
            if (segment.Index == 0)
            {
                var message = decodeResult.IsRaw ? "세그먼트 형식 감지" : "세그먼트 복호화 방식 감지";
                Log($"{message}: {decodeResult.BestCandidate.Strategy}, syncOffset={decodeResult.BestCandidate.Offset}, syncPackets={decodeResult.BestCandidate.SyncCount}");
            }

            return decodeResult.Bytes;
        }

        var firstBytes = Convert.ToHexString(encryptedBytes.AsSpan(0, Math.Min(encryptedBytes.Length, 16)));
        throw new InvalidOperationException(
            $"세그먼트 #{segment.Index} 복호화 결과에서 MPEG-TS sync를 찾지 못했습니다. " +
            $"encLen={encryptedBytes.Length}, first16={firstBytes}, keys={segment.KeyCandidates.Count}, ivs={decodeResult.IvCandidateCount}, attempts={decodeResult.Attempts}, " +
            $"bestSync={decodeResult.BestCandidate.SyncCount}, bestOffset={decodeResult.BestCandidate.Offset}, URL: {segment.Url}");
    }

    private static async Task ValidateTransportStreamFileAsync(string transportStreamPath, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            transportStreamPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true);

        var header = new byte[188 * 4];
        var read = await input.ReadAsync(header, cancellationToken);
        if (read < 188 * 4)
        {
            throw new InvalidOperationException("병합된 TS 파일이 너무 작습니다.");
        }

        if (header[0] != 0x47 || header[188] != 0x47 || header[376] != 0x47 || header[564] != 0x47)
        {
            var firstBytes = Convert.ToHexString(header.AsSpan(0, Math.Min(read, 16)));
            throw new InvalidOperationException($"병합된 TS 파일의 sync 바이트가 맞지 않습니다. first16={firstBytes}");
        }
    }

    private async Task<string> BuildFfmpegHeaderLinesAsync(VideoCandidate candidate)
    {
        var builder = new StringBuilder();

        if (Uri.TryCreate(candidate.Referer, UriKind.Absolute, out var referer))
        {
            builder.Append("Referer: ").Append(candidate.Referer).Append("\r\n");
            builder.Append("Origin: ").Append(GetOrigin(referer)).Append("\r\n");
        }

        var cookieHeader = await GetCookieHeaderAsync(candidate.Url);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            builder.Append("Cookie: ").Append(cookieHeader).Append("\r\n");
        }

        return builder.ToString();
    }

    private async Task<string> GetCookieHeaderAsync(string targetUrl)
    {
        if (webView.CoreWebView2 is null)
        {
            return "";
        }

        try
        {
            var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(targetUrl);
            return string.Join("; ", cookies
                .GroupBy(cookie => cookie.Name)
                .Select(group => $"{group.Key}={group.Last().Value}"));
        }
        catch (Exception ex)
        {
            Log($"쿠키 읽기 실패: {ex.Message}");
            return "";
        }
    }

    private string GetUniqueOutputPath(VideoCandidate candidate)
    {
        var title = webView.CoreWebView2?.DocumentTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "video";
        }

        var baseName = SanitizeFileName(title);
        if (baseName.Length > 90)
        {
            baseName = baseName[..90].Trim();
        }

        var extension = candidate.Kind is VideoKind.Hls or VideoKind.Level5Hls ? ".mp4" : GetDirectDownloadExtension(candidate.Url);
        var fileName = $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
        var outputPath = Path.Combine(_downloadFolder, fileName);

        for (var index = 1; File.Exists(outputPath); index++)
        {
            outputPath = Path.Combine(_downloadFolder, $"{Path.GetFileNameWithoutExtension(fileName)} ({index}){extension}");
        }

        return outputPath;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<byte[]>>> FetchStandardHlsKeysAsync(
        string manifestText,
        string manifestUrl,
        string referer,
        CancellationToken cancellationToken)
    {
        var keyUrls = HlsManifestService.ExtractAes128KeyUrls(manifestText, manifestUrl);
        var keyByUrl = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < keyUrls.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetStatus($"HLS 키 요청 중... {index + 1}/{keyUrls.Count}");

            var keyBytes = await FetchBytesAsync(keyUrls[index], referer, cancellationToken);
            if (keyBytes.Length != 16)
            {
                throw new InvalidOperationException($"HLS AES-128 키 길이가 16바이트가 아닙니다. URL: {keyUrls[index]}");
            }

            keyByUrl[keyUrls[index]] = new[] { keyBytes };
        }

        return keyByUrl;
    }
}


