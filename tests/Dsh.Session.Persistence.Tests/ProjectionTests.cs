using Dsh.Llm;
using Dsh.Session;
using Dsh.Session.Projection;

namespace Dsh.Session.Persistence.Tests;

internal static class ProjectionTests
{
    /// <summary>Fold state for the user-count unit: how many user prompts committed.</summary>
    private sealed record UserCount(int Count);

    /// <summary>Client-visible keys selected by the cropped-snapshot test.</summary>
    private static readonly string[] TitlesOnly = { "titles" };

    /// <summary>Fold state for the turn-count unit: how many turns opened.</summary>
    private sealed record TurnCount(int Count);

    private static ProjectionUnit<UserCount> UserCountUnit() => new()
    {
        Init = () => new UserCount(0),
        Apply = (state, evt) => evt is UserMessageEvent ? new UserCount(state.Count + 1) : state,
        View = state => state.Count,
    };

    public static void StateOf_ReturnsSameReference_UntilTheFactMoves()
    {
        using var scope = new TestScope();
        var session = scope.Store.Create(new SessionId("proj-1"));
        var registry = new SessionProjectionRegistry(scope.Ctx);
        using (registry.Register("userCount", UserCountUnit()))
        {
            session.Append(TestEvents.UserPrompt("hello", "msg-1"));
            var first = registry.StateOf<UserCount>(session, "userCount");
            Assert.NotNull(first, "the eager drive must fold the committed prompt");
            Assert.Equal(1, first!.Count);

            // A boundary event the unit ignores must keep the SAME state reference.
            session.Append(new TurnStartEvent { Turn = 1 });
            var same = registry.StateOf<UserCount>(session, "userCount");
            Assert.Same(first, same);
            Assert.Equal(1, same!.Count);

            // A second prompt moves the fact: a NEW reference with the new value.
            session.Append(TestEvents.UserPrompt("again", "msg-2"));
            var moved = registry.StateOf<UserCount>(session, "userCount");
            Assert.NotSame(same, moved);
            Assert.Equal(2, moved!.Count);
        }
    }

    public static void LateRegistration_FoldsTheCommittedLog()
    {
        using var scope = new TestScope();
        var session = scope.Store.Create(new SessionId("proj-2"));
        session.Append(TestEvents.UserPrompt("one", "msg-1"));
        session.Append(TestEvents.UserPrompt("two", "msg-2"));
        var registry = new SessionProjectionRegistry(scope.Ctx);
        using (registry.Register("userCount", UserCountUnit()))
        {
            var state = registry.StateOf<UserCount>(session, "userCount");
            Assert.NotNull(state, "a late registration must fold the committed log on first read");
            Assert.Equal(2, state!.Count);
        }
    }

    public static void Snapshot_ReturnsCroppedViews()
    {
        using var scope = new TestScope();
        var session = scope.Store.Create(new SessionId("proj-3"));
        var registry = new SessionProjectionRegistry(scope.Ctx);
        using (registry.Register("turns", new ProjectionUnit<TurnCount>
        {
            Init = () => new TurnCount(0),
            Apply = (state, evt) => evt is TurnStartEvent ? new TurnCount(state.Count + 1) : state,
            View = state => state.Count,
        }))
        using (registry.Register("titles", new ProjectionUnit<string?>
        {
            Init = () => null,
            Apply = (state, evt) => evt is UserMessageEvent ? "title" : state,
            View = state => state,
        }))
        using (registry.Register("internal", new ProjectionUnit<long>
        {
            Init = () => 0,
            Apply = (state, evt) => state + 1,
            // No view: host-only units never appear in client snapshots.
        }))
        {
            session.Append(new TurnStartEvent { Turn = 1 });
            session.Append(TestEvents.UserPrompt("hello", "msg-1"));

            var all = registry.Snapshot(session);
            Assert.Equal(session.Seq - 1, all.AsOfSeq);
            Assert.Equal(2, all.Values.Count);
            Assert.True(all.Values.ContainsKey("turns"), "snapshot must include every client-visible unit");
            Assert.True(all.Values.ContainsKey("titles"), "snapshot must include every client-visible unit");
            Assert.True(!all.Values.ContainsKey("internal"), "host-only units must be cropped out of snapshots");
            Assert.Equal(1, all.Values["turns"]);

            var cropped = registry.Snapshot(session, TitlesOnly);
            Assert.Equal(1, cropped.Values.Count);
            Assert.True(cropped.Values.ContainsKey("titles"), "a cropped snapshot must keep the selected key");
            Assert.True(!cropped.Values.ContainsKey("turns"), "a cropped snapshot must drop unselected keys");
            Assert.Equal("title", cropped.Values["titles"]);
        }
    }

    public static void HostReader_FailsExplicitly_WhenRegistryAbsent()
    {
        using var scope = new TestScope();
        Assert.Throws<InvalidOperationException>(
            () => SessionProjectionRegistry.Require(scope.Ctx),
            "a host reader requiring an absent registry must fail explicitly");
    }

    public static void DuplicateKeyRegistration_FailsLoud()
    {
        using var scope = new TestScope();
        var registry = new SessionProjectionRegistry(scope.Ctx);
        using (registry.Register("k", new ProjectionUnit<long> { Init = () => 0, Apply = (state, _) => state }))
        {
            Assert.Throws<InvalidOperationException>(
                () => registry.Register("k", new ProjectionUnit<long> { Init = () => 0, Apply = (state, _) => state }),
                "a second registration of one key must fail loud");
        }
    }
}
