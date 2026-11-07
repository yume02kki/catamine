using System.Text.Json;
using Mirror.Core.Models;
using Mirror.Core.Rules;
using Npgsql;

namespace Mirror.Data;

/// <summary>Loads all mission configs and rules with two cheap queries (raw Npgsql; used by the proxy, which has no EF).</summary>
public static class MissionSnapshotLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<List<MissionConfig>> LoadAsync(NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var rules = new Dictionary<Guid, List<RuleDefinition>>();
        await using (var cmd = new NpgsqlCommand("SELECT id, mission_id, \"order\", type, name, enabled, config::text FROM mission_rules", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var missionId = r.GetGuid(1);
                if (!rules.TryGetValue(missionId, out var list)) rules[missionId] = list = [];
                using var doc = JsonDocument.Parse(r.GetString(6));
                list.Add(new RuleDefinition(r.GetGuid(0), r.GetString(3), r.GetBoolean(5), r.GetInt32(2), doc.RootElement.Clone(),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            }
        }

        var result = new List<MissionConfig>();
        const string sql = """
            SELECT id, slug, name, main_url, dev_url, enabled, mirror_enabled, sample_rate, mirror_methods, max_mirror_body_bytes,
                   dev_timeout_ms, dev_max_concurrency, header_policy::text, compared_headers, rules_version, updated_at
            FROM missions
            """;
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var id = r.GetGuid(0);
                result.Add(new MissionConfig
                {
                    Id = id,
                    Slug = r.GetString(1),
                    Name = r.GetString(2),
                    MainUrl = r.GetString(3),
                    DevUrl = r.GetString(4),
                    Enabled = r.GetBoolean(5),
                    MirrorEnabled = r.GetBoolean(6),
                    SampleRate = r.GetDouble(7),
                    MirrorMethods = r.GetFieldValue<string[]>(8),
                    MaxMirrorBodyBytes = r.GetInt32(9),
                    DevTimeoutMs = r.GetInt32(10),
                    DevMaxConcurrency = r.GetInt32(11),
                    Headers = JsonSerializer.Deserialize<HeaderPolicy>(r.GetString(12), JsonOptions) ?? new HeaderPolicy(),
                    ComparedHeaders = r.GetFieldValue<string[]>(13),
                    RulesVersion = r.GetInt32(14),
                    UpdatedAt = r.GetFieldValue<DateTimeOffset>(15),
                    Rules = rules.TryGetValue(id, out var rl) ? rl.OrderBy(x => x.Order).ToList() : [],
                });
            }
        }
        return result;
    }
}
