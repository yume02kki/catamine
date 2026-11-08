using Mirror.Core.Models;

namespace Mirror.Core.Tests;

public class Uuid7Tests
{
    [Fact]
    public void Has_version_7_rfc_variant_and_the_timestamp_in_the_first_48_bits()
    {
        var at = DateTimeOffset.Parse("2026-11-07T18:00:16.703Z");
        var id = Uuid7.NewGuid(at);
        var s = id.ToString();
        Assert.Equal('7', s[14]);
        Assert.Contains(s[19], "89ab");
        Assert.Equal(at.ToUnixTimeMilliseconds(), Convert.ToInt64(s[..8] + s[9..13], 16));

        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b, bigEndian: true, out _);
        long ms = ((long)b[0] << 40) | ((long)b[1] << 32) | ((long)b[2] << 24) | ((long)b[3] << 16) | ((long)b[4] << 8) | b[5];
        Assert.Equal(at.ToUnixTimeMilliseconds(), ms);
    }

    [Fact]
    public void Ids_sort_by_time_and_are_unique()
    {
        var t0 = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 1000).Select(i => Uuid7.NewGuid(t0.AddMilliseconds(i))).ToList();
        Assert.Equal(ids, ids.OrderBy(g => g.ToString(), StringComparer.Ordinal));
        Assert.Equal(1000, Enumerable.Range(0, 1000).Select(_ => Uuid7.NewGuid(t0)).Distinct().Count());
    }

    [Fact]
    public void Matches_a_known_id_written_by_the_previous_runtime()
    {
        // 01a1b72b-163f-7671-… was created by Guid.CreateVersion7 at 2026-11-07T18:00:16.703Z.
        var id = Guid.Parse("01a1b72b-163f-7671-aff5-6bc190b5b7d9");
        var mine = Uuid7.NewGuid(DateTimeOffset.Parse("2026-11-07T18:00:16.703Z"));
        Assert.Equal(id.ToString()[..13], mine.ToString()[..13]);
    }
}
