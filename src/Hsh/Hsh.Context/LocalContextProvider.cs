using Harness.Cordis.Core;

namespace Harness.Context;

/// <summary>
/// ctx.context: the local request-context provider. Holds the contributor registry and assembles
/// the combined request-context text: non-empty sections joined in registration order inside a
/// <c>&lt;request-context&gt;</c> frame, each labeled with its contributor key. Registration is a
/// disposer-returning effect like the harness registries, and the assembled text is deterministic
/// for a fixed session and contributor set.
/// </summary>
public sealed class LocalContextProvider : Service, IContextService
{
    private readonly List<IContextContributor> _contributors = new();

    /// <summary>Create and register the service as <c>context</c>.</summary>
    /// <param name="ctx">the owner context.</param>
    /// <param name="contributors">contributors registered immediately, in order.</param>
    public LocalContextProvider(Harness.Cordis.Core.Context ctx, IEnumerable<IContextContributor>? contributors = null)
        : base(ctx, "context")
    {
        if (contributors is not null)
        {
            foreach (var contributor in contributors) Register(contributor);
        }
    }

    /// <summary>Read the context service from a context, failing explicitly when it is absent.</summary>
    public static LocalContextProvider Require(Harness.Cordis.Core.Context ctx) => ctx.Require<LocalContextProvider>("context");

    /// <inheritdoc />
    public IReadOnlyList<IContextContributor> Contributors => _contributors.ToArray();

    /// <inheritdoc />
    public IDisposable Register(IContextContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        _contributors.Add(contributor);
        return new UnregisterDisposer(this, contributor);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextSection>> CollectAsync(Harness.Session.Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sections = new List<ContextSection>();
        foreach (var contributor in _contributors.ToArray())
        {
            var section = await contributor.ContributeAsync(session, cancellationToken).ConfigureAwait(false);
            if (section is not null) sections.Add(section);
        }
        return sections;
    }

    /// <inheritdoc />
    public async Task<string> AssembleAsync(Harness.Session.Session session, CancellationToken cancellationToken = default)
    {
        var sections = await CollectAsync(session, cancellationToken).ConfigureAwait(false);
        if (sections.Count == 0) return string.Empty;
        var body = string.Join("\n\n", sections.Select(section => $"[{section.Key}]\n{section.Text}"));
        return $"<request-context>\n{body}\n</request-context>";
    }

    /// <summary>Single-shot disposer that removes one registered contributor.</summary>
    private sealed class UnregisterDisposer : IDisposable
    {
        private LocalContextProvider? _owner;
        private IContextContributor? _contributor;

        public UnregisterDisposer(LocalContextProvider owner, IContextContributor contributor)
        {
            _owner = owner;
            _contributor = contributor;
        }

        public void Dispose()
        {
            if (_owner is not null && _contributor is not null) _owner._contributors.Remove(_contributor);
            _owner = null;
            _contributor = null;
        }
    }
}
