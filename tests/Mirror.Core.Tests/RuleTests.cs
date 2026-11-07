using System.Text.Json;
using Mirror.Core.Rules;

namespace Mirror.Core.Tests;

public class PrimaryKeyRuleTests
{
    [Fact]
    public void Matches_items_by_key_instead_of_index()
    {
        var rules = T.Rules(("PrimaryKey", new { path = "$.items", key = "sku" }));
        var r = T.Diff(
            """{"items":[{"sku":"A","p":1},{"sku":"B","p":2}]}""",
            """{"items":[{"sku":"B","p":2},{"sku":"A","p":1}]}""", rules);
        var d = r.Single();
        Assert.Equal("order", d.Kind);
        Assert.Equal("$.items", d.Path);
    }

    [Fact]
    public void Reports_keyed_paths_for_changes_and_missing_items()
    {
        var rules = T.Rules(("PrimaryKey", new { path = "$.items", key = "sku" }));
        var r = T.Diff(
            """{"items":[{"sku":"A","p":1},{"sku":"B","p":2},{"sku":"C","p":3}]}""",
            """{"items":[{"sku":"A","p":9},{"sku":"C","p":3},{"sku":"D","p":4}]}""", rules);
        Assert.Contains("changed $.items[sku=A].p", r.Paths());
        Assert.Contains("removed $.items[sku=B]", r.Paths());
        Assert.Contains("added $.items[sku=D]", r.Paths());
        Assert.DoesNotContain(r.Differences, d => d.Kind == "order");
    }

    [Fact]
    public void Supports_nested_arrays_and_nested_key_fields()
    {
        var rules = T.Rules(
            ("PrimaryKey", new { path = "$.orders", key = "meta.id" }),
            ("PrimaryKey", new { path = "$.orders[*].lines", key = "lineNo" }));
        var r = T.Diff(
            """{"orders":[{"meta":{"id":7},"lines":[{"lineNo":1,"q":1},{"lineNo":2,"q":5}]}]}""",
            """{"orders":[{"meta":{"id":7},"lines":[{"lineNo":2,"q":6},{"lineNo":1,"q":1}]}]}""", rules);
        Assert.Contains("changed $.orders[meta.id=7].lines[lineNo=2].q", r.Paths());
        Assert.Contains("order $.orders[meta.id=7].lines", r.Paths());
    }

    [Fact]
    public void Composite_keys()
    {
        var rules = T.Rules(("PrimaryKey", new { path = "$", keys = new[] { "a", "b" } }));
        var r = T.Diff("""[{"a":1,"b":1,"v":0},{"a":1,"b":2,"v":0}]""", """[{"a":1,"b":2,"v":0},{"a":1,"b":1,"v":1}]""", rules);
        Assert.Contains("changed $[a+b=1|1].v", r.Paths());
    }

    [Fact]
    public void Items_without_unique_keys_fall_back_to_positional_comparison()
    {
        var rules = T.Rules(("PrimaryKey", new { path = "$", key = "id" }));
        var r = T.Diff("""[{"id":1,"v":1},{"v":"x"},{"id":2,"v":1},{"id":2,"v":2}]""", """[{"id":1,"v":1},{"v":"y"},{"id":2,"v":1},{"id":2,"v":3}]""", rules);
        Assert.Contains("changed $[1].v", r.Paths());
        Assert.Contains("changed $[3].v", r.Paths());
    }

    [Fact]
    public void Combined_with_ignore_order_suppresses_order_difference()
    {
        var rules = T.Rules(("PrimaryKey", new { path = "$.items", key = "id" }), ("IgnoreOrder", new { path = "$.items" }));
        Assert.True(T.Diff("""{"items":[{"id":1},{"id":2}]}""", """{"items":[{"id":2},{"id":1}]}""", rules).IsEqual);
    }

