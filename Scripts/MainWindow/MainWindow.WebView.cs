#pragma warning disable CS8600

using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using WebVideoDownloader.Models;
using WebVideoDownloader.Services;

namespace WebVideoDownloader;

public partial class MainWindow
{
    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            SetStatus($"페이지 로드 실패: {e.WebErrorStatus}");
            Log($"페이지 로드 실패: {e.WebErrorStatus}");
        }
        else
        {
            _currentPageUrl = webView.Source?.AbsoluteUri ?? _currentPageUrl;
            SetStatus("페이지 로드 완료. 플레이어를 재생하면 추가 영상 요청도 감지됩니다.");
            Log("페이지 로드 완료: " + _currentPageUrl);
            await Task.Delay(1200);
            await ScanPageForVideoUrlsAsync();
            StartShortScanLoop();
        }
    }

    private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
    {
        string text = webView.CoreWebView2?.DocumentTitle;
        Text = (string.IsNullOrWhiteSpace(text) ? "Web Video Downloader" : (text + " - Web Video Downloader"));
    }

    private async Task InitializeDevToolsNetworkCaptureAsync()
    {
        if (webView.CoreWebView2 != null)
        {
            _requestWillBeSentReceiver = webView.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
            _responseReceivedReceiver = webView.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.responseReceived");
            _loadingFinishedReceiver = webView.CoreWebView2.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
            _requestWillBeSentReceiver.DevToolsProtocolEventReceived += DevToolsNetwork_RequestWillBeSent;
            _responseReceivedReceiver.DevToolsProtocolEventReceived += DevToolsNetwork_ResponseReceived;
            _loadingFinishedReceiver.DevToolsProtocolEventReceived += DevToolsNetwork_LoadingFinished;
            await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{\"maxTotalBufferSize\":100000000,\"maxResourceBufferSize\":10000000}");
            Log("CDP 네트워크 감지 활성화");
        }
    }

    private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            AddExtractedCandidateUrls(e.Request.Uri, "요청URL");
            if (MediaClassifier.DetermineVideoKind(e.Request.Uri, "") != VideoKind.Unknown)
            {
                AddCandidate(e.Request.Uri, "요청", "");
            }
        }
        catch (Exception ex)
        {
            Log("요청 분석 오류: " + ex.Message);
        }
    }

    private void CoreWebView2_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            string uri = e.Request.Uri;
            string contentType = TryGetHeader(e.Response.Headers, "Content-Type");
            AddExtractedCandidateUrls(uri, "응답URL");
            if (MediaClassifier.DetermineVideoKind(uri, contentType) != VideoKind.Unknown)
            {
                AddCandidate(uri, "네트워크", contentType);
            }
        }
        catch (Exception ex)
        {
            Log("네트워크 응답 분석 오류: " + ex.Message);
        }
    }

    private void DevToolsNetwork_RequestWillBeSent(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(e.ParameterObjectAsJson);
            JsonElement rootElement = jsonDocument.RootElement;
            string jsonString = GetJsonString(rootElement, "requestId");
            string jsonString2 = GetJsonString(rootElement, "type");
            JsonElement value;
            JsonElement element = (rootElement.TryGetProperty("request", out value) ? value : default(JsonElement));
            string text = ((element.ValueKind == JsonValueKind.Object) ? GetJsonString(element, "url") : "");
            if (!string.IsNullOrWhiteSpace(jsonString) && !string.IsNullOrWhiteSpace(text))
            {
                _networkRequests[jsonString] = new NetworkRequestInfo(text, jsonString2);
            }
            if (MediaClassifier.IsLikelySegmentUrl(text, ""))
            {
                MarkPlaybackUrl(text, "세그먼트 요청");
            }
            AddExtractedCandidateUrls(text, "CDP 요청");
            if (MediaClassifier.DetermineVideoKind(text, "") != VideoKind.Unknown)
            {
                AddCandidate(text, "CDP 요청", "");
            }
        }
        catch (Exception ex)
        {
            Log("CDP 요청 분석 오류: " + ex.Message);
        }
    }

    private void DevToolsNetwork_ResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(e.ParameterObjectAsJson);
            JsonElement rootElement = jsonDocument.RootElement;
            string jsonString = GetJsonString(rootElement, "requestId");
            string resourceType = GetJsonString(rootElement, "type");
            JsonElement value;
            JsonElement jsonElement = (rootElement.TryGetProperty("response", out value) ? value : default(JsonElement));
            if (jsonElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            string url = GetJsonString(jsonElement, "url");
            string jsonString2 = GetJsonString(jsonElement, "mimeType");
            string text = GetHeaderFromDevToolsResponse(jsonElement, "content-type");
            if (string.IsNullOrWhiteSpace(text))
            {
                text = jsonString2;
            }
            string headerFromDevToolsResponse = GetHeaderFromDevToolsResponse(jsonElement, "content-length");
            long result;
            long? contentLength = (long.TryParse(headerFromDevToolsResponse, out result) ? new long?(result) : ((long?)null));
            if (!string.IsNullOrWhiteSpace(jsonString))
            {
                NetworkRequestInfo orAdd = _networkRequests.GetOrAdd(jsonString, (string _) => new NetworkRequestInfo(url, resourceType));
                orAdd.Url = (string.IsNullOrWhiteSpace(url) ? orAdd.Url : url);
                orAdd.ResourceType = (string.IsNullOrWhiteSpace(resourceType) ? orAdd.ResourceType : resourceType);
                orAdd.MimeType = text;
                orAdd.ContentLength = contentLength;
            }
            if (MediaClassifier.IsLikelySegmentUrl(url, text))
            {
                MarkPlaybackUrl(url, "세그먼트 응답");
            }
            AddExtractedCandidateUrls(url, "CDP 응답");
            VideoKind videoKind = MediaClassifier.DetermineVideoKind(url, text);
            if (videoKind != VideoKind.Unknown)
            {
                AddCandidate(url, "CDP 응답", text, videoKind);
            }
            else if (MediaClassifier.IsLikelyDirectMediaResponse(url, resourceType, text))
            {
                AddCandidate(url, "CDP Media", text, VideoKind.DirectFile);
            }
        }
        catch (Exception ex)
        {
            Log("CDP 응답 분석 오류: " + ex.Message);
        }
    }

    private void DevToolsNetwork_LoadingFinished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(e.ParameterObjectAsJson);
            string jsonString = GetJsonString(jsonDocument.RootElement, "requestId");
            if (!string.IsNullOrWhiteSpace(jsonString) && _networkRequests.TryGetValue(jsonString, out NetworkRequestInfo value) && !value.BodyProbeStarted && MediaClassifier.ShouldInspectResponseBody(value))
            {
                value.BodyProbeStarted = true;
                _ = InspectResponseBodyAsync(jsonString, value);
            }
        }
        catch (Exception ex)
        {
            Log("CDP 로딩 완료 분석 오류: " + ex.Message);
        }
    }

    private async Task InspectResponseBodyAsync(string requestId, NetworkRequestInfo requestInfo)
    {
        if (webView.CoreWebView2 == null || Interlocked.Increment(ref _responseBodyProbeCount) > 120)
        {
            return;
        }
        try
        {
            string parameterJson = JsonSerializer.Serialize(new { requestId });
            using JsonDocument document = JsonDocument.Parse(await webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.getResponseBody", parameterJson));
            JsonElement root = document.RootElement;
            string body = GetJsonString(root, "body");
            JsonElement encodedElement;
            bool base64Encoded = root.TryGetProperty("base64Encoded", out encodedElement) && encodedElement.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(body))
            {
                return;
            }
            if (base64Encoded)
            {
                byte[] bytes = Convert.FromBase64String(body);
                body = Encoding.UTF8.GetString(bytes);
            }
            if (HlsManifestService.LooksLikeManifest(body))
            {
                AddCandidate(requestInfo.Url, "응답바디 HLS", string.IsNullOrWhiteSpace(requestInfo.MimeType) ? "application/vnd.apple.mpegurl" : requestInfo.MimeType, VideoKind.Hls, null, null, null, null, body);
                foreach (string playlistUrl in HlsManifestService.ExtractPlaylistUrls(body, requestInfo.Url))
                {
                    AddCandidate(playlistUrl, "HLS 목록", "application/vnd.apple.mpegurl", VideoKind.Hls);
                }
            }
            AddExtractedCandidateUrls(body, "응답바디");
        }
        catch
        {
        }
    }

    private async void ScanTimer_Tick(object? sender, EventArgs e)
    {
        _scanTimerTicks++;
        ExpirePlaybackSignals();
        if (_scanTimerTicks > 120)
        {
            _scanTimer.Stop();
        }
        else
        {
            await ScanPageForVideoUrlsAsync();
        }
    }

    private void NavigateToUrl(string rawUrl)
    {
        if (!_webViewReady || webView.CoreWebView2 == null)
        {
            SetStatus("WebView2가 아직 준비되지 않았습니다.");
            return;
        }
        if (!UrlTools.TryNormalizePageUrl(rawUrl, out string normalizedUrl))
        {
            MessageBox.Show(this, "올바른 웹 URL을 입력하세요.", "URL 오류", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            return;
        }
        _currentPageUrl = normalizedUrl;
        urlTextBox.Text = normalizedUrl;
        ClearCandidates();
        SetProgress(0, indeterminate: false);
        SetStatus("페이지 로드 중...");
        Log("페이지 열기: " + normalizedUrl);
        webView.CoreWebView2.Navigate(normalizedUrl);
    }

    private void ClearCandidates()
    {
        _candidateUrls.Clear();
        _candidates.Clear();
        _playerUrls.Clear();
        _blobUrls.Clear();
        _networkRequests.Clear();
        _responseBodyProbeCount = 0;
        candidatesListView.Items.Clear();
        downloadButton.Enabled = false;
    }

    private async Task ScanPageForVideoUrlsAsync()
    {
        if (!_webViewReady || webView.CoreWebView2 == null || _scanInProgress)
        {
            return;
        }
        _scanInProgress = true;
        try
        {
            foreach (string candidateUrl in DeserializeStringArray(await webView.CoreWebView2.ExecuteScriptAsync(VideoProbeScripts.VideoProbe)))
            {
                if (candidateUrl.StartsWith("wvd-playing:", StringComparison.OrdinalIgnoreCase))
                {
                    string text = candidateUrl;
                    int length = "wvd-playing:".Length;
                    string playingUrl = text.Substring(length, text.Length - length);
                    MarkPlaybackUrl(playingUrl, "비디오 태그");
                }
                else if (candidateUrl.StartsWith("wvd-hls:", StringComparison.OrdinalIgnoreCase))
                {
                    string text = candidateUrl;
                    int length = "wvd-hls:".Length;
                    string hlsUrl = text.Substring(length, text.Length - length);
                    AddCandidate(hlsUrl, "JS HLS", "application/vnd.apple.mpegurl", VideoKind.Hls);
                }
                else if (candidateUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                {
                    if (_blobUrls.Add(candidateUrl))
                    {
                        Log("blob 영상 주소 감지. 실제 원본은 네트워크 요청과 플레이어 스크립트에서 계속 추적합니다.");
                    }
                }
                else if (IsPlayerUrl(candidateUrl))
                {
                    await AddLevel5CandidatesFromPlayerAsync(candidateUrl);
                }
                else
                {
                    AddCandidate(candidateUrl, "태그", "");
                }
            }
            string html = DeserializeJsonString(await webView.CoreWebView2.ExecuteScriptAsync("document.documentElement ? document.documentElement.outerHTML : ''"));
            foreach (string candidateUrl2 in _mediaUrlExtractor.ExtractMediaUrls(html))
            {
                AddCandidate(candidateUrl2, "HTML", "");
            }
            foreach (string playerUrl in _mediaUrlExtractor.ExtractPlayerUrls(html))
            {
                await AddLevel5CandidatesFromPlayerAsync(playerUrl);
            }
        }
        catch (Exception ex)
        {
            Log("페이지 탐색 오류: " + ex.Message);
        }
        finally
        {
            _scanInProgress = false;
        }
    }

    private void StartShortScanLoop()
    {
        _scanTimerTicks = 0;
        _scanTimer.Stop();
        _scanTimer.Start();
    }
}





