using Dsh.Credentials;

namespace Dsh.Authorization;

/// <summary>Stable error taxonomy for authorization failures.</summary>
public class AuthorizationError : Exception
{
    /// <summary>Create the error with a stable machine code and a human-readable message.</summary>
    public AuthorizationError(string message, string code)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Stable machine code for wire layers mapping this to their own taxonomy.</summary>
    public string Code { get; }
}

/// <summary>
/// The rejection an <see cref="AuthorizationInteraction.PromptAsync"/> uses to say the human
/// declined — dismissed the question, chose not to answer — rather than that the surface broke.
/// An attempt whose flow fails after a prompt was declined settles as cancelled, the same outcome
/// as a withdrawn signal, because the human saying no is a refusal, not a breakage. Only a
/// human's "no" may reject with this class: a prompt withdrawn by its own signal (a flow retiring
/// the losing question of a race) must reject with something else, or a later genuine failure
/// would be misread as a decline.
/// </summary>
public sealed class AuthorizationDeclinedError : AuthorizationError
{
    /// <summary>Create the decline error.</summary>
    public AuthorizationDeclinedError(string message = "the authorization prompt was declined")
        : base(message, "DECLINED")
    {
    }
}

/// <summary>
/// What a running flow is given to talk to the human. Every member is scoped to one attempt: the
/// flow neither knows nor chooses which surface is listening.
/// </summary>
public sealed class AuthorizationSession
{
    /// <summary>The method id the caller picked, always one this flow declared.</summary>
    public required string Method { get; init; }

    /// <summary>Aborted when the caller withdraws or <see cref="IAuthorizationService.Cancel"/> is called for this key.</summary>
    public required CancellationToken Signal { get; init; }

    /// <summary>
    /// Report progress, or tell the human what to do next. Fire-and-forget: a surface that cannot
    /// render a notice must not stall the flow.
    /// </summary>
    public required Action<AuthorizationNotice> Notify { get; init; }

    /// <summary>
    /// Ask the human a question the flow cannot answer for itself.
    /// </summary>
    /// <returns>what the human typed, or the chosen option's id.</returns>
    /// <exception cref="AuthorizationDeclinedError">when the human declines.</exception>
    public required Func<AuthorizationPrompt, Task<string>> PromptAsync { get; init; }

    /// <summary>
    /// The credential service this attempt's flow commits through. The seam observes writes to its
    /// key through this facade to confirm the commit contract: the C# credentials port has not
    /// landed the <c>credentials/record-updated</c> event yet, so the observed surface rides on
    /// the session instead of being watched on the bus (deviation from the TS seam, documented on
    /// <see cref="LocalAuthorizationService"/>).
    /// </summary>
    public required ICredentialsService Credentials { get; init; }
}

/// <summary>
/// A plugin's knowledge of how to obtain one credential. The flow owns the write: the
/// <see cref="Run"/> delegate resolving means the record for <see cref="Key"/> is committed
/// through the credentials service during that run, which the seam confirms — a commit observed
/// within the attempt, still present after it — before reporting success. Committing inside the
/// flow is what lets a library that persists through its own store adapter stay the single
/// writer instead of being copied back out and written twice.
/// </summary>
public sealed class AuthorizationFlow
{
    /// <summary>The credential record this flow writes. Its scope names the owning plugin.</summary>
    public required string Key { get; init; }

    /// <summary>User-facing name of what is being authorized.</summary>
    public required string Label { get; init; }

    /// <summary>
    /// The methods offered, most preferred first; a caller naming none gets the first. Must be
    /// non-empty: the TS type enforces a non-empty tuple at the one place flows are written, and
    /// the C# port validates the same invariant at registration.
    /// </summary>
    public required IReadOnlyList<AuthorizationMethod> Methods { get; init; }

    /// <summary>
    /// Run one attempt to obtain and commit the credential.
    /// </summary>
    /// <param name="session">the chosen method, the cancellation signal, the interaction callbacks, and the observed credentials surface.</param>
    /// <returns>a task completing once the record is committed.</returns>
    /// <exception cref="AuthorizationDeclinedError">when the human declines.</exception>
    public required Func<AuthorizationSession, Task> Run { get; init; }
}

