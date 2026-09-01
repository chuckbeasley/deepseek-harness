namespace Harness.Goal;

/// <summary>Stable error codes for rejected goal reads and mutations (TS <c>GoalErrorCode</c>).</summary>
public static class GoalErrorCode
{
    public const string NotFound = "GOAL_NOT_FOUND";
    public const string AlreadyExists = "GOAL_ALREADY_EXISTS";
    public const string StaleRevision = "GOAL_STALE_REVISION";
    public const string InvalidObjective = "GOAL_INVALID_OBJECTIVE";
    public const string InvalidMaxRounds = "GOAL_INVALID_MAX_ROUNDS";
    public const string InvalidBlockReason = "GOAL_INVALID_BLOCK_REASON";
    public const string InvalidEdit = "GOAL_INVALID_EDIT";
    public const string InvalidTransition = "GOAL_INVALID_TRANSITION";
}

/// <summary>Error returned by the goal domain boundary with a stable machine-routable code.</summary>
public sealed class GoalError : Exception
{
    /// <summary>Create the failure; <paramref name="code"/> is one of <see cref="GoalErrorCode"/>.</summary>
    public GoalError(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable machine-routable classification.</summary>
    public string Code { get; }
}
