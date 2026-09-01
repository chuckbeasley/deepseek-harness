namespace Harness.Authorization;

/// <summary>One way a flow can obtain its credential, named by the flow that offers it.</summary>
public sealed record AuthorizationMethod(
    /// <summary>Flow-owned identifier, echoed back when a caller picks this method.</summary>
    string Id,
    /// <summary>User-facing label for a picker.</summary>
    string Label);

/// <summary>A running flow's report to whoever is watching it. Never carries a secret.</summary>
public sealed record AuthorizationNotice(
    /// <summary>What is happening, or what the human must do next.</summary>
    string Message,
    /// <summary>A page the human must open to continue.</summary>
    string? Url = null,
    /// <summary>A short code the human must enter on that page.</summary>
    string? Code = null);

/// <summary>One choice offered by a <see cref="AuthorizationPromptKind.Select"/> prompt.</summary>
public sealed record AuthorizationPromptOption(
    /// <summary>Value returned when this option is chosen.</summary>
    string Id,
    /// <summary>User-facing label.</summary>
    string Label,
    /// <summary>Optional extra context rendered by capable surfaces.</summary>
    string? Description = null);

/// <summary>How one authorization attempt ended, as its own caller sees it.</summary>
public enum AuthorizationStatus
{
    /// <summary>The record was committed during this attempt and observed.</summary>
    Authorized,
    /// <summary>The human declined or the caller withdrew.</summary>
    Cancelled,
}

/// <summary>
/// How one attempt ended, as an onlooker sees it. A failure reaches its caller as a thrown error
/// rather than an outcome, so <see cref="Failed"/> exists only here — on the event stream, where a
/// watcher that did not start the attempt has no other way to tell a refusal from a breakage.
/// </summary>
public enum AuthorizationSettlement
{
    /// <summary>The attempt authorized its key.</summary>
    Authorized,
    /// <summary>The attempt was cancelled.</summary>
    Cancelled,
    /// <summary>The attempt failed; its caller saw the thrown error.</summary>
    Failed,
}

/// <summary>
/// The kind of question an <see cref="AuthorizationPrompt"/> asks. <c>secret</c> differs from
/// <c>text</c> only in presentation — a surface masks it and keeps it out of logs — and
/// <c>select</c> answers with the chosen option's id.
/// </summary>
public enum AuthorizationPromptKind
{
    /// <summary>Free text input.</summary>
    Text,
    /// <summary>Masked text input, kept out of logs.</summary>
    Secret,
    /// <summary>One choice from the prompt's options.</summary>
    Select,
}

/// <summary>
/// A question a flow must have answered before it can continue.
/// </summary>
public abstract record AuthorizationPrompt(
    /// <summary>How the question is presented and answered.</summary>
    AuthorizationPromptKind Kind,
    /// <summary>The question text.</summary>
    string Message)
{
    /// <summary>Optional input placeholder; meaningful only for text and secret prompts.</summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// Withdraws this prompt alone, leaving the flow running. A flow that races a typed code
    /// against a browser callback aborts the losing prompt here; the whole authorization is
    /// cancelled through the request's signal instead.
    /// </summary>
    public CancellationToken? Signal { get; init; }
}

/// <summary>A free-text question.</summary>
public sealed record AuthorizationTextPrompt(string Message) : AuthorizationPrompt(AuthorizationPromptKind.Text, Message);

/// <summary>A masked-input question.</summary>
public sealed record AuthorizationSecretPrompt(string Message) : AuthorizationPrompt(AuthorizationPromptKind.Secret, Message);

/// <summary>A single-choice question; the answer is the chosen option's id.</summary>
public sealed record AuthorizationSelectPrompt(string Message, IReadOnlyList<AuthorizationPromptOption> Options)
    : AuthorizationPrompt(AuthorizationPromptKind.Select, Message);

/// <summary>The result of one <see cref="IAuthorizationService.BeginAsync"/> attempt.</summary>
public sealed record AuthorizationOutcome(
    /// <summary><see cref="AuthorizationStatus.Authorized"/> once the record is committed and
    /// observed; <see cref="AuthorizationStatus.Cancelled"/> when the human or caller withdrew.</summary>
    AuthorizationStatus Status);

/// <summary>A registered flow as a surface sees it: what it authorizes and whether it is busy.</summary>
public sealed record AuthorizationEntry(
    /// <summary>The credential record this flow writes.</summary>
    string Key,
    /// <summary>User-facing name of what is being authorized.</summary>
    string Label,
    /// <summary>The methods this flow offers, most preferred first.</summary>
    IReadOnlyList<AuthorizationMethod> Methods,
    /// <summary>Whether an attempt for this key is running right now.</summary>
    bool InFlight);
