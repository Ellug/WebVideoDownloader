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

    /// <summary>
    /// 재생 중 브라우저가 디코더로 넘긴 바이트를 모아 하나의 파일로 저장합니다.
    /// 재생한 만큼만 저장되므로, 영상을 끝까지 재생한 뒤 눌러야 전체가 나옵니다.
    /// </summary>
    private async Task SaveCapturedMediaAsync(string outputPath, CancellationToken cancellationToken)
    {
        var snapshotDirectory = Path.Combine(Path.GetTempPath(), "WebVideoDownloader", Guid.NewGuid().ToString("N"));
        try
        {
            SetStatus("캡처본 정리 중...");
            if (webView.CoreWebView2 is { } core)
            {
                var flush = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
                {
                    expression = "window.__wvdFlushCapture?.()", awaitPromise = true, returnByValue = true
                })).WaitAsync(cancellationToken);
                using var flushResult = JsonDocument.Parse(flush);
                if (flushResult.RootElement.TryGetProperty("exceptionDetails", out _))
                    throw new InvalidOperationException("캡처 데이터 전송이 누락되었습니다. 페이지를 다시 열고 캡처하세요.");
            }
            var tracks = await _captureSink.SnapshotAsync(snapshotDirectory, cancellationToken);

            if (tracks.Count == 0)
            {
                throw new InvalidOperationException(
                    "캡처된 재생 데이터가 없습니다. 페이지에서 영상을 재생한 뒤 다시 시도하세요. " +
                    "DRM(EME/Widevine)으로 보호된 영상은 브라우저 안에서도 평문이 나오지 않아 캡처할 수 없습니다.");
            }

            foreach (var track in tracks)
            {
                Log($"캡처 트랙 #{track.TrackId}, 조각 #{track.PartIndex}: {FormatBytes(track.ByteCount)} ({track.MimeType})");
            }

            SetProgress(0, indeterminate: true);
            SetStatus($"캡처본 {tracks.Count}개 트랙 병합 중...");
            Log("현재까지 수신한 캡처 조각을 모두 저장합니다. 아직 재생/수신하지 않은 구간은 포함되지 않습니다.");
            await _ffmpegRunner.SaveCapturePartsAsync(tracks, snapshotDirectory, outputPath, cancellationToken);
        }
        finally { TryDeleteDirectory(snapshotDirectory); }
    }

    private async Task DownloadHlsAsync(VideoCandidate candidate, string outputPath, CancellationToken cancellationToken)
    {
        var headerLines = await BuildFfmpegHeaderLinesAsync(candidate);
        var stderrTail = new Queue<string>();

        if (string.IsNullOrWhiteSpace(candidate.CapturedManifestText))
        {
            var manifest = await FetchStringAsync(candidate.Url, candidate.Referer, cancellationToken);
            if (!HlsManifestService.LooksLikeManifest(manifest))
                throw new InvalidOperationException("영상 목록 대신 HTML/차단 응답을 받았습니다. 앱 안에서 영상이 재생되는지 확인하세요.");
            candidate = candidate with { CapturedManifestText = manifest };
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
            await RunFfmpegOnManifestAsync(
                manifestText, manifestUrl, outputPath, tempRoot, headerLines, stderrTail, null, cancellationToken);
            return;
        }

        LogManifestDiagnostics(manifestText, manifestUrl);

        var encryptionMethods = HlsManifestService.ExtractEncryptionMethods(manifestText);
        var unsupportedMethods = encryptionMethods
            .Where(method => !method.Equals("AES-128", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (unsupportedMethods.Count > 0)
        {
            Log($"직접 복호화가 불가능한 HLS 암호화 방식({string.Join(", ", unsupportedMethods)})입니다. ffmpeg에 맡깁니다.");
            await RunFfmpegOnManifestAsync(
                manifestText, manifestUrl, outputPath, tempRoot, headerLines, stderrTail, null, cancellationToken);
            return;
        }

        SetStatus("HLS 세그먼트를 직접 다운로드 중...");
        var decodedKeyByUrl = await FetchStandardHlsKeysAsync(manifestText, manifestUrl, candidate.Referer, cancellationToken);

        if (encryptionMethods.Count > 0 && decodedKeyByUrl.Count == 0)
        {
            Log("매니페스트가 암호화를 선언했지만 AES-128 키를 얻지 못했습니다. ffmpeg에 맡깁니다.");
            await RunFfmpegOnManifestAsync(
                manifestText, manifestUrl, outputPath, tempRoot, headerLines, stderrTail, null, cancellationToken);
            return;
        }

        try
        {
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
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception segmentFailure)
        {
            Log($"세그먼트 직접 처리 실패, ffmpeg로 재시도합니다: {segmentFailure.Message}");
            SetStatus("ffmpeg로 다시 시도하는 중...");
            SetProgress(0, indeterminate: true);
            await RunFfmpegOnManifestAsync(
                manifestText, manifestUrl, outputPath, tempRoot, headerLines, stderrTail, segmentFailure, cancellationToken);
        }
    }

    /// <summary>
    /// 실패 신고를 받았을 때 원인을 좁힐 수 있도록 매니페스트의 암호화 선언을 로그에 남깁니다.
    /// </summary>
    private void LogManifestDiagnostics(string manifestText, string manifestUrl)
    {
        var lines = manifestText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();

        var segmentCount = lines.Count(line => line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase));
        Log($"HLS 매니페스트 분석: {manifestUrl} (세그먼트 {segmentCount}개, {manifestText.Length}자)");

        var keyLines = lines
            .Where(line => line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        if (keyLines.Count == 0)
        {
            Log("  암호화 선언 없음(#EXT-X-KEY 부재). 평문 세그먼트로 간주합니다.");
            return;
        }

        foreach (var keyLine in keyLines)
        {
            Log("  " + keyLine);
        }
    }

    /// <summary>
    /// 매니페스트를 절대 URL로 정규화한 로컬 플레이리스트로 ffmpeg를 돌리고,
    /// 실패하면 원본 m3u8 주소로 한 번 더 시도합니다.
    /// ffmpeg는 표준 AES-128 HLS를 스스로 복호화하므로 직접 복호화가 막힌 사이트의 최종 대안입니다.
    /// </summary>
    private async Task RunFfmpegOnManifestAsync(
        string manifestText,
        string manifestUrl,
        string outputPath,
        string tempRoot,
        string headerLines,
        Queue<string> stderrTail,
        Exception? primaryFailure,
        CancellationToken cancellationToken)
    {
        var localManifestPath = Path.Combine(tempRoot, "playlist.m3u8");
        await File.WriteAllTextAsync(
            localManifestPath,
            HlsManifestService.Normalize(manifestText, manifestUrl),
            new UTF8Encoding(false),
            cancellationToken);

        try
        {
            SetStatus("브라우저 세션으로 HLS 리소스를 모으는 중...");
            localManifestPath = await HlsLocalizer.SaveAsync(manifestText, manifestUrl, tempRoot,
                (url, token) => FetchKeyBytesAsync(url, _currentPageUrl, token), cancellationToken);
            await _ffmpegRunner.DownloadHlsAsync(localManifestPath, outputPath, "", stderrTail, cancellationToken);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception localFailure)
        {
            Log($"로컬 플레이리스트 ffmpeg 실행 실패, 원본 주소로 재시도합니다: {localFailure.Message}");
            SetStatus("원본 m3u8 주소로 ffmpeg 재시도 중...");

            try
            {
                await _ffmpegRunner.DownloadHlsAsync(manifestUrl, outputPath, headerLines, new Queue<string>(), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception remoteFailure)
            {
                throw BuildHlsFailure(primaryFailure, localFailure, remoteFailure);
            }
        }
    }

    private static InvalidOperationException BuildHlsFailure(
        Exception? primaryFailure,
        Exception localFailure,
        Exception remoteFailure)
    {
        var builder = new StringBuilder();
        builder.AppendLine("HLS 다운로드에 실패했습니다. 시도한 방법이 모두 실패했습니다.");

        if (primaryFailure is not null)
        {
            builder.AppendLine().AppendLine("[1] 세그먼트 직접 다운로드/복호화").AppendLine(primaryFailure.Message);
        }

        builder.AppendLine().AppendLine("[2] ffmpeg + 로컬 플레이리스트").AppendLine(localFailure.Message);
        builder.AppendLine().AppendLine("[3] ffmpeg + 원본 m3u8 주소").AppendLine(remoteFailure.Message);

        return new InvalidOperationException(builder.ToString().TrimEnd());
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
            LogManifestDiagnostics(manifestText, candidate.Url);

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

            try
            {
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception segmentFailure)
            {
                Log($"Level5 세그먼트 처리 실패, ffmpeg로 재시도합니다: {segmentFailure.Message}");
                SetStatus("ffmpeg로 다시 시도하는 중...");
                SetProgress(0, indeterminate: true);
                await RunFfmpegOnManifestAsync(
                    manifestText,
                    candidate.Url,
                    outputPath,
                    tempRoot,
                    await BuildFfmpegHeaderLinesAsync(candidate),
                    new Queue<string>(),
                    segmentFailure,
                    cancellationToken);
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task AddCommonRequestHeadersAsync(HttpRequestMessage request, VideoCandidate candidate)
    {
        request.Headers.UserAgent.ParseAdd(webView.CoreWebView2?.Settings.UserAgent ?? BrowserUserAgent);
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
        var bytes = await FetchBytesAsync(url, referer, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// 키 URI는 data: 스킴으로 인라인되는 경우가 있어 HTTP 요청 전에 먼저 처리합니다.
    /// </summary>
    private async Task<byte[]> FetchKeyBytesAsync(string url, string referer, CancellationToken cancellationToken)
    {
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return await FetchBytesAsync(url, referer, cancellationToken);
        }

        var commaIndex = url.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
        {
            throw new InvalidOperationException($"data: 키 URI 형식이 올바르지 않습니다. URL: {url}");
        }

        var metadata = url[..commaIndex];
        var payload = url[(commaIndex + 1)..];

        return metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(payload)
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
    }

    private async Task<byte[]> FetchBytesAsync(string url, string referer, CancellationToken cancellationToken)
    {
        var origin = new Uri(url).GetLeftPart(UriPartial.Authority);
        if (_browserDownloadOrigins.Contains(origin))
            return await FetchBrowserBytesAsync(url, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        await AddRequestHeadersAsync(request, referer, url);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            Log($"외부 요청 HTTP {(int)response.StatusCode}: 브라우저 재생 세션으로 다시 요청합니다. ({new Uri(url).Host})");
            var bytes = await FetchBrowserBytesAsync(url, cancellationToken);
            _browserDownloadOrigins.Add(origin);
            return bytes;
        }
        response.EnsureSuccessStatusCode();
        return await NetworkResponseReader.ReadBytesAsync(response, cancellationToken);
    }

    private async Task AddRequestHeadersAsync(HttpRequestMessage request, string refererUrl, string targetUrl)
    {
        request.Headers.UserAgent.ParseAdd(webView.CoreWebView2?.Settings.UserAgent ?? BrowserUserAgent);
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

        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (nextToWrite < segments.Count)
            {
                batch.Token.ThrowIfCancellationRequested();

                while (nextToStart < segments.Count && inFlight.Count < concurrency)
                {
                    var segmentIndex = nextToStart;
                    inFlight[segmentIndex] = DownloadAndDecryptSegmentAsync(segments[segmentIndex], referer, batch.Token);
                    nextToStart++;
                }

                var segmentBytes = await inFlight[nextToWrite];
                inFlight.Remove(nextToWrite);
                await output.WriteAsync(segmentBytes, batch.Token);

                nextToWrite++;
                var percent = (int)Math.Clamp(nextToWrite * 100D / segments.Count, 0, 100);
                SetProgress(percent, indeterminate: false);
                SetStatus($"세그먼트 다운로드/복호화 중... {nextToWrite}/{segments.Count}");
            }
        }
        finally
        {
            await batch.CancelAsync();
            try { await Task.WhenAll(inFlight.Values); }
            catch { /* Preserve the original segment error after draining requests. */ }
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
        var hint = segment.KeyCandidates.Count == 0
            ? "매니페스트에서 사용할 수 있는 AES-128 키를 찾지 못했습니다(keys=0). "
            : "";

        throw new InvalidOperationException(
            hint +
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

        var extension = candidate.Kind switch
        {
            VideoKind.Hls or VideoKind.Level5Hls => ".mp4",
            // WebM(VP9/Opus)으로 캡처된 경우 mp4로 담으면 -c copy가 실패합니다.
            VideoKind.MediaCapture => candidate.ContentType.Contains("webm", StringComparison.OrdinalIgnoreCase)
                ? ".webm"
                : ".mp4",
            _ => GetDirectDownloadExtension(candidate.Url)
        };
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

            // 키 서버가 막혀 있어도 여기서 죽이지 않습니다. 호출부가 ffmpeg 경로로 넘어갑니다.
            byte[] responseBytes;
            try
            {
                responseBytes = await FetchKeyBytesAsync(keyUrls[index], referer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"HLS 키 요청 실패: {keyUrls[index]} ({ex.Message})");
                continue;
            }

            var keyBytes = HlsManifestService.ParseKeyMaterial(responseBytes);
            if (keyBytes is null)
            {
                Log($"HLS AES-128 키를 해석하지 못했습니다({responseBytes.Length}바이트). URL: {keyUrls[index]}");
                continue;
            }

            keyByUrl[keyUrls[index]] = new[] { keyBytes };
        }

        return keyByUrl;
    }
}


