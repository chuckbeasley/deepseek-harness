namespace Harness.Context;

/// <summary>
/// One request-context contributor: produces a named text section for a session, or null when it
/// has nothing to contribute. Port of the context capability's plugin roles — agent instructions,
/// file references, session references, and time context each become a contributor.
/// </summary>
public interface IContextContributor
{
    /// <summary>Stable contributor key used to label the section.</summary>
    string Key { get; }

    /// <summary>Produce the contributor's section for one session, or null when nothing applies.</summary>
    Task<ContextSection?> ContributeAsync(Harness.Session.Session session, CancellationToken cancellationToken = default);
}

/// <summary>
/// Request-context Service Definition (ctx.context): contributors register and the service
/// assembles the combined request-context text for a session. Port of the context capability
/// (agent-instructions, file-reference + file-reference-local, session-reference, time-context).
/// The TS plugins each inject their own user message directly into the request; assembling every
/// contributor's sections into one <c>&lt;request-context&gt;</c> frame is a port decision —
/// the registration surface is the seam this port's service owns.
/// </summary>
public interface IContextService
{
    /// <summary>Register one contributor; the returned disposer removes it.</summary>
    IDisposable Register(IContextContributor contributor);

    /// <summary>Registered contributors in registration order.</summary>
    IReadOnlyList<IContextContributor> Contributors { get; }

    /// <summary>Collect every contributor's non-null section for one session, in registration order.</summary>
    Task<IReadOnlyList<ContextSection>> CollectAsync(Harness.Session.Session session, CancellationToken cancellationToken = default);

    /// <summary>Assemble the combined request-context text for one session; empty when no contributor produced a section.</summary>
    Task<string> AssembleAsync(Harness.Session.Session session, CancellationToken cancellationToken = default);
}
