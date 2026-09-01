using Harness.Cordis.Core;
using Harness.Context;
using Harness.Llm;
using Harness.Session;

namespace Harness.Context.Tests;

public static class ContextTests
{
    public static void EachContributorText_AppearsInAssembly()
    {
        using var ctx = new global::Harness.Cordis.Core.Context();
        var store = new SessionStore(ctx);
        var session = store.Create();
        var tempRoot = CreateTempRoot(out var cleanup);
        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "notes.txt"), "notes content");
            var referenced = store.Create(new SessionId("referenced-1"));
            AppendUserMessage(referenced, "background fact from the other session");

            var clock = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
            var service = new LocalContextProvider(ctx, new IContextContributor[]
            {
                new AgentInstructionsContributor("Always run the gates.", "AGENTS.md"),
                new FileReferenceContributor(tempRoot),
                new SessionReferenceContributor(id => store.Get(id)),
                new TimeContextContributor(() => clock),
            });

            var mention = SessionReferenceUri.Encode("referenced-1");
            AppendUserMessage(session, $"Check @notes.txt and see {mention} for background.");
            var assembled = service.AssembleAsync(session).GetAwaiter().GetResult();

            Assert.True(assembled.Contains("Instructions from: AGENTS.md"), "agent-instructions heading appears");
            Assert.True(assembled.Contains("Always run the gates."), "instruction text appears");
            Assert.True(assembled.Contains("File reference: notes.txt"), "file-reference heading appears");
            Assert.True(assembled.Contains("notes content"), "file content appears");
            Assert.True(assembled.Contains("Referenced session: referenced-1 (referenced-1)"), "session-reference heading appears");
            Assert.True(assembled.Contains("background fact from the other session"), "referenced messages appear");
            Assert.True(assembled.Contains("Time sampled while preparing context: 2026-01-02T03:04:05+08:00[UTC]"),
                "the injected clock renders exactly");
            Assert.True(assembled.StartsWith("<request-context>") && assembled.EndsWith("</request-context>"),
                "the assembly is framed");
        }
        finally
        {
            cleanup();
        }
    }

    public static void EmptyContributors_ContributeNothing()
    {
        using var ctx = new global::Harness.Cordis.Core.Context();
        var store = new SessionStore(ctx);
        var session = store.Create();
        var service = new LocalContextProvider(ctx);
        service.Register(new AgentInstructionsContributor(string.Empty)); // empty instructions
        service.Register(new FileReferenceContributor(Path.GetTempPath())); // no @refs in the log
        service.Register(new SessionReferenceContributor(_ => null)); // no mentions in the log

        var sections = service.CollectAsync(session).GetAwaiter().GetResult();
        Assert.Empty(sections, "empty contributors produce no sections");
        Assert.Equal(string.Empty, service.AssembleAsync(session).GetAwaiter().GetResult(), "an empty assembly is an empty string");
    }

    public static void FileReferences_ResolveWithinRoot_AndFailLoudOutside()
    {
        using var ctx = new global::Harness.Cordis.Core.Context();
        var store = new SessionStore(ctx);
        var tempRoot = CreateTempRoot(out var cleanup);
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, "sub"));
            File.WriteAllText(Path.Combine(tempRoot, "a.txt"), "alpha");
            File.WriteAllText(Path.Combine(tempRoot, "sub", "b.txt"), "beta");

            var service = new LocalContextProvider(ctx, new IContextContributor[] { new FileReferenceContributor(tempRoot) });

            // Plain and quoted mentions resolve within the root; directory mentions list entries.
            var session = store.Create();
            AppendUserMessage(session, "@a.txt and @\"sub/b.txt\" and @sub");
            var section = service.CollectAsync(session).GetAwaiter().GetResult().Single();
            Assert.True(section.Text.Contains("alpha"), "plain reference content appears");
            Assert.True(section.Text.Contains("beta"), "quoted reference content appears");
            Assert.True(section.Text.Contains("b.txt"), "directory listing appears");

            // Outside the root: traversal, absolute, drive-qualified, and missing targets fail loud.
            Assert.Throws<FileReferenceError>(() => MentionAndCollect(service, store, "@../secret.txt"),
                "a traversal segment escapes the root");
            Assert.Throws<FileReferenceError>(() => MentionAndCollect(service, store, "@/etc/passwd"),
                "an absolute path is outside the root");
            Assert.Throws<FileReferenceError>(() => MentionAndCollect(service, store, "@C:/Windows/win.ini"),
                "a drive-qualified path is outside the root");
            Assert.Throws<FileReferenceError>(() => MentionAndCollect(service, store, "@missing.txt"),
                "a missing target fails loud");
        }
        finally
        {
            cleanup();
        }
    }

    public static void RegistrationOrder_IsPreserved_AndDisposerUnregisters()
    {
        using var ctx = new global::Harness.Cordis.Core.Context();
        var store = new SessionStore(ctx);
        var session = store.Create();
        var service = new LocalContextProvider(ctx);
        var a = new AgentInstructionsContributor("text-A", "a");
        var b = new AgentInstructionsContributor("text-B", "b");
        var c = new AgentInstructionsContributor("text-C", "c");
        var bRegistration = service.Register(b);
        service.Register(a);
        service.Register(c);

        var texts = service.CollectAsync(session).GetAwaiter().GetResult().Select(section => section.Text).ToList();
        Assert.Equal(new[] { "Instructions from: b\n\ntext-B", "Instructions from: a\n\ntext-A", "Instructions from: c\n\ntext-C" },
            texts, "contributors collect in registration order");
        Assert.Equal(3, service.Contributors.Count);

        bRegistration.Dispose();
        var after = service.CollectAsync(session).GetAwaiter().GetResult();
        Assert.Equal(2, after.Count, "disposal removes the contributor");
        Assert.False(after.Any(section => section.Text.Contains("text-B")), "the disposed contributor is gone");
    }

    public static void TimeContext_UsesInjectedClock()
    {
        using var ctx = new global::Harness.Cordis.Core.Context();
        var store = new SessionStore(ctx);
        var session = store.Create();
        var clock = new DateTimeOffset(2026, 6, 7, 8, 9, 10, TimeSpan.FromHours(2));
        var contributor = new TimeContextContributor(() => clock, "Europe/Paris");

        var section = contributor.ContributeAsync(session).GetAwaiter().GetResult();
        Assert.NotNull(section);
        Assert.True(section!.Text.Contains("2026-06-07T08:09:10+02:00[Europe/Paris]"),
            "the injected clock and zone render exactly");
    }

    public static void SessionReference_Resolves_AndFailsLoudOnSelfAndUnknown()
    {
        using var ctx = new global::Harness.Cordis.Core.Context();
        var store = new SessionStore(ctx);
        var referenced = store.Create(new SessionId("referenced-1"));
        AppendUserMessage(referenced, "remembered fact");
        var service = new LocalContextProvider(ctx, new IContextContributor[]
        {
            new SessionReferenceContributor(id => store.Get(id)),
        });

        // A resolvable reference contributes the referenced session's derived messages.
        var current = store.Create(new SessionId("current-1"));
        AppendUserMessage(current, $"use {SessionReferenceUri.Encode("referenced-1")}");
        var section = service.CollectAsync(current).GetAwaiter().GetResult().Single();
        Assert.True(section.Text.Contains("Referenced session: referenced-1 (referenced-1)"), "the heading labels the session");
        Assert.True(section.Text.Contains("remembered fact"), "referenced messages are contributed");

        // A self reference fails loud.
        var self = store.Create(new SessionId("current-2"));
        AppendUserMessage(self, $"see {SessionReferenceUri.Encode("current-2")}");
        var selfError = Assert.Throws<SessionReferenceError>(() => service.CollectAsync(self).GetAwaiter().GetResult());
        Assert.Equal(SessionReferenceErrorCodes.SelfReference, selfError.Code);

        // An unresolvable session fails loud.
        var unknown = store.Create(new SessionId("current-3"));
        AppendUserMessage(unknown, $"see {SessionReferenceUri.Encode("ghost-1")}");
        var readError = Assert.Throws<SessionReferenceError>(() => service.CollectAsync(unknown).GetAwaiter().GetResult());
        Assert.Equal(SessionReferenceErrorCodes.ReadFailed, readError.Code);
    }

    public static void RegistersAsTheContextService()
    {
        using var ctx = new global::Harness.Cordis.Core.Context();
        var provider = new LocalContextProvider(ctx);

        Assert.Same(provider, ctx.Get<IContextService>("context"));
        Assert.Same(provider, LocalContextProvider.Require(ctx));
    }

    // --- helpers ---

    private static string CreateTempRoot(out Action cleanup)
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-context-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        cleanup = () =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best-effort teardown; a leftover temp dir is harmless
            }
        };
        return root;
    }

    private static void AppendUserMessage(global::Harness.Session.Session session, string text)
        => session.Append(new UserMessageEvent
        {
            Message = Messages.CreateUserMessage(new ContentBlock[] { new TextBlock(text) }),
            SurfaceOp = SurfaceOp.Append,
        });

    private static void MentionAndCollect(IContextService service, SessionStore store, string mention)
    {
        var session = store.Create();
        AppendUserMessage(session, mention);
        service.CollectAsync(session).GetAwaiter().GetResult();
    }
}