    [Fact]
    public void Validation_requires_plain_key_path()
    {
        Assert.NotEmpty(RuleRegistry.Validate("PrimaryKey", Json(new { path = "$.items" })));
        Assert.NotEmpty(RuleRegistry.Validate("PrimaryKey", Json(new { path = "$.items", key = "a[*].b" })));
        Assert.Empty(RuleRegistry.Validate("PrimaryKey", Json(new { path = "$.items", key = "a.b" })));
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);
}

public class IgnoreFieldRuleTests
{
    [Fact]
    public void Ignores_exact_field_and_its_subtree()
    {
        var rules = T.Rules(("IgnoreField", new { path = "$.meta" }));
        Assert.True(T.Diff("""{"meta":{"t":1,"x":[1]},"a":1}""", """{"meta":{"t":2},"a":1}""", rules).IsEqual);
    }

    [Fact]
    public void Ignores_with_recursive_descent_everywhere()
    {
        var rules = T.Rules(("IgnoreField", new { paths = new[] { "$..requestId", "$..took_ms" } }));
        var r = T.Diff(
            """{"requestId":"a","data":[{"requestId":"b","v":1}],"took_ms":5}""",
            """{"requestId":"z","data":[{"requestId":"y","v":2}]}""", rules);
        Assert.Equal(["changed $.data[0].v"], r.Paths());
    }

    [Fact]
    public void Ignores_with_index_wildcard()
    {
        var rules = T.Rules(("IgnoreField", new { path = "$.items[*].updatedAt" }));
        var r = T.Diff("""{"items":[{"updatedAt":1,"v":1},{"updatedAt":2}]}""", """{"items":[{"updatedAt":3,"v":1},{"updatedAt":4}],"updatedAt":0}""", rules);
        Assert.Equal(["added $.updatedAt"], r.Paths());
    }

    [Fact]
    public void Ignored_fields_do_not_count_as_added_or_removed()
    {
        var rules = T.Rules(("IgnoreField", new { path = "$.debug" }));
        Assert.True(T.Diff("""{"a":1,"debug":{}}""", """{"a":1}""", rules).IsEqual);
        Assert.True(T.Diff("""{"a":1}""", """{"a":1,"debug":{}}""", rules).IsEqual);
    }

    [Fact]
    public void Ignored_array_elements_do_not_count()
    {
        var rules = T.Rules(("IgnoreField", new { path = "$.a[2]" }));
        var r = T.Diff("""{"a":[1,2,3]}""", """{"a":[1,2]}""", rules);
        Assert.Equal(["array_length $.a"], r.Paths());
    }

    [Fact]
    public void Requires_a_path() => Assert.NotEmpty(RuleRegistry.Validate("IgnoreField", JsonSerializer.SerializeToElement(new { })));
}

public class SearchAndReplaceRuleTests
{
    [Fact]
    public void Plain_text_replace_across_whole_body()
    {
        var rules = T.Rules(("SearchAndReplace", new { scope = "body", search = "dev.example.com", replace = "prod.example.com" }));
        Assert.True(T.Diff("""{"url":"https://prod.example.com/x"}""", """{"url":"https://dev.example.com/x"}""", rules).IsEqual);
    }

    [Fact]
    public void Regex_replace_across_whole_body_normalises_timestamps()
    {
        var rules = T.Rules(("SearchAndReplace", new { scope = "body", search = @"\d{4}-\d{2}-\d{2}T[0-9:.]+Z", replace = "<ts>", regex = true }));
        Assert.True(T.Diff("""{"a":"2026-01-01T10:00:00.123Z","b":["2026-02-02T00:00:00Z"]}""", """{"a":"2026-03-03T11:11:11.999Z","b":["2027-02-02T01:00:00Z"]}""", rules).IsEqual);
    }

    [Fact]
    public void Body_scope_can_rewrite_keys_and_structure_text()
    {
        var rules = T.Rules(("SearchAndReplace", new { scope = "body", search = "\"count_v2\"", replace = "\"count\"" }));
        Assert.True(T.Diff("""{"count":1}""", """{"count_v2":1}""", rules).IsEqual);
    }

