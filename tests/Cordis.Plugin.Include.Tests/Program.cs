using Harness.Cordis.Core;
using Harness.Cordis.Plugin.Loader;

namespace Harness.Cordis.Plugin.Include.Tests;

/// <summary>Zero-dependency console runner for the Include port tests.</summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    /// <summary>Run all tests; exit 0 only when every test passes.</summary>
    public static async Task<int> Main()
    {
        Run("yaml: nested map and list parse", YamlTests.NestedMapAndList);
        Run("yaml: list of maps", YamlTests.ListOfMaps);
        Run("yaml: comments and quotes", YamlTests.CommentsAndQuotes);
        Run("yaml: !!js marker becomes an expression", YamlTests.JsExprMarker);
        Run("yaml: scalar kinds", YamlTests.ScalarKinds);

        Run("expr: literals and env lookup", ExprTests.LiteralsAndEnv);
        Run("expr: ternary", ExprTests.Ternary);
        Run("expr: and/or/not", ExprTests.Logic);
        Run("expr: string concatenation", ExprTests.Concat);
        Run("expr: list and member access", ExprTests.ListAndMember);
        Run("expr: unsupported call fails loud", ExprTests.UnsupportedCallFails);
        Run("expr: unsupported identifier fails loud", ExprTests.UnsupportedIdentifierFails);

        Run("patch: insert into group", PatchTests.InsertIntoGroup);
        Run("patch: inserted row is patchable by a later patch", PatchTests.InsertThenPatchSameList);
        Run("patch: name mismatch skips with warning", PatchTests.NameMismatchSkips);
        Run("patch: overrides config and disabled", PatchTests.OverridesConfigAndDisabled);
        Run("patch: missing target skips with warning", PatchTests.MissingTargetSkips);

        await RunAsync("include: mounts rows from a yaml file", IncludeTests.MountsRowsFromFile);
        await RunAsync("include: runtime patch layer changes config", IncludeTests.PatchLayerChangesConfig);
        await RunAsync("include: refresh picks up file changes", IncludeTests.RefreshPicksUpFileChanges);
        await RunAsync("include: initial content seeds a missing file", IncludeTests.InitialSeedsMissingFile);
        await RunAsync("include: group row mounts nested children", IncludeTests.GroupRowMountsChildren);
        await RunAsync("include: disabled expression keeps a row unmounted", IncludeTests.DisabledExpression);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"  PASS {name}");
            _passed++;
        }
        catch (Exception error)
        {
            Console.WriteLine($"  FAIL {name}: {error.Message}");
            _failed++;
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"  PASS {name}");
            _passed++;
        }
        catch (Exception error)
        {
            Console.WriteLine($"  FAIL {name}: {error.Message}");
            _failed++;
        }
    }
}

/// <summary>Minimal assertion helpers.</summary>
public static class Assert
{
    /// <summary>Assert that <paramref name="condition"/> holds.</summary>
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"expected true: {message}");
    }

    /// <summary>Assert reference or value equality.</summary>
    public static void Equal(object? expected, object? actual)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
        }
    }

    /// <summary>Assert that <paramref name="action"/> throws <typeparamref name="T"/>.</summary>
    public static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{message}: expected {typeof(T).Name}, got {error.GetType().Name}");
        }
        throw new InvalidOperationException($"{message}: expected {typeof(T).Name}, nothing was thrown");
    }
}

internal static class YamlTests
{
    public static void NestedMapAndList()
    {
        var parsed = YamlSubset.Parse("root:\n  child: x\n  items:\n    - a\n    - b\n");
        Assert.True(parsed is Dictionary<string, object?>, "top level must be a map");
        var map = (Dictionary<string, object?>)parsed!;
        var root = map["root"] as Dictionary<string, object?>;
        Assert.True(root is not null, "root must be a nested map");
        Assert.Equal("x", root!["child"]);
        var items = root["items"] as List<object?>;
        Assert.True(items is { Count: 2 }, "items must be a two-element list");
        Assert.Equal("a", items![0]);
        Assert.Equal("b", items[1]);
    }

    public static void ListOfMaps()
    {
        var parsed = YamlSubset.Parse("- name: one\n  config:\n    value: 1\n- name: two\n");
        Assert.True(parsed is List<object?> { Count: 2 }, "top level must be a two-row list");
        var first = ((List<object?>)parsed!)[0] as Dictionary<string, object?>;
        Assert.True(first is not null, "first row must be a map");
        Assert.Equal("one", first!["name"]);
        var config = first["config"] as Dictionary<string, object?>;
        Assert.True(config is not null, "config must be a map");
        Assert.Equal(1L, config!["value"]);
    }

    public static void CommentsAndQuotes()
    {
        var parsed = YamlSubset.Parse("# leading comment\na: \"hello world\"  # trailing\nb: 'literal # hash'\n");
        var map = (Dictionary<string, object?>)parsed!;
        Assert.Equal("hello world", map["a"]);
        Assert.Equal("literal # hash", map["b"]);
    }

