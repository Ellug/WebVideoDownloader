using System.Text;
using WebVideoDownloader.Models;

namespace WebVideoDownloader.Services;

internal sealed partial class FfmpegRunner
{
    public async Task SaveCapturePartsAsync(IReadOnlyList<CapturedTrack> parts, string directory,
        string outputPath, CancellationToken cancellationToken)
    {
        var tracks = new List<string>();
        foreach (var group in parts.GroupBy(part => part.TrackId).OrderBy(group => group.Key))
        {
            var ordered = group.OrderBy(part => part.PartIndex).ToList();
            if (ordered.Count == 1)
            {
                tracks.Add(ordered[0].FilePath);
                continue;
            }
            setStatus($"캡처 트랙 {group.Key}: {ordered.Count}개 조각 연결 중...");
            var listPath = Path.Combine(directory, $"track-{group.Key}.ffconcat");
            var list = "ffconcat version 1.0\n" + string.Join("\n", ordered.Select(part =>
                "file '" + part.FilePath.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'"));
            await File.WriteAllTextAsync(listPath, list, new UTF8Encoding(false), cancellationToken);
            var trackPath = Path.Combine(directory, $"track-{group.Key}.mkv");
            using var process = CreateBaseProcess();
            foreach (var arg in new[] { "-f", "concat", "-safe", "0", "-i", listPath, "-map", "0", "-c", "copy", trackPath })
                process.StartInfo.ArgumentList.Add(arg);
            var errors = new Queue<string>();
            process.ErrorDataReceived += (_, e) => AppendStderr(errors, e.Data);
            await RunAsync(process, errors, "캡처 조각 연결 실패", cancellationToken);
            tracks.Add(trackPath);
        }
        await MuxCapturedTracksAsync(tracks, outputPath, cancellationToken);
    }
}