    [Fact]
    public void Field_scope_replaces_only_in_matching_fields()
    {
        var rules = T.Rules(("SearchAndReplace", new { scope = "field", path = "$.items[*].id", search = "^dev-", replace = "", regex = true }));
        var r = T.Diff(
            """{"items":[{"id":"42","ref":"dev-1"}]}""",
            """{"items":[{"id":"dev-42","ref":"dev-2"}]}""", rules);
        Assert.Equal(["changed $.items[0].ref"], r.Paths());
    }

    [Fact]
    public void Field_scope_on_a_subtree_replaces_all_strings_below()
    {
        var rules = T.Rules(("SearchAndReplace", new { scope = "field", path = "$.links", search = "DEV", replace = "PROD", ignoreCase = true }));
        Assert.True(T.Diff("""{"links":{"a":"PROD/1","b":["prod/2"]}}""", """{"links":{"a":"dev/1","b":["Dev/2"]}}""", rules).IsEqual is false);
        var rules2 = T.Rules(("SearchAndReplace", new { scope = "field", path = "$.links", search = "dev", replace = "prod", ignoreCase = true }));
        Assert.True(T.Diff("""{"links":{"a":"prod/1","b":["prod/2"]}}""", """{"links":{"a":"DEV/1","b":["Dev/2"]}}""", rules2).IsEqual);
    }

    [Fact]
    public void Field_scope_on_root_works()
    {
        var rules = T.Rules(("SearchAndReplace", new { scope = "field", path = "$", search = "x", replace = "y" }));
        Assert.True(T.Diff("\"y\"", "\"x\"", rules).IsEqual);
    }

    [Fact]
    public void Invalid_regex_fails_validation()
    {
        var errors = RuleRegistry.Validate("SearchAndReplace", JsonSerializer.SerializeToElement(new { scope = "body", search = "([", regex = true }));
        Assert.Contains(errors, e => e.Contains("regex"));
    }

    [Fact]
    public void Field_scope_requires_path()
    {
        var errors = RuleRegistry.Validate("SearchAndReplace", JsonSerializer.SerializeToElement(new { scope = "field", search = "a" }));
        Assert.Contains(errors, e => e.Contains("path"));
    }
}

public class IgnoreOrderRuleTests
{
    [Fact]
    public void Arrays_compare_as_sets()
    {
        var rules = T.Rules(("IgnoreOrder", new { path = "$.tags" }));
        Assert.True(T.Diff("""{"tags":["a","b","c"]}""", """{"tags":["c","a","b"]}""", rules).IsEqual);
    }

    [Fact]
    public void Multiset_semantics_and_object_elements()
    {
        var rules = T.Rules(("IgnoreOrder", new { path = "$" }));
        Assert.True(T.Diff("""[{"a":1,"b":2},{"a":3}]""", """[{"a":3},{"b":2,"a":1}]""", rules).IsEqual);
        var r = T.Diff("""["a","a","b"]""", """["a","b","b"]""", rules);
        Assert.Equal(["changed $[1]"], r.Paths());
    }

    [Fact]
    public void Leftovers_are_diffed_field_by_field()
    {
        var rules = T.Rules(("IgnoreOrder", new { path = "$" }));
        var r = T.Diff("""[{"id":1,"v":1},{"id":2,"v":2}]""", """[{"id":2,"v":2},{"id":1,"v":5}]""", rules);
        Assert.Equal(["changed $[0].v"], r.Paths());
    }

    [Fact]
    public void Extra_elements_reported_as_added()
    {
        var rules = T.Rules(("IgnoreOrder", new { path = "$" }));
        var r = T.Diff("[1,2]", "[2,1,3]", rules);
        Assert.Contains("added $[2]", r.Paths());
        Assert.Contains("array_length $", r.Paths());
    }

