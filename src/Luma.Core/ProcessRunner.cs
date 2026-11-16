using System.Diagnostics;
using System.Text;

namespace Luma.Core;

public sealed record ProcessResult(int ExitCode, string Output, string Error);
public sealed class ToolException : Exception
{
    public string Details { get; }
    public ToolException(string message, string details) : base(message) => Details = details;
    public override string ToString() => Message + Environment.NewLine + Details;
}
public sealed class ProcessRunner
{
    // Only owns the process started by this invocation. Both pipes are always drained.
    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken ct = default, Action<string>? outputLine = null, bool lowPriority = false)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new ToolException("Cannot start the media tool. Select a valid executable in Settings.", e.Message); }
        if (lowPriority)
        {
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            { DiagnosticLog.Write("Process priority could not be lowered: " + e.Message); }
        }
        var stdout = DrainAsync(process.StandardOutput, 8 * 1024 * 1024, outputLine);
        var stderr = DrainAsync(process.StandardError, 256 * 1024, null);
        try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { DiagnosticLog.Write("Process termination: " + e.Message); }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new(process.ExitCode, await stdout, await stderr);
    }
    static async Task<string> DrainAsync(StreamReader reader, int limit, Action<string>? lineHandler)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            try { lineHandler?.Invoke(line); }
            catch (Exception e) { DiagnosticLog.Write("Progress observer failed; pipe continues to drain: " + e.Message); }
            text.AppendLine(line);
            if (text.Length > limit) text.Remove(0, text.Length - limit);
        }
        return text.ToString();
    }
}
public static class DiagnosticLog
{
    public static string DirectoryPath { get; set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "Logs");
    private static readonly object Gate = new();
    public static event Action<string>? LogFailure;
    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = System.IO.Path.Combine(DirectoryPath, $"luma-{DateTime.Now:yyyy-MM-dd}.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.WriteAllText(path, "Log rotated at 2 MiB.\n");
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LogFailure?.Invoke(e.Message); }
        }
    }
    public static void Cleanup(int days = 7)
    {
        if (!Directory.Exists(DirectoryPath)) return;
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "luma-*.log"))
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-days)) File.Delete(file);
    }
}
