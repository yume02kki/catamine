using Mirror.Core.Diffing;

namespace Mirror.Core.Tests;

public class DiffEngineTests
{
    [Fact]
    public void Identical_documents_are_equal_regardless_of_key_order()
    {
        var r = T.Diff("""{"a":1,"b":{"c":[1,2,3],"d":"x"}}""", """{"b":{"d":"x","c":[1,2,3]},"a":1}""");
        Assert.True(r.IsEqual);
        Assert.Equal(DiffKinds.None, r.Kinds);
        Assert.Equal(0, r.Signature);
    }

    [Fact]
    public void Changed_value_reports_path_and_both_values()
    {
        var d = T.Diff("""{"user":{"name":"Ann"}}""", """{"user":{"name":"Bob"}}""").Single();
        Assert.Equal("changed", d.Kind);
        Assert.Equal("$.user.name", d.Path);
        Assert.Equal("\"Ann\"", d.Main);
        Assert.Equal("\"Bob\"", d.Dev);
    }

    [Fact]
    public void Added_and_removed_fields()
    {
        var r = T.Diff("""{"a":1,"gone":true}""", """{"a":1,"new":[1]}""");
        Assert.Equal(["removed $.gone", "added $.new"], r.Paths());
        Assert.Null(r.Differences[0].Dev);
        Assert.Null(r.Differences[1].Main);
        Assert.Equal(1, r.Summary.Added);
        Assert.Equal(1, r.Summary.Removed);
    }

    [Theory]
    [InlineData("1", "\"1\"")]
    [InlineData("{}", "[]")]
    [InlineData("null", "0")]
    [InlineData("true", "\"true\"")]
    [InlineData("[1]", "1")]
    public void Type_changes_are_detected(string main, string dev)
    {
        var d = T.Diff($$"""{"v":{{main}}}""", $$"""{"v":{{dev}}}""").Single();
        Assert.Equal("type_changed", d.Kind);
        Assert.Equal("$.v", d.Path);
    }

    [Fact]
    public void Bool_flip_is_a_change_not_a_type_change() =>
        Assert.Equal("changed", T.Diff("""{"v":true}""", """{"v":false}""").Single().Kind);

    [Fact]
    public void Numbers_compare_by_value()
    {
        Assert.True(T.Diff("""{"v":1}""", """{"v":1.0}""").IsEqual);
        Assert.True(T.Diff("""{"v":1e2}""", """{"v":100}""").IsEqual);
        Assert.False(T.Diff("""{"v":49.99}""", """{"v":49.9}""").IsEqual);
    }

    [Fact]
    public void Array_length_and_extra_items_are_reported()
    {
        var r = T.Diff("""{"items":[1,2,3]}""", """{"items":[1,2]}""");
        Assert.Contains("array_length $.items", r.Paths());
        Assert.Contains("removed $.items[2]", r.Paths());
        var len = Assert.Single(r.Summary.Arrays);
        Assert.Equal((3, 2), (len.Main, len.Dev));
        Assert.True(r.Kinds.HasFlag(DiffKinds.ArrayLength));
    }

    [Fact]
    public void Arrays_compare_by_index_by_default()
    {
        var r = T.Diff("""[{"id":1},{"id":2}]""", """[{"id":2},{"id":1}]""");
        Assert.Equal(["changed $[0].id", "changed $[1].id"], r.Paths());
    }

    [Fact]
    public void Status_difference_is_reported()
    {
        var r = T.Diff("{}", "{}", mainStatus: 200, devStatus: 404);
        var d = r.Single();
        Assert.Equal(("status", "200", "404"), (d.Kind, d.Main, d.Dev));
        Assert.True(r.Kinds.HasFlag(DiffKinds.Status));
    }

    [Fact]
    public void Selected_headers_are_compared_case_insensitively()
    {
        var settings = new DiffSettings { ComparedHeaders = ["Content-Type", "cache-control"] };
        var r = T.Diff("{}", "{}", settings: settings,
            mainHeaders: new() { ["content-type"] = "application/json; charset=utf-8", ["Cache-Control"] = "max-age=60", ["x-other"] = "1" },
            devHeaders: new() { ["Content-Type"] = "application/json;charset=utf-8", ["cache-control"] = "no-store", ["x-other"] = "2" });
        var d = r.Single();
        Assert.Equal(("header", "header:cache-control"), (d.Kind, d.Path));
        Assert.Equal(["cache-control"], r.Summary.Headers);
    }

