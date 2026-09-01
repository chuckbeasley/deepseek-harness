namespace Harness.Goal;

/// <summary>
/// The goal capability surface (ctx.goal): current goal state folded from the session log plus
/// compare-and-set mutations that append durable <see cref="GoalWriteEvent"/>s. The fold is
/// whole-value last-write-wins over <c>goal/write</c> events; a log with none folds to no goal.
/// Deviating from the TS service, the surface is session-keyed rather than agent-keyed (the live
/// agent registry is a later phase), so there is no <c>GOAL_AGENT_NOT_LIVE</c> boundary here.
/// </summary>
public interface IGoalService
{
    /// <summary>Read the current goal for one session, folded from its log.</summary>
    /// <param name="session">the session whose log is folded.</param>
    /// <returns>a fresh view or <c>null</c> when no goal is current.</returns>
    GoalView? Get(Harness.Session.Session session);

    /// <summary>Remove process-local continuation authority without changing durable goal phase or revision.</summary>
    /// <param name="session">the session whose goal is disarmed.</param>
    /// <returns>a fresh disarmed view, or <c>null</c> when no goal is current.</returns>
    GoalView? Disarm(Harness.Session.Session session);

    /// <summary>Create and arm a goal. A completed goal may be replaced; every other current phase must be cleared or resumed instead.</summary>
    /// <param name="session">the owning session.</param>
    /// <param name="objective">the concrete completion objective (trimmed, non-empty).</param>
    /// <param name="maxGoalRounds">the total admitted round cap; the service default applies when omitted.</param>
    /// <returns>the created live view.</returns>
    GoalView Create(Harness.Session.Session session, string objective, int? maxGoalRounds = null);

    /// <summary>Edit objective and/or round cap without changing phase.</summary>
    /// <param name="session">the owning session.</param>
    /// <param name="reference">the expected current revision (compare-and-set).</param>
    /// <param name="objective">replacement objective; at least one replacement field must be present.</param>
    /// <param name="maxGoalRounds">replacement round cap; at least one replacement field must be present.</param>
    /// <returns>the edited view.</returns>
    GoalView Edit(Harness.Session.Session session, GoalRef reference, string? objective, int? maxGoalRounds);

    /// <summary>Pause an active goal and disarm automatic continuation.</summary>
    /// <param name="session">the owning session.</param>
    /// <param name="reference">the expected current revision.</param>
    /// <returns>the paused view.</returns>
    GoalView Pause(Harness.Session.Session session, GoalRef reference);

    /// <summary>Resume and arm a stopped goal, or rearm an active goal, while its round budget still has capacity.</summary>
    /// <param name="session">the owning session.</param>
    /// <param name="reference">the expected current revision.</param>
    /// <returns>the active view.</returns>
    GoalView Resume(Harness.Session.Session session, GoalRef reference);

    /// <summary>Mark a current non-complete goal complete and disarm it.</summary>
    /// <param name="session">the owning session.</param>
    /// <param name="reference">the expected current revision.</param>
    /// <returns>the completed view.</returns>
    GoalView Complete(Harness.Session.Session session, GoalRef reference);

    /// <summary>Mark an active goal blocked and disarm it.</summary>
    /// <param name="session">the owning session.</param>
    /// <param name="reference">the expected current revision.</param>
    /// <param name="reason">the policy-owned stable code and human-readable explanation.</param>
    /// <returns>the blocked view with its durable reason.</returns>
    GoalView Block(Harness.Session.Session session, GoalRef reference, GoalBlockReason reason);

    /// <summary>Clear the current goal while retaining a durable tombstone and history.</summary>
    /// <param name="session">the owning session.</param>
    /// <param name="reference">the expected current revision.</param>
    /// <returns>the tombstone ref whose revision is one past the cleared snapshot.</returns>
    GoalRef Clear(Harness.Session.Session session, GoalRef reference);
}
