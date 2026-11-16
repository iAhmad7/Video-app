using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Luma.Core;

public sealed class JobQueue : IAsyncDisposable
{
    readonly Channel<Job> channel = Channel.CreateUnbounded<Job>(new() { SingleReader = false, SingleWriter = true });
    readonly ConcurrentDictionary<Guid, CancellationTokenSource> cancellations = new();
    readonly Task[] workers;
    readonly JobEngine engine;
    public event Action<Job>? Finished;
    public bool HasWork => !cancellations.IsEmpty;
    public JobQueue(MediaTools tools, int concurrency = 1)
    {
        engine = new(tools);
        workers = Enumerable.Range(0, Math.Clamp(concurrency, 1, 2)).Select(_ => Task.Run(WorkAsync)).ToArray();
    }
    public Job Enqueue(MediaInfo source, JobOptions options)
    {
        var job = new Job(source, options);
        cancellations[job.Id] = new();
        if (!channel.Writer.TryWrite(job)) { cancellations.TryRemove(job.Id, out var cts); cts?.Dispose(); throw new InvalidOperationException("The queue has stopped."); }
        return job;
    }
    public void Cancel(Job job) { if (cancellations.TryGetValue(job.Id, out var ct)) { try { ct.Cancel(); } catch (ObjectDisposedException) { } } }
    public void CancelAll() { foreach (var id in cancellations.Keys) if (cancellations.TryGetValue(id, out var ct)) { try { ct.Cancel(); } catch (ObjectDisposedException) { } } }
    async Task WorkAsync()
    {
        await foreach (var job in channel.Reader.ReadAllAsync())
        {
            if (!cancellations.TryGetValue(job.Id, out var cts)) continue;
            try
            {
                cts.Token.ThrowIfCancellationRequested();
                job.State = JobState.Running; job.Detail = "Starting FFmpeg…";
                var cpuFallback = false;
                job.Output = await engine.ExecuteAsync(job.Source, job.Options, new InlineProgress(p => { if (p.Detail.StartsWith("Quick Sync failed")) cpuFallback = true; job.Progress = p.Fraction * 100; job.Detail = p.Detail; }), cts.Token);
                job.State = JobState.Completed; job.Progress = 100;
                var size = new FileInfo(job.Output).Length;
                var savings = job.Source.Size > 0 ? 100 * (1 - size / (double)job.Source.Size) : 0;
                job.Detail = (cpuFallback ? "Quick Sync failed; CPU fallback succeeded (details in logs). " : "") + (job.Options.DeepValidateOutput ? "Deep decode completed; warning report in logs. " : "") + $"{Format.Bytes(size)} · {(savings >= 0 ? $"{savings:0.#}% saved" : $"{-savings:0.#}% larger")} · {job.Output}";
            }
            catch (OperationCanceledException) { job.State = JobState.Cancelled; job.Detail = "Cancelled or tool timed out; no successful output published. Original kept."; DiagnosticLog.Write("Job cancelled: " + job.Source.Path); }
            catch (Exception e) { job.State = JobState.Failed; job.Detail = e + "\nRecovery: verify FFmpeg paths, available codecs, source integrity, destination permissions and free space."; DiagnosticLog.Write("Job failed: " + job.Source.Path + "\n" + e); }
            finally { cancellations.TryRemove(job.Id, out _); cts.Dispose(); }
            Finished?.Invoke(job);
        }
    }
    public async ValueTask DisposeAsync() { channel.Writer.TryComplete(); CancelAll(); await Task.WhenAll(workers); }
    sealed class InlineProgress(Action<JobProgress> handler) : IProgress<JobProgress> { public void Report(JobProgress value) => handler(value); }
}
