using Harness.Cordis.Core;
using Harness.Authorization;
using Harness.Credentials;

namespace Harness.Authorization.Tests;

/// <summary>One disposable temp directory per test, removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hsh-authorization-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // already gone
        }
    }
}

/// <summary>
/// Registry, begin, commit-confirmation, cancellation, and observer-event behavior of
/// <see cref="LocalAuthorizationService"/> over a real file-backed credentials provider.
/// </summary>
public static class AuthorizationTests
{
    private const string Key = "OPENAI_CODEX";
    private const string Other = "ANTHROPIC";

    private static readonly IReadOnlyList<AuthorizationMethod> Methods = new[]
    {
        new AuthorizationMethod("oauth", "Sign in with ChatGPT"),
        new AuthorizationMethod("api-key", "Paste a key"),
    };

    /// <summary>
    /// A committing flow: runs the optional <paramref name="before"/> step, then commits its key
    /// through the observed credentials surface. A test that needs a flow which does not commit
    /// builds the <see cref="AuthorizationFlow"/> directly.
    /// </summary>
    private static AuthorizationFlow Flow(string key, Func<AuthorizationSession, Task>? before = null)
        => new()
        {
            Key = key,
            Label = "ChatGPT (Codex)",
            Methods = Methods,
            Run = async session =>
            {
                if (before is not null) await before(session);
                await session.Credentials.SetAsync(key, "granted");
            },
        };

    private static AuthorizationInteraction Surface(string answer = "typed")
        => new()
        {
            Notify = _ => { },
            PromptAsync = _ => Task.FromResult(answer),
        };

    private static TestHarness Harness() => new();

    public static void Registration_ProjectsEntry_AndDisposalRemovesTheFlow()
    {
        using var harness = Harness();
        var disposer = harness.Authorization.RegisterFlow(Flow(Key));

        var entries = harness.Authorization.List();
        Assert.Equal(1, entries.Count);
        var entry = entries[0];
        Assert.Equal(Key, entry.Key);
        Assert.Equal("ChatGPT (Codex)", entry.Label);
        Assert.Equal(2, entry.Methods.Count);
        Assert.Equal("oauth", entry.Methods[0].Id);
        Assert.Equal("Sign in with ChatGPT", entry.Methods[0].Label);
        Assert.Equal("api-key", entry.Methods[1].Id);
        Assert.False(entry.InFlight);
        Assert.Equal("ChatGPT (Codex)", harness.Authorization.Describe(Key)!.Label);
        Assert.Null(harness.Authorization.Describe(Other));

        disposer.Dispose();

        Assert.Equal(0, harness.Authorization.List().Count);
        Assert.Null(harness.Authorization.Describe(Key));
    }

    public static void DuplicateRegistration_FailsLoud()
    {
        using var harness = Harness();
        harness.Authorization.RegisterFlow(Flow(Key));

        var error = Assert.Throws<AuthorizationError>(() => harness.Authorization.RegisterFlow(Flow(Key)));

        Assert.Equal("DUPLICATE_FLOW", error.Code);
        // The first registration is unaffected.
        Assert.NotNull(harness.Authorization.Describe(Key));
    }

    public static void FlowWithNoMethods_IsRejectedAtRegistration()
    {
        using var harness = Harness();

        var error = Assert.Throws<ArgumentException>(() => harness.Authorization.RegisterFlow(new AuthorizationFlow
        {
            Key = Key,
            Label = "Empty",
            Methods = Array.Empty<AuthorizationMethod>(),
            Run = _ => Task.CompletedTask,
        }));

        Assert.True(error.Message.Contains("at least one method", StringComparison.Ordinal));
    }

