using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Mirror.Core.Compression;
using Mirror.Core.Fuzzing;

namespace Mirror.Core.Tests;

public class FuzzGeneratorTests
{
    private static readonly FuzzSeed Seed = new("POST", "/v1/quote", "currency=EUR&qty=2", null,
        """{"customer":{"id":42,"vip":true},"items":[{"sku":"A","qty":1}],"note":"hi"}""");

    [Fact]
    public void Generates_each_mutation_family()
    {
        var cases = FuzzGenerator.Generate(Seed, new FuzzOptions { Volume = 10_000, HugeStringLength = 100 }).ToList();
        foreach (var m in FuzzGenerator.AllMutations)
            Assert.Contains(cases, c => c.Mutation == m);
        Assert.All(cases, c => Assert.Equal("POST", c.Method));
    }

    [Fact]
    public void Mutations_produce_valid_json_with_the_change_applied()
    {
        var cases = FuzzGenerator.Generate(Seed, new FuzzOptions { Volume = 10_000, HugeStringLength = 10 }).ToList();
        var missing = cases.First(c => c.Mutation == "missing" && c.Target == "$.customer.id");
        Assert.False(JsonNode.Parse(missing.Body!)!["customer"]!.AsObject().ContainsKey("id"));
        var nulled = cases.First(c => c.Mutation == "null" && c.Target == "$.note");
        Assert.Null(JsonNode.Parse(nulled.Body!)!["note"]);
        var wrong = cases.First(c => c.Mutation == "wrong_type" && c.Target == "$.customer.id");
        Assert.Equal("\"42\"", JsonNode.Parse(wrong.Body!)!["customer"]!["id"]!.ToJsonString());
        var extra = cases.First(c => c.Mutation == "extra_field" && c.Target == "$.customer");
        Assert.NotNull(JsonNode.Parse(extra.Body!)!["customer"]!["__fuzz_extra"]);
        var q = cases.First(c => c.Mutation == "missing" && c.Target == "query.qty");
        Assert.Equal("currency=EUR", q.Query);
    }

    [Fact]
    public void Volume_is_respected_including_combinations()
    {
        Assert.Equal(5, FuzzGenerator.Generate(Seed, new FuzzOptions { Volume = 5 }).Count());
        var many = FuzzGenerator.Generate(Seed, new FuzzOptions { Volume = 400, HugeStringLength = 10 }).ToList();
        Assert.Equal(400, many.Count);
        Assert.Contains(many, c => c.Mutation.Contains('+'));
    }

    [Fact]
    public void Mutation_filter_limits_families()
    {
        var cases = FuzzGenerator.Generate(Seed, new FuzzOptions { Volume = 1000, Mutations = ["null"] }).ToList();
        Assert.NotEmpty(cases);
        Assert.All(cases, c => Assert.StartsWith("null", c.Mutation));
    }

    [Fact]
    public void Get_without_body_mutates_query_only()
    {
        var cases = FuzzGenerator.Generate(new FuzzSeed("GET", "/s", "q=lamp&page=2", null, null), new FuzzOptions { Volume = 1000, HugeStringLength = 10 }).ToList();
        Assert.NotEmpty(cases);
        Assert.All(cases, c => Assert.StartsWith("query", c.Target));
    }
}

public class BodyCodecTests
{
    [Fact]
    public void Roundtrips_and_handles_empty()
    {
        var data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"a\":1},", 1000)));
        var packed = BodyCodec.Compress(data)!;
        Assert.True(packed.Length < data.Length / 10);
        Assert.Equal(data, BodyCodec.Decompress(packed));
        Assert.Null(BodyCodec.Compress([]));
        Assert.Empty(BodyCodec.Decompress(null));
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    public void Decodes_content_encodings(string encoding)
    {
        var data = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        using var ms = new MemoryStream();
        using (Stream s = encoding switch
        {
            "gzip" => new GZipStream(ms, CompressionLevel.Fastest, true),
            "br" => new BrotliStream(ms, CompressionLevel.Fastest, true),
            _ => new ZLibStream(ms, CompressionLevel.Fastest, true),
        }) s.Write(data);
        var decoded = BodyCodec.DecodeContent(ms.ToArray(), encoding, 1 << 20, out var ok);
        Assert.True(ok);
        Assert.Equal(data, decoded);
    }

    [Fact]
    public void Unknown_encoding_is_passed_through()
    {
        var decoded = BodyCodec.DecodeContent(new byte[] { 1, 2 }, "zstd", 100, out var ok);
        Assert.False(ok);
        Assert.Equal(new byte[] { 1, 2 }, decoded);
    }
}
