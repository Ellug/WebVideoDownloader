using WebVideoDownloader.Models;
using WebVideoDownloader.Services;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
Check(DownloadRange.Parse("01:30", "120").Start.TotalSeconds == 90, "minutes");
Check(DownloadRange.Parse("00:01:30.5", "").Start.TotalSeconds == 90.5, "fractional seconds");
Check(DownloadRange.Parse("", "").End is null, "open end");
foreach (var bad in new[]{"-1", "NaN", "1:60", "1.2:30", "Infinity", "a"}) {
 try { DownloadRange.Parse(bad, ""); throw new Exception("accepted " + bad); } catch (FormatException) { }
}
try { DownloadRange.Parse("10", "9"); throw new Exception("reversed"); } catch (FormatException) { }
Console.WriteLine("PASS invalid ranges");
Check(MediaClassifier.DetermineConfiguredVideoKind("https://example.com/id/items1.shtml?token=x") == VideoKind.Hls, "configured shtml HLS");
Check(MediaClassifier.DetermineVideoKind("https://example.com/page.shtml", "text/html") == VideoKind.Unknown, "ordinary HTML not HLS");
Check(MediaClassifier.DetermineConfiguredVideoKind("https://example.com/v.mp4") == VideoKind.DirectFile, "configured MP4");
if (args.Length < 2) return;
var runner = new FfmpegRunner("test", _ => {}, Console.WriteLine);
await runner.TrimAsync(args[0], args[1], DownloadRange.Parse("1.25", "3.75"), CancellationToken.None);
Check(File.Exists(args[1]) && new FileInfo(args[1]).Length > 1000, "real clip");
try { await runner.TrimAsync(args[0], args[1]+".mp4", DownloadRange.Parse("50", ""), CancellationToken.None); throw new Exception("accepted empty clip"); }
catch (InvalidOperationException) { Console.WriteLine("PASS past EOF"); }