    public static void JsExprMarker()
    {
        var parsed = YamlSubset.Parse("disabled: !!js $env.FLAG\n");
        var map = (Dictionary<string, object?>)parsed!;
        Assert.True(map["disabled"] is ConfigExpression, "!!js must parse as a ConfigExpression");
        Assert.Equal("!!js $env.FLAG", ((ConfigExpression)map["disabled"]!).ToString());
    }

    public static void ScalarKinds()
    {
        var parsed = YamlSubset.Parse("a: null\nb: true\nc: 3.5\nd: []\ne: {}\n");
        var map = (Dictionary<string, object?>)parsed!;
        Assert.Equal(null, map["a"]);
        Assert.Equal(true, map["b"]);
        Assert.Equal(3.5, map["c"]);
        Assert.True(map["d"] is List<object?> { Count: 0 }, "[] must parse as an empty list");
        Assert.True(map["e"] is Dictionary<string, object?> { Count: 0 }, "{} must parse as an empty map");
    }
}

internal static class ExprTests
{
    public static void LiteralsAndEnv()
    {
        Environment.SetEnvironmentVariable("DSH_INCLUDE_TEST_FLAG", "yes");
        Assert.Equal("yes", new ConfigExpression("$env.DSH_INCLUDE_TEST_FLAG").Evaluate());
        Assert.Equal(42L, new ConfigExpression("42").Evaluate());
        Assert.Equal(true, new ConfigExpression("true").Evaluate());
        Assert.Equal(null, new ConfigExpression("null").Evaluate());
        Assert.Equal("plain", new ConfigExpression("'plain'").Evaluate());
    }

    public static void Ternary()
    {
        Assert.Equal("a", new ConfigExpression("true ? 'a' : 'b'").Evaluate());
        Assert.Equal("b", new ConfigExpression("false ? 'a' : 'b'").Evaluate());
    }

    public static void Logic()
    {
        Assert.Equal(true, new ConfigExpression("true || false").Evaluate());
        Assert.Equal(false, new ConfigExpression("true && false").Evaluate());
        Assert.Equal(true, new ConfigExpression("!false").Evaluate());
    }

    public static void Concat()
    {
        Assert.Equal("ab", new ConfigExpression("'a' + 'b'").Evaluate());
        Assert.Equal(5L, new ConfigExpression("2 + 3").Evaluate());
    }

    public static void ListAndMember()
    {
        var list = new ConfigExpression("['x', 'y']").Evaluate() as List<object?>;
        Assert.True(list is { Count: 2 }, "list expression must evaluate to a two-element list");
        Assert.Equal("y", new ConfigExpression("['x', 'y'][1]").Evaluate());
    }

    public static void UnsupportedCallFails()
    {
        Assert.Throws<ConfigExpressionException>(() => new ConfigExpression("foo()"), "function calls must fail loud");
    }

    public static void UnsupportedIdentifierFails()
    {
        Assert.Throws<ConfigExpressionException>(() => new ConfigExpression("foo"), "bare identifiers must fail loud");
    }
}

internal static class PatchTests
{
    private static Dictionary<string, object?> Row(string id, string name) => new(StringComparer.Ordinal)
    {
        ["id"] = id,
        ["name"] = name,
    };

    private static Dictionary<string, object?> Patch(params (string Key, object? Value)[] fields)
    {
        var patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in fields) patch[key] = value;
        return patch;
    }

    public static void InsertIntoGroup()
    {
        var data = new List<object?>
        {
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = "g1",
                ["name"] = "group-test",
                ["group"] = true,
                ["config"] = new List<object?> { Row("c1", "one") },
            },
        };
        var patches = new List<object?> { Patch(("id", "g1"), ("insert", new List<object?> { Row("c2", "two") })) };
        var warnings = new List<string>();
        var result = EntryPatches.Apply(data, patches, warnings.Add);
        var group = (Dictionary<string, object?>)result[0];
        var children = (List<object?>)group["config"]!;
        Assert.Equal(2, children.Count);
        Assert.Equal("c2", ((Dictionary<string, object?>)children[1])["id"]);
        Assert.Equal(0, warnings.Count);
    }

    public static void InsertThenPatchSameList()
    {
        var data = new List<object?>();
        var patches = new List<object?>
        {
            Patch(("insert", new List<object?> { Row("c2", "two") })),
            Patch(("id", "c2"), ("config", new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = "x" })),
        };
        var result = EntryPatches.Apply(data, patches, _ => { });
        var inserted = (Dictionary<string, object?>)result[0];
        var config = (Dictionary<string, object?>)inserted["config"]!;
        Assert.Equal("x", config["value"]);
    }

    public static void NameMismatchSkips()
    {
        var data = new List<object?> { Row("c1", "one") };
        var patches = new List<object?> { Patch(("id", "c1"), ("name", "WRONG"), ("config", "nope")) };
        var warnings = new List<string>();
        var result = EntryPatches.Apply(data, patches, warnings.Add);
        var row = (Dictionary<string, object?>)result[0];
        Assert.True(!row.ContainsKey("config"), "a name-mismatched patch must not apply");
        Assert.Equal(1, warnings.Count);
    }

    public static void OverridesConfigAndDisabled()
    {
        var data = new List<object?> { Row("c1", "one") };
        var patches = new List<object?> { Patch(("id", "c1"), ("config", "fresh"), ("disabled", true)) };
        var result = EntryPatches.Apply(data, patches, _ => { });
        var row = (Dictionary<string, object?>)result[0];
        Assert.Equal("fresh", row["config"]);
        Assert.Equal(true, row["disabled"]);
    }

    public static void MissingTargetSkips()
    {
        var data = new List<object?> { Row("c1", "one") };
        var patches = new List<object?> { Patch(("id", "missing"), ("config", "nope")) };
        var warnings = new List<string>();
        var result = EntryPatches.Apply(data, patches, warnings.Add);
        Assert.Equal(1, result.Count);
        Assert.Equal(1, warnings.Count);
    }
}

