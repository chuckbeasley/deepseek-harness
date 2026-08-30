using Dsh.Session;

namespace Dsh.Session.Projection;

/// <summary>
/// One domain's state-driven computation unit: a pure synchronous fold over committed session
/// events. The registry drives <see cref="Apply"/> on every committed event; a unit uninterested
/// in an event MUST return the same state reference — an unchanged reference produces zero
/// downstream work and lets <see cref="SessionProjectionRegistry.StateOf{T}"/> keep returning the
/// same object until the fact moves.
/// </summary>
/// <typeparam name="TState">the unit's folded state type.</typeparam>
public sealed class ProjectionUnit<TState>
{
    /// <summary>State for the empty log.</summary>
    public required Func<TState> Init { get; init; }

    /// <summary>
    /// Pure transition: previous state + one committed event → next state. Return the same
    /// reference when the event is not this unit's.
    /// </summary>
    public required Func<TState, SessionEvent, TState> Apply { get; init; }

    /// <summary>
    /// Optional client view producing the cropped snapshot value. Omit for host-only units, which
    /// stay out of <see cref="SessionProjectionRegistry.Snapshot"/>.
    /// </summary>
    public Func<TState, object?>? View { get; init; }
}
