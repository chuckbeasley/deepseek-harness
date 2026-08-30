using Cordis.Core;
using Dsh.Llm;

namespace Dsh.Skill;

/// <summary>
/// Layered skill registry (ctx.skills): merges provider catalogs, resolves the winning skill for a
/// name, and exposes winning summaries and definitions to consumers. Provider and runtime
/// registrations are effects: disposing the returned disposer (or the context) removes the
/// contribution and emits <c>skills/change</c>. The C# port keeps a single global layer; scope
/// layering is deferred (see TS SkillLayer).
/// </summary>
public sealed class SkillRegistry : Service
{
    /// <summary>The service key this instance registers under.</summary>
    public const string ServiceKey = "skills";

    /// <summary>Provider label reserved for runtime skill registrations.</summary>
    public const string RuntimeProvider = "runtime";

    /// <summary>Standard precedence rank for packaged skill providers and local bundled roots.</summary>
    public const int BundledSkillRank = 600;

    private const int RuntimeRank = 250;

    private readonly List<RegisteredProvider> _providers = new();
    private readonly Dictionary<string, SkillDefinition> _runtime = new(StringComparer.Ordinal);
    private int _nextProviderOrder;

    private sealed record RegisteredProvider(ISkillProvider Provider, int Order);

    private sealed record IndexedCandidate(SkillCandidate Candidate, ISkillProvider Provider, int ProviderOrder, int LocalOrder);

    /// <summary>Register the service under the <c>skills</c> key.</summary>
    public SkillRegistry(Context ctx)
        : base(ctx, ServiceKey)
    {
    }