internal static class IncludeTests
{
    private static string TempConfig(string content)
    {
        var file = Path.Combine(Path.GetTempPath(), $"dsh-include-{Guid.NewGuid():N}.yml");
        File.WriteAllText(file, content);
        return file;
    }

    private static (Context Ctx, global::Harness.Cordis.Plugin.Loader.Loader Loader) Boot()
    {
        var ctx = new Context();
        var loader = new global::Harness.Cordis.Plugin.Loader.Loader(ctx);
        loader.Catalog.RegisterType("probe", typeof(ProbePlugin));
        return (ctx, loader);
    }

    public static async Task MountsRowsFromFile()
    {
        ProbePlugin.Seen = null;
        var file = TempConfig("- name: probe\n  config:\n    value: hello\n");
        try
        {
            var (ctx, _) = Boot();
            var include = new Include(ctx, new IncludeConfig { Path = file });
            await include.ApplyFileAsync();
            Assert.True(ctx.Get<ProbePlugin>("probe") is not null, "the probe service must be mounted");
            Assert.Equal("hello", ProbePlugin.Seen);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static async Task PatchLayerChangesConfig()
    {
        ProbePlugin.Seen = null;
        var file = TempConfig("- id: svc\n  name: probe\n  config:\n    value: before\n");
        try
        {
            var (ctx, _) = Boot();
            var include = new Include(ctx, new IncludeConfig
            {
                Path = file,
                Patches = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = "svc",
                        ["config"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = "after" },
                    },
                },
            });
            await include.ApplyFileAsync();
            Assert.Equal("after", ProbePlugin.Seen);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static async Task RefreshPicksUpFileChanges()
    {
        ProbePlugin.Seen = null;
        var file = TempConfig("- id: svc\n  name: probe\n  config:\n    value: one\n");
        try
        {
            var (ctx, _) = Boot();
            var include = new Include(ctx, new IncludeConfig { Path = file });
            await include.ApplyFileAsync();
            Assert.Equal("one", ProbePlugin.Seen);
            File.WriteAllText(file, "- id: svc\n  name: probe\n  config:\n    value: two\n");
            await include.RefreshAsync();
            Assert.Equal("two", ProbePlugin.Seen);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static async Task InitialSeedsMissingFile()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dsh-include-{Guid.NewGuid():N}.yml");
        try
        {
            var (ctx, _) = Boot();
            var include = new Include(ctx, new IncludeConfig
            {
                Path = file,
                Initial = new List<object?>
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["id"] = "svc",
                        ["name"] = "probe",
                        ["config"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = "seed" },
                    },
                },
            });
            await include.ApplyFileAsync();
            Assert.True(File.Exists(file), "the initial content must seed the file");
            Assert.Equal("seed", ProbePlugin.Seen);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static async Task GroupRowMountsChildren()
    {
        var file = TempConfig("- id: g1\n  name: group-test\n  group: true\n  config:\n    - name: probe\n      config:\n        value: child\n");
        try
        {
            var (ctx, _) = Boot();
            var include = new Include(ctx, new IncludeConfig { Path = file });
            await include.ApplyFileAsync();
            Assert.True(ctx.Get<ProbePlugin>("probe") is not null, "the nested child plugin must be mounted");
            Assert.Equal("child", ProbePlugin.Seen);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static async Task DisabledExpression()
    {
        ProbePlugin.Seen = null;
        Environment.SetEnvironmentVariable("DSH_INCLUDE_DISABLE", "true");
        var file = TempConfig("- id: svc\n  name: probe\n  disabled: !!js $env.DSH_INCLUDE_DISABLE\n  config:\n    value: hidden\n");
        try
        {
            var (ctx, _) = Boot();
            var include = new Include(ctx, new IncludeConfig { Path = file });
            await include.ApplyFileAsync();
            Assert.True(ctx.Get<ProbePlugin>("probe") is null, "a disabled row must not mount");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
