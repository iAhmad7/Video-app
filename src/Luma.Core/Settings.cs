using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luma.Core;

public sealed class AppSettings
{
    public string FFmpegPath { get; set; } = "";
    public string FFprobePath { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string FilenameSuffix { get; set; } = "";
    public bool OpenOutput { get; set; }
    public bool ConfirmClose { get; set; } = true;
    public bool RememberPreferences { get; set; } = true;
    public bool RestoreWindow { get; set; } = true;
    public double WindowWidth { get; set; } = 1260;
    public double WindowHeight { get; set; } = 840;
    public bool LightTheme { get; set; }
    public Acceleration Acceleration { get; set; } = Acceleration.Auto;
    public int Threads { get; set; } = 2;
    public int MaxJobs { get; set; } = 1;
    public int CacheMiB { get; set; } = 64;
    public bool LowPriority { get; set; } = true;
    public bool DeepValidation { get; set; }
    public JobOptions? LastOptions { get; set; }
}
public sealed class SettingsStore
{
    public string DataDirectory { get; }
    public string FilePath => Path.Combine(DataDirectory, "settings.json");
    public string CacheDirectory => Path.Combine(DataDirectory, "Thumbnails");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public SettingsStore(string? dataDirectory = null)
    {
        if (dataDirectory != null) DataDirectory = dataDirectory;
        else
        {
            var portable = Path.Combine(AppContext.BaseDirectory, "Data");
            try { Directory.CreateDirectory(portable); var test = Path.Combine(portable, Guid.NewGuid() + ".tmp"); File.WriteAllText(test, ""); File.Delete(test); DataDirectory = portable; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaToolkit"); }
        }
        Directory.CreateDirectory(DataDirectory);
        DiagnosticLog.DirectoryPath = Path.Combine(DataDirectory, "Logs");
    }
    public AppSettings Load()
    {
        try { return File.Exists(FilePath) ? Validate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? throw new InvalidDataException("Empty settings")) : new(); }
        catch (Exception e) when (e is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        { DiagnosticLog.Write("Settings could not be loaded: " + e); throw new ToolException("Settings could not be read. Defaults can be used; the existing file has been kept.", e.Message); }
    }
    public void Save(AppSettings settings)
    {
        Validate(settings);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
    public void Export(string path, AppSettings settings) => File.WriteAllText(path, JsonSerializer.Serialize(Validate(settings), JsonOptions));
    public AppSettings Import(string path) => Validate(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("Settings are empty."));
    static AppSettings Validate(AppSettings s)
    {
        s.Threads = Math.Clamp(s.Threads, 1, 4); s.MaxJobs = Math.Clamp(s.MaxJobs, 1, 2); s.CacheMiB = Math.Clamp(s.CacheMiB, 8, 512);
        s.WindowWidth = double.IsFinite(s.WindowWidth) ? Math.Clamp(s.WindowWidth, 980, 2560) : 1260;
        s.WindowHeight = double.IsFinite(s.WindowHeight) ? Math.Clamp(s.WindowHeight, 700, 1600) : 840;
        if (!Enum.IsDefined(s.Acceleration)) s.Acceleration = Acceleration.Auto;
        s.FFmpegPath ??= ""; s.FFprobePath ??= ""; s.OutputDirectory ??= ""; s.FilenameSuffix ??= "";
        if (s.LastOptions is { } o)
        {
            s.LastOptions = Enum.IsDefined(o.Compression) && double.IsFinite(o.Reduction) && double.IsFinite(o.Fps)
                ? o with { Reduction = Math.Clamp(o.Reduction, 10, 90), Crf = Math.Clamp(o.Crf, 0, 51), Fps = Math.Clamp(o.Fps, 0, 240), VideoKbps = Math.Clamp(o.VideoKbps, 0, 200000) }
                : null;
        }
        return s;
    }
}
public static class SafeFiles
{
    public static string Stem(string source, string suffix)
    {
        var invalid = "<>:\"/\\|?*";
        var cleaned = new string(suffix.Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length > 60) cleaned = cleaned[..60];
        if (cleaned.Length == 0) cleaned = "processed";
        var stem = Path.GetFileNameWithoutExtension(source);
        if (stem.Length > 120) stem = stem[..120];
        return stem + "_" + cleaned;
    }
    public static string UniquePath(string directory, string stem, string extension)
    {
        for (var i = 0; i < 10000; i++)
        {
            var path = Path.Combine(directory, stem + (i == 0 ? "" : $" ({i})") + "." + extension);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
        throw new IOException("Too many output name conflicts. Choose another folder or suffix.");
    }
    public static void CheckSpace(string directory, long? expectedBytes)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(directory));
        if (root == null) return;
        var drive = new DriveInfo(root);
        if (drive.IsReady && drive.AvailableFreeSpace < (expectedBytes ?? 0) + 32L * 1024 * 1024)
            throw new IOException("Not enough free disk space for the estimated output and a 32 MiB safety margin. Choose another output folder.");
    }
}