    /// <summary>
    /// Register a borrowed same-process provider synchronously during plugin apply. Duplicate names
    /// and the reserved <see cref="RuntimeProvider"/> name throw; remote initialization belongs in
    /// <c>ListAsync</c>. Fiber disposal unregisters the provider and invalidates catalog caches.
    /// </summary>
    /// <returns>The exact Cordis effect disposer that unregisters this provider.</returns>
    public IDisposable RegisterProvider(ISkillProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Name == RuntimeProvider)
        {
            throw new ArgumentException($"\"{RuntimeProvider}\" is reserved for runtime skill registrations", nameof(provider));
        }
        return Ctx.Effect(() =>
        {
            if (_providers.Any(p => p.Provider.Name == provider.Name))
            {
                throw new InvalidOperationException($"a skill provider named \"{provider.Name}\" is already registered");
            }
            var order = _nextProviderOrder;
            _nextProviderOrder += 1;
            _providers.Add(new RegisteredProvider(provider, order));
            EmitChange();
            return new ActionDisposer(() =>
            {
                _providers.RemoveAll(p => ReferenceEquals(p.Provider, provider));
                EmitChange();
            });
        }, "skills.registerProvider()");
    }

    /// <summary>
    /// Register a borrowed readonly runtime skill into the calling context's layer. Same-name
    /// runtime entries are first-wins; a duplicate warns and receives a no-op disposer so it cannot
    /// remove the winner.
    /// </summary>
    /// <returns>The exact Cordis effect disposer, preserving composite teardown order.</returns>
    public IDisposable Register(SkillRegistration skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ValidateRuntimeSkill(skill);
        if (_runtime.ContainsKey(skill.Name))
        {
            Ctx.Logger.Warn($"runtime skill \"{skill.Name}\" ignored because it is already registered");
            return new ActionDisposer(() => { });
        }
        var definition = new SkillDefinition(
            skill.Name,
            skill.Description,
            skill.Invocation ?? new SkillInvocationPolicy(true, true),
            skill.Source,
            skill.Provider ?? RuntimeProvider,
            skill.Content,
            skill.WhenToUse,
            skill.ResourceBase,
            skill.Path,
            skill.Metadata);
        return Ctx.Effect(() =>
        {
            _runtime[definition.Name] = definition;
            EmitChange();
            return new ActionDisposer(() =>
            {
                _runtime.Remove(definition.Name);
                EmitChange();
            });
        }, "skills.register()");
    }

    /// <summary>
    /// List invocation-neutral skill summaries for a workspace. Consumers apply model or user
    /// invocation policy at their operational boundary.
    /// </summary>
    /// <param name="options">Lookup options; <c>Cwd</c> selects project roots and <c>CancellationToken</c> cancels discovery.</param>
    /// <returns>All sorted winning summaries.</returns>
    public async Task<IReadOnlyList<SkillSummary>> ListAsync(SkillLookupOptions? options = null)
    {
        options ??= new SkillLookupOptions();
        options.CancellationToken.ThrowIfCancellationRequested();
        var winners = await CollectAsync(options);
        return winners.Values
            .Select(entry => ToSummary(entry.Candidate))
            .OrderBy(summary => summary.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Load and validate the winning candidate, passing its opaque discovery locator back to the
    /// provider. Cancellation is rechecked after selection, including cache hits.
    /// </summary>
    /// <param name="name">Kebab-case skill name.</param>
    /// <param name="options">Lookup options; <c>Cwd</c> selects workspace-sensitive skills and <c>CancellationToken</c> cancels work.</param>
    /// <returns>The full skill, including body content, or <c>null</c>.</returns>
    public async Task<SkillDefinition?> GetAsync(string name, SkillLookupOptions? options = null)
    {
        if (!SkillNames.IsSkillName(name)) return null;
        options ??= new SkillLookupOptions();
        options.CancellationToken.ThrowIfCancellationRequested();
        var winners = await CollectAsync(options);
        if (!winners.TryGetValue(name, out var entry)) return null;
        var definition = await entry.Provider.GetAsync(entry.Candidate, options);
        if (definition is null) return null;
        ValidateDefinition(definition);
        if (definition.Name != entry.Candidate.Name) return null;
        return definition;
    }

    private async Task<Dictionary<string, IndexedCandidate>> CollectAsync(SkillLookupOptions options)
    {
        var collected = new List<IndexedCandidate>();
        var runtimeOrder = 0;
        foreach (var skill in _runtime.Values.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            collected.Add(new IndexedCandidate(RuntimeCandidate(skill), RuntimeSkillProvider.Instance, -1, runtimeOrder));
            runtimeOrder += 1;
        }
        foreach (var registered in _providers)
        {
            var localOrder = 0;
            var candidates = await registered.Provider.ListAsync(options);
            foreach (var candidate in candidates)
            {
                ValidateCandidate(candidate, registered.Provider.Name);
                collected.Add(new IndexedCandidate(candidate, registered.Provider, registered.Order, localOrder));
                localOrder += 1;
            }
        }
        // Lower ranks win duplicate skill names; provider order then local order break ties.
        var ordered = collected.ToList();
        ordered.Sort(CompareIndexedCandidates);
        var winners = new Dictionary<string, IndexedCandidate>(StringComparer.Ordinal);
        foreach (var entry in ordered)
        {
            var name = entry.Candidate.Name;
            if (!winners.ContainsKey(name)) winners[name] = entry;
        }
        return winners;
    }

    private void EmitChange()
    {
        try
        {
            Ctx.Emit("skills/change");
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"skills: skills/change listener threw: {error.Message}");
        }
    }

    private static SkillCandidate RuntimeCandidate(SkillDefinition skill) => new(
        skill.Name,
        skill.Description,
        skill.Invocation,
        skill.Source,
        skill.Provider,
        RuntimeRank,
        skill,
        skill.WhenToUse,
        skill.ResourceBase,
        skill.Path,
        skill.Metadata);

    private static SkillSummary ToSummary(SkillCandidate candidate) => new(
        candidate.Name,
        candidate.Description,
        candidate.Invocation,
        candidate.Source,
        candidate.Provider,
        candidate.WhenToUse,
        candidate.ResourceBase);

    private static int CompareIndexedCandidates(IndexedCandidate left, IndexedCandidate right)
    {
        var byRank = left.Candidate.Rank.CompareTo(right.Candidate.Rank);
        if (byRank != 0) return byRank;
        var byProvider = left.ProviderOrder.CompareTo(right.ProviderOrder);
        if (byProvider != 0) return byProvider;
        return left.LocalOrder.CompareTo(right.LocalOrder);
    }

    private static void ValidateCandidate(SkillCandidate candidate, string providerName)
    {
        if (!SkillNames.IsSkillName(candidate.Name))
        {
            throw new ArgumentException($"skill provider \"{providerName}\" returned invalid skill name \"{candidate.Name}\"");
        }
        if (candidate.Description.Length == 0)
        {
            throw new ArgumentException($"skill provider \"{providerName}\" returned skill \"{candidate.Name}\" without a description");
        }
        if (candidate.Source.Length == 0)
        {
            throw new ArgumentException($"skill provider \"{providerName}\" returned skill \"{candidate.Name}\" with an empty source");
        }
        if (candidate.Provider != providerName)
        {
            throw new ArgumentException($"skill provider \"{providerName}\" returned skill \"{candidate.Name}\" for provider \"{candidate.Provider}\"");
        }
    }

    private static void ValidateRuntimeSkill(SkillRegistration skill)
    {
        if (!SkillNames.IsSkillName(skill.Name))
        {
            throw new ArgumentException($"invalid skill name \"{skill.Name}\"");
        }
        if (skill.Description.Length == 0)
        {
            throw new ArgumentException($"skill \"{skill.Name}\" requires a description");
        }
        if (skill.Source.Length == 0)
        {
            throw new ArgumentException($"skill \"{skill.Name}\" requires a source");
        }
    }

    private static void ValidateDefinition(SkillDefinition skill)
    {
        if (!SkillNames.IsSkillName(skill.Name))
        {
            throw new ArgumentException($"loaded skill has invalid name \"{skill.Name}\"");
        }
        if (skill.Description.Length == 0)
        {
            throw new ArgumentException($"loaded skill \"{skill.Name}\" requires a description");
        }
    }

    /// <summary>Pseudo-provider owning the registry-injected runtime skill bodies.</summary>
    private sealed class RuntimeSkillProvider : ISkillProvider
    {
        public static readonly RuntimeSkillProvider Instance = new();

        public string Name => RuntimeProvider;

        public Task<IReadOnlyList<SkillCandidate>> ListAsync(SkillLookupOptions options)
            => Task.FromResult<IReadOnlyList<SkillCandidate>>(Array.Empty<SkillCandidate>());

        public Task<SkillDefinition?> GetAsync(SkillCandidate candidate, SkillLookupOptions options)
            => Task.FromResult<SkillDefinition?>(candidate.Locator as SkillDefinition);
    }
}
