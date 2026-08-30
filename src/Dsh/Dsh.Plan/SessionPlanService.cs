using System.Runtime.CompilerServices;
using Cordis.Core;
using Dsh.Session;

namespace Dsh.Plan;

/// <summary>
/// ctx.plan: the plan-mode service. It registers the plugin-merged <see cref="PlanWriteEvent"/> in
/// the session event-type registry (so the JSONL backend can round-trip it) and folds the session
/// log into current plan state, whole-value last-write-wins. The fold subscribes to
/// <c>session/event</c> once and advances each session's cell eagerly; a session predating the
/// service folds its committed log on first read, so resume and fork restore the state.
/// </summary>
public sealed class SessionPlanService : Service, IPlanService
{
    private readonly ConditionalWeakTable<Dsh.Session.Session, Cell> _cells = new();

    /// <summary>Create and install the service as <c>plan</c>.</summary>
    /// <param name="ctx">the owner context whose <c>session/event</c> stream is observed.</param>
    public SessionPlanService(Context ctx)
        : base(ctx, "plan")
    {
        // Plugin-boot equivalent of the TS event-type registration: the JSONL backend must
        // serialize and replay this plugin-merged event.
        SessionEventTypes.Register(PlanWriteEvent.EventTypeName, typeof(PlanWriteEvent));
        ctx.On("session/event", (Delegate)(Action<Dsh.Session.Session, SessionEvent>)Drive);
    }

    /// <summary>Read the plan service from a context, failing explicitly when it is absent.</summary>
    public static SessionPlanService Require(Context ctx) => ctx.Require<SessionPlanService>("plan");

    /// <inheritdoc />
    public PlanState Current(Dsh.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _cells.GetValue(session, BuildCell).State;
    }

    /// <summary>Eager drive: fold one committed <c>plan/write</c> into its session's cell.</summary>
    private void Drive(Dsh.Session.Session session, SessionEvent evt)
    {
        if (evt is not PlanWriteEvent write) return;
        var cell = _cells.GetValue(session, BuildCell);
        if (cell.ObservedSeq >= evt.Seq) return;
        cell.ObservedSeq = evt.Seq;
        cell.State = new PlanState(write.Plan);
    }

    /// <summary>Fold one session's committed log into the current plan state (last write wins).</summary>
    private static Cell BuildCell(Dsh.Session.Session session)
    {
        IReadOnlyList<PlanItem> items = Array.Empty<PlanItem>();
        long observed = -1;
        foreach (var evt in session.Events)
        {
            observed = evt.Seq;
            if (evt is PlanWriteEvent write) items = write.Plan;
        }
        return new Cell { State = new PlanState(items), ObservedSeq = observed };
    }

    /// <summary>One session's folded cell: the current state and the seq of the last folded event.</summary>
    private sealed class Cell
    {
        public PlanState State { get; set; } = PlanState.Empty;

        public long ObservedSeq { get; set; } = -1;
    }
}
