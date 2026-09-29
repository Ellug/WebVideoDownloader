using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WebVideoDownloader.Models;

namespace WebVideoDownloader.Services;

/// <summary>
/// 페이지가 <c>SourceBuffer.appendBuffer</c>로 디코더에 밀어 넣는 바이트를 받아 파일로 모으는 로컬 싱크입니다.
/// 브라우저가 이미 복호화를 끝낸 결과이므로 사이트가 어떤 방식으로 세그먼트를 암호화했는지와 무관하게 동작합니다.
/// (EME/Widevine처럼 CDM 안에서 복호화되는 DRM 콘텐츠는 애초에 JS로 평문이 나오지 않으므로 대상이 아닙니다.)
///
/// HTTP.sys URL 예약(관리자 권한)을 피하려고 <see cref="HttpListener"/> 대신 TcpListener로
/// 우리가 직접 보내는 요청만 처리하는 최소 HTTP 서버를 씁니다.
/// </summary>
internal sealed class MediaCaptureSink(Action<string> log) : IDisposable
{
    private const long MaxChunkBytes = 32L * 1024 * 1024;
    private const long MaxSessionBytes = 8L * 1024 * 1024 * 1024;

    private readonly ConcurrentDictionary<int, TrackWriter> _tracks = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _sessionLock = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private string _sessionRoot = "";
    private long _sessionBytes;
    private bool _captureAnnounced;
    private volatile bool _disposed;

    public int Port { get; private set; }

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public bool IsRunning => _listener is not null;

    /// <summary>첫 미디어 바이트가 들어왔을 때 한 번 발생합니다.</summary>
    public event Action? CaptureStarted;

    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listenerCts = new CancellationTokenSource();

