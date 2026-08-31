using Cordis.Core;
using Cordis.Schemastery;

namespace Dsh.Settings.Tests;

/// <summary>
/// Settings seam tests (ported from packages/settings/{settings,settings-file}): registration and
/// installSection, read-through commit semantics with validation, the JSON file provider, and
/// secret redaction.
/// </summary>
public static class SettingsTests
{
    public static async Task InstallSectionAttachesDefaultEntry()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            Func<Dictionary<string, object?>>? source = null;
            var changeCount = 0;
            provider.InstallSection(
                ctx,
                "llm-test",
                Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Default("deepseek-chat") }),
                new Dictionary<string, object?> { ["model"] = "entry-model" },
                new SettingsSectionHooks<Dictionary<string, object?>>
                {
                    SetSource = thunk => source = thunk,
                    OnChange = () => changeCount += 1,
                });
            Assert.NotNull(source, "installSection must set the source hook");
            Assert.Equal("entry-model", source!()["model"]);
            Assert.True(changeCount >= 1, "installSection must notify on attach");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task UpdateCommitsAndGetReadsThrough()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Default("deepseek-chat") });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);

            Assert.Equal("deepseek-chat", scope.Get()["model"]);

            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "deepseek-chat" });
            Assert.Equal("deepseek-chat", scope.Get()["model"]);

            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "deepseek-reasoner" });
            Assert.Equal("deepseek-reasoner", scope.Get()["model"]);

            await scope.ReplaceAsync(new Dictionary<string, object?>());
            Assert.Equal("deepseek-chat", scope.Get()["model"], "replace({}) must re-inherit the schema default");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task ValidationRejectsInvalidCommittedChangeKeepingLastGood()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String() });
            var scope = provider.Register<Dictionary<string, object?>>(
                "llm-test",
                schema,
                new SettingsRegisterOptions<Dictionary<string, object?>>
                {
                    Base = new Dictionary<string, object?> { ["model"] = "entry" },
                    Validate = value =>
                    {
                        if (value.TryGetValue("model", out var model) && model as string == "blocked")
                        {
                            throw new InvalidOperationException("model is blocked by policy");
                        }
                    },
                });

            Assert.Equal("entry", scope.Get()["model"]);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "blocked" }));
            Assert.True(error.Message.Contains("blocked", StringComparison.Ordinal), error.Message);
            Assert.Equal("entry", scope.Get()["model"], "an invalid write must keep the last good resolved value");

            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "allowed" });
            Assert.Equal("allowed", scope.Get()["model"], "a committed change caps later reads");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task StaleWriteRefusesWithConflictError()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String() });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);

            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "a" });
            var conflict = await Assert.ThrowsAsync<SettingsConflictError>(
                () => scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "b" }, expectedRevision: 0));
            Assert.Equal(0L, conflict.Expected);
            Assert.Equal(1L, conflict.Actual);
            Assert.Equal("SETTINGS_CONFLICT", conflict.Code);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task ProviderPublishKeepsLastGoodValue()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Default("deepseek-chat") });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);

            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "good" });
            Assert.Equal("good", scope.Get()["model"]);

            // A stored section the schema rejects keeps the last good resolved value: reads are
            // read-through to the committed value, never re-resolved from the raw document.
            provider.PublishDoc(new Dictionary<string, object?>
            {
                ["llm-test"] = new Dictionary<string, object?> { ["model"] = 123 },
            });
            Assert.Equal("good", scope.Get()["model"]);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task FileProviderPersistsAndReloadsAcrossInstances()
    {
        using var root = TempRoot.Create();
        var path = Path.Combine(root.Path, "settings.json");
        var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Default("deepseek-chat") });

        var first = new Context();
        try
        {
            var provider = new FileSettingsProvider(first, path);
            Assert.True(provider.Writable);
            Assert.Equal(path, provider.DocumentPath);
            await provider.StartAsync();
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);
            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "persisted" });
            Assert.Equal("persisted", scope.Get()["model"]);
        }
        finally
        {
            first.Dispose();
        }

        var second = new Context();
        try
        {
            var provider = new FileSettingsProvider(second, path);
            await provider.StartAsync();
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);
            Assert.Equal("persisted", scope.Get()["model"], "a new provider instance must reload the committed section");
        }
        finally
        {
            second.Dispose();
        }
    }

    public static async Task RedactionMasksSecretValues()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema>
            {
                ["apiKey"] = Schema.String().Role("secret"),
                ["model"] = Schema.String().Default("deepseek-chat"),
            });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);
            await scope.UpdateAsync(new Dictionary<string, object?>
            {
                ["apiKey"] = "sk-live-secret",
                ["model"] = "deepseek-chat",
            });

            var verbatim = provider.Describe();
            var verbatimValue = Assert.Single(verbatim);
            var verbatimDict = verbatimValue.Value as Dictionary<string, object?>;
            Assert.NotNull(verbatimDict);
            Assert.Equal("sk-live-secret", verbatimDict!["apiKey"], "the verbatim descriptor carries the secret");

            var redacted = provider.Describe(new SettingsDescribeOptions(RedactSecrets: true));
            var redactedValue = Assert.Single(redacted);
            var redactedDict = redactedValue.Value as Dictionary<string, object?>;
            Assert.NotNull(redactedDict);
            Assert.True(!redactedDict!.ContainsKey("apiKey"), "redaction must remove the secret field");
            Assert.Equal("deepseek-chat", redactedDict["model"]);
            var secret = Assert.Single(redactedValue.Secrets!);
            Assert.Equal(new[] { "apiKey" }, secret.Path);
            Assert.True(secret.Set, "the secret slot must report it currently holds a value");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task InvalidStoredSectionFailsRegistrationLoud()
    {
        using var root = TempRoot.Create();
        var path = Path.Combine(root.Path, "settings.json");
        File.WriteAllText(path, "{ \"llm-test\": { \"model\": 123 } }\n");
        var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Required() });

        var ctx = new Context();
        try
        {
            var provider = new FileSettingsProvider(ctx, path);
            await provider.StartAsync();
            Assert.Throws<Cordis.Schemastery.ValidationError>(
                () => provider.Register<Dictionary<string, object?>>("llm-test", schema),
                "an invalid stored section must fail the registration itself");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task InvalidNamespaceFailsLoud()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            Assert.Throws<ArgumentException>(
                () => provider.Register<Dictionary<string, object?>>("Bad Name", Schema.Any()),
                "a namespace outside the grammar must be rejected");
            Assert.Throws<ArgumentException>(
                () => provider.Get("Not-Valid!"),
                "an invalid namespace read must be rejected");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task FileProviderRejectsUnsupportedExtension()
    {
        var ctx = new Context();
        try
        {
            Assert.Throws<ArgumentException>(
                () => new FileSettingsProvider(ctx, "C:\\tmp\\settings.yaml"),
                "a non-.json extension must fail loud");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Mutate_SetCreatesIntermediateObjects()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Default("deepseek-chat") });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);

            await scope.MutateAsync(new[] { new SettingsPathOp("set", new[] { "a", "b", "c" }, 1L) });
            var value = scope.Get();
            var a = (Dictionary<string, object?>)value["a"];
            var b = (Dictionary<string, object?>)a["b"];
            Assert.Equal(1L, b["c"], "a deep set creates the intermediate objects");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Mutate_UnsetRemovesAndAbsentIsSatisfied()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Default("deepseek-chat") });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);
            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "deepseek-reasoner" });

            await scope.MutateAsync(new[] { new SettingsPathOp("unset", new[] { "model" }) });
            Assert.Equal("deepseek-chat", scope.Get()["model"], "unset re-inherits the schema default");

            var revision = provider.Describe()[0].Revision;
            await scope.MutateAsync(new[] { new SettingsPathOp("unset", new[] { "never-there" }) });
            Assert.Equal(revision, provider.Describe()[0].Revision, "an unset through an absent path is already satisfied (no revision bump)");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Mutate_RootOpSemantics()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Default("deepseek-chat") });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);
            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "deepseek-reasoner" });

            var rootSet = Assert.Throws<ArgumentException>(
                () => scope.MutateAsync(new[] { new SettingsPathOp("set", Array.Empty<string>(), "not-an-object") })
                    .GetAwaiter().GetResult(),
                "setting the section root requires a plain object");

            await scope.MutateAsync(new[] { new SettingsPathOp("unset", Array.Empty<string>()) });
            Assert.Equal("deepseek-chat", scope.Get()["model"], "a root unset clears the section back to the defaults");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Mutate_OrderedOpsObserveEarlierOnes()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Default("deepseek-chat") });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);

            // The first op writes a scalar at "a"; the second descends into it and must create
            // the intermediate object instead of failing on the scalar.
            await scope.MutateAsync(new[]
            {
                new SettingsPathOp("set", new[] { "a" }, "scalar"),
                new SettingsPathOp("set", new[] { "a", "b" }, 2L),
            });
            var value = scope.Get();
            var a = (Dictionary<string, object?>)value["a"];
            Assert.Equal(2L, a["b"], "later ops observe earlier ones, creating intermediates");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Mutate_StaleRevisionRefusesWithConflict()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema> { ["model"] = Schema.String().Default("deepseek-chat") });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);
            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "a" });

            var conflict = await Assert.ThrowsAsync<SettingsConflictError>(
                () => scope.MutateAsync(new[] { new SettingsPathOp("set", new[] { "model" }, "b") }, expectedRevision: 0));
            Assert.Equal(0L, conflict.Expected);
            Assert.Equal(1L, conflict.Actual);
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static async Task Mutate_NamesSecretFieldWithoutRestatingSection()
    {
        var ctx = new Context();
        try
        {
            var provider = new MemorySettingsProvider(ctx, new Dictionary<string, object?>());
            await provider.StartAsync();
            var schema = Schema.Object(new Dictionary<string, Schema>
            {
                ["apiKey"] = Schema.String().Role("secret"),
                ["model"] = Schema.String().Default("deepseek-chat"),
            });
            var scope = provider.Register<Dictionary<string, object?>>("llm-test", schema);
            await scope.UpdateAsync(new Dictionary<string, object?> { ["model"] = "deepseek-reasoner" });

            // The redacted view never carries the secret; a path op names the field without
            // restating the section (the case a wholesale replace would silently delete).
            var redacted = provider.Describe(new SettingsDescribeOptions(RedactSecrets: true));
            Assert.True(!((Dictionary<string, object?>)redacted[0].Value!).ContainsKey("apiKey"), "the redacted view has no apiKey field");

            await scope.MutateAsync(new[] { new SettingsPathOp("set", new[] { "apiKey" }, "sk-rotated") });
            Assert.Equal("sk-rotated", scope.Get()["apiKey"], "the path op lands the secret without restating the section");
            Assert.Equal("deepseek-reasoner", scope.Get()["model"], "the untouched fields survive");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    /// <summary>In-memory settings provider exposing the protected publish hook for tests.</summary>
    private sealed class MemorySettingsProvider : SettingsProvider
    {
        public MemorySettingsProvider(Context ctx, Dictionary<string, object?> doc)
            : base(ctx)
        {
            Doc = doc;
        }

        public Dictionary<string, object?> Doc { get; }

        public override bool Writable => true;

        protected override Task<Dictionary<string, object?>> LoadAsync() => Task.FromResult(Doc);

        protected override Task PersistAsync(SettingsNamespace ns, Dictionary<string, object?> section)
        {
            Doc[ns.Value] = section;
            return Task.CompletedTask;
        }

        public void PublishDoc(Dictionary<string, object?> doc, SettingsUpdateSource source = SettingsUpdateSource.Provider)
            => Publish(doc, source);
    }
}
