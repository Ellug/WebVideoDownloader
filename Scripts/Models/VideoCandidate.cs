namespace WebVideoDownloader.Models;

internal sealed record VideoCandidate(
    string Url,
    VideoKind Kind,
    string Source,
    string ContentType,
    string Referer,
    DateTime DetectedAt,
    string? WasmJsUrl = null,
    string? WasmBinUrl = null,
    string? ExpectedWasmSha384Hex = null,
    string? CapturedManifestText = null,
    DateTime? LastPlaybackSignalAt = null,
    string? PlaybackSignalSource = null,
    string? CaptureNote = null)
{
    public string KindLabel => Kind switch
    {
        VideoKind.Hls => "HLS",
        VideoKind.Level5Hls => "Level5 HLS",
        VideoKind.MediaCapture => "재생 캡처",
        _ => "파일"
    };
}

/// <summary>SourceBuffer 하나가 만들어 낸 연속 구간 파일입니다.</summary>
internal sealed record CapturedTrack(int TrackId, int PartIndex, string MimeType, string FilePath, long ByteCount);

internal sealed record CaptureStatus(int TrackCount, long ByteCount, string MimeType);

internal sealed record CandidateDisplayInfo(
    int Priority,
    int? Height,
    string Recommendation,
    string QualityLabel,
    string HostLabel,
    string ShortUrl);

internal sealed record HlsVariantPlaylist(string Url, int? Height, long? Bandwidth, string Label);

internal sealed class NetworkRequestInfo(string url, string resourceType)
{
    public string Url { get; set; } = url;
    public string ResourceType { get; set; } = resourceType;
    public string MimeType { get; set; } = "";
    public bool BodyProbeStarted { get; set; }
    public long? ContentLength { get; set; }
}

internal sealed record HlsSegment(
    int Index,
    long SequenceNumber,
    string Url,
    IReadOnlyList<byte[]> KeyCandidates,
    byte[]? ManifestIv);

internal sealed record TransportStreamCandidate(byte[] Bytes, int Offset, int SyncCount, string Strategy);

internal enum VideoKind
{
    Unknown,
    Hls,
    Level5Hls,
    DirectFile,
    MediaCapture
}