        ResetSessionCore();
        _ = AcceptLoopAsync(listener, _listenerCts.Token);
    }

    /// <summary>페이지를 새로 열 때 지금까지 모은 트랙을 버리고 새 세션을 시작합니다.</summary>
    public void ResetSession()
    {
        if (_disposed)
        {
            return;
        }

        // 진행 중인 청크 쓰기가 끝난 뒤에 파일을 정리해야 합니다.
        // 쓰기 경로는 UI를 동기적으로 건드리지 않으므로 UI 스레드에서 기다려도 안전합니다.
        _writeLock.Wait();
        try
        {
            ResetSessionCore();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void ResetSessionCore()
    {
        lock (_sessionLock)
        {
            foreach (var track in _tracks.Values)
            {
                track.Dispose();
            }

            _tracks.Clear();
            _sessionBytes = 0;
            _captureAnnounced = false;

            if (!string.IsNullOrEmpty(_sessionRoot))
            {
                TryDeleteDirectory(_sessionRoot);
            }

            _sessionRoot = Path.Combine(
                Path.GetTempPath(),
                "WebVideoDownloader",
                "capture",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sessionRoot);
        }
    }

    public CaptureStatus GetStatus()
    {
        var parts = _tracks.Values.SelectMany(track => track.SnapshotParts()).ToList();
        var trackCount = parts.Select(part => part.TrackId).Distinct().Count();
        var mimeType = _tracks.Values
            .OrderBy(track => track.TrackId)
            .Select(track => track.MimeType)
            .FirstOrDefault(mime => !string.IsNullOrWhiteSpace(mime)) ?? "";

        return new CaptureStatus(trackCount, parts.Sum(part => part.ByteCount), mimeType);
    }

    /// <summary>
    /// 저장 중 재생이 계속되어도 결과가 변하지 않도록 모든 조각을 별도 파일로 복사합니다.
    /// </summary>
    public async Task<IReadOnlyList<CapturedTrack>> SnapshotAsync(string directory, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var track in _tracks.Values)
            {
                await track.FlushAsync();
            }
            Directory.CreateDirectory(directory);
            var result = new List<CapturedTrack>();
            foreach (var part in _tracks.Values.SelectMany(track => track.SnapshotParts())
                .Where(part => part.ByteCount > 0).OrderBy(part => part.TrackId).ThenBy(part => part.PartIndex))
            {
                var path = Path.Combine(directory, $"part-{part.TrackId}-{part.PartIndex}.bin");
                await using var input = new FileStream(part.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken);
                result.Add(part with { FilePath = path });
            }
            return result;
        }
        finally
        {
            _writeLock.Release();
        }

    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log($"캡처 싱크 accept 실패: {ex.Message}");
                return;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                var request = await ReadRequestAsync(stream, cancellationToken);
                if (request is null)
                {
                    return;
                }

                if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, "204 No Content", cancellationToken);
                    return;
                }

                var query = ParseQuery(request.Target);
                if (!string.Equals(query.GetValueOrDefault("token"), Token, StringComparison.Ordinal))
                {
                    await WriteResponseAsync(stream, "403 Forbidden", cancellationToken);
                    return;
                }

                if (request.Target.StartsWith("/chunk", StringComparison.OrdinalIgnoreCase) && request.Body.Length > 0)
                {
                    await AppendChunkAsync(query, request.Body);
                }

                await WriteResponseAsync(stream, "204 No Content", cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // 페이지가 연결을 끊는 건 정상입니다.
            }
            catch (Exception ex)
            {
                log($"캡처 청크 처리 실패: {ex.Message}");
            }
        }
    }

    private async Task AppendChunkAsync(IReadOnlyDictionary<string, string> query, byte[] body)
    {
        if (_disposed || !int.TryParse(query.GetValueOrDefault("track"), out var trackId))
        {
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            if (_sessionBytes + body.Length > MaxSessionBytes)
            {
                throw new InvalidOperationException("캡처 저장 한도(8GB)를 초과했습니다.");
            }

            string sessionRoot;
            lock (_sessionLock)
            {
                sessionRoot = _sessionRoot;
            }

            if (string.IsNullOrEmpty(sessionRoot))
            {
                return;
            }

            var track = _tracks.GetOrAdd(
                trackId,
                id => new TrackWriter(id, WebUtility.UrlDecode(query.GetValueOrDefault("mime") ?? ""), sessionRoot));

            await track.AppendAsync(body);
            _sessionBytes += body.Length;

            if (!_captureAnnounced)
            {
                _captureAnnounced = true;
                CaptureStarted?.Invoke();
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<CaptureRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headBuffer = new byte[8192];
        var headLength = 0;
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            if (headLength == headBuffer.Length)
            {
                return null;
            }

            var read = await stream.ReadAsync(headBuffer.AsMemory(headLength), cancellationToken);
            if (read <= 0)
            {
                return null;
            }

            headLength += read;
            headerEnd = FindHeaderEnd(headBuffer, headLength);
        }

        var headerText = Encoding.ASCII.GetString(headBuffer, 0, headerEnd);
        var headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (headerLines.Length == 0)
        {
            return null;
        }

        var requestParts = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2)
        {
            return null;
        }

        var contentLength = 0L;
        foreach (var headerLine in headerLines.Skip(1))
        {
            var separatorIndex = headerLine.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex > 0 &&
                headerLine[..separatorIndex].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                _ = long.TryParse(headerLine[(separatorIndex + 1)..].Trim(), out contentLength);
            }
        }

        if (contentLength is < 0 or > MaxChunkBytes)
        {
            return null;
        }

        var body = new byte[contentLength];
        var bodyStart = headerEnd + 4;
        var carried = Math.Min((int)contentLength, headLength - bodyStart);
        if (carried > 0)
        {
            Buffer.BlockCopy(headBuffer, bodyStart, body, 0, carried);
        }

        var received = Math.Max(carried, 0);
        while (received < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(received), cancellationToken);
            if (read <= 0)
            {
                return null;
            }

            received += read;
        }

        return new CaptureRequest(requestParts[0], requestParts[1], body);
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var index = 0; index + 3 < length; index++)
        {
            if (buffer[index] == (byte)'\r' &&
                buffer[index + 1] == (byte)'\n' &&
                buffer[index + 2] == (byte)'\r' &&
                buffer[index + 3] == (byte)'\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, CancellationToken cancellationToken)
    {
        // 페이지 출처가 무엇이든 로컬 싱크로 POST할 수 있어야 하고,
        // Chrome의 Private Network Access 프리플라이트도 통과시켜야 합니다.
        var response =
            $"HTTP/1.1 {status}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Access-Control-Allow-Private-Network: true\r\n" +
            "Access-Control-Max-Age: 86400\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var questionIndex = target.IndexOf('?', StringComparison.Ordinal);
        if (questionIndex < 0)
        {
            return result;
        }

        foreach (var pair in target[(questionIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[parts[0]] = parts.Length == 2 ? parts[1] : "";
        }

        return result;
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // 파일이 잠겨 있으면 임시 폴더에 남겨 둡니다.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _listenerCts?.Cancel();

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // 종료 중 예외는 무시합니다.
        }

        _listener = null;

        foreach (var track in _tracks.Values)
        {
            track.Dispose();
        }

        _tracks.Clear();
        _listenerCts?.Dispose();

        // 아직 진행 중인 청크 핸들러가 _writeLock을 잡을 수 있어 세마포어는 그대로 둡니다.
        // 프로세스 종료 시점이라 회수 이득이 없습니다.

        if (!string.IsNullOrEmpty(_sessionRoot))
        {
            TryDeleteDirectory(_sessionRoot);
        }
    }

    private sealed record CaptureRequest(string Method, string Target, byte[] Body);

    /// <summary>
    /// 한 SourceBuffer가 만들어 내는 바이트 스트림을 파일로 씁니다.
    /// 스트림 도중에 init 세그먼트가 다시 나오면(화질 전환 등) 이어 붙일 수 없으므로 조각을 나눕니다.
    /// </summary>
    private sealed class TrackWriter(int trackId, string mimeType, string sessionRoot) : IDisposable
    {
        private readonly List<CapturedTrack> _completedParts = [];
        private FileStream? _currentStream;
        private string _currentPath = "";
        private long _currentBytes;
        private int _partIndex = -1;
        private bool _currentHasMedia;

        public int TrackId => trackId;

        public string MimeType => mimeType;

        public async Task AppendAsync(byte[] chunk)
        {
            if (_currentStream is null || (_currentBytes > 0 && IsInitializationSegment(chunk)))
            {
                await StartNewPartAsync();
            }

            await _currentStream!.WriteAsync(chunk);
            _currentBytes += chunk.Length;
            _currentHasMedia |= !IsInitializationSegment(chunk) ||
                chunk.AsSpan().IndexOf("mdat"u8) >= 0 || chunk.AsSpan().IndexOf(new byte[] { 0x1F, 0x43, 0xB6, 0x75 }) >= 0;
        }

        public async Task FlushAsync()
        {
            if (_currentStream is not null)
            {
                await _currentStream.FlushAsync();
            }
        }

        public IReadOnlyList<CapturedTrack> SnapshotParts()
        {
            var parts = new List<CapturedTrack>(_completedParts);
            if (_currentStream is not null && _currentBytes > 0 && _currentHasMedia)
            {
                parts.Add(new CapturedTrack(trackId, _partIndex, mimeType, _currentPath, _currentBytes));
            }

            return parts;
        }

        private async Task StartNewPartAsync()
        {
            if (_currentStream is not null)
            {
                await _currentStream.FlushAsync();
                await _currentStream.DisposeAsync();
                if (_currentHasMedia)
                    _completedParts.Add(new CapturedTrack(trackId, _partIndex, mimeType, _currentPath, _currentBytes));
            }

            _partIndex++;
            _currentBytes = 0;
            _currentHasMedia = false;
            _currentPath = Path.Combine(sessionRoot, $"track{trackId}_{_partIndex}.bin");
            // 캡처를 계속하면서 ffmpeg가 같은 파일을 읽어야 하므로 공유를 최대한 열어 둡니다.
            _currentStream = new FileStream(
                _currentPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024,
                useAsync: true);
        }

        /// <summary>fMP4의 ftyp 박스나 WebM의 EBML 헤더로 시작하면 새 스트림의 시작입니다.</summary>
        private static bool IsInitializationSegment(byte[] chunk)
        {
            if (chunk.Length >= 8 &&
                chunk[4] == (byte)'f' && chunk[5] == (byte)'t' && chunk[6] == (byte)'y' && chunk[7] == (byte)'p')
            {
                return true;
            }

            return chunk.Length >= 4 &&
                chunk[0] == 0x1A && chunk[1] == 0x45 && chunk[2] == 0xDF && chunk[3] == 0xA3;
        }

        public void Dispose()
        {
            try
            {
                _currentStream?.Dispose();
            }
            catch
            {
                // 종료 중 예외는 무시합니다.
            }

            _currentStream = null;
        }
    }
}
