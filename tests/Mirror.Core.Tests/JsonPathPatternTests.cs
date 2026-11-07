using Mirror.Core.Paths;

namespace Mirror.Core.Tests;

public class JsonPathPatternTests
{
    private static PathSegment P(string n) => PathSegment.Prop(n);
    private static PathSegment I(int i) => PathSegment.Idx(i);

    [Theory]
    [InlineData("$")]
    [InlineData("$.a.b")]
    [InlineData("$['weird key'].x")]
    [InlineData("$.items[*].price")]
    [InlineData("$..id")]
    [InlineData("$.a.*")]
    [InlineData("$.a[3]")]
    [InlineData("meta.id")]
    public void Parses_supported_syntax(string path) =>
        Assert.True(JsonPathPattern.TryParse(path, out _, out var err), err);

    [Theory]
    [InlineData("")]
    [InlineData("$.")]
    [InlineData("$.a[")]
    [InlineData("$.a[?(@.x)]")]
    [InlineData("$.a[-1]")]
    public void Rejects_invalid_syntax(string path) =>
        Assert.False(JsonPathPattern.TryParse(path, out _, out _));

    [Fact]
    public void Exact_property_chain_matches_only_that_path()
    {
        var p = JsonPathPattern.Parse("$.a.b");
        Assert.True(p.Matches([P("a"), P("b")]));
        Assert.False(p.Matches([P("a")]));
        Assert.False(p.Matches([P("a"), P("c")]));
        Assert.False(p.Matches([P("a"), P("b"), P("c")]));
    }

    [Fact]
    public void Index_wildcard_matches_indices_and_keyed_items()
    {
        var p = JsonPathPattern.Parse("$.items[*].price");
        Assert.True(p.Matches([P("items"), I(0), P("price")]));
        Assert.True(p.Matches([P("items"), I(17), P("price")]));
        Assert.True(p.Matches([P("items"), PathSegment.Keyed("sku", "A1", 3), P("price")]));
        Assert.False(p.Matches([P("items"), P("x"), P("price")]));
    }

    [Fact]
    public void Specific_index_matches_only_that_index()
    {
        var p = JsonPathPattern.Parse("$.a[1]");
        Assert.True(p.Matches([P("a"), I(1)]));
        Assert.False(p.Matches([P("a"), I(0)]));
    }

    [Fact]
    public void Recursive_descent_matches_at_any_depth()
    {
        var p = JsonPathPattern.Parse("$..requestId");
        Assert.True(p.Matches([P("requestId")]));
        Assert.True(p.Matches([P("meta"), P("requestId")]));
        Assert.True(p.Matches([P("a"), I(4), P("b"), P("requestId")]));
        Assert.False(p.Matches([P("a"), P("requestIdX")]));
    }

    [Fact]
    public void Star_child_matches_any_single_segment()
    {
        var p = JsonPathPattern.Parse("$.a.*");
        Assert.True(p.Matches([P("a"), P("x")]));
        Assert.True(p.Matches([P("a"), I(2)]));
        Assert.False(p.Matches([P("a"), P("x"), P("y")]));
    }

    [Fact]
    public void Quoted_names_and_relative_paths()
    {
        Assert.True(JsonPathPattern.Parse("$['a.b']").Matches([P("a.b")]));
        var rel = JsonPathPattern.Parse("meta.id");
        Assert.True(rel.IsSimplePropertyChain);
        Assert.Equal(["meta", "id"], rel.PropertyChain);
    }

    [Fact]
    public void Root_pattern_matches_root()
    {
        var m = new PathMatcher<int>([(JsonPathPattern.Parse("$"), 1)]);
        Assert.True(m.Root.HasMatches);
    }

    [Fact]
    public void Formats_concrete_paths()
    {
        var n = PathNode.Root.Child(P("items")).Child(PathSegment.Keyed("sku", "LMP-1", 4)).Child(P("price tag"));
        Assert.Equal("$.items[sku=LMP-1]['price tag']", n.ToString());
        Assert.Equal("$.items[*]['price tag']", n.Normalized());
    }
}
