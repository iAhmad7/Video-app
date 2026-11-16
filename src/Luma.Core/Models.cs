using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luma.Core;

public enum Operation { Convert, Compress, Remux, Cut, Repair, ExtractAudio, ExtractFrame, Transform }
public enum CompressionMode { Quality, Reduction, TargetSize }
public enum Acceleration { Auto, GPU, CPU }
public enum JobState { Waiting, Running, Completed, Failed, Cancelled }

public sealed record MediaStream(int Index, string Type, string Codec, int Width, int Height,
    string Fps, long Bitrate, int Channels, int SampleRate, string Language, string PixelFormat, bool AttachedPicture);
public sealed record MediaInfo(string Path, long Size, double Duration, string Container,
    long Bitrate, IReadOnlyList<MediaStream> Streams, string Json, string Warnings)
{
    public MediaStream? Video => Streams.FirstOrDefault(s => s.Type == "video" && !s.AttachedPicture);
    public string Summary => $"{Format.Bytes(Size)}  ·  {(Duration > 0 ? Format.Time(Duration) : "unknown duration")}  ·  {Video?.Width} × {Video?.Height}  ·  {Video?.Codec.ToUpperInvariant()}  ·  {Video?.Fps} fps";
    public static MediaInfo Parse(string path, string json, string warnings = "")
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var error)) throw new InvalidDataException(error.ToString());
        var f = root.TryGetProperty("format", out var fmt) ? fmt : default;
        var streams = new List<MediaStream>();
        if (root.TryGetProperty("streams", out var list))
            foreach (var s in list.EnumerateArray())
            {
                var lang = s.TryGetProperty("tags", out var tags) ? Str(tags, "language") : "";
                var attached = s.TryGetProperty("disposition", out var d) && Num(d, "attached_pic") == 1;
                streams.Add(new((int)Num(s, "index"), Str(s, "codec_type"), Str(s, "codec_name"),
                    (int)Num(s, "width"), (int)Num(s, "height"), Rate(Str(s, "avg_frame_rate")), Num(s, "bit_rate"),
                    (int)Num(s, "channels"), (int)Num(s, "sample_rate"), lang, Str(s, "pix_fmt"), attached));
            }
        var duration = Double(f, "duration");
        if (duration <= 0 && root.TryGetProperty("streams", out var ss))
            duration = ss.EnumerateArray().Select(s => Double(s, "duration")).DefaultIfEmpty(0).Max();
        return new(System.IO.Path.GetFullPath(path), File.Exists(path) ? new FileInfo(path).Length : Num(f, "size"),
            duration, Str(f, "format_name"), Num(f, "bit_rate"), streams, json, warnings);
    }
    static string Str(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ToString() : "";
    static long Num(JsonElement e, string key) => long.TryParse(Str(e, key), out var n) ? n : 0;
    static double Double(JsonElement e, string key) => double.TryParse(Str(e, key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : 0;
    static string Rate(string value) { var p = value.Split('/'); return p.Length == 2 && double.TryParse(p[0], out var a) && double.TryParse(p[1], out var b) && b != 0 ? (a / b).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "unknown"; }
}

public sealed record JobOptions
{
    public Operation Operation { get; init; }
    public string Container { get; init; } = "mp4";
    public string Codec { get; init; } = "h264";
    public string Preset { get; init; } = "Balanced";
    public string Speed { get; init; } = "veryfast";
    public int Crf { get; init; } = 23;
    public int Height { get; init; }
    public double Fps { get; init; }
    public int VideoKbps { get; init; }
    public CompressionMode Compression { get; init; }
    public double Reduction { get; init; } = 50;
    public long TargetBytes { get; init; }
    public bool PreferCopy { get; init; } = true;
    public double Start { get; init; }
    public double End { get; init; }
    public bool Precise { get; init; }
    public bool DeepValidateOutput { get; init; }
    public bool RemoveAudio { get; init; }
    public bool RemoveSubtitles { get; init; }
    public int[]? AudioTracks { get; init; }
    public int[]? SubtitleTracks { get; init; }
    public string RepairMode { get; init; } = "Fix seeking";
    public string AudioFormat { get; init; } = "mp3";
    public string Transform { get; init; } = "None";
    public string Crop { get; init; } = "";
    public string OutputDirectory { get; init; } = "";
    public string Suffix { get; init; } = "";
    public Acceleration Acceleration { get; init; } = Acceleration.Auto;
    public int Threads { get; init; } = 2;
    public bool LowPriority { get; init; } = true;
}
public sealed record EncodePlan(string Extension, IReadOnlyList<string> InputArgs, IReadOnlyList<string> OutputArgs,
    bool TwoPass, double Duration, long? TargetBytes, string Explanation, string Warning, bool UsesGpu);
public sealed record JobProgress(double Fraction, string Detail);
public sealed class Job : INotifyPropertyChanged
{
    public Guid Id { get; } = Guid.NewGuid();
    public MediaInfo Source { get; }
    public JobOptions Options { get; }
    public string Name => System.IO.Path.GetFileName(Source.Path);
    public string Action => Options.Operation.ToString();
    private JobState state;
    private double progress;
    private string detail = "Waiting", output = "";
    public JobState State { get => state; internal set { state = value; Changed(); } }
    public double Progress { get => progress; internal set { progress = value; Changed(); } }
    public string Detail { get => detail; internal set { detail = value; Changed(); } }
    public string Output { get => output; internal set { output = value; Changed(); } }
    public Job(MediaInfo source, JobOptions options) { Source = source; Options = options; }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public static class Format
{
    public static string Bytes(long n) => n >= 1L << 30 ? $"{n / (double)(1L << 30):0.##} GiB" : n >= 1L << 20 ? $"{n / (double)(1L << 20):0.##} MiB" : $"{n / 1024.0:0.#} KiB";
    public static string Time(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds) return "unknown";
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(long)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }
    public static string Number(double n) => n.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
