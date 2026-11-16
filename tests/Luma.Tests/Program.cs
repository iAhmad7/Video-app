using System.Security.Cryptography;
using Luma.Core;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "luma-integration-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
DiagnosticLog.DirectoryPath = Path.Combine(root, "logs");
var runner = new ProcessRunner();
var tools = new MediaTools(MediaTools.Locate("", "ffmpeg"), MediaTools.Locate("", "ffprobe"));
var engine = new JobEngine(tools);
var results = new List<string>();
var failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body(); results.Add("PASS " + name); Console.WriteLine(results[^1]); }
    catch (Exception e) { failed++; results.Add("FAIL " + name + "\n" + e); Console.WriteLine(results[^1]); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task FF(params string[] ffargs)
{
    var r = await runner.RunAsync(tools.FFmpeg, new[] { "-hide_banner", "-v", "error", "-nostdin", "-n", "-threads", "2", "-filter_threads", "1" }.Concat(ffargs));
    if (r.ExitCode != 0) throw new Exception(r.Error);
}
async Task Decode(string path)
{
    var r = await runner.RunAsync(tools.FFmpeg, ["-v", "error", "-i", path, "-map", "0:v?", "-map", "0:a?", "-f", "null", "-"]);
    Check(r.ExitCode == 0 && string.IsNullOrWhiteSpace(r.Error), "Output must fully decode without errors: " + r.Error);
}
async Task<string[]> FrameHashes(string path)
{
    var r = await runner.RunAsync(tools.FFmpeg, ["-v", "error", "-i", path, "-map", "0:v:0", "-an", "-f", "framemd5", "-"]);
    Check(r.ExitCode == 0, r.Error);
    return r.Output.Split('\n').Where(l => !l.StartsWith('#') && l.Contains(',')).Select(l => l.Split(',')[^1].Trim()).ToArray();
}
async Task Reject(Func<Task> action)
{
    try { await action(); } catch (Exception e) when (e is InvalidOperationException or ToolException or IOException) { return; }
    throw new Exception("Expected the operation to be rejected.");
}
var source = Path.Combine(root, "original spaced ' [video] & $(literal).mp4");
await tools.VerifyAsync();
Console.WriteLine(tools.Version);
Console.WriteLine("Fixtures and outputs: " + root);
await FF("-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-threads", "2", "-c:a", "aac", "-b:a", "128k", "-metadata", "title=Luma integration fixture", "-metadata:s:a:0", "language=eng", source);
var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
var info = await tools.ProbeAsync(source);
JobOptions Options(Operation op, string suffix) => new() { Operation = op, Container = "mp4", PreferCopy = false, Acceleration = Acceleration.CPU, Suffix = suffix };
await Test("FFprobe metadata parsing from actual media + missing metadata", async () =>
{
    Check(info.Video is { Width: 640, Height: 360, Codec: "h264", Fps: "25" }, "Video properties");
    Check(info.Duration is > 11.9 and < 12.2, "Duration"); Check(info.Streams.Any(s => s.Type == "audio" && s.SampleRate == 48000 && s.Language == "eng"), "Audio metadata");
    Check(info.Json.Contains("Luma integration fixture"), "Raw metadata retained");
    var missing = MediaInfo.Parse("missing.mkv", "{\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"h264\"}],\"format\":{}}");
    Check(missing.Duration == 0 && missing.Streams[0].Language == "" && missing.Video!.Fps == "unknown", "Missing values must remain unknown"); await Task.CompletedTask;
});
await Test("Conversion: H.264 MP4 -> MPEG-4 AVI, resize + FPS + full decode", async () =>
{
    var output = await engine.ExecuteAsync(info, Options(Operation.Convert, "convert") with { Container = "avi", Codec = "mpeg4", Height = 240, Fps = 20 });
    var m = await tools.ProbeAsync(output); Check(m.Video is { Codec: "mpeg4", Height: 240, Fps: "20" }, "Converted properties"); await Decode(output);
});
await Test("Container-only converter automatically copies compatible streams", async () =>
{
    var o = Options(Operation.Convert, "copy") with { Container = "mkv", PreferCopy = true };
    Check(Planner.Create(info, o).Explanation.StartsWith("Stream copy"), "Expected stream-copy plan");
    var output = await engine.ExecuteAsync(info, o); Check((await FrameHashes(source)).AsEnumerable().SequenceEqual(await FrameHashes(output)), "Decoded video hashes must be identical");
});
await Test("Quality compression: smaller output + preserved duration + full decode", async () =>
{
    var output = await engine.ExecuteAsync(info, Options(Operation.Compress, "quality") with { Preset = "Small File", Height = 240 });
    var m = await tools.ProbeAsync(output); Check(m.Size < info.Size && Math.Abs(m.Duration - info.Duration) < .2, "Expected smaller file with full duration"); await Decode(output);
});
await Test("Percentage reduction: 50% calculated size, real two-pass encode, within 15% of target", async () =>
{
    var o = Options(Operation.Compress, "reduce50") with { Compression = CompressionMode.Reduction, Reduction = 50 };
    var target = info.Size / 2; Check(Math.Abs(Planner.Target(info, o)!.Value - target) <= 1, "Reduction means intended bytes, not quality");
    var output = await engine.ExecuteAsync(info, o); var size = new FileInfo(output).Length;
    Console.WriteLine($"  Original {info.Size}, target {target}, actual {size}, reduction {100 * (1 - size / (double)info.Size):0.0}%");
    Check(Math.Abs(size - target) / (double)target < .15, "Target mismatch beyond fixture tolerance"); await Decode(output);
});
await Test("Target-size compression: real two-pass encode, within 15% of requested bytes", async () =>
{
    var target = (long)(info.Size * .65);
    var output = await engine.ExecuteAsync(info, Options(Operation.Compress, "target") with { Compression = CompressionMode.TargetSize, TargetBytes = target });
    var size = new FileInfo(output).Length; Console.WriteLine($"  Target {target}, actual {size}");
    Check(Math.Abs(size - target) / (double)target < .15, "Size mismatch"); await Decode(output);
});
await Test("Remux MP4 -> MKV preserves every decoded video frame", async () =>
{
    var output = await engine.ExecuteAsync(info, Options(Operation.Remux, "remux") with { Container = "mkv" });
    var m = await tools.ProbeAsync(output); Check(m.Video?.Codec == "h264" && m.Streams.Count == info.Streams.Count, "Streams preserved");
    Check((await FrameHashes(source)).AsEnumerable().SequenceEqual(await FrameHashes(output)), "Remux changed video frames"); await Decode(output);
});
await Test("MP4 -> TS -> repaired MKV remains decodable and seekable near end", async () =>
{
    var ts = await engine.ExecuteAsync(info, Options(Operation.Remux, "ts") with { Container = "ts" });
    var tsi = await tools.ProbeAsync(ts);
    foreach (var mode in new[] { "Fix seeking", "Rebuild container", "Timestamp repair" })
    {
        var repaired = await engine.ExecuteAsync(tsi, Options(Operation.Repair, mode) with { Container = "mkv", RepairMode = mode });
        var m = await tools.ProbeAsync(repaired); Check(m.Duration > 11, "Repair duration"); await Decode(repaired);
        var seek = await runner.RunAsync(tools.FFmpeg, ["-v", "error", "-ss", "10", "-i", repaired, "-frames:v", "1", "-f", "framemd5", "-"]);
        Check(seek.ExitCode == 0 && seek.Output.Split('\n').Any(l => !l.StartsWith('#') && l.Contains(',')), "Seeking near end should produce a frame");
    }
});
await Test("Fast lossless cut preserves codec, produces playable bounded clip", async () =>
{
    var output = await engine.ExecuteAsync(info, Options(Operation.Cut, "fastcut") with { Start = 2, End = 6 });
    var m = await tools.ProbeAsync(output); Check(m.Video?.Codec == "h264" && m.Duration is > 3.8 and < 10.2, "Fast-cut keyframe duration bounds"); await Decode(output);
});
await Test("Precise cut verifies duration and first frame matches requested source boundary", async () =>
{
    var output = await engine.ExecuteAsync(info, Options(Operation.Cut, "precisecut") with { Start = 2, End = 6, Precise = true, Preset = "Custom", Crf = 0 });
    var m = await tools.ProbeAsync(output); Check(Math.Abs(m.Duration - 4) < .15, "Precise duration"); await Decode(output);
    var hashes = await FrameHashes(output); var originalFrames = await FrameHashes(source);
    Check(hashes.Length == 100 && hashes[0] == originalFrames[50], "Precise cut should begin at frame 50 and include 100 frames (lossless re-encode fixture)");
});
await Test("Lazy thumbnail extraction and cache hit without re-encode", async () =>
{
    var cache = Path.Combine(root, "cache"); var thumb = await tools.ThumbnailAsync(info, cache, 8);
    Check(File.Exists(thumb) && new FileInfo(thumb).Length > 500, "Thumbnail should exist");
    var hash = SHA256.HashData(await File.ReadAllBytesAsync(thumb)); var again = await tools.ThumbnailAsync(info, cache, 8);
    Check(again == thumb && hash.AsEnumerable().SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(again))), "Cached thumbnail should match");
});
await Test("PNG frame extraction produces actual image", async () =>
{
    var output = await engine.ExecuteAsync(info, Options(Operation.ExtractFrame, "frame") with { Start = 3 });
    Check((await tools.ProbeAsync(output)).Video?.Codec == "png", "PNG codec");
});
await Test("Audio extraction: MP3, AAC/M4A, PCM/WAV and FLAC fully decode", async () =>
{
    foreach (var format in new[] { "mp3", "m4a", "wav", "flac" })
    {
        var output = await engine.ExecuteAsync(info, Options(Operation.ExtractAudio, "audio") with { AudioFormat = format });
        var m = await tools.ProbeAsync(output); Check(m.Video == null && m.Streams.Any(s => s.Type == "audio"), "Audio-only output"); await Decode(output);
    }
});
await Test("Rotation, horizontal/vertical flips, crop and resize perform real encoding", async () =>
{
    foreach (var transform in new[] { "Rotate 90°", "Rotate 180°", "Rotate 270°", "Flip horizontal", "Flip vertical" })
    {
        var output = await engine.ExecuteAsync(info, Options(Operation.Transform, transform) with { Transform = transform, Crop = "320:240:0:0", Height = 240, RemoveAudio = true });
        var m = await tools.ProbeAsync(output); Check(m.Video?.Height == 240 && !m.Streams.Any(s => s.Type == "audio"), "Transform / remove audio"); await Decode(output);
    }
});
await Test("WebM / VP9 and H.265 conversion work when available", async () =>
{
    foreach (var pair in new[] { ("webm", "vp9"), ("mkv", "hevc") })
    {
        var output = await engine.ExecuteAsync(info, Options(Operation.Cut, pair.Item2) with { Precise = true, Start = 0, End = 1, Container = pair.Item1, Codec = pair.Item2, Height = 240 });
        Check((await tools.ProbeAsync(output)).Video?.Codec == pair.Item2, "Codec mismatch"); await Decode(output);
    }
});
var multi = Path.Combine(root, "multitrack.mkv");
var srt = Path.Combine(root, "captions.srt");
await File.WriteAllTextAsync(srt, "1\n00:00:00,000 --> 00:00:02,000\nLuma subtitle fixture\n");
await FF("-i", source, "-i", srt, "-map", "0:v", "-map", "0:a", "-map", "0:a", "-map", "1:0", "-c", "copy", "-metadata:s:a:1", "language=ara", "-metadata:s:s:0", "language=eng", multi);
var mi = await tools.ProbeAsync(multi);
await Test("Remux preserves multiple audio and subtitle tracks; track selection uses global IDs", async () =>
{
    var output = await engine.ExecuteAsync(mi, Options(Operation.Remux, "tracks") with { Container = "mkv" });
    var m = await tools.ProbeAsync(output); Check(m.Streams.Count == 4 && m.Streams.Any(s => s.Type == "subtitle") && m.Streams.Any(s => s.Language == "ara"), "All tracks/language preserved");
    output = await engine.ExecuteAsync(mi, Options(Operation.Remux, "one-track") with { Container = "mkv", AudioTracks = [2], RemoveSubtitles = true });
    m = await tools.ProbeAsync(output); Check(m.Streams.Count == 2 && m.Streams.Single(s => s.Type == "audio").Language == "ara", "Correct selected track");
});
await Test("Incompatible remux rejects, converter converts text subtitles for MP4", async () =>
{
    await Reject(() => engine.ExecuteAsync(mi, Options(Operation.Remux, "reject-sub") with { Container = "mp4" }));
    var output = await engine.ExecuteAsync(mi, Options(Operation.Convert, "subtitle-convert") with { Container = "mp4", Height = 240 });
    Check((await tools.ProbeAsync(output)).Streams.Any(s => s.Type == "subtitle" && s.Codec == "mov_text"), "Subtitles converted for MP4");
});
await Test("Unsupported codec/container and impossible targets produce readable errors", async () =>
{
    await Reject(() => engine.ExecuteAsync(info, Options(Operation.Convert, "badwebm") with { Container = "webm", Codec = "h264" }));
    await Reject(() => engine.ExecuteAsync(info, Options(Operation.Compress, "tiny") with { Compression = CompressionMode.TargetSize, TargetBytes = 100 }));
    await Reject(() => engine.ExecuteAsync(info, Options(Operation.Cut, "badcut") with { Start = 8, End = 3 }));
    await Reject(() => engine.ExecuteAsync(info, Options(Operation.Remux, "badtracks") with { AudioTracks = [777] }));
});
await Test("Output filename collision, suffix sanitization and original-file safety", async () =>
{
    var o = Options(Operation.Remux, "safe:/\\<>name") with { Container = "mkv" };
    var a = await engine.ExecuteAsync(info, o); var ah = SHA256.HashData(await File.ReadAllBytesAsync(a));
    var b = await engine.ExecuteAsync(info, o);
    Check(a != b && File.Exists(a) && File.Exists(b) && ah.AsEnumerable().SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(a))), "Existing output overwritten");
    Check(!Path.GetFileName(a).Contains('<') && Path.GetDirectoryName(a) == root, "Invalid suffix escaped the destination");
    Check(originalHash.AsEnumerable().SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(source))), "Original file changed");
});
await Test("Cancellation terminates an active owned encoding process and removes partial output", async () =>
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var observed = false;
    var progress = new InlineProgress(p => { if (p.Fraction > 0) { observed = true; cts.Cancel(); } });
    try { await engine.ExecuteAsync(info, Options(Operation.Convert, "cancelled") with { Preset = "Custom", Speed = "slow", Height = 1080 }, progress, cts.Token); throw new Exception("Cancelled encode should not complete."); }
    catch (OperationCanceledException) { }
    Check(observed, "Must cancel after real FFmpeg progress, not before launch");
    Check(!Directory.EnumerateFiles(root, "*cancelled*").Any() && !Directory.EnumerateFiles(root, ".luma-*").Any(), "Cancelled job left output/temporary files");
    var r = await runner.RunAsync(tools.FFprobe, ["-version"]); Check(r.ExitCode == 0, "Other processes must remain usable");
});
await Test("Probe and encoder failures include technical details, clean partials, leave originals", async () =>
{
    var bad = Path.Combine(root, "broken.mp4"); await File.WriteAllTextAsync(bad, "not video");
    await Reject(() => tools.ProbeAsync(bad));
    var brokenTools = new MediaTools(tools.FFprobe, tools.FFprobe);
    await Reject(() => new JobEngine(brokenTools).ExecuteAsync(info, Options(Operation.Convert, "failure")));
    Check(!Directory.EnumerateFiles(root, ".luma-*").Any(), "Failed job left partials");
    Check(originalHash.AsEnumerable().SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(source))), "Original changed after failure");
});
await Test("Queue continues after a failed job, cancellation marks waiting jobs", async () =>
{
    await using var q = new JobQueue(tools);
    var bad = q.Enqueue(info, Options(Operation.Cut, "queue-failure") with { Start = 9, End = 1 });
    var cancelled = q.Enqueue(info, Options(Operation.Convert, "queue-cancel")); q.Cancel(cancelled);
    var good = q.Enqueue(info, Options(Operation.Remux, "queue-success") with { Container = "mkv" });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    while (q.HasWork) await Task.Delay(30, timeout.Token);
    Check(bad.State == JobState.Failed && cancelled.State == JobState.Cancelled && good.State == JobState.Completed, "Queue did not isolate failures / cancellation");
});
await Test("Quick + full decode validation report real streams and diagnostics", async () =>
{
    var quick = await tools.ValidateAsync(mi, false); var deep = await tools.ValidateAsync(mi, true);
    Check(quick.Contains("Stream #3: subtitle") && deep.Contains("Full A/V decode exit code: 0"), "Validation report incomplete");
});
await Test("GPU capability uses a real encode; unsupported GPU falls back to CPU", async () =>
{
    await tools.TestGpuAsync(); Console.WriteLine("  " + tools.GpuReport.Split('\n')[0]);
    var output = await engine.ExecuteAsync(info, Options(Operation.Cut, "gpu-fallback") with { Acceleration = Acceleration.GPU, Precise = true, End = 1, Height = 240 });
    Check((await tools.ProbeAsync(output)).Video?.Codec == "h264", "Fallback failed"); await Decode(output);
});
await Test("Settings JSON round-trip, validation bounds, corrupted JSON error", async () =>
{
    var store = new SettingsStore(Path.Combine(root, "settings")); var s = new AppSettings { Threads = 99, MaxJobs = 4, FilenameSuffix = "mine", LastOptions = Options(Operation.Cut, "remember") };
    store.Save(s); var loaded = store.Load(); Check(loaded.Threads == 4 && loaded.MaxJobs == 2 && loaded.FilenameSuffix == "mine" && loaded.LastOptions?.Operation == Operation.Cut, "Settings persistence");
    var export = Path.Combine(root, "settings-export.json"); store.Export(export, loaded); Check(store.Import(export).FilenameSuffix == "mine", "Settings export/import");
    await File.WriteAllTextAsync(store.FilePath, "broken"); await Reject(() => Task.FromResult(store.Load()));
});
await Test("No unfinished media/pass files and source hash remains identical after full suite", async () =>
{
    Check(!Directory.EnumerateFiles(root, ".luma-*").Any(), "Temporary/pass files remain");
    Check(originalHash.AsEnumerable().SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(source))), "Source changed");
});
await Test("MOV and FLV encode and fully decode", async () =>
{
    foreach (var container in new[] { "mov", "flv" })
    {
        var output = await engine.ExecuteAsync(info, Options(Operation.Cut, container) with { Container = container, Precise = true, End = 1, Height = 240 });
        Check((await tools.ProbeAsync(output)).Video?.Codec == "h264", "Container encode failed"); await Decode(output);
    }
});
await Test("Optional repaired-output deep validation runs before publication", async () =>
{
    var stages = new List<string>();
    var output = await engine.ExecuteAsync(info, Options(Operation.Repair, "deep-repair") with { Container = "mkv", DeepValidateOutput = true }, new InlineProgress(p => stages.Add(p.Detail)));
    Check(File.Exists(output) && stages.Any(s => s.Contains("Deep validation")), "Deep validation was skipped"); await Decode(output);
});
await Test("Runtime QSV failure after stale successful detection retries CPU and reports fallback", async () =>
{
    if (tools.QsvAvailable) { Console.WriteLine("  QSV exists; actual success path covered instead."); return; }
    // Simulate a device that passed startup but became unavailable. Both attempts use real FFmpeg.
    typeof(MediaTools).GetProperty(nameof(MediaTools.QsvAvailable))!.SetValue(tools, true);
    try
    {
        var sawFallback = false;
        var output = await engine.ExecuteAsync(info, Options(Operation.Cut, "runtime-fallback") with { Precise = true, End = 1, Height = 240, Acceleration = Acceleration.Auto }, new InlineProgress(p => { if (p.Detail.Contains("retrying on CPU")) sawFallback = true; }));
        Check(sawFallback, "GPU failure must be reported before real CPU retry"); await Decode(output);
    }
    finally { typeof(MediaTools).GetProperty(nameof(MediaTools.QsvAvailable))!.SetValue(tools, false); }
});
await Test("Long recordings format without wrapping after 24 hours; cache can be cleared", async () =>
{
    Check(Format.Time(27 * 3600 + 61.25) == "27:01:01.250", "Hours wrapped after 24h");
    MediaTools.TrimCache(Path.Combine(root, "cache"), 0);
    Check(!Directory.EnumerateFiles(Path.Combine(root, "cache"), "*.jpg").Any(), "Cache clear failed"); await Task.CompletedTask;
});
await Test("Repair live-style MKV with no duration/index into duration-bearing seekable MKV", async () =>
{
    var live = Path.Combine(root, "live-no-index.mkv");
    await FF("-i", source, "-map", "0", "-c", "copy", "-f", "matroska", "-live", "1", live);
    var liveInfo = await tools.ProbeAsync(live);
    Console.WriteLine($"  Live fixture duration before repair: {liveInfo.Duration}");
    Check(liveInfo.Duration <= 0, "Fixture must lack duration to exercise the recovery path");
    var repaired = await engine.ExecuteAsync(liveInfo, Options(Operation.Repair, "index-rebuilt") with { Container = "mkv", RepairMode = "Fix seeking" });
    var repairedInfo = await tools.ProbeAsync(repaired); Check(repairedInfo.Duration > 11.9, "Rebuilt MKV needs recovered duration");
    Check((await FrameHashes(source)).AsEnumerable().SequenceEqual(await FrameHashes(repaired)), "Repair changed decoded frames");
    var seek = await runner.RunAsync(tools.FFmpeg, ["-v", "error", "-ss", "10", "-i", repaired, "-frames:v", "1", "-f", "framemd5", "-"]);
    Check(seek.ExitCode == 0 && seek.Output.Split('\n').Any(l => !l.StartsWith('#') && l.Contains(',')), "Rebuilt container must seek near end"); await Decode(repaired);
});
await Test("Timestamp repair normalizes actual negative-start TS timestamps", async () =>
{
    var negative = Path.Combine(root, "negative-start.ts");
    await FF("-copyts", "-itsoffset", "-2", "-i", source, "-map", "0", "-c", "copy", "-avoid_negative_ts", "disabled", "-muxdelay", "0", "-mpegts_copyts", "1", "-f", "mpegts", negative);
    var before = await tools.ProbeAsync(negative);
    using var originalJson = System.Text.Json.JsonDocument.Parse(before.Json);
    var originalStart = double.Parse(originalJson.RootElement.GetProperty("format").GetProperty("start_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    Check(originalStart < 0, "Fixture must actually contain a negative start timestamp");
    var repaired = await engine.ExecuteAsync(before, Options(Operation.Repair, "timestamps-normalized") with { Container = "mkv", RepairMode = "Timestamp repair" });
    var after = await tools.ProbeAsync(repaired);
    using var repairedJson = System.Text.Json.JsonDocument.Parse(after.Json);
    var repairedStart = double.Parse(repairedJson.RootElement.GetProperty("format").GetProperty("start_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    Console.WriteLine($"  Start timestamp before {originalStart}, after {repairedStart}");
    Check(repairedStart >= 0 && after.Duration > 11, "Timestamps were not normalized"); await Decode(repaired);
});
await Test("Queue progress binding is OneWay and the Job setter stays internal (static verification)", async () =>
{
    var xaml = System.Xml.Linq.XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MainWindow.xaml"));
    var progressBars = xaml.Descendants().Where(e => e.Name.LocalName == "ProgressBar" && e.Attribute("Value")?.Value.Contains("Binding Progress") == true).ToArray();
    Check(progressBars.Length == 1 && progressBars[0].Attribute("Value")!.Value == "{Binding Progress, Mode=OneWay}", "Queue progress must explicitly be read-only in the UI");
    Check(typeof(Job).GetProperty(nameof(Job.Progress))!.SetMethod!.IsAssembly, "Progress setter must remain internal to the engine assembly");
    await Task.CompletedTask;
});
await Test("Identical UI errors show once per session; distinct errors remain eligible", async () =>
{
    var guard = new Luma.Desktop.UiErrorDialogGuard();
    Check(guard.TryBegin(new InvalidOperationException("binding failed")), "First error must be shown");
    guard.End();
    for (var i = 0; i < 16; i++) Check(!guard.TryBegin(new InvalidOperationException("binding failed")), "Repeated error must not reopen a dialog");
    Check(!guard.TryBegin(new Exception("wrapper", new InvalidOperationException("binding failed"))), "Wrapped identical root cause must be suppressed");
    Check(guard.TryBegin(new InvalidOperationException("different error")), "Different error must still be shown");
    guard.End();
    Check(guard.TryBegin(new IOException("binding failed")), "Different exception types must remain distinct");
    guard.End();
    await Task.CompletedTask;
});
await Test("UI exception dialogs cannot reenter; an unseen nested error may be shown later", async () =>
{
    var guard = new Luma.Desktop.UiErrorDialogGuard();
    Check(guard.TryBegin(new InvalidOperationException("first")), "First dialog");
    Check(!guard.TryBegin(new InvalidOperationException("first")), "Same error must not reenter");
    Check(!guard.TryBegin(new InvalidOperationException("second")), "Different error must not stack another dialog while one is open");
    guard.End();
    Check(guard.TryBegin(new InvalidOperationException("second")), "An error not yet displayed must remain eligible");
    guard.End();
    await Task.CompletedTask;
});
var summary = $"Luma integration tests — {DateTimeOffset.UtcNow:O}\n{tools.Version}\nOS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n.NET: {Environment.Version}\n{results.Count - failed}/{results.Count} passed\n\n" + string.Join("\n", results);
await File.WriteAllTextAsync(Path.Combine(root, "test-results.txt"), summary);
Console.WriteLine($"\n{results.Count - failed}/{results.Count} passed. Report: {root}/test-results.txt");
return failed == 0 ? 0 : 1;

sealed class InlineProgress(Action<JobProgress> handler) : IProgress<JobProgress> { public void Report(JobProgress value) => handler(value); }
