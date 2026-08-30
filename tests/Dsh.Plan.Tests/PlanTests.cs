using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Plan.Tests;

public static class PlanTests
{
    private static readonly PlanItem[] FirstPlan =
    {
        new("Port the identity seam", PlanItemStatus.InProgress),
        new("Port the plan seam", PlanItemStatus.Pending),
    };

    private static readonly PlanItem[] SecondPlan =
    {
        new("Port the identity seam", PlanItemStatus.Completed),
        new("Port the plan seam", PlanItemStatus.InProgress),
        new("Run the Phase 4 gate", PlanItemStatus.Pending),
    };

    public static void EmptyLog_YieldsAnEmptyPlan()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionPlanService(ctx);
        var session = sessions.Create();

        Assert.Empty(service.Current(session).Items, "a log with no plan/write folds to an empty plan");
    }

    public static void PlanWriteEvents_FoldLastWriteWins()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionPlanService(ctx);
        var session = sessions.Create();

        session.Append(new PlanWriteEvent { Plan = FirstPlan });
        session.Append(new PlanWriteEvent { Plan = SecondPlan });

        Assert.Equal(SecondPlan, service.Current(session).Items, "the latest plan/write must win the fold");
    }

    public static void StateUpdatesLiveOnSessionEvent()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var service = new SessionPlanService(ctx);
        var session = sessions.Create();

        session.Append(new TurnStartEvent { Turn = 1 });
        Assert.Empty(service.Current(session).Items, "non-plan events must not change the plan");

        session.Append(new PlanWriteEvent { Plan = FirstPlan });
        Assert.Equal(FirstPlan, service.Current(session).Items, "the session/event subscription must drive the fold live");

        session.Append(new PlanWriteEvent { Plan = SecondPlan });
        Assert.Equal(SecondPlan, service.Current(session).Items);
    }

    public static void PlanWriteEvent_RoundTripsTheJsonl()
    {
        using var ctx = new Context();
        _ = new SessionPlanService(ctx); // registers plan/write into the session event-type registry

        var evt = new PlanWriteEvent
        {
            Id = "evt-0",
            Seq = 0,
            TimeMs = 1,
            Plan = FirstPlan,
        };
        var options = SessionEventTypes.CreateSerializerOptions();
        var json = JsonSerializer.Serialize<SessionEvent>(evt, options);
        var back = Assert.IsType<PlanWriteEvent>(JsonSerializer.Deserialize<SessionEvent>(json, options));

        Assert.Equal("plan/write", back.Type);
        Assert.Equal(evt.Type, back.Type);
        Assert.Equal(evt.Id, back.Id);
        Assert.Equal(evt.Seq, back.Seq);
        Assert.Equal(evt.Plan, back.Plan);
    }

    public static void PlanWriteTool_ExecutesThroughToolRuntime_AndAppendsTheDurableEvent()
    {
        using var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var tools = new ToolRuntime(ctx);
        var service = new SessionPlanService(ctx);
        tools.Register(PlanTools.Definition());
        var session = sessions.Create();

        var args = JsonSerializer.SerializeToElement(JsonNode.Parse(
            "{\"plan\":[{\"content\":\"Port the identity seam\",\"status\":\"in_progress\"},{\"content\":\"Port the plan seam\",\"status\":\"pending\"}]}")
            !);
        var input = new ToolExecutionInput(new ToolCallId("call-1"), "plan_write", args, CancellationToken.None) { Session = session };
        var result = tools.ExecuteAsync(input, CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(result.IsError, "plan_write must succeed through the tool runtime");
        var success = Assert.IsType<ToolExecutionSuccess>(result);
        Assert.Equal(2, success.Value.GetProperty("plan").GetArrayLength());
        var counts = success.Value.GetProperty("counts");
        Assert.Equal(1, counts.GetProperty("pending").GetInt32());
        Assert.Equal(1, counts.GetProperty("inProgress").GetInt32());
        Assert.Equal(0, counts.GetProperty("completed").GetInt32());
        var text = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.Equal("Updated plan: 1 pending, 1 in progress, 0 completed.", text.Text);

        var committed = Assert.Single(session.Events.OfType<PlanWriteEvent>());
        Assert.Equal(2, committed.Plan.Count);
        Assert.Equal("Port the identity seam", committed.Plan[0].Content);
        Assert.Equal(PlanItemStatus.InProgress, committed.Plan[0].Status);
        Assert.Equal(PlanItemStatus.Pending, committed.Plan[1].Status);
        // The append published through session/event, so the folded state followed it.
        Assert.Equal(committed.Plan, service.Current(session).Items);
    }

    public static void Write_RejectsEmptyContentDuplicatesAndMultipleInProgress()
    {
        Assert.Throws<ArgumentException>(() => PlanTools.Write(new[] { new PlanItem("   ", PlanItemStatus.Pending) }));
        Assert.Throws<ArgumentException>(() => PlanTools.Write(new[]
        {
            new PlanItem("same", PlanItemStatus.Pending),
            new PlanItem("same", PlanItemStatus.Pending),
        }));
        Assert.Throws<ArgumentException>(() => PlanTools.Write(new[]
        {
            new PlanItem("a", PlanItemStatus.InProgress),
            new PlanItem("b", PlanItemStatus.InProgress),
        }));
    }

    public static void RegistersAsThePlanService()
    {
        using var ctx = new Context();
        var service = new SessionPlanService(ctx);

        Assert.Same(service, ctx.Get<IPlanService>("plan"));
        Assert.Same(service, SessionPlanService.Require(ctx));
    }
}