    [Fact]
    public void Ignored_fields_inside_elements_do_not_break_set_matching()
    {
        var rules = T.Rules(("IgnoreOrder", new { path = "$" }), ("IgnoreField", new { path = "$[*].ts" }));
        Assert.True(T.Diff("""[{"id":1,"ts":1},{"id":2,"ts":2}]""", """[{"id":2,"ts":8},{"id":1,"ts":9}]""", rules).IsEqual);
    }

    [Fact]
    public void Wildcard_paths_apply_to_many_arrays()
    {
        var rules = T.Rules(("IgnoreOrder", new { path = "$.facets.*" }));
        Assert.True(T.Diff("""{"facets":{"a":[1,2],"b":[3,4]}}""", """{"facets":{"a":[2,1],"b":[4,3]}}""", rules).IsEqual);
    }
}

public class NumericToleranceRuleTests
{
    [Fact]
    public void Absolute_tolerance()
    {
        var rules = T.Rules(("NumericTolerance", new { path = "$..price", tolerance = 0.01 }));
        Assert.True(T.Diff("""{"a":{"price":49.99}}""", """{"a":{"price":50.0}}""", rules).IsEqual);
        Assert.False(T.Diff("""{"a":{"price":49.99}}""", """{"a":{"price":49.9}}""", rules).IsEqual);
    }

    [Fact]
    public void Relative_tolerance()
    {
        var rules = T.Rules(("NumericTolerance", new { path = "$.v", tolerance = 0.01, mode = "relative" }));
        Assert.True(T.Diff("""{"v":1000}""", """{"v":1009}""", rules).IsEqual);
        Assert.False(T.Diff("""{"v":1000}""", """{"v":1011}""", rules).IsEqual);
    }

    [Fact]
    public void Only_applies_to_matching_paths()
    {
        var rules = T.Rules(("NumericTolerance", new { path = "$.a", tolerance = 1 }));
        Assert.Equal(["changed $.b"], T.Diff("""{"a":1,"b":1}""", """{"a":2,"b":2}""", rules).Paths());
    }

    [Fact]
    public void Works_for_large_and_float_values()
    {
        var rules = T.Rules(("NumericTolerance", new { path = "$.v", tolerance = 1e292 }));
        Assert.True(T.Diff("""{"v":1e300}""", """{"v":1.000000001e300}""", rules).IsEqual);
    }

    [Fact]
    public void Negative_tolerance_is_rejected() =>
        Assert.NotEmpty(RuleRegistry.Validate("NumericTolerance", JsonSerializer.SerializeToElement(new { path = "$.v", tolerance = -1 })));
}

public class IgnoreHeaderRuleTests
{
    [Fact]
    public void Ignored_headers_are_not_compared()
    {
        var settings = new Mirror.Core.Diffing.DiffSettings { ComparedHeaders = ["content-type", "cache-control"] };
        var rules = T.Rules(("IgnoreHeader", new { headers = new[] { "Cache-Control" } }));
        var r = T.Diff("{}", "{}", rules, settings,
            mainHeaders: new() { ["cache-control"] = "a", ["content-type"] = "x" },
            devHeaders: new() { ["cache-control"] = "b", ["content-type"] = "y" });
        Assert.Equal(["header header:content-type"], r.Paths());
    }

    [Fact]
    public void Requires_headers() =>
        Assert.NotEmpty(RuleRegistry.Validate("IgnoreHeader", JsonSerializer.SerializeToElement(new { headers = Array.Empty<string>() })));
}

public class TypeCoercionRuleTests
{
    [Theory]
    [InlineData("1", "\"1\"")]
    [InlineData("\"1840\"", "1840")]
    [InlineData("1.50", "\"1.5\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("\"False\"", "false")]
    public void Coerces_equal_values(string main, string dev)
    {
        var rules = T.Rules(("TypeCoercion", new { path = "$.v" }));
        Assert.True(T.Diff($$"""{"v":{{main}}}""", $$"""{"v":{{dev}}}""", rules).IsEqual);
    }