    [Fact]
    public void Non_json_bodies_are_compared_as_text()
    {
        Assert.True(T.Diff("<html/>", "<html/>").IsEqual);
        var r = T.Diff("""{"a":1}""", "<html>502 Bad Gateway</html>");
        Assert.Equal("body", r.Single().Kind);
        Assert.Equal("Dev body is not JSON", r.Note);
    }

    [Fact]
    public void Empty_bodies_are_equal()
    {
        Assert.True(T.Diff("", "").IsEqual);
        Assert.Equal("body", T.Diff("", """{"a":1}""").Single().Kind);
    }

    [Fact]
    public void Differences_are_capped_but_counted()
    {
        var main = "[" + string.Join(",", Enumerable.Range(0, 100)) + "]";
        var dev = "[" + string.Join(",", Enumerable.Range(1000, 100)) + "]";
        var r = T.Diff(main, dev, settings: new DiffSettings { MaxDifferences = 10 });
        Assert.Equal(10, r.Differences.Count);
        Assert.Equal(100, r.TotalCount);
        Assert.True(r.Truncated);
        Assert.True(r.Summary.Truncated);
    }

    [Fact]
    public void Oversized_bodies_fall_back_to_byte_comparison()
    {
        var r = T.Diff("""{"a":1}""", """{"a":2}""", settings: new DiffSettings { MaxDiffBodyBytes = 4 });
        Assert.Equal("body", r.Single().Kind);
        Assert.NotNull(r.Note);
    }

    [Fact]
    public void Signature_is_stable_and_ignores_indices()
    {
        var a = T.Diff("""{"items":[{"p":1},{"p":2}]}""", """{"items":[{"p":9},{"p":2}]}""");
        var b = T.Diff("""{"items":[{"p":1},{"p":2}]}""", """{"items":[{"p":1},{"p":7}]}""");
        var c = T.Diff("""{"items":[{"q":1}]}""", """{"items":[{"q":2}]}""");
        Assert.NotEqual(0, a.Signature);
        Assert.Equal(a.Signature, b.Signature);
        Assert.NotEqual(a.Signature, c.Signature);
    }

    [Fact]
    public void Summary_keeps_top_differences_shortened()
    {
        var big = new string('x', 500);
        var r = T.Diff($$"""{"a":"{{big}}"}""", """{"a":"y"}""");
        var top = Assert.Single(r.Summary.Top);
        Assert.True(top.Main!.Length <= 81);
        Assert.Equal(301, r.Differences[0].Main!.Length); // snippet limit 300 + ellipsis
    }

    [Theory]
    [InlineData(DevOutcome.Timeout, 200, 0, false, Verdict.DevTimeout)]
    [InlineData(DevOutcome.Error, 200, 0, false, Verdict.DevError)]
    [InlineData(DevOutcome.Ok, 200, 503, true, Verdict.DevError)]
    [InlineData(DevOutcome.Ok, 503, 503, false, Verdict.Match)]
    [InlineData(DevOutcome.Ok, 200, 200, true, Verdict.Mismatch)]
    [InlineData(DevOutcome.Ok, 200, 200, false, Verdict.Match)]
    public void Verdicts(DevOutcome outcome, int main, int dev, bool differs, Verdict expected)
    {
        var diff = differs ? T.Diff("1", "2") : T.Diff("1", "1");
        Assert.Equal(expected, DiffEngine.DecideVerdict(outcome, main, dev, diff));
    }

    [Fact]
    public void Deeply_nested_and_large_documents_diff_quickly()
    {
        var items = string.Join(",", Enumerable.Range(0, 20000).Select(i => $$"""{"id":{{i}},"name":"item {{i}}","tags":["a","b"],"price":{{i}}.5}"""));
        var devItems = string.Join(",", Enumerable.Range(0, 20000).Select(i => $$"""{"id":{{i}},"name":"item {{i}}","tags":["a","b"],"price":{{(i == 1234 ? 0 : i)}}.5}"""));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = T.Diff($$"""{"items":[{{items}}]}""", $$"""{"items":[{{devItems}}]}""");
        sw.Stop();
        Assert.Equal("$.items[1234].price", r.Single().Path);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
