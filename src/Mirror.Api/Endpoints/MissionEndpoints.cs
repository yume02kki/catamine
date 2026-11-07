using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Mirror.Api.Jobs;
using Mirror.Core.Compression;
using Mirror.Core.Diffing;
using Mirror.Core.Models;
using Mirror.Core.Rules;
using Mirror.Data;
using Npgsql;

namespace Mirror.Api.Endpoints;

public static partial class MissionEndpoints
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex SlugRegex();

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/missions");

        g.MapGet("", async (MirrorDbContext db, string? q, CancellationToken ct) =>
        {
            var query = db.Missions.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(m => EF.Functions.ILike(m.Name, $"%{q}%") || EF.Functions.ILike(m.Slug, $"%{q}%"));
            var list = await query.OrderBy(m => m.Name).Take(1000).ToListAsync(ct);
            return Results.Ok(list.Select(MissionMapping.ToDto));
        });

        g.MapGet("/{id:guid}", async (Guid id, MirrorDbContext db, CancellationToken ct) =>
            await db.Missions.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct) is { } m
                ? Results.Ok(MissionMapping.ToDto(m))
                : Results.NotFound());

        g.MapPost("", async (MissionUpsert body, MirrorDbContext db, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var m = new MissionEntity
            {
                Id = Guid.CreateVersion7(),
                Slug = body.Slug?.Trim().ToLowerInvariant() ?? "",
                Name = body.Name?.Trim() ?? "",
                MainUrl = body.MainUrl?.Trim() ?? "",
                DevUrl = body.DevUrl?.Trim() ?? "",
                CreatedAt = now,
                UpdatedAt = now,
            };
            var errors = Apply(m, body, isNew: true);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (await db.Missions.AnyAsync(x => x.Slug == m.Slug, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slug"] = ["slug already in use"] });
            db.Missions.Add(m);
            await db.SaveChangesAsync(ct);
            await using (var conn = await ds.OpenConnectionAsync(ct))
                await LogCopyWriter.EnsurePartitionsAsync(conn, m.Id, DateOnly.FromDateTime(DateTime.UtcNow), 3, ct);
            return Results.Created($"/api/missions/{m.Id}", MissionMapping.ToDto(m));
        });

        g.MapPut("/{id:guid}", async (Guid id, MissionUpsert body, MirrorDbContext db, CancellationToken ct) =>
        {
            var m = await db.Missions.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return Results.NotFound();
            var errors = Apply(m, body, isNew: false);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            if (body.Slug is not null && await db.Missions.AnyAsync(x => x.Slug == m.Slug && x.Id != id, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["slug"] = ["slug already in use"] });
            m.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(MissionMapping.ToDto(m));
        });

        g.MapDelete("/{id:guid}", async (Guid id, MirrorDbContext db, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var m = await db.Missions.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return Results.NotFound();
            db.Missions.Remove(m);
            await db.Jobs.Where(j => j.MissionId == id && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.CancelRequested, true), ct);
            await db.SaveChangesAsync(ct);
            await using var cmd = ds.CreateCommand("SELECT mirror_drop_mission_partitions(@m)");
            cmd.Parameters.AddWithValue("m", id);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });

        // ── Rules ──
        g.MapGet("/{id:guid}/rules", async (Guid id, MirrorDbContext db, CancellationToken ct) =>
        {
            if (!await db.Missions.AnyAsync(m => m.Id == id, ct)) return Results.NotFound();
            var rules = await db.MissionRules.AsNoTracking().Where(r => r.MissionId == id).OrderBy(r => r.Order).ToListAsync(ct);
            return Results.Ok(rules.Select(MissionMapping.ToDto));
        });

        g.MapPut("/{id:guid}/rules", async (Guid id, RulesUpdate body, MirrorDbContext db, JobScheduler jobs, CancellationToken ct) =>
        {
            var m = await db.Missions.Include(x => x.Rules).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return Results.NotFound();
            var errors = ValidateRules(body.Rules);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            // Upsert by id: keep ids stable for existing rules, add new ones, delete the rest.
            var existing = m.Rules.ToDictionary(r => r.Id);
            var keep = new HashSet<Guid>();
            for (var i = 0; i < body.Rules.Count; i++)
            {
                var r = body.Rules[i];
                if (r.Id is { } rid && existing.TryGetValue(rid, out var e))
                {
                    keep.Add(rid);
                    e.Order = i;
                    e.Type = RuleRegistry.CanonicalName(r.Type);
                    e.Name = r.Name;
                    e.Enabled = r.Enabled;
                    e.Config = r.Config.GetRawText();
                }
                else
                {
                    db.MissionRules.Add(new MissionRuleEntity
                    {
                        Id = Guid.CreateVersion7(), MissionId = id, Order = i, Type = RuleRegistry.CanonicalName(r.Type),
                        Name = r.Name, Enabled = r.Enabled, Config = r.Config.GetRawText(),
                    });
                }
            }
            var removed = existing.Values.Where(r => !keep.Contains(r.Id)).ToList();
            db.MissionRules.RemoveRange(removed);
            m.RulesVersion++;
            m.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            JobDto? job = null;
            if (body.Rediff) job = JobDto.From(await jobs.EnqueueAsync(JobTypes.Rediff, id, new RediffParams(null), ct));
            var saved = await db.MissionRules.AsNoTracking().Where(r => r.MissionId == id).OrderBy(r => r.Order).ToListAsync(ct);
            return Results.Ok(new { rulesVersion = m.RulesVersion, rules = saved.Select(MissionMapping.ToDto), job });
        });

        g.MapPost("/{id:guid}/rules/preview", async (Guid id, RulePreviewRequest body, MirrorDbContext db, LogRepository repo, CancellationToken ct) =>
        {
            var m = await db.Missions.AsNoTracking().Include(x => x.Rules).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (m is null) return Results.NotFound();
            var errors = ValidateRules(body.Rules);
            if (errors.Count > 0) return Results.ValidationProblem(errors);
            var proposed = CompiledRuleSet.Build(m.RulesVersion + 1,
                body.Rules.Select((r, i) => new RuleDefinition(r.Id ?? Guid.Empty, r.Type, r.Enabled, i, r.Config, r.Name)));
            var current = MissionMapping.CompileRules(m, m.Rules);
            var settings = MissionMapping.DiffSettings(m);

            var sample = Math.Clamp(body.SampleSize, 1, 1000);
            var query = new LogQuery { MissionId = id, Verdicts = [Verdict.Match, Verdict.Mismatch], Limit = sample, Source = LogSource.Live };
            var page = await repo.ListAsync(query, ct);
            int before = 0, after = 0, fixedCount = 0, broken = 0;
            var examples = new List<object>();
            foreach (var log in page.Items)
            {
                if (log.DevStatus is null) continue;
                var b = await repo.GetBodyAsync(id, log.Id, ct);
                if (b is null) continue;
                var input = new DiffInput
                {
                    MainStatus = log.MainStatus, DevStatus = log.DevStatus.Value,
                    MainBody = BodyCodec.Decompress(b.MainBody), DevBody = BodyCodec.Decompress(b.DevBody),
                    MainHeaders = LogRecord.UnpackHeaders(b.MainHeaders), DevHeaders = LogRecord.UnpackHeaders(b.DevHeaders),
                };
                var x = DiffEngine.Compare(input, current, settings);
                var y = DiffEngine.Compare(input, proposed, settings);
                if (!x.IsEqual) before++;
                if (!y.IsEqual) after++;
                if (!x.IsEqual && y.IsEqual) fixedCount++;
                if (x.IsEqual && !y.IsEqual) broken++;
                if (examples.Count < 5 && x.TotalCount != y.TotalCount)
                    examples.Add(new { logId = log.Id, path = log.Path, before = x.TotalCount, after = y.TotalCount });
            }
            return Results.Ok(new
            {
                sampled = page.Items.Count, mismatchesBefore = before, mismatchesAfter = after, nowMatching = fixedCount, nowMismatching = broken,
                ruleErrors = proposed.Errors.Select(e => new { rule = e.Rule.Type, e.Error }), examples,
            });
        });
    }

    private static Dictionary<string, string[]> ValidateRules(List<RuleInput> rules)
    {
        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < rules.Count; i++)
        {
            var e = RuleRegistry.Validate(rules[i].Type, rules[i].Config);
            if (e.Count > 0) errors[$"rules[{i}]"] = e.ToArray();
        }
        return errors;
    }

    private static Dictionary<string, string[]> Apply(MissionEntity m, MissionUpsert b, bool isNew)
    {
        var errors = new Dictionary<string, string[]>();
        void Err(string k, string msg) => errors[k] = [msg];

        if (b.Slug is not null || isNew)
        {
            m.Slug = b.Slug?.Trim().ToLowerInvariant() ?? m.Slug;
            if (!SlugRegex().IsMatch(m.Slug)) Err("slug", "lowercase letters, digits and dashes, 1–63 characters");
        }
        if (b.Name is not null || isNew)
        {
            m.Name = b.Name?.Trim() ?? m.Name;
            if (string.IsNullOrWhiteSpace(m.Name)) Err("name", "required");
        }
        if (b.Description is not null) m.Description = b.Description;
        if (b.MainUrl is not null || isNew)
        {
            m.MainUrl = b.MainUrl?.Trim() ?? m.MainUrl;
            if (!IsHttpUrl(m.MainUrl)) Err("mainUrl", "absolute http(s) URL required");
        }
        if (b.DevUrl is not null || isNew)
        {
            m.DevUrl = b.DevUrl?.Trim() ?? m.DevUrl;
            if (!IsHttpUrl(m.DevUrl)) Err("devUrl", "absolute http(s) URL required");
        }
        if (b.Enabled is { } en) m.Enabled = en;
        if (b.MirrorEnabled is { } me) m.MirrorEnabled = me;
        if (b.SampleRate is { } sr)
        {
            if (sr is < 0 or > 1 || double.IsNaN(sr)) Err("sampleRate", "between 0 and 1");
            else m.SampleRate = sr;
        }
        if (b.MirrorMethods is { } methods)
        {
            var upper = methods.Select(x => x.Trim().ToUpperInvariant()).Distinct().ToArray();
            var unknown = upper.Except(MissionMapping.KnownMethods).ToArray();
            if (unknown.Length > 0) Err("mirrorMethods", $"unknown methods: {string.Join(", ", unknown)}");
            var newlyUnsafe = upper.Intersect(MissionMapping.UnsafeMethods).Except(isNew ? [] : m.MirrorMethods).ToArray();
            if (newlyUnsafe.Length > 0 && !b.AcknowledgeUnsafeMethods)
                Err("mirrorMethods", $"Mirroring {string.Join(", ", newlyUnsafe)} replays real writes against Dev. Set acknowledgeUnsafeMethods=true to confirm.");
            if (!errors.ContainsKey("mirrorMethods")) m.MirrorMethods = upper;
        }
        if (b.MaxMirrorBodyBytes is { } mb)
        {
            if (mb is < 0 or > 64 * 1024 * 1024) Err("maxMirrorBodyBytes", "0 – 64 MB");
            else m.MaxMirrorBodyBytes = mb;
        }
        if (b.DevTimeoutMs is { } to)
        {
            if (to is < 10 or > 120_000) Err("devTimeoutMs", "10 – 120000");
            else m.DevTimeoutMs = to;
        }
        if (b.DevMaxConcurrency is { } dc)
        {
            if (dc is < 1 or > 10_000) Err("devMaxConcurrency", "1 – 10000");
            else m.DevMaxConcurrency = dc;
        }
        if (b.HeaderPolicy is { } hp)
            m.HeaderPolicy = JsonSerializer.Serialize(new HeaderPolicy
            {
                Strip = (hp.Strip ?? []).Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToArray(),
                Set = hp.Set ?? [],
            }, MissionSnapshotLoader.JsonOptions);
        if (b.ComparedHeaders is { } ch) m.ComparedHeaders = ch.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToArray();
        if (b.RetentionDays is { } rd)
        {
            if (rd is < 1 or > 3650) Err("retentionDays", "1 – 3650");
            else m.RetentionDays = rd;
        }
        if (b.MaxStorageMb is { } ms)
        {
            m.MaxStorageMb = ms <= 0 ? null : ms; // 0 removes the limit
        }
        return errors;
    }

    private static bool IsHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
}
