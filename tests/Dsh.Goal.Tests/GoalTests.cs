using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Goal.Tests;

public static class GoalTests
{
    private static GoalWriteEvent CreateEvent(GoalSnapshot goal) => new()
    {
        Operation = GoalOperation.Create,
        Goal = goal,
        RoundsStarted = 0,
        CreatedAt = 1,
        UpdatedAt = 1,
    };

    public static void EmptyLog_YieldsNoCurrentGoal()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionGoalService(ctx);
        var session = sessions.Create();

        Assert.Null(service.Get(session), "a log with no goal/write folds to no current goal");
    }

    public static void GoalWriteEvents_FoldLastWriteWins()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionGoalService(ctx);
        var session = sessions.Create();

        var first = new GoalSnapshot("goal-1", 1, "Port the goal seam", GoalPhase.Active, null, 256);
        var second = new GoalSnapshot("goal-1", 2, "Port the schedule seam", GoalPhase.Active, null, 128);
        session.Append(CreateEvent(first));
        session.Append(new GoalWriteEvent
        {
            Operation = GoalOperation.Edit,
            Goal = second,
            RoundsStarted = 0,
            CreatedAt = 1,
            UpdatedAt = 2,
        });

        var view = service.Get(session);
        Assert.NotNull(view);
        Assert.Equal("goal-1", view!.Id);
        Assert.Equal(2, view.Revision);
        Assert.Equal("Port the schedule seam", view.Objective);
        Assert.Equal(128, view.MaxGoalRounds);
        Assert.Equal(GoalPhase.Active, view.Phase);
    }

    public static void StateUpdatesLiveOnSessionEvent()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionGoalService(ctx);
        var session = sessions.Create();

        session.Append(new TurnStartEvent { Turn = 1 });
        Assert.Null(service.Get(session), "non-goal events must not change the goal");

        var created = service.Create(session, "Port the goal seam");
        Assert.Equal(GoalActivation.Armed, created.Activation, "a create arms the goal");
        var live = service.Get(session);
        Assert.NotNull(live);
        Assert.Equal("Port the goal seam", live!.Objective);

        session.Append(new TurnStartEvent { Turn = 2 });
        Assert.Equal(live.Revision, service.Get(session)!.Revision, "non-goal events must not disturb the folded goal");
    }

    public static void GoalWriteEvent_RoundTripsTheJsonl()
    {
        using var ctx = new Context();
        _ = new SessionGoalService(ctx); // registers goal/write into the session event-type registry

        var snapshot = new GoalSnapshot("goal-1", 1, "Port the goal seam", GoalPhase.Active, null, 256);
        var write = new GoalWriteEvent
        {
            Id = "evt-0",
            Seq = 0,
            TimeMs = 1,
            Operation = GoalOperation.Create,
            Goal = snapshot,
            RoundsStarted = 0,
            CreatedAt = 1,
            UpdatedAt = 1,
        };
        var options = SessionEventTypes.CreateSerializerOptions();
        var json = JsonSerializer.Serialize<SessionEvent>(write, options);
        var back = Assert.IsType<GoalWriteEvent>(JsonSerializer.Deserialize<SessionEvent>(json, options));
        Assert.Equal("goal/write", back.Type);
        Assert.Equal(write.Operation, back.Operation);
        Assert.Equal(snapshot, back.Goal);

        var clear = new GoalWriteEvent
        {
            Id = "evt-1",
            Seq = 1,
            TimeMs = 2,
            Operation = GoalOperation.Clear,
            Cleared = new GoalRef("goal-1", 2),
            ClearedAt = 2,
        };
        var clearBack = Assert.IsType<GoalWriteEvent>(
            JsonSerializer.Deserialize<SessionEvent>(JsonSerializer.Serialize<SessionEvent>(clear, options), options));
        Assert.Equal(GoalOperation.Clear, clearBack.Operation);
        Assert.Null(clearBack.Goal);
        Assert.Equal(new GoalRef("goal-1", 2), clearBack.Cleared);
        Assert.Equal(2L, clearBack.ClearedAt);
    }

    public static void GoalWriteTool_ExecutesThroughToolRuntime_AndAppendsTheDurableEvent()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var tools = new ToolRuntime(ctx);
        var service = new SessionGoalService(ctx);
        tools.Register(GoalTools.Definition(service));
        var session = sessions.Create();

        var args = JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"objective\":\"Port the goal seam\",\"max_goal_rounds\":10}")
            !);
        var input = new ToolExecutionInput(new ToolCallId("call-1"), "goal_write", args, CancellationToken.None) { Session = session };
        var result = tools.ExecuteAsync(input, CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(result.IsError, "goal_write must succeed through the tool runtime");
        var success = Assert.IsType<ToolExecutionSuccess>(result);
        var goal = success.Value.GetProperty("goal");
        Assert.Equal("Port the goal seam", goal.GetProperty("objective").GetString());
        Assert.Equal(1, goal.GetProperty("revision").GetInt32());
        Assert.Equal("active", goal.GetProperty("phase").GetString());
        Assert.Equal(10, goal.GetProperty("maxGoalRounds").GetInt32());
        Assert.Equal("armed", success.Value.GetProperty("activation").GetString());
        var text = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.Equal("Updated goal: \"Port the goal seam\" (revision 1, phase active, 0 of 10 rounds started).", text.Text);

        var committed = Assert.Single(session.Events.OfType<GoalWriteEvent>());
        Assert.Equal(GoalOperation.Create, committed.Operation);
        Assert.Equal("Port the goal seam", committed.Goal!.Objective);
        // The append published through session/event, so the folded state followed it.
        var folded = service.Get(session);
        Assert.NotNull(folded);
        Assert.Equal(committed.Goal.Id, folded!.Id);
        Assert.Equal(committed.Goal.Objective, folded.Objective);

        // A second call edits the same goal at the next revision.
        var editArgs = JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"objective\":\"Port the schedule seam\"}")
            !);
        var editResult = tools.ExecuteAsync(
            new ToolExecutionInput(new ToolCallId("call-2"), "goal_write", editArgs, CancellationToken.None) { Session = session },
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(editResult.IsError, "a second goal_write must edit the current goal");
        Assert.Equal(2, editResult is ToolExecutionSuccess es ? es.Value.GetProperty("goal").GetProperty("revision").GetInt32() : -1);
        Assert.Equal(2, session.Events.OfType<GoalWriteEvent>().Count());
        Assert.Equal(GoalOperation.Edit, session.Events.OfType<GoalWriteEvent>().Last().Operation);
    }

    public static void Create_RejectsBlankObjectiveAndInvalidRoundCap()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionGoalService(ctx);
        var session = sessions.Create();

        var objective = Assert.Throws<GoalError>(() => service.Create(session, "   "));
        Assert.Equal(GoalErrorCode.InvalidObjective, objective.Code);
        var rounds = Assert.Throws<GoalError>(() => service.Create(session, "objective", 0));
        Assert.Equal(GoalErrorCode.InvalidMaxRounds, rounds.Code);
    }

    public static void Create_RejectsAnExistingNonCompleteGoal()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionGoalService(ctx);
        var session = sessions.Create();

        service.Create(session, "Port the goal seam");
        var error = Assert.Throws<GoalError>(() => service.Create(session, "Another objective"));
        Assert.Equal(GoalErrorCode.AlreadyExists, error.Code);
    }

    public static void Edit_RejectsStaleRevisionsAndEmptyEdits()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionGoalService(ctx);
        var session = sessions.Create();

        var created = service.Create(session, "Port the goal seam");
        var stale = Assert.Throws<GoalError>(() => service.Edit(session, new GoalRef(created.Id, 99), "x", null));
        Assert.Equal(GoalErrorCode.StaleRevision, stale.Code);
        var empty = Assert.Throws<GoalError>(() => service.Edit(session, new GoalRef(created.Id, created.Revision), null, null));
        Assert.Equal(GoalErrorCode.InvalidEdit, empty.Code);

        var edited = service.Edit(session, new GoalRef(created.Id, created.Revision), "Port the schedule seam", null);
        Assert.Equal(2, edited.Revision);
        Assert.Equal(GoalPhase.Active, edited.Phase, "an edit must not change the phase");
        Assert.Equal(GoalActivation.Disarmed, edited.Activation, "an edit disarms the goal");
    }

    public static void PhaseTransitions_AndClear_Behave()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionGoalService(ctx);
        var session = sessions.Create();

        var created = service.Create(session, "Port the goal seam");
        var paused = service.Pause(session, new GoalRef(created.Id, created.Revision));
        Assert.Equal(GoalPhase.Paused, paused.Phase);

        var invalidBlock = Assert.Throws<GoalError>(() => service.Block(session, new GoalRef(created.Id, paused.Revision), new GoalBlockReason("stuck", "blocked")));
        Assert.Equal(GoalErrorCode.InvalidTransition, invalidBlock.Code, "block requires an active goal");

        var resumed = service.Resume(session, new GoalRef(created.Id, paused.Revision));
        Assert.Equal(GoalPhase.Active, resumed.Phase);
        Assert.Equal(GoalActivation.Armed, resumed.Activation, "a resume arms the goal");

        var completed = service.Complete(session, new GoalRef(created.Id, resumed.Revision));
        Assert.Equal(GoalPhase.Complete, completed.Phase);

        var tombstone = service.Clear(session, new GoalRef(created.Id, completed.Revision));
        Assert.Equal(created.Id, tombstone.Id);
        Assert.Equal(completed.Revision + 1, tombstone.Revision);
        Assert.Null(service.Get(session), "a clear removes the current goal");

        // A completed goal may be replaced by a fresh create.
        var replacement = service.Create(session, "Port the schedule seam");
        Assert.Equal(1, replacement.Revision);
    }

    public static void Block_RequiresAValidReason()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionGoalService(ctx);
        var session = sessions.Create();

        var created = service.Create(session, "Port the goal seam");
        var badCode = Assert.Throws<GoalError>(() => service.Block(
            session, new GoalRef(created.Id, created.Revision), new GoalBlockReason("Not Kebab", "message")));
        Assert.Equal(GoalErrorCode.InvalidBlockReason, badCode.Code);
        var badMessage = Assert.Throws<GoalError>(() => service.Block(
            session, new GoalRef(created.Id, created.Revision), new GoalBlockReason("stuck", "   ")));
        Assert.Equal(GoalErrorCode.InvalidBlockReason, badMessage.Code);

        var blocked = service.Block(session, new GoalRef(created.Id, created.Revision), new GoalBlockReason("stuck", "the provider never returns"));
        Assert.Equal(GoalPhase.Blocked, blocked.Phase);
        Assert.Equal("stuck", blocked.BlockedReason!.Code);
        Assert.Equal("the provider never returns", blocked.BlockedReason.Message);
    }

    public static void Disarm_RemovesProcessLocalAuthorityWithoutChangingTheGoal()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionGoalService(ctx);
        var session = sessions.Create();

        var created = service.Create(session, "Port the goal seam");
        Assert.Equal(GoalActivation.Armed, service.Get(session)!.Activation);
        var disarmed = service.Disarm(session);
        Assert.NotNull(disarmed);
        Assert.Equal(GoalActivation.Disarmed, disarmed!.Activation);
        Assert.Equal(created.Revision, service.Get(session)!.Revision, "disarm must not mutate the durable goal");
    }

    public static void RegistersAsTheGoalService()
    {
        using var ctx = new Context();
        var service = new SessionGoalService(ctx);

        Assert.Same(service, ctx.Get<IGoalService>("goal"));
        Assert.Same(service, SessionGoalService.Require(ctx));
    }
}
