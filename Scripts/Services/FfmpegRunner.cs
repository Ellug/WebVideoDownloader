using System.ComponentModel;
using System.Diagnostics;

namespace WebVideoDownloader.Services;

internal sealed class FfmpegRunner(string browserUserAgent, Action<string> setStatus, Action<string> log)
{
    private static readonly string? BundledFfmpegPath = ResolveBundledFfmpegPath();

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