    public static void DisposingTheFlow_MidAttempt_WithdrawsTheAttempt()
    {
        using var harness = Harness();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposer = harness.Authorization.RegisterFlow(Flow(Key, before: session =>
        {
            started.TrySetResult(true);
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        }));

        var attempt = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() });
        started.Task.GetAwaiter().GetResult();
        disposer.Dispose();

        var outcome = attempt.GetAwaiter().GetResult();
        Assert.Equal(AuthorizationStatus.Cancelled, outcome.Status);
        Assert.Null(harness.Authorization.Describe(Key));
    }

    public static void SuccessfulBegin_CommitsTheRecord_AndSettlesAuthorized()
    {
        using var harness = Harness();
        using var listen = harness.ListenSettled();
        harness.Authorization.RegisterFlow(Flow(Key));

        var outcome = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }).GetAwaiter().GetResult();

        Assert.Equal(AuthorizationStatus.Authorized, outcome.Status);
        var stored = harness.Credentials.DescribeAsync(Key).GetAwaiter().GetResult();
        Assert.True(stored.Configured);
        Assert.Equal(1, harness.Settled.Count);
        Assert.Equal(Key, harness.Settled[0].Key);
        Assert.Equal(AuthorizationSettlement.Authorized, harness.Settled[0].Settlement);
        Assert.False(harness.Authorization.Describe(Key)!.InFlight);
    }

    public static void Session_CarriesNoticesAndPrompts_BetweenFlowAndSurface()
    {
        using var harness = Harness();
        var notices = new List<AuthorizationNotice>();
        var prompts = new List<AuthorizationPrompt>();
        var answers = new List<string>();
        var interaction = new AuthorizationInteraction
        {
            Notify = notice => notices.Add(notice),
            PromptAsync = async prompt =>
            {
                prompts.Add(prompt);
                return "code-123";
            },
        };
        harness.Authorization.RegisterFlow(Flow(Key, before: async session =>
        {
            session.Notify(new AuthorizationNotice("Continue in your browser", Url: "https://auth.example/start"));
            answers.Add(await session.PromptAsync(new AuthorizationTextPrompt("Paste the code")));
        }));

        var outcome = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = interaction }).GetAwaiter().GetResult();

        Assert.Equal(AuthorizationStatus.Authorized, outcome.Status);
        Assert.Equal(1, notices.Count);
        Assert.Equal("Continue in your browser", notices[0].Message);
        Assert.Equal("https://auth.example/start", notices[0].Url);
        Assert.Equal(1, prompts.Count);
        Assert.Equal(AuthorizationPromptKind.Text, prompts[0].Kind);
        Assert.Equal("Paste the code", prompts[0].Message);
        Assert.Equal(new[] { "code-123" }, answers);
    }

    public static void Method_DefaultsToTheFirst_AndHonorsTheNamedOne()
    {
        using var harness = Harness();
        var seen = new List<string>();
        harness.Authorization.RegisterFlow(Flow(Key, before: session =>
        {
            seen.Add(session.Method);
            return Task.CompletedTask;
        }));

        harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }).GetAwaiter().GetResult();
        harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Method = "api-key", Interaction = Surface() }).GetAwaiter().GetResult();

        Assert.Equal(new[] { "oauth", "api-key" }, seen);
    }

    public static void UnknownKey_FailsLoud()
    {
        using var harness = Harness();

        var error = Assert.ThrowsAny<AuthorizationError>(() =>
            harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }));

        Assert.Equal("NO_FLOW", error.Code);
    }

    public static void UnknownMethod_FailsLoud()
    {
        using var harness = Harness();
        harness.Authorization.RegisterFlow(Flow(Key));

        var error = Assert.ThrowsAny<AuthorizationError>(() =>
            harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Method = "device", Interaction = Surface() }));

        Assert.Equal("UNKNOWN_METHOD", error.Code);
    }

    public static void SecondAttempt_WhileInFlight_IsRefused_AndTheKeyIsReleasedAfter()
    {
        using var harness = Harness();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        harness.Authorization.RegisterFlow(Flow(Key, before: session =>
        {
            if (!first) return Task.CompletedTask;
            first = false;
            started.TrySetResult(true);
            return hold.Task;
        }));

        var attempt = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() });
        started.Task.GetAwaiter().GetResult();
        Assert.True(harness.Authorization.Describe(Key)!.InFlight);

        var refused = Assert.ThrowsAny<AuthorizationError>(() =>
            harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }));
        Assert.Equal("ALREADY_IN_FLIGHT", refused.Code);

        hold.TrySetResult(true);
        var outcome = attempt.GetAwaiter().GetResult();
        Assert.Equal(AuthorizationStatus.Authorized, outcome.Status);
        Assert.False(harness.Authorization.Describe(Key)!.InFlight);

        // The slot was released, so a later attempt is admitted.
        var second = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }).GetAwaiter().GetResult();
        Assert.Equal(AuthorizationStatus.Authorized, second.Status);
    }

    public static void PreCancelledBegin_ReturnsCancelled_WithoutRunningTheFlow()
    {
        using var harness = Harness();
        using var listen = harness.ListenSettled();
        var ran = false;
        harness.Authorization.RegisterFlow(Flow(Key, before: _ =>
        {
            ran = true;
            return Task.CompletedTask;
        }));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var outcome = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface(), Signal = cts.Token }).GetAwaiter().GetResult();

        Assert.Equal(AuthorizationStatus.Cancelled, outcome.Status);
        Assert.False(ran);
        // Nothing occupied the key, so nothing settled on it either.
        Assert.Equal(0, harness.Settled.Count);
        Assert.False(harness.Authorization.Describe(Key)!.InFlight);
    }

    public static void CancelledBegin_SettlesCancelled_AndCommitsNothing()
    {
        using var harness = Harness();
        using var listen = harness.ListenSettled();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Authorization.RegisterFlow(Flow(Key, before: async session =>
        {
            started.TrySetResult(true);
            await hold.Task;
        }));

        var attempt = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() });
        started.Task.GetAwaiter().GetResult();
        Assert.True(harness.Authorization.Describe(Key)!.InFlight);

        harness.Authorization.Cancel(Key);
        var outcome = attempt.GetAwaiter().GetResult();

        Assert.Equal(AuthorizationStatus.Cancelled, outcome.Status);
        Assert.Equal(1, harness.Settled.Count);
        Assert.Equal(AuthorizationSettlement.Cancelled, harness.Settled[0].Settlement);
        Assert.False(harness.Credentials.DescribeAsync(Key).GetAwaiter().GetResult().Configured);
        Assert.False(harness.Authorization.Describe(Key)!.InFlight);
    }

    public static void Cancel_OnAnIdleKey_IsANoOp()
    {
        using var harness = Harness();
        harness.Authorization.RegisterFlow(Flow(Key));

        harness.Authorization.Cancel(Other);
        harness.Authorization.Cancel(Key);

        Assert.False(harness.Authorization.Describe(Key)!.InFlight);
    }

    public static void ThrowingFlow_FailsItsCaller_AndSettlesFailedOnTheEventStream()
    {
        using var harness = Harness();
        using var listen = harness.ListenSettled();
        harness.Authorization.RegisterFlow(Flow(Key, before: _ => throw new InvalidOperationException("the token endpoint said no")));

        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
            harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }));

        Assert.Equal("the token endpoint said no", error.Message);
        Assert.Equal(1, harness.Settled.Count);
        Assert.Equal(AuthorizationSettlement.Failed, harness.Settled[0].Settlement);
        Assert.False(harness.Authorization.Describe(Key)!.InFlight);
    }

    public static void FlowResolving_WithoutCommitting_FailsLoud()
    {
        using var harness = Harness();
        harness.Authorization.RegisterFlow(new AuthorizationFlow
        {
            Key = Key,
            Label = "Forgetful",
            Methods = new[] { new AuthorizationMethod("oauth", "Sign in") },
            Run = _ => Task.CompletedTask,
        });

        var error = Assert.ThrowsAny<AuthorizationError>(() =>
            harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }));

        Assert.Equal("NOT_COMMITTED", error.Code);
        Assert.True(error.Message.Contains("resolved without committing", StringComparison.Ordinal));
    }

    public static void ReAuth_ThatLeftOnlyAnEarlierRecord_FailsLoud()
    {
        using var harness = Harness();
        harness.Credentials.SetAsync(Key, "stale").GetAwaiter().GetResult();
        harness.Authorization.RegisterFlow(new AuthorizationFlow
        {
            Key = Key,
            Label = "Forgetful",
            Methods = new[] { new AuthorizationMethod("oauth", "Sign in") },
            Run = _ => Task.CompletedTask,
        });

        var error = Assert.ThrowsAny<AuthorizationError>(() =>
            harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }));

        Assert.Equal("NOT_COMMITTED", error.Code);
        // Refused, not cleaned up: the stale record still belongs to its owner.
        Assert.True(harness.Credentials.DescribeAsync(Key).GetAwaiter().GetResult().Configured);
    }

    public static void FlowDeletingItsRecord_FailsLoud()
    {
        using var harness = Harness();
        harness.Credentials.SetAsync(Key, "stale").GetAwaiter().GetResult();
        harness.Authorization.RegisterFlow(new AuthorizationFlow
        {
            Key = Key,
            Label = "Destructive",
            Methods = new[] { new AuthorizationMethod("oauth", "Sign in") },
            Run = session => session.Credentials.UnsetAsync(Key),
        });

        var error = Assert.ThrowsAny<AuthorizationError>(() =>
            harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }));

        Assert.Equal("NOT_COMMITTED", error.Code);
        Assert.True(error.Message.Contains("deleted its credential record", StringComparison.Ordinal));
    }

    public static void DeclinedPrompt_SettlesCancelled_NotFailed()
    {
        using var harness = Harness();
        using var listen = harness.ListenSettled();
        var declining = new AuthorizationInteraction
        {
            Notify = _ => { },
            PromptAsync = _ => throw new AuthorizationDeclinedError(),
        };
        harness.Authorization.RegisterFlow(Flow(Key, before: async session =>
        {
            await session.PromptAsync(new AuthorizationTextPrompt("Paste the code"));
        }));

        var outcome = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = declining }).GetAwaiter().GetResult();

        Assert.Equal(AuthorizationStatus.Cancelled, outcome.Status);
        Assert.Equal(1, harness.Settled.Count);
        Assert.Equal(AuthorizationSettlement.Cancelled, harness.Settled[0].Settlement);
    }

    public static void DeclineIsReadThroughAFlow_ThatRewrapsTheRejection()
    {
        using var harness = Harness();
        var declining = new AuthorizationInteraction
        {
            Notify = _ => { },
            PromptAsync = _ => throw new AuthorizationDeclinedError(),
        };
        harness.Authorization.RegisterFlow(Flow(Key, before: async session =>
        {
            try
            {
                await session.PromptAsync(new AuthorizationTextPrompt("Paste the code"));
            }
            catch
            {
                throw new InvalidOperationException("sign-in aborted");
            }
        }));

        var outcome = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = declining }).GetAwaiter().GetResult();

        Assert.Equal(AuthorizationStatus.Cancelled, outcome.Status);
    }

    public static void PromptFailure_ThatIsNotADecline_FailsTheAttempt()
    {
        using var harness = Harness();
        using var listen = harness.ListenSettled();
        var broken = new AuthorizationInteraction
        {
            Notify = _ => { },
            PromptAsync = _ => throw new InvalidOperationException("the transport dropped"),
        };
        harness.Authorization.RegisterFlow(Flow(Key, before: async session =>
        {
            await session.PromptAsync(new AuthorizationTextPrompt("Paste the code"));
        }));

        var error = Assert.ThrowsAny<InvalidOperationException>(() =>
            harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = broken }));

        Assert.Equal("the transport dropped", error.Message);
        Assert.Equal(1, harness.Settled.Count);
        Assert.Equal(AuthorizationSettlement.Failed, harness.Settled[0].Settlement);
    }

    public static void BrokenNoticeSurface_LosesTheNotice_NeverTheAttempt()
    {
        using var harness = Harness();
        var broken = new AuthorizationInteraction
        {
            Notify = _ => throw new InvalidOperationException("page connection closed"),
            PromptAsync = _ => Task.FromResult("unused"),
        };
        harness.Authorization.RegisterFlow(Flow(Key, before: session =>
        {
            session.Notify(new AuthorizationNotice("Continue in your browser"));
            return Task.CompletedTask;
        }));

        var outcome = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = broken }).GetAwaiter().GetResult();

        Assert.Equal(AuthorizationStatus.Authorized, outcome.Status);
    }

    public static void SettledFanOut_Contains_AThrowingListener()
    {
        using var harness = Harness();
        harness.Authorization.RegisterFlow(Flow(Key));
        harness.Ctx.On("authorization/settled", new Action<string, AuthorizationSettlement>((_, _) =>
            throw new InvalidOperationException("watcher boom")));

        var outcome = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }).GetAwaiter().GetResult();

        // A broken watcher can never turn the caller's settled result into a failure of its own.
        Assert.Equal(AuthorizationStatus.Authorized, outcome.Status);
    }

    public static void SettleFires_AfterTheSlotIsReleased()
    {
        using var harness = Harness();
        harness.Authorization.RegisterFlow(Flow(Key));
        var reentered = false;
        var reacted = false;
        harness.Ctx.On("authorization/settled", new Action<string, AuthorizationSettlement>((key, settlement) =>
        {
            // A listener reacting by starting the next attempt must not be refused by the one
            // that just finished: the slot is released before the event fires.
            if (reacted) return;
            reacted = true;
            var next = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }).GetAwaiter().GetResult();
            reentered = next.Status == AuthorizationStatus.Authorized;
        }));

        var outcome = harness.Authorization.BeginAsync(new AuthorizationRequest { Key = Key, Interaction = Surface() }).GetAwaiter().GetResult();

        Assert.Equal(AuthorizationStatus.Authorized, outcome.Status);
        Assert.True(reentered);
    }

    private sealed class TestHarness : IDisposable
    {
        private readonly TempDir _dir = new();

        public TestHarness()
        {
            Ctx = new Context();
            Credentials = new LocalCredentialsProvider(
                Ctx,
                new LocalCredentialsConfig(
                    ManagedPath: _dir.File("credentials.env"),
                    ProjectEnvPath: _dir.File("project.env"),
                    UserEnvPath: _dir.File("user.env")),
                _ => null);
            Authorization = new LocalAuthorizationService(Ctx, Credentials);
        }

        public Context Ctx { get; }

        public LocalCredentialsProvider Credentials { get; }

        public LocalAuthorizationService Authorization { get; }

        public List<(string Key, AuthorizationSettlement Settlement)> Settled { get; } = new();

        public IDisposable ListenSettled()
            => Ctx.On("authorization/settled", new Action<string, AuthorizationSettlement>((key, settlement) =>
                Settled.Add((key, settlement))));

        public void Dispose()
        {
            Ctx.Dispose();
            _dir.Dispose();
        }
    }
}
