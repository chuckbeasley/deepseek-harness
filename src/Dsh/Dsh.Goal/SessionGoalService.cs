using System.Runtime.CompilerServices;
using Harness.Cordis.Core;
using Harness.Session;

namespace Harness.Goal;

/// <summary>
/// ctx.goal: the goal service. It registers the plugin-merged <see cref="GoalWriteEvent"/> in the
/// session event-type registry (so the JSONL backend can round-trip it) and folds the session log
/// into current goal state, whole-value last-write-wins over <c>goal/write</c> events. The fold
/// subscribes to <c>session/event</c> once and advances each session's cell eagerly; a session
/// predating the service folds its committed log on first read, so resume and fork restore the
/// state. Mutations validate against the current view (compare-and-set revisions, phase
/// transitions) and append the durable event; unlike the TS strict replay fold, replay does not
/// re-validate transitions — the service is the single writer, so the fold trusts it.
/// The <c>Session</c> type is fully qualified because the <c>Dsh</c> root namespace member
/// <c>Session</c> (the Harness.Session namespace) shadows the imported type at simple-name lookup.
/// </summary>
public sealed class SessionGoalService : Service, IGoalService
{
    private const int DefaultMaxGoalRounds = 256;

    private readonly int _defaultMaxGoalRounds;
    private readonly ConditionalWeakTable<Harness.Session.Session, Cell> _cells = new();

