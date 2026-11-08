using Mirror.Core.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mirror.Api.Services;
using Mirror.Data;
using Npgsql;

namespace Mirror.Api.Jobs;

public static class JobTypes
{
    public const string Rediff = "rediff";
    public const string Replay = "replay";
    public const string Fuzz = "fuzz";
    public const string Export = "export";
    public const string Import = "import";
}

public interface IJobHandler
{
    string Type { get; }
    /// <summary>Runs (or resumes from <see cref="JobContext.Checkpoint"/>) and returns the result object stored on the job.</summary>
    Task<object?> RunAsync(JobContext ctx, CancellationToken ct);
}

/// <summary>Handle given to a running job: parameters, checkpoint, throttled progress reporting and cancellation.</summary>
public sealed class JobContext(Guid id, Guid? missionId, string paramsJson, string? checkpoint, NpgsqlDataSource ds, CancellationTokenSource cancel)
{
    private DateTime _lastReport = DateTime.MinValue;

    public Guid Id { get; } = id;
    public Guid? MissionId { get; } = missionId;
    public string? Checkpoint { get; private set; } = checkpoint;
    public long Done { get; private set; }
    public long? Total { get; private set; }

    public T Params<T>() => JsonSerializer.Deserialize<T>(paramsJson, JsonDefaults.Options)!;

    public async Task ReportAsync(long done, long? total, string? checkpoint, bool force = false)
    {
        Done = done;
        Total = total ?? Total;
        if (checkpoint is not null) Checkpoint = checkpoint;
        if (!force && DateTime.UtcNow - _lastReport < TimeSpan.FromSeconds(1)) return;
        _lastReport = DateTime.UtcNow;
        await using var cmd = ds.CreateCommand("""
            UPDATE jobs SET done = @d, total = @t, checkpoint = @c::jsonb, heartbeat_at = now() WHERE id = @id RETURNING cancel_requested
            """);
        cmd.Parameters.AddWithValue("d", Done);
        cmd.Parameters.AddWithValue("t", (object?)Total ?? DBNull.Value);
        cmd.Parameters.AddWithValue("c", Checkpoint is null ? DBNull.Value : JsonSerializer.Serialize(Checkpoint));
        cmd.Parameters.AddWithValue("id", Id);
        if (await cmd.ExecuteScalarAsync() is true) await cancel.CancelAsync();
    }
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Claims queued jobs (or jobs whose worker died) with <c>FOR UPDATE SKIP LOCKED</c>, so several api replicas can
/// share the work. Each job heartbeats; a job with a stale heartbeat is resumed from its checkpoint elsewhere.
/// </summary>
public sealed class JobRunner(
    NpgsqlDataSource ds,
    IEnumerable<IJobHandler> handlers,
    IOptions<ApiOptions> options,
    ILogger<JobRunner> log) : BackgroundService
{
    private readonly Dictionary<string, IJobHandler> _handlers = handlers.ToDictionary(h => h.Type);
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
    private readonly SemaphoreSlim _wake = new(0);

    public void Wake() => _wake.Release();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var free = Math.Max(1, options.Value.MaxConcurrentJobs) - _running.Count;
                if (free > 0) await ClaimAsync(free, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Job claim failed: {Message}", ex.Message);
            }
            await _wake.WaitAsync(TimeSpan.FromSeconds(2), ct).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        await Task.WhenAll(_running.Values);
    }

    private async Task ClaimAsync(int count, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            UPDATE jobs SET status = 1, worker_id = @w, started_at = coalesce(started_at, now()), heartbeat_at = now(), attempts = attempts + 1
            WHERE id IN (
                SELECT id FROM jobs
                WHERE status = 0 OR (status = 1 AND heartbeat_at < now() - interval '60 seconds')
                ORDER BY created_at LIMIT @n FOR UPDATE SKIP LOCKED)
            RETURNING id, mission_id, type, params::text, checkpoint::text, attempts, cancel_requested
            """);
        cmd.Parameters.AddWithValue("w", _workerId);
        cmd.Parameters.AddWithValue("n", count);
        var claimed = new List<(Guid Id, Guid? Mission, string Type, string Params, string? Checkpoint, int Attempts, bool Cancel)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                claimed.Add((r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : JsonSerializer.Deserialize<string>(r.GetString(4)), r.GetInt32(5), r.GetBoolean(6)));

        foreach (var job in claimed)
            _running[job.Id] = Task.Run(() => RunAsync(job.Id, job.Mission, job.Type, job.Params, job.Checkpoint, job.Attempts, job.Cancel, ct), CancellationToken.None);
    }

    private async Task RunAsync(Guid id, Guid? missionId, string type, string paramsJson, string? checkpoint, int attempts, bool cancelRequested, CancellationToken stopping)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (cancelRequested) await cancel.CancelAsync();
        var ctx = new JobContext(id, missionId, paramsJson, checkpoint, ds, cancel);
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var hbTask = Task.Run(async () =>
        {
            try { while (await heartbeat.WaitForNextTickAsync(cancel.Token)) await ctx.ReportAsync(ctx.Done, ctx.Total, null, force: true); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogDebug("Heartbeat failed for job {Id}: {Message}", id, ex.Message); }
        }, CancellationToken.None);

        try
        {
            if (!_handlers.TryGetValue(type, out var handler)) throw new InvalidOperationException($"unknown job type '{type}'");
            if (attempts > 3) throw new InvalidOperationException("job failed after 3 attempts");
            var result = await handler.RunAsync(ctx, cancel.Token);
            await FinishAsync(id, JobStatus.Succeeded, ctx, result, null);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutting down: leave it running; another instance (or this one after restart) resumes from the checkpoint.
            await ctx.ReportAsync(ctx.Done, ctx.Total, null, force: true).ContinueWith(_ => { }, TaskScheduler.Default);
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(id, JobStatus.Cancelled, ctx, null, "cancelled");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Job {Id} ({Type}) failed", id, type);
            await FinishAsync(id, JobStatus.Failed, ctx, null, ex.Message);
        }
        finally
        {
            await cancel.CancelAsync();
            await hbTask;
            _running.TryRemove(id, out _);
            _wake.Release();
        }
    }

    private async Task FinishAsync(Guid id, JobStatus status, JobContext ctx, object? result, string? error)
    {
        try
        {
            await using var cmd = ds.CreateCommand("""
                UPDATE jobs SET status = @s, done = @d, total = coalesce(@t, total), result = @r::jsonb, error = @e, finished_at = now(), heartbeat_at = now()
                WHERE id = @id
                """);
            cmd.Parameters.AddWithValue("s", (int)status);
            cmd.Parameters.AddWithValue("d", ctx.Done);
            cmd.Parameters.AddWithValue("t", (object?)ctx.Total ?? DBNull.Value);
            cmd.Parameters.AddWithValue("r", result is null ? DBNull.Value : JsonSerializer.Serialize(result, JsonDefaults.Options));
            cmd.Parameters.AddWithValue("e", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not record completion of job {Id}", id);
        }
    }
}

/// <summary>Creates job rows; the runner picks them up.</summary>
public sealed class JobScheduler(MirrorDbContext db, JobRunner runner)
{
    public async Task<JobEntity> EnqueueAsync(string type, Guid? missionId, object parameters, CancellationToken ct)
    {
        var job = new JobEntity
        {
            Id = Uuid7.NewGuid(),
            MissionId = missionId,
            Type = type,
            Status = JobStatus.Queued,
            Params = JsonSerializer.Serialize(parameters, JsonDefaults.Options),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        runner.Wake();
        return job;
    }
}
