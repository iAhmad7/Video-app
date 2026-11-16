using System.Security.Cryptography;
using System.Text;

namespace Luma.Core;

public sealed class MediaTools
{
    public string FFmpeg { get; }
    public string FFprobe { get; }
    public string Version { get; private set; } = "Not checked";
    public string ProbeVersion { get; private set; } = "Not checked";
    public string Encoders { get; private set; } = "";
    public bool QsvAvailable { get; private set; }
    public string GpuReport { get; private set; } = "Not tested — CPU is available.";
    readonly ProcessRunner runner = new();
    readonly SemaphoreSlim thumbnails = new(1, 1);
    public MediaTools(string ffmpeg, string ffprobe) { FFmpeg = ffmpeg; FFprobe = ffprobe; }
    public static string Locate(string configured, string name, string? sibling = null)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var exe = name + (OperatingSystem.IsWindows() ? ".exe" : "");
        if (!string.IsNullOrWhiteSpace(sibling))
        {
            var candidate = Path.Combine(Path.GetDirectoryName(sibling) ?? "", exe);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), exe);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new FileNotFoundException($"{exe} was not found in PATH. Select the existing executable in Settings.");
    }
    public async Task VerifyAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var v = await runner.RunAsync(FFmpeg, ["-version"], timeout.Token);
        var p = await runner.RunAsync(FFprobe, ["-version"], timeout.Token);
        if (v.ExitCode != 0 || !v.Output.StartsWith("ffmpeg version") || p.ExitCode != 0 || !p.Output.StartsWith("ffprobe version")) throw new ToolException("The selected files are not working FFmpeg/FFprobe executables.", v.Error + p.Error);
        Version = v.Output.Split('\n')[0].Trim(); ProbeVersion = p.Output.Split('\n')[0].Trim();
        var enc = await runner.RunAsync(FFmpeg, ["-hide_banner", "-encoders"], timeout.Token);
        if (enc.ExitCode != 0) throw new ToolException("Cannot read encoder capabilities.", enc.Error);
        Encoders = enc.Output;
        DiagnosticLog.Write("Tools verified: " + Version + " | " + ProbeVersion);
    }
    public async Task TestGpuAsync(CancellationToken ct = default)
    {
        QsvAvailable = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var r = await runner.RunAsync(FFmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=black:s=320x180:r=25", "-t", "0.4", "-an", "-c:v", "h264_qsv", "-global_quality", "25", "-f", "null", "-"], timeout.Token, lowPriority: true);
            QsvAvailable = r.ExitCode == 0;
            GpuReport = QsvAvailable ? "Intel H.264 Quick Sync passed a real 10-frame encoding test. H.265/AV1 hardware encoding is not used." : "Quick Sync test failed. CPU fallback is active.\n" + r.Error;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { GpuReport = "Quick Sync test timed out after 15 seconds. CPU fallback is active."; }
        catch (ToolException e) { GpuReport = "Quick Sync unavailable. CPU fallback is active.\n" + e; }
        DiagnosticLog.Write(GpuReport);
    }
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var r = await runner.RunAsync(FFprobe, ["-v", "warning", "-show_error", "-show_format", "-show_streams", "-show_chapters", "-of", "json", Path.GetFullPath(path)], timeout.Token);
        if (r.ExitCode != 0) throw new ToolException("Cannot read this media file. Check the path, permissions and file integrity; try Repair if another player can read it.", r.Error + r.Output);
        return MediaInfo.Parse(path, r.Output, r.Error);
    }
    public async Task<string> ValidateAsync(MediaInfo media, bool deep, int threads = 2, CancellationToken ct = default, IProgress<JobProgress>? progress = null)
    {
        var fresh = await ProbeAsync(media.Path, ct);
        var report = new StringBuilder($"{(deep ? "Deep decode validation" : "Quick container / metadata validation")}\n{media.Path}\n\n{fresh.Summary}\nContainer: {fresh.Container}\n");
        foreach (var s in fresh.Streams) report.AppendLine($"Stream #{s.Index}: {s.Type}, {s.Codec}, language: {(s.Language == "" ? "unknown" : s.Language)}");
        report.AppendLine(fresh.Duration <= 0 ? "WARNING: duration is missing. Rebuild the container or try Fix seeking." : "Duration is present. This alone does not prove seekability.");
        report.AppendLine(string.IsNullOrWhiteSpace(fresh.Warnings) ? "FFprobe reported no warnings." : "FFprobe warnings:\n" + fresh.Warnings);
        if (deep)
        {
            var r = await runner.RunAsync(FFmpeg, ["-hide_banner", "-v", "warning", "-nostdin", "-threads", Math.Clamp(threads, 1, 4).ToString(), "-i", media.Path, "-map", "0:v?", "-map", "0:a?", "-sn", "-progress", "pipe:1", "-nostats", "-f", "null", "-"], ct,
                line => ReportProgress(line, fresh.Duration, 0, 1, progress), true);
            report.AppendLine($"\nFull A/V decode exit code: {r.ExitCode}");
            report.AppendLine(string.IsNullOrWhiteSpace(r.Error) ? "No decoder warnings were reported." : "Decoder / timestamp warnings (last 256 KiB):\n" + r.Error);
            report.AppendLine("Repair suggestions: timestamp/index warnings may respond to Fix seeking or Timestamp repair. Decode errors may indicate lost/corrupt media that remuxing cannot restore.");
        }
        report.AppendLine("\nQuick checks do not decode every frame or test every seek point. Deep checks read and decode the full A/V streams, but do not validate subtitle rendering. No repair is guaranteed; originals are always kept.");
        DiagnosticLog.Write(report.ToString()); return report.ToString();
    }
    public async Task<string> ThumbnailAsync(MediaInfo media, string directory, int limitMiB, CancellationToken ct = default)
    {
        await thumbnails.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(media.Path + ":" + media.Size + ":" + File.GetLastWriteTimeUtc(media.Path).Ticks)))[..24];
            var target = Path.Combine(directory, key + ".jpg");
            if (File.Exists(target)) { File.SetLastWriteTimeUtc(target, DateTime.UtcNow); return target; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var temporary = Path.Combine(directory, key + "." + Guid.NewGuid().ToString("N") + ".partial.jpg");
            try
            {
                var r = await runner.RunAsync(FFmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-n", "-ss", Format.Number(Math.Min(5, media.Duration * .1)), "-threads", "1", "-i", media.Path, "-map", $"0:{media.Video?.Index ?? 0}", "-vf", "scale=320:-2", "-frames:v", "1", "-q:v", "5", "-update", "1", temporary], timeout.Token, lowPriority: true);
                if (r.ExitCode != 0 || !File.Exists(temporary)) throw new ToolException("Thumbnail unavailable. The video can still be processed.", r.Error);
                try { File.Move(temporary, target, false); }
                catch (IOException) when (File.Exists(target)) { File.Delete(temporary); }
                TrimCache(directory, limitMiB); return target;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { thumbnails.Release(); }
    }
    public static void TrimCache(string directory, int limitMiB)
    {
        if (!Directory.Exists(directory)) return;
        var files = new DirectoryInfo(directory).GetFiles("*.jpg").Where(f => !f.Name.Contains(".partial.", StringComparison.Ordinal)).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length), limit = Math.Max(0, limitMiB) * 1024L * 1024;
        foreach (var file in files) { if (total <= limit) break; total -= file.Length; file.Delete(); }
    }
    internal static void ReportProgress(string line, double duration, int pass, int passes, IProgress<JobProgress>? progress)
    {
        if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out var us) )
            progress?.Report(new(duration > 0 ? Math.Clamp((pass + us / 1000000.0 / duration) / passes, 0, .995) : 0, $"{(passes == 2 ? $"Pass {pass + 1}/2 · " : "")}{Format.Time(Math.Max(0, us / 1000000.0))} / {(duration > 0 ? Format.Time(duration) : "unknown duration")}"));
    }
}