    /// <summary>Create and install the service as <c>goal</c>.</summary>
    /// <param name="ctx">the owner context whose <c>session/event</c> stream is observed.</param>
    /// <param name="defaultMaxGoalRounds">the round cap used when a create request omits its own.</param>
    /// <exception cref="ArgumentOutOfRangeException">when <paramref name="defaultMaxGoalRounds"/> is not a positive integer.</exception>
    public SessionGoalService(Context ctx, int defaultMaxGoalRounds = DefaultMaxGoalRounds)
        : base(ctx, "goal")
    {
        if (defaultMaxGoalRounds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultMaxGoalRounds), "defaultMaxGoalRounds must be a positive integer");
        }
        _defaultMaxGoalRounds = defaultMaxGoalRounds;
        // Plugin-boot equivalent of the TS event-type registration: the JSONL backend must
        // serialize and replay this plugin-merged event.
        SessionEventTypes.Register(GoalWriteEvent.EventTypeName, typeof(GoalWriteEvent));
        ctx.On("session/event", (Delegate)(Action<Harness.Session.Session, SessionEvent>)Drive);
    }

    /// <summary>Read the goal service from a context, failing explicitly when it is absent.</summary>
    public static SessionGoalService Require(Context ctx) => ctx.Require<SessionGoalService>("goal");

    /// <inheritdoc />
    public GoalView? Get(Harness.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cell = _cells.GetValue(session, BuildCell);
        return ToView(cell.State, cell.Activation);
    }

    /// <inheritdoc />
    public GoalView? Disarm(Harness.Session.Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var cell = _cells.GetValue(session, BuildCell);
        cell.Activation = GoalActivation.Disarmed;
        return ToView(cell.State, cell.Activation);
    }

    /// <inheritdoc />
    public GoalView Create(Harness.Session.Session session, string objective, int? maxGoalRounds = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var specObjective = ResolveObjective(objective);
        var specRounds = ResolveMaxGoalRounds(maxGoalRounds ?? _defaultMaxGoalRounds);
        var current = Get(session);
        if (current is not null && current.Phase != GoalPhase.Complete)
        {
            throw new GoalError($"goal \"{current.Id}\" already exists with phase \"{current.Phase}\"", GoalErrorCode.AlreadyExists);
        }
        var now = Now();
        var goal = new GoalSnapshot($"goal-{Guid.NewGuid()}", 1, specObjective, GoalPhase.Active, null, specRounds);
        return CommitSnapshot(session, GoalOperation.Create, goal, 0, now, now, GoalActivation.Armed);
    }

    /// <inheritdoc />
    public GoalView Edit(Harness.Session.Session session, GoalRef reference, string? objective, int? maxGoalRounds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reference);
        if (objective is null && maxGoalRounds is null)
        {
            throw new GoalError("goal edit requires objective and/or maxGoalRounds", GoalErrorCode.InvalidEdit);
        }
        var current = ExpectCurrent(session, reference);
        var goal = new GoalSnapshot(
            current.Id,
            current.Revision + 1,
            objective is null ? current.Objective : ResolveObjective(objective),
            current.Phase,
            current.BlockedReason,
            maxGoalRounds is null ? current.MaxGoalRounds : ResolveMaxGoalRounds(maxGoalRounds.Value));
        return CommitCurrent(session, current, GoalOperation.Edit, goal, GoalActivation.Disarmed);
    }

    /// <inheritdoc />
    public GoalView Pause(Harness.Session.Session session, GoalRef reference)
        => Transition(session, reference, GoalOperation.Pause, [GoalPhase.Active], GoalPhase.Paused, GoalActivation.Disarmed);

    /// <inheritdoc />
    public GoalView Resume(Harness.Session.Session session, GoalRef reference)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reference);
        var current = ExpectCurrent(session, reference);
        if (current.Phase is not GoalPhase.Active and not GoalPhase.Paused and not GoalPhase.Blocked)
        {
            throw TransitionError(current, GoalOperation.Resume, [GoalPhase.Active, GoalPhase.Paused, GoalPhase.Blocked]);
        }
        if (current.Phase == GoalPhase.Active && current.Activation == GoalActivation.Armed)
        {
            throw new GoalError($"goal \"{current.Id}\" is already active and armed", GoalErrorCode.InvalidTransition);
        }
        if (current.RoundsStarted >= current.MaxGoalRounds)
        {
            throw new GoalError(
                $"goal \"{current.Id}\" exhausted {current.MaxGoalRounds} goal rounds; increase maxGoalRounds before resuming",
                GoalErrorCode.InvalidTransition);
        }
        return CommitCurrent(session, current, GoalOperation.Resume, WithPhase(current, GoalPhase.Active), GoalActivation.Armed);
    }

    /// <inheritdoc />
    public GoalView Complete(Harness.Session.Session session, GoalRef reference)
        => Transition(session, reference, GoalOperation.Complete, [GoalPhase.Active, GoalPhase.Paused, GoalPhase.Blocked], GoalPhase.Complete, GoalActivation.Disarmed);

    /// <inheritdoc />
    public GoalView Block(Harness.Session.Session session, GoalRef reference, GoalBlockReason reason)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(reason);
        var current = ExpectCurrent(session, reference);
        if (current.Phase != GoalPhase.Active)
        {
            throw TransitionError(current, GoalOperation.Block, [GoalPhase.Active]);
        }
        var resolved = ResolveBlockReason(reason);
        var blocked = new GoalSnapshot(current.Id, current.Revision + 1, current.Objective, GoalPhase.Blocked, resolved, current.MaxGoalRounds);
        return CommitCurrent(session, current, GoalOperation.Block, blocked, GoalActivation.Disarmed);
    }

    /// <inheritdoc />
    public GoalRef Clear(Harness.Session.Session session, GoalRef reference)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reference);
        var current = ExpectCurrent(session, reference);
        var tombstone = new GoalRef(current.Id, current.Revision + 1);
        var cell = _cells.GetValue(session, BuildCell);
        var write = new GoalWriteEvent
        {
            Operation = GoalOperation.Clear,
            Cleared = tombstone,
            ClearedAt = NextMutationTime(current.UpdatedAt),
        };
        Commit(cell, session, write, GoalActivation.Disarmed);
        return tombstone;
    }

    /// <summary>Eager drive: fold one committed <c>goal/write</c> into its session's cell.</summary>
    private void Drive(Harness.Session.Session session, SessionEvent evt)
    {
        if (evt is not GoalWriteEvent write) return;
        var cell = _cells.GetValue(session, BuildCell);
        if (cell.ObservedSeq >= evt.Seq) return;
        cell.ObservedSeq = evt.Seq;
        cell.State = ApplyWrite(cell.State, write);
        // The TS service reconciles the process-local activation edge against the seq it
        // appended; any other writer (or a replay) leaves the goal disarmed.
        cell.Activation = cell.PendingActivationSeq == evt.Seq
            ? cell.PendingActivation
            : GoalActivation.Disarmed;
    }

    /// <summary>Fold one session's committed log into the current goal state (last write wins).</summary>
    private static Cell BuildCell(Harness.Session.Session session)
    {
        var state = GoalState.Empty;
        long observed = -1;
        foreach (var evt in session.Events)
        {
            observed = evt.Seq;
            if (evt is GoalWriteEvent write) state = ApplyWrite(state, write);
        }
        return new Cell { State = state, ObservedSeq = observed };
    }

    /// <summary>Apply one decoded write to a folded state: snapshot ops replace, a clear empties.</summary>
    private static GoalState ApplyWrite(GoalState state, GoalWriteEvent write)
    {
        if (write.Operation == GoalOperation.Clear) return GoalState.Empty;
        return new GoalState(write.Goal, write.RoundsStarted, write.CreatedAt, write.UpdatedAt);
    }

    /// <summary>Project a folded state plus its process-local activation into a detached view.</summary>
    private static GoalView? ToView(GoalState state, GoalActivation activation)
    {
        if (state.Goal is null) return null;
        var goal = state.Goal;
        return new GoalView(
            goal.Id, goal.Revision, goal.Objective, goal.Phase, goal.BlockedReason, goal.MaxGoalRounds,
            state.RoundsStarted, state.CreatedAt, state.UpdatedAt, activation);
    }

    /// <summary>Reject a stale or missing current-state ref.</summary>
    private GoalView ExpectCurrent(Harness.Session.Session session, GoalRef reference)
    {
        var current = Get(session);
        if (current is null) throw new GoalError("no current goal", GoalErrorCode.NotFound);
        if (reference.Id != current.Id || reference.Revision != current.Revision)
        {
            throw new GoalError(
                $"stale goal ref \"{reference.Id}\" revision {reference.Revision}; current is \"{current.Id}\" revision {current.Revision}",
                GoalErrorCode.StaleRevision);
        }
        return current;
    }

    /// <summary>Build a new revision with one replacement phase, retaining the definition.</summary>
    private static GoalSnapshot WithPhase(GoalView current, GoalPhase phase)
        => new(current.Id, current.Revision + 1, current.Objective, phase, current.BlockedReason, current.MaxGoalRounds);

    /// <summary>Shared validated phase transition.</summary>
    private GoalView Transition(Harness.Session.Session session, GoalRef reference, GoalOperation operation, GoalPhase[] allowed, GoalPhase phase, GoalActivation activation)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reference);
        var current = ExpectCurrent(session, reference);
        if (!allowed.Contains(current.Phase)) throw TransitionError(current, operation, allowed);
        return CommitCurrent(session, current, operation, WithPhase(current, phase), activation);
    }

    /// <summary>Render a stable invalid-transition error.</summary>
    private static GoalError TransitionError(GoalView current, GoalOperation operation, GoalPhase[] allowed)
        => new(
            $"cannot {OperationName(operation)} goal \"{current.Id}\" from phase \"{PhaseName(current.Phase)}\"; expected {string.Join(" or ", allowed.Select(PhaseName))}",
            GoalErrorCode.InvalidTransition);

    /// <summary>Commit a mutation that retains the current goal's derived counters and timestamps.</summary>
    private GoalView CommitCurrent(Harness.Session.Session session, GoalView current, GoalOperation operation, GoalSnapshot goal, GoalActivation activation)
        => CommitSnapshot(session, operation, goal, current.RoundsStarted, current.CreatedAt, NextMutationTime(current.UpdatedAt), activation);

    /// <summary>Clamp a current goal's next timestamp across backward wall-clock movement.</summary>
    private static long NextMutationTime(long updatedAt) => Math.Max(Now(), updatedAt);

    /// <summary>Build and commit one full-snapshot mutation.</summary>
    private GoalView CommitSnapshot(Harness.Session.Session session, GoalOperation operation, GoalSnapshot goal, int roundsStarted, long createdAt, long updatedAt, GoalActivation activation)
    {
        var cell = _cells.GetValue(session, BuildCell);
        Commit(cell, session, new GoalWriteEvent
        {
            Operation = operation,
            Goal = goal,
            RoundsStarted = roundsStarted,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        }, activation);
        return ToView(cell.State, cell.Activation)!;
    }

    /// <summary>Append one mutation and let the synchronous <c>session/event</c> publication drive the fold.</summary>
    private void Commit(Cell cell, Harness.Session.Session session, GoalWriteEvent write, GoalActivation activation)
    {
        cell.PendingActivationSeq = session.Seq;
        cell.PendingActivation = activation;
        try
        {
            session.Append(write);
        }
        finally
        {
            cell.PendingActivationSeq = -1;
            cell.PendingActivation = GoalActivation.Disarmed;
        }
    }

    /// <summary>Validate and normalize an objective at the domain boundary.</summary>
    private static string ResolveObjective(string objective)
    {
        ArgumentNullException.ThrowIfNull(objective);
        var trimmed = objective.Trim();
        if (trimmed.Length == 0)
        {
            throw new GoalError("goal objective must be a non-empty string", GoalErrorCode.InvalidObjective);
        }
        return trimmed;
    }

    /// <summary>Require a caller-visible positive safe-integer round cap.</summary>
    private static int ResolveMaxGoalRounds(int value)
    {
        if (value < 1)
        {
            throw new GoalError("maxGoalRounds must be a positive integer", GoalErrorCode.InvalidMaxRounds);
        }
        return value;
    }

    /// <summary>Validate one policy-owned blocker explanation.</summary>
    private static GoalBlockReason ResolveBlockReason(GoalBlockReason reason)
    {
        var code = reason.Code;
        if (code.Length == 0 || code != code.Trim() || !IsLowerKebab(code))
        {
            throw new GoalError(
                "goal block reason requires a lower-kebab-case code and a non-empty message",
                GoalErrorCode.InvalidBlockReason);
        }
        var message = reason.Message.Trim();
        if (message.Length == 0)
        {
            throw new GoalError(
                "goal block reason requires a lower-kebab-case code and a non-empty message",
                GoalErrorCode.InvalidBlockReason);
        }
        return new GoalBlockReason(code, message);
    }

    /// <summary>Whether a string is lower-kebab-case: lower-case letters/digits separated by single dashes.</summary>
    private static bool IsLowerKebab(string value)
    {
        if (value.Length == 0 || !IsLowerAlpha(value[0])) return false;
        var lastWasDash = false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '-')
            {
                if (lastWasDash) return false;
                lastWasDash = true;
                continue;
            }
            if (!IsLowerAlpha(c) && !char.IsDigit(c)) return false;
            lastWasDash = false;
        }
        return !lastWasDash;
    }

    private static bool IsLowerAlpha(char c) => c is >= 'a' and <= 'z';

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string OperationName(GoalOperation operation) => operation switch
    {
        GoalOperation.Create => "create",
        GoalOperation.Edit => "edit",
        GoalOperation.Pause => "pause",
        GoalOperation.Resume => "resume",
        GoalOperation.Complete => "complete",
        GoalOperation.Block => "block",
        GoalOperation.Clear => "clear",
        _ => operation.ToString(),
    };

    private static string PhaseName(GoalPhase phase) => phase switch
    {
        GoalPhase.Active => "active",
        GoalPhase.Paused => "paused",
        GoalPhase.Blocked => "blocked",
        GoalPhase.Complete => "complete",
        _ => phase.ToString(),
    };

    /// <summary>One session's folded cell: the current state, activation, and the seq of the last folded event.</summary>
    private sealed class Cell
    {
        public GoalState State { get; set; } = GoalState.Empty;

        public GoalActivation Activation { get; set; } = GoalActivation.Disarmed;

        public long ObservedSeq { get; set; } = -1;

        public long PendingActivationSeq { get; set; } = -1;

        public GoalActivation PendingActivation { get; set; } = GoalActivation.Disarmed;
    }
}