    [Fact]
    public void Different_values_are_still_changes()
    {
        var rules = T.Rules(("TypeCoercion", new { path = "$.v" }));
        Assert.Equal("type_changed", T.Diff("""{"v":1}""", """{"v":"2"}""", rules).Single().Kind);
        Assert.Equal("type_changed", T.Diff("""{"v":null}""", """{"v":"null"}""", rules).Single().Kind);
    }

    [Fact]
    public void Without_path_applies_everywhere()
    {
        var rules = T.Rules(("TypeCoercion", new { }));
        Assert.True(T.Diff("""{"a":1,"b":{"c":[true]}}""", """{"a":"1","b":{"c":["true"]}}""", rules).IsEqual);
    }

    [Fact]
    public void Without_rule_type_change_is_reported() =>
        Assert.Equal("type_changed", T.Diff("""{"v":1}""", """{"v":"1"}""").Single().Kind);
}

public class RuleRegistryTests
{
    [Fact]
    public void All_built_in_types_are_registered_with_valid_examples()
    {
        var names = RuleRegistry.Types.Select(t => t.Type).ToList();
        foreach (var expected in new[] { "IgnoreField", "IgnoreHeader", "IgnoreOrder", "NumericTolerance", "PrimaryKey", "SearchAndReplace", "TypeCoercion" })
            Assert.Contains(expected, names);
        foreach (var t in RuleRegistry.Types)
            Assert.Empty(RuleRegistry.Validate(t.Type, t.ExampleConfig));
    }

    [Fact]
    public void Unknown_type_and_unknown_fields_are_rejected()
    {
        Assert.NotEmpty(RuleRegistry.Validate("Nope", JsonSerializer.SerializeToElement(new { })));
        Assert.NotEmpty(RuleRegistry.Validate("IgnoreOrder", JsonSerializer.SerializeToElement(new { path = "$", bogus = 1 })));
    }

    [Fact]
    public void Bad_rules_are_skipped_and_reported_not_thrown()
    {
        var set = CompiledRuleSet.Build(3, [
            T.Rule("IgnoreField", new { path = "$[" }, 0),
            T.Rule("IgnoreField", new { path = "$.ok" }, 1),
            T.Rule("Nope", new { }, 2),
        ]);
        Assert.Single(set.Rules);
        Assert.Equal(2, set.Errors.Count);
        Assert.Equal(3, set.Version);
    }

    [Fact]
    public void Disabled_rules_are_not_applied()
    {
        var def = T.Rule("IgnoreField", new { path = "$.a" }) with { Enabled = false };
        var set = CompiledRuleSet.Build(1, [def]);
        Assert.False(T.Diff("""{"a":1}""", """{"a":2}""", set).IsEqual);
    }

    [Fact]
    public void Rules_run_in_order_within_text_stage()
    {
        var set = T.Rules(
            ("SearchAndReplace", new { scope = "body", search = "a", replace = "b" }),
            ("SearchAndReplace", new { scope = "body", search = "b", replace = "c" }));
        Assert.True(T.Diff("\"c\"", "\"a\"", set).IsEqual);
    }

    private sealed class AlwaysEqualRule : IDiffRule
    {
        public string Type => "TestOnlyIgnoreAll";
        public void Configure(Mirror.Core.Diffing.CompareOptionsBuilder options) =>
            options.Add(Mirror.Core.Paths.JsonPathPattern.Parse("$"), new Mirror.Core.Diffing.IgnoreDirective());
    }

    public sealed record EmptyConfig;

    [Fact]
    public void New_rule_types_can_be_registered()
    {
        RuleRegistry.Register<EmptyConfig>("TestOnlyIgnoreAll", "test", _ => new AlwaysEqualRule(), null, new EmptyConfig());
        var set = CompiledRuleSet.Build(1, [T.Rule("TestOnlyIgnoreAll", new { })]);
        Assert.True(T.Diff("""{"a":1}""", """[2]""", set).IsEqual);
    }
}
