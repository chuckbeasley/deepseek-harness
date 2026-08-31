using System.Text.Json.Serialization;

namespace Dsh.Interaction;

/// <summary>
/// Every approval outcome the seam produces (port of the TS <c>ApprovalOutcome</c>).
/// <see cref="AllowedOnce"/> is the only grant; everything else fails the request closed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalOutcome
{
    /// <summary>The request is granted for exactly this action.</summary>
    [JsonStringEnumMemberName("allowed-once")]
    AllowedOnce,

    /// <summary>The request is refused.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    /// <summary>The request was withdrawn before a decision.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>No answerer was available; the request failed closed.</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

/// <summary>
/// A session's approval policy (port of the TS <c>ApprovalPolicy</c>): <see cref="Ask"/> delegates
/// every ask to the composed answerers (fail-closed with none); <see cref="Never"/> rejects every
/// ask deterministically without prompting (the strict headless stance).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalPolicy
{
    /// <summary>Delegate every ask to the composed answerers.</summary>
    [JsonStringEnumMemberName("ask")]
    Ask,

    /// <summary>Reject every ask deterministically, without prompting.</summary>
    [JsonStringEnumMemberName("never")]
    Never,
}

/// <summary>
/// One pending approval decision (port of the TS <c>ApprovalRequest</c>): the agent on whose behalf
/// the question is asked (its session receives the audit pair), the tool the question is about, and
/// the asker's reason. Aborting the token withdraws the question: the ask settles
/// <see cref="ApprovalOutcome.Cancelled"/> and a late answer is discarded.
/// </summary>
public sealed record ApprovalRequest(
    Dsh.Agent.Agent Agent,
    string ToolName,
    string? CallId = null,
    string? Reason = null,
    CancellationToken? CancellationToken = null);

/// <summary>One question the model asks the human (port of the TS <c>AskUserQuestionItem</c>).</summary>
public sealed record UserQuestionItem(
    string Id,
    string Question,
    string? Header = null,
    IReadOnlyList<UserQuestionOption>? Options = null,
    bool MultiSelect = false);

/// <summary>One optional choice shown under a question; the label is what the answer selects.</summary>
public sealed record UserQuestionOption(string Label, string? Description = null);

/// <summary>One human answer to a question: the selected option labels and an optional typed answer.</summary>
public sealed record UserQuestionAnswerItem(string Id, IReadOnlyList<string> Selected, string? Custom = null);

/// <summary>The complete answer to one <see cref="UserQuestionService.AskAsync"/> call.</summary>
public sealed record UserQuestionAnswer(IReadOnlyList<UserQuestionAnswerItem> Answers);

/// <summary>
/// One ask (port of the TS <c>AskUserQuestionRequest</c>): the questions, the optional calling agent,
/// and the abort signal. An aborted token settles the ask with <see cref="UserQuestionError"/> code
/// <c>ASK_ABORTED</c>.
/// </summary>
public sealed record UserQuestionRequest(
    IReadOnlyList<UserQuestionItem> Questions,
    Dsh.Agent.Agent? Agent = null,
    CancellationToken? CancellationToken = null);

/// <summary>Stable error taxonomy for user-questions failures (port of the TS <c>UserQuestionError</c>).</summary>
public sealed class UserQuestionError : Exception
{
    /// <summary>Create the coded failure.</summary>
    public UserQuestionError(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable machine code (<c>ASK_ABORTED</c>, <c>EMPTY_QUESTIONS</c>, <c>UNAVAILABLE</c>).</summary>
    public string Code { get; }
}