/// <summary>
/// The surface half of one attempt. Supplied with the request rather than registered, because the
/// caller that starts an authorization is the one that can talk to the human about it: prompts
/// reach exactly the page that asked, and a headless caller supplies an interaction that declines.
/// </summary>
public sealed class AuthorizationInteraction
{
    /// <summary>Render a notice from the running flow.</summary>
    public required Action<AuthorizationNotice> Notify { get; init; }

    /// <summary>
    /// Put a question to the human and wait.
    /// </summary>
    /// <returns>the typed text, or the chosen option's id.</returns>
    /// <exception cref="AuthorizationDeclinedError">when the human declines; any other rejection
    /// reads as the surface failing, not as an answer.</exception>
    public required Func<AuthorizationPrompt, Task<string>> PromptAsync { get; init; }
}

/// <summary>One request to authorize a key.</summary>
public sealed class AuthorizationRequest
{
    /// <summary>The credential record to authorize; a flow must be registered for it.</summary>
    public required string Key { get; init; }

    /// <summary>Which of the flow's methods to run. Defaults to the flow's first.</summary>
    public string? Method { get; init; }

    /// <summary>The surface that will render this attempt's notices and prompts.</summary>
    public required AuthorizationInteraction Interaction { get; init; }

    /// <summary>Withdraws the whole attempt.</summary>
    public CancellationToken Signal { get; init; }
}

/// <summary>
/// Service Definition of the authorization capability seam (ctx.authorization): obtaining a
/// credential nobody can supply from configuration alone, because getting it requires a
/// conversation with the human — open this page, paste that code, pick an account. The seam owns
/// the conversation and the lifecycle; it never owns the protocol: a plugin that knows how to
/// obtain its own credential registers a flow keyed by the credential record that flow writes,
/// and the flow talks to whatever surface started it through one neutral vocabulary of notices
/// and prompts. Port of <c>@deepseek-ai/dsh-authorization</c>.
/// </summary>
public interface IAuthorizationService
{
    /// <summary>
    /// Offer a way to obtain one credential. One flow per key: two plugins claiming the same key
    /// would each write a record in their own format, and whichever ran last would leave the other
    /// reading a payload it cannot parse.
    /// </summary>
    /// <param name="flow">the key it writes, its label, its methods, and its runner.</param>
    /// <returns>A disposer that withdraws this flow: disposing it removes the flow, and an
    /// attempt still running for its key is cancelled.</returns>
    /// <exception cref="AuthorizationError">code <c>DUPLICATE_FLOW</c> when the key is already claimed.</exception>
    /// <exception cref="ArgumentException">when the flow offers no methods.</exception>
    IDisposable RegisterFlow(AuthorizationFlow flow);

    /// <summary>Every registered flow, for a surface listing what can be authorized.</summary>
    /// <returns>one entry per flow, in registration order.</returns>
    IReadOnlyList<AuthorizationEntry> List();

    /// <summary>One registered flow.</summary>
    /// <param name="key">the credential record to ask about.</param>
    /// <returns>the entry, or <c>null</c> when no flow claims that key.</returns>
    AuthorizationEntry? Describe(string key);

    /// <summary>
    /// Withdraw the attempt running for a key, if any. Separate from the request's own signal
    /// because a request/response transport answers a Cancel button on a second call, with no
    /// handle on the first one's signal.
    /// </summary>
    /// <param name="key">the credential record whose attempt should stop.</param>
    void Cancel(string key);

    /// <summary>
    /// Run one attempt to authorize a key, and report how it ended. One attempt per key at a time:
    /// a second caller is refused rather than joined — the two would be prompting different humans
    /// through the same flow, and the second would answer questions the first was asked.
    /// </summary>
    /// <param name="request">the key, the method, the surface, and the cancel signal.</param>
    /// <returns><see cref="AuthorizationStatus.Authorized"/> once the flow's record is committed
    /// during this attempt and observed, or <see cref="AuthorizationStatus.Cancelled"/> when the
    /// human declined or the caller withdrew.</returns>
    /// <exception cref="AuthorizationError">code <c>NO_FLOW</c> when nothing claims the key,
    /// <c>UNKNOWN_METHOD</c> when the named method is not one the flow offers,
    /// <c>ALREADY_IN_FLIGHT</c> when an attempt is already running for the key, or
    /// <c>NOT_COMMITTED</c> when the flow resolved without committing a record during the attempt.</exception>
    Task<AuthorizationOutcome> BeginAsync(AuthorizationRequest request);
}
