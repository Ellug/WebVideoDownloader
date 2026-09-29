using System.Text;
using System.Text.RegularExpressions;

namespace WebVideoDownloader.Services;

internal static class HlsLocalizer
{
    // Keep BYTERANGE, IV, MAP and discontinuity tags intact; only replace resource URIs.
    public static async Task<string> SaveAsync(string manifest, string manifestUrl, string directory,
        Func<string, CancellationToken, Task<byte[]>> fetch, CancellationToken cancellationToken)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var output = new StringBuilder();
        async Task<string> LocalPath(string url, bool key)
        {
            if (paths.TryGetValue(url, out var existing)) return existing;
            var bytes = await fetch(url, cancellationToken);
            if (key) bytes = HlsManifestService.ParseKeyMaterial(bytes) ?? bytes;
            var name = $"resource-{paths.Count:D6}.bin";
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, cancellationToken);
            paths.Add(url, name);
            return name;
        }
        foreach (var raw in HlsManifestService.Normalize(manifest, manifestUrl).Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#'))
                line = await LocalPath(line, false);
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var uri = HlsManifestService.ExtractAttribute(line, "URI");
                if (uri.Length > 0)
                {
                    var path = await LocalPath(uri, line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase));
                    line = Regex.Replace(line, "URI=\"[^\"]*\"", _ => $"URI=\"{path}\"", RegexOptions.IgnoreCase);
                }
            }
            output.AppendLine(line);
        }
        var playlist = Path.Combine(directory, "local.m3u8");
        await File.WriteAllTextAsync(playlist, output.ToString(), new UTF8Encoding(false), cancellationToken);
        return playlist;
    }
}
