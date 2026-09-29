using System.Globalization;

namespace WebVideoDownloader.Models;

internal sealed record DownloadRange(TimeSpan Start, TimeSpan? End)
{
    public static DownloadRange Parse(string start, string end)
    {
        var startTime = string.IsNullOrWhiteSpace(start) ? TimeSpan.Zero : ParseTime(start);
        TimeSpan? endTime = string.IsNullOrWhiteSpace(end) ? null : ParseTime(end);
        if (endTime.HasValue && endTime.Value <= startTime)
            throw new FormatException("종료 시간은 시작 시간보다 커야 합니다.");
        return new DownloadRange(startTime, endTime);
    }

    private static TimeSpan ParseTime(string value)
    {
        var parts = value.Trim().Split(':');
        if (parts.Length is < 1 or > 3)
            throw new FormatException("시간은 초, 분:초 또는 시:분:초 형식으로 입력하세요.");
        double seconds = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var part) ||
                !double.IsFinite(part) || part < 0 || (i > 0 && part >= 60) ||
                (i < parts.Length - 1 && part != Math.Floor(part)))
                throw new FormatException("올바른 시간을 입력하세요. 예: 00:01:30 또는 90");
            seconds = seconds * 60 + part;
        }
        if (seconds >= TimeSpan.MaxValue.TotalSeconds)
            throw new FormatException("입력한 시간이 너무 큽니다.");
        return TimeSpan.FromSeconds(seconds);
    }
}
