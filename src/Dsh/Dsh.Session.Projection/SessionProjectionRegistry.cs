using System.Runtime.CompilerServices;
using Cordis.Core;
using Dsh.Session;

namespace Dsh.Session.Projection;

/// <summary>
/// ctx.sessionProjections: the projection unit table and its drive. The service subscribes to
/// <c>session/event</c> once; every committed event passes every registered unit's
/// <see cref="ProjectionUnit{TState}.Apply"/> (eager drive), and a unit's state reference is only
/// replaced when the unit returns a new reference. Cells build lazily — a unit registered after
/// events flowed, or a session older than the registry, folds <c>init</c> over the in-memory log
/// on first touch. Registration is an effect (the disposer rides the calling fiber) and a second
/// registration of one key fails loud. A host reader requires this service during activation via
/// <see cref="Require"/> or fails explicitly when the registry is absent.
/// </summary>
public sealed class SessionProjectionRegistry : Service
{
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);

    /// <summary>Create and install the registry as <c>sessionProjections</c>.</summary>
    /// <param name="ctx">the context that owns the service.</param>
    public SessionProjectionRegistry(Context ctx)
        : base(ctx, "sessionProjections")
    {
        ctx.On("session/event", (Delegate)(Action<Session, SessionEvent>)Drive);
    }

    /// <summary>Read the registry from a context, failing explicitly when it is absent (host-reader contract).</summary>
    public static SessionProjectionRegistry Require(Context ctx)
    {
        return ctx.Require<SessionProjectionRegistry>("sessionProjections");
    }

    /// <summary>Whether a unit is currently registered under the key.</summary>
    public bool IsRegistered(string key) => _registrations.ContainsKey(key);

    /// <summary>
    /// Register one domain's unit. The registration is an effect on the calling context's fiber:
    /// disposing the fiber (or the returned disposer) removes the key from subsequent drives and
    /// snapshots. A second registration of the same key fails loud.
    /// </summary>
    /// <param name="key">the projection key this unit owns.</param>
    /// <param name="unit">the pure fold unit.</param>
    /// <returns>the disposer that unregisters this unit.</returns>
    /// <exception cref="InvalidOperationException">when the key is already registered.</exception>
    public IDisposable Register<TState>(string key, ProjectionUnit<TState> unit)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(unit);
        var registration = new Registration
        {
            Key = key,
            Init = () => unit.Init(),
            Apply = (state, evt) => unit.Apply((TState)state!, evt),
            View = unit.View is null ? null : state => unit.View((TState)state!),
        };
        return Ctx.Effect(() =>
        {
            if (_registrations.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"session projection key \"{key}\" is already registered; a second registration fails loud");
            }
            _registrations[key] = registration;
            return new DisposableAction(() => _registrations.Remove(key));
        }, $"sessionProjections.register(\"{key}\")");
    }

    /// <summary>
    /// Read one unit's current host state for a session, materializing the unit's cell lazily over
    /// the session's committed log when needed. The returned value is the live cell state; callers
    /// must not mutate it.
    /// </summary>
    /// <param name="session">the session whose state is read.</param>
    /// <param name="key">the registered unit key.</param>
    /// <returns>current state, or <c>default</c> when the key is not registered (host readers
    /// combine <see cref="IsRegistered"/> with <see cref="Require"/> to fail loud).</returns>
    /// <exception cref="InvalidOperationException">when the registered state is not assignable to <typeparamref name="TState"/>.</exception>
    public TState? StateOf<TState>(Session session, string key)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_registrations.TryGetValue(key, out var registration))
        {
            return default;
        }
        var cell = CellFor(registration, session);
        if (cell.State is not TState typed)
        {
            throw new InvalidOperationException(
                $"session projection \"{key}\" state is {cell.State?.GetType().FullName ?? "null"}, not {typeof(TState).FullName}");
        }
        return typed;
    }

    /// <summary>
    /// One consistent cut over every registered client-visible unit for one session: each selected
    /// key's <see cref="ProjectionUnit{TState}.View"/> of its current state, all read at the same
    /// log position. Host-only units (no view) are always omitted.
    /// </summary>
    /// <param name="session">the session whose projection values are read.</param>
    /// <param name="keys">optional client-visible keys to include; null includes every registered view.</param>
    /// <returns>the snapshot; <see cref="ProjectionSnapshot.Values"/> is empty when no selected unit is registered.</returns>
    public ProjectionSnapshot Snapshot(Session session, IReadOnlyCollection<string>? keys = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var selected = keys is null ? null : new HashSet<string>(keys, StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var registration in _registrations.Values)
        {
            if (registration.View is null) continue;
            if (selected is not null && !selected.Contains(registration.Key)) continue;
            var cell = CellFor(registration, session);
            values[registration.Key] = registration.View(cell.State);
        }
        return new ProjectionSnapshot(session.Seq - 1, values);
    }

    /// <summary>Eager drive: pass one committed event through every registered unit, advancing that session's cells.</summary>
    private void Drive(Session session, SessionEvent evt)
    {
        foreach (var registration in _registrations.Values)
        {
            if (!registration.Cells.TryGetValue(session, out var cell))
            {
                // Late build mid-stream: fold history before this event, then take the normal gate.
                cell = BuildCell(registration, session, throughSeq: evt.Seq - 1);
                registration.Cells.Add(session, cell);
            }
            if (cell.ObservedSeq >= evt.Seq) continue;
            cell.State = registration.Apply(cell.State, evt);
            cell.ObservedSeq = evt.Seq;
        }
    }

    /// <summary>Read (or lazily build, folding the session's committed log) one unit's cell.</summary>
    private static UnitCell CellFor(Registration registration, Session session)
    {
        if (!registration.Cells.TryGetValue(session, out var cell))
        {
            cell = BuildCell(registration, session, throughSeq: session.Seq - 1);
            registration.Cells.Add(session, cell);
        }
        else
        {
            AdvanceCell(registration, cell, session);
        }
        return cell;
    }

    /// <summary>Fold one unit from init over the events up to and including <paramref name="throughSeq"/>.</summary>
    private static UnitCell BuildCell(Registration registration, Session session, long throughSeq)
    {
        object? state = registration.Init();
        var events = session.Events;
        var count = (int)Math.Min(events.Count, throughSeq + 1);
        for (var seq = 0; seq < count; seq++)
        {
            state = registration.Apply(state, events[seq]);
        }
        return new UnitCell { State = state, ObservedSeq = count - 1 };
    }

    /// <summary>Advance one existing cell through a contiguous session prefix.</summary>
    private static void AdvanceCell(Registration registration, UnitCell cell, Session session)
    {
        var throughSeq = session.Seq - 1;
        if (cell.ObservedSeq >= throughSeq) return;
        var events = session.Events;
        for (var seq = cell.ObservedSeq + 1; seq <= throughSeq; seq++)
        {
            if (seq >= events.Count || events[(int)seq].Seq != seq)
            {
                throw new InvalidOperationException(
                    $"session projection \"{registration.Key}\" cannot advance across missing seq {seq}");
            }
            cell.State = registration.Apply(cell.State, events[(int)seq]);
            cell.ObservedSeq = seq;
        }
    }

    /// <summary>One live registration: the unit plus its per-session cells (keyed weakly, so a disposed session's cell is collectable).</summary>
    private sealed class Registration
    {
        public required string Key { get; init; }

        public required Func<object?> Init { get; init; }

        public required Func<object?, SessionEvent, object?> Apply { get; init; }

        public Func<object?, object?>? View { get; init; }

        public ConditionalWeakTable<Session, UnitCell> Cells { get; } = new();
    }

    /// <summary>One session's per-unit cell: the folded state and the seq of the last folded event.</summary>
    private sealed class UnitCell
    {
        public object? State { get; set; }

        public long ObservedSeq { get; set; } = -1;
    }
}

/// <summary>Minimal <see cref="IDisposable"/> built from an action (Cordis.Core's is internal).</summary>
internal sealed class DisposableAction : IDisposable
{
    private readonly Action _action;

    public DisposableAction(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}
