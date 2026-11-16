namespace Luma.Core;

public sealed class JobEngine(MediaTools tools)
{
    readonly ProcessRunner runner = new();
    public async Task<string> ExecuteAsync(MediaInfo source, JobOptions options, IProgress<JobProgress>? progress = null, CancellationToken ct = default)
    {
        var plan = Planner.Create(source, options, tools.QsvAvailable);
        var directory = string.IsNullOrWhiteSpace(options.OutputDirectory) ? Path.GetDirectoryName(source.Path)! : Path.GetFullPath(options.OutputDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Output folder does not exist. Choose an existing folder in Settings.");
        var expected = plan.TargetBytes ?? (options.Operation == Operation.ExtractFrame ? 10L * 1024 * 1024 : source.Size);
        SafeFiles.CheckSpace(directory, expected);
        var token = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(directory, ".luma-" + token + ".partial." + plan.Extension);
        var passLog = Path.Combine(directory, ".luma-" + token + "-pass");
        try
        {
            try { await EncodeAsync(plan, temporary, passLog, source, options, progress, ct); }
            catch (ToolException gpuError) when (plan.UsesGpu)
            {
                DiagnosticLog.Write("GPU job failed; retrying once on CPU. " + gpuError);
                progress?.Report(new(0, "Quick Sync failed; retrying on CPU. See diagnostic log for GPU details."));
                if (File.Exists(temporary)) File.Delete(temporary);
                plan = Planner.Create(source, options, tools.QsvAvailable, forceCpu: true);
                await EncodeAsync(plan, temporary, passLog, source, options, progress, ct);
            }
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new ToolException("FFmpeg produced an empty output. The original has been kept.", temporary);
            progress?.Report(new(.995, "Checking output before publishing…"));
            var check = await tools.ProbeAsync(temporary, ct);
            if (check.Streams.Count == 0 || options.Operation != Operation.ExtractAudio && check.Video == null)
                throw new ToolException("The output did not contain the expected media streams. It was not published.", check.Json);
            if (options.DeepValidateOutput)
            {
                progress?.Report(new(.995, "Deep-validating output; full decode in progress…"));
                var report = await tools.ValidateAsync(check, true, options.Threads, ct, new StageProgress(progress));
                if (!report.Contains("Full A/V decode exit code: 0", StringComparison.Ordinal))
                    throw new ToolException("The repaired output failed full decode validation and was not published. Try another repair method; the original is kept.", report);
            }
            ct.ThrowIfCancellationRequested();
            var suffix = options.Suffix.Trim();
            if (suffix == "") suffix = options.Operation.ToString().ToLowerInvariant();
            var stem = SafeFiles.Stem(source.Path, suffix);
            // Non-overwriting rename is atomic on the destination filesystem. Resolve races again.
            for (var attempt = 0; ; attempt++)
            {
                var final = SafeFiles.UniquePath(directory, stem, plan.Extension);
                try { File.Move(temporary, final, overwrite: false); DiagnosticLog.Write($"Completed {options.Operation}: {source.Path} → {final}"); return final; }
                catch (IOException) when (File.Exists(final) && attempt < 20) { }
            }
        }
        finally
        {
            TryDelete(temporary);
            try { foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(passLog) + "*")) TryDelete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("Pass log cleanup: " + e.Message); }
        }
    }
    async Task EncodeAsync(EncodePlan plan, string temporary, string passLog, MediaInfo source, JobOptions options, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        var passes = plan.TwoPass ? 2 : 1;
        for (var pass = 0; pass < passes; pass++)
        {
            var args = new List<string> { "-hide_banner", "-v", "warning", "-nostdin", "-n", "-filter_threads", "1", "-threads", Math.Clamp(options.Threads, 1, 4).ToString() };
            args.AddRange(plan.InputArgs); args.AddRange(["-i", source.Path]); args.AddRange(plan.OutputArgs);
            args.AddRange(["-progress", "pipe:1", "-stats_period", "0.5", "-nostats"]);
            if (plan.TwoPass) args.AddRange(["-pass", (pass + 1).ToString(), "-passlogfile", passLog]);
            if (plan.TwoPass && pass == 0) args.AddRange(["-an", "-sn", "-f", "null", "-"]);
            else args.Add(temporary);
            var currentPass = pass;
            var r = await runner.RunAsync(tools.FFmpeg, args, ct,
                line => MediaTools.ReportProgress(line, plan.Duration, currentPass, passes, progress), options.LowPriority);
            if (r.ExitCode != 0) throw new ToolException("FFmpeg could not finish this job. The original is unchanged. Check codec/container compatibility, free disk space and selected tracks; see technical details below.", r.Error);
            if (!string.IsNullOrWhiteSpace(r.Error)) DiagnosticLog.Write($"{options.Operation} warnings: {r.Error}");
        }
    }
    sealed class StageProgress(IProgress<JobProgress>? progress) : IProgress<JobProgress>
    {
        public void Report(JobProgress p) => progress?.Report(new(.995, "Deep validation · " + p.Detail));
    }
    static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("Temporary file could not be removed: " + file + "\n" + e.Message); }
    }
}
