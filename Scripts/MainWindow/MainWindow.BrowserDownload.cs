using System.Text.Json;

namespace WebVideoDownloader;

public partial class MainWindow
{
    private readonly HashSet<string> _browserDownloadOrigins = new(StringComparer.OrdinalIgnoreCase);

    // Run in the page's browser session: cookies, TLS and referrer policy match playback.
    private async Task<byte[]> FetchBrowserBytesAsync(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var core = webView.CoreWebView2 ?? throw new InvalidOperationException("브라우저가 준비되지 않았습니다.");
        var id = Guid.NewGuid().ToString("N");
        var expression = $$"""
            (async () => {
                const requests = window.__wvdDownloads ||= {};
                const controller = requests[{{JsonSerializer.Serialize(id)}}] = new AbortController();
                const timeout = setTimeout(() => controller.abort(), 45000);
                try {
                    const response = await fetch({{JsonSerializer.Serialize(url)}}, {
                        credentials: 'same-origin', signal: controller.signal
                    });
                    if (!response.ok) throw new Error('브라우저 요청 HTTP ' + response.status);
                    const buffer = await response.arrayBuffer();
                    if (buffer.byteLength > 64 * 1024 * 1024) throw new Error('세그먼트가 64MB를 초과합니다.');
                    const bytes = new Uint8Array(buffer);
                    const chunks = [];
                    for (let i = 0; i < bytes.length; i += 16384)
                        chunks.push(String.fromCharCode(...bytes.subarray(i, i + 16384)));
                    return { data: btoa(chunks.join('')) };
                } catch (error) {
                    return { error: String(error.message || error) };
                } finally { clearTimeout(timeout); delete requests[{{JsonSerializer.Serialize(id)}}]; }
            })()
            """;
        try
        {
            var json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression, awaitPromise = true, returnByValue = true
            })).WaitAsync(cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("value", out var value))
                throw new InvalidOperationException("브라우저 다운로드 응답을 받지 못했습니다. 페이지가 바뀌었는지 확인하세요.");
            if (value.TryGetProperty("error", out var error))
                throw new InvalidOperationException(error.GetString());
            return Convert.FromBase64String(value.GetProperty("data").GetString()!);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                try { await core.ExecuteScriptAsync($"window.__wvdDownloads?.[{JsonSerializer.Serialize(id)}]?.abort()"); }
                catch { }
            }
        }
    }
}
