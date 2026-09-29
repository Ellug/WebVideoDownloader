using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using WebVideoDownloader.Models;

namespace WebVideoDownloader.Services;

internal sealed class FfmpegRunner(string browserUserAgent, Action<string> setStatus, Action<string> log)
{
    private static readonly string? BundledFfmpegPath = ResolveBundledFfmpegPath();

    public async Task TrimAsync(string inputPath, string outputPath, DownloadRange range, CancellationToken cancellationToken)
    {
        using var process = CreateBaseProcess();
        var args = process.StartInfo.ArgumentList;
        args.Add("-ss");
        args.Add(range.Start.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        args.Add("-i");
        args.Add(inputPath);
        if (range.End is { } end)
        {
            args.Add("-t");
            args.Add((end - range.Start).TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        }
        foreach (var arg in new[] { "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-movflags", "+faststart", outputPath })
            args.Add(arg);
        var errors = new Queue<string>();
        long frames = 0;
        process.ErrorDataReceived += (_, e) => AppendStderr(errors, e.Data);
        process.OutputDataReceived += (_, e) =>
        {
            if (IsProgressTime(e.Data, out var time)) setStatus($"지정 구간 저장 중... {time}");
            if (e.Data?.StartsWith("frame=", StringComparison.Ordinal) == true && long.TryParse(e.Data[6..].Trim(), out var count))
                Interlocked.Exchange(ref frames, count);
        };
        await RunAsync(process, errors, "구간 저장 실패", cancellationToken);
        if (Interlocked.Read(ref frames) == 0)
            throw new InvalidOperationException("지정 구간에 영상이 없습니다. 시작 시간이 영상 길이 이내인지 확인하세요.");
    }

    public async Task DownloadHlsAsync(
        string inputUrlOrPath,
        string outputPath,
        string headerLines,
        Queue<string> stderrTail,
        CancellationToken cancellationToken)
    {
        using var process = CreateBaseProcess();
        process.StartInfo.ArgumentList.Add("-user_agent");
        process.StartInfo.ArgumentList.Add(browserUserAgent);
        process.StartInfo.ArgumentList.Add("-allowed_extensions");
        process.StartInfo.ArgumentList.Add("ALL");
        process.StartInfo.ArgumentList.Add("-allowed_segment_extensions");
        process.StartInfo.ArgumentList.Add("ALL");
        process.StartInfo.ArgumentList.Add("-extension_picky");
        process.StartInfo.ArgumentList.Add("0");
        process.StartInfo.ArgumentList.Add("-protocol_whitelist");
        process.StartInfo.ArgumentList.Add("file,http,https,tcp,tls,crypto,data");

        if (headerLines.Length > 0)
        {
            process.StartInfo.ArgumentList.Add("-headers");
            process.StartInfo.ArgumentList.Add(headerLines);
        }

        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(inputUrlOrPath);
        AddCopyMp4OutputArguments(process, outputPath);

        process.OutputDataReceived += (_, e) =>
        {
            if (IsProgressTime(e.Data, out var time))
            {
                setStatus($"HLS 다운로드 중... {time}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            AppendStderr(stderrTail, e.Data);

            if (!string.IsNullOrWhiteSpace(e.Data) &&
                (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("403", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("404", StringComparison.OrdinalIgnoreCase)))
            {
                log($"ffmpeg: {e.Data}");
            }
        };

        await RunAsync(process, stderrTail, "ffmpeg 다운로드 실패", cancellationToken);
    }

    public async Task RemuxTransportStreamAsync(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        var stderrTail = new Queue<string>();

        using var process = CreateBaseProcess();
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("mpegts");
        process.StartInfo.ArgumentList.Add("-analyzeduration");
        process.StartInfo.ArgumentList.Add("100M");
        process.StartInfo.ArgumentList.Add("-probesize");
        process.StartInfo.ArgumentList.Add("100M");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(inputPath);
        AddCopyMp4OutputArguments(process, outputPath);

        process.OutputDataReceived += (_, e) =>
        {
            if (IsProgressTime(e.Data, out var time))
            {
                setStatus($"MP4 변환 중... {time}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            AppendStderr(stderrTail, e.Data);

            if (!string.IsNullOrWhiteSpace(e.Data) &&
                e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                log($"ffmpeg: {e.Data}");
            }
        };

        await RunAsync(process, stderrTail, "ffmpeg MP4 변환 실패", cancellationToken);
    }

    /// <summary>
    /// 재생 캡처로 모은 트랙 파일들을 하나로 합칩니다.
    /// MSE는 영상과 음성을 각각 다른 SourceBuffer로 넣는 경우가 많아 입력이 여러 개일 수 있습니다.
    /// </summary>
    public async Task MuxCapturedTracksAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var stderrTail = new Queue<string>();

        using var process = CreateBaseProcess();
        process.StartInfo.ArgumentList.Add("-analyzeduration");
        process.StartInfo.ArgumentList.Add("100M");
        process.StartInfo.ArgumentList.Add("-probesize");
        process.StartInfo.ArgumentList.Add("100M");

        foreach (var inputPath in inputPaths)
        {
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(inputPath);
        }

        for (var index = 0; index < inputPaths.Count; index++)
        {
            process.StartInfo.ArgumentList.Add("-map");
            process.StartInfo.ArgumentList.Add(index.ToString());
        }

        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("copy");

        // +faststart는 mp4 계열 muxer 전용이라 webm 출력에 붙이면 ffmpeg가 거부합니다.
        if (Path.GetExtension(outputPath) is ".mp4" or ".m4v" or ".mov")
        {
            process.StartInfo.ArgumentList.Add("-movflags");
            process.StartInfo.ArgumentList.Add("+faststart");
        }

        process.StartInfo.ArgumentList.Add(outputPath);

        process.OutputDataReceived += (_, e) =>
        {
            if (IsProgressTime(e.Data, out var time))
            {
                setStatus($"캡처본 병합 중... {time}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            AppendStderr(stderrTail, e.Data);

            if (!string.IsNullOrWhiteSpace(e.Data) &&
                e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                log($"ffmpeg: {e.Data}");
            }
        };

        await RunAsync(process, stderrTail, "캡처본 병합 실패", cancellationToken);
    }

    private static Process CreateBaseProcess()
    {
        var process = new Process();
        process.StartInfo.FileName = BundledFfmpegPath ?? "ffmpeg";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-y");
        process.StartInfo.ArgumentList.Add("-nostdin");
        process.StartInfo.ArgumentList.Add("-progress");
        process.StartInfo.ArgumentList.Add("pipe:1");
        return process;
    }

    private static string? ResolveBundledFfmpegPath()
    {
        var candidates = new List<string>();

        var overridePath = Environment.GetEnvironmentVariable("WVD_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            candidates.Add(overridePath);
        }

        AddCandidateDirectories(candidates, AppContext.BaseDirectory);
        AddCandidateDirectories(candidates, Path.GetDirectoryName(Environment.ProcessPath));

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(directory))
                candidates.Add(Path.Combine(directory.Trim('"'), "ffmpeg.exe"));
        }

        // A .cmd shim on PATH cannot be launched with UseShellExecute=false.
        // Locate the native executable from WinGet without executing a shell shim.
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        try
        {
            if (Directory.Exists(packages))
                foreach (var package in Directory.EnumerateDirectories(packages, "Gyan.FFmpeg*"))
                    candidates.AddRange(Directory.EnumerateFiles(package, "ffmpeg.exe", SearchOption.AllDirectories));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore invalid path candidates.
            }
        }

        return null;
    }

    private static void AddCandidateDirectories(List<string> candidates, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return;
        }

        candidates.Add(Path.Combine(baseDirectory, "ffmpeg.exe"));
        candidates.Add(Path.Combine(baseDirectory, "Tools", "ffmpeg", "win-x64", "ffmpeg.exe"));
    }

    private static void AddCopyMp4OutputArguments(Process process, string outputPath)
    {
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("copy");
        process.StartInfo.ArgumentList.Add("-movflags");
        process.StartInfo.ArgumentList.Add("+faststart");
        process.StartInfo.ArgumentList.Add(outputPath);
    }

    private async Task RunAsync(
        Process process,
        Queue<string> stderrTail,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("ffmpeg 프로세스를 시작하지 못했습니다.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "다운로드와 MP4 변환에는 ffmpeg가 필요합니다. " +
                "PATH에 ffmpeg.exe를 추가하거나 앱과 같은 폴더(또는 Tools/ffmpeg/win-x64)에 ffmpeg.exe를 두고 다시 실행하세요.",
                ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        if (process.ExitCode == 0)
        {
            return;
        }

        string details;
        lock (stderrTail)
        {
            details = string.Join(Environment.NewLine, stderrTail);
        }

        throw new InvalidOperationException($"{failureMessage}. 종료 코드: {process.ExitCode}{Environment.NewLine}{details}");
    }

    private static void AppendStderr(Queue<string> stderrTail, string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        lock (stderrTail)
        {
            stderrTail.Enqueue(data);
            while (stderrTail.Count > 12)
            {
                stderrTail.Dequeue();
            }
        }
    }

    private static bool IsProgressTime(string? data, out string time)
    {
        time = "";
        if (string.IsNullOrWhiteSpace(data) || !data.StartsWith("out_time=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        time = data["out_time=".Length..];
        return true;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
