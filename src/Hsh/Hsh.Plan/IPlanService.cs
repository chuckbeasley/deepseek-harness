namespace Harness.Plan;

/// <summary>
/// The plan capability surface (ctx.plan): current plan state folded from the session log. The
/// fold is whole-value last-write-wins over <c>plan/write</c> events; a log with none folds to an
/// empty plan.
/// </summary>
public interface IPlanService
{
    /// <summary>Read the current plan state for one session, folded from its log.</summary>
    /// <param name="session">the session whose log is folded.</param>
    /// <returns>the items of the latest <c>plan/write</c>, or an empty plan before the first.</returns>
    PlanState Current(Harness.Session.Session session);
}
