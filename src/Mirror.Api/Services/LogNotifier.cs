using System.Collections.Concurrent;
using Mirror.Data;
using Npgsql;

namespace Mirror.Api.Services;

/// <summary>
/// Holds one LISTEN connection for <c>mirror_log</c> notifications (sent by the proxy writer once per batch) and keeps a
/// per-mission version counter. SSE streams poll the counter once per second, so live updates are batched no matter
/// how many requests arrive.
/// </summary>
public sealed class LogNotifier(NpgsqlDataSource ds, ILogger<LogNotifier> log) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, long> _versions = new();

    public long Version(Guid missionId) => _versions.TryGetValue(missionId, out var v) ? v : 0;

    /// <summary>Marks a mission changed from inside the api (replays, imports…).</summary>
    public void Touch(Guid missionId) => _versions.AddOrUpdate(missionId, 1, (_, v) => v + 1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                conn.Notification += (_, e) =>
                {
                    foreach (var part in e.Payload.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (Guid.TryParse(part, out var id)) Touch(id);
                };
                await using (var cmd = new NpgsqlCommand($"LISTEN {LogCopyWriter.NotifyChannel}", conn))
                    await cmd.ExecuteNonQueryAsync(ct);
                backoff = TimeSpan.FromSeconds(1);
                // Missed notifications while disconnected: bump everything so streams re-query once.
                foreach (var key in _versions.Keys) Touch(key);
                while (!ct.IsCancellationRequested) await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log.LogWarning("LISTEN connection lost: {Message}; retrying in {Delay}s", ex.Message, backoff.TotalSeconds);
                await Task.Delay(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
    }
}
