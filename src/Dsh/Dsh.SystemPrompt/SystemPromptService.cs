using Cordis.Core;
using Dsh.Llm;
using Dsh.Tools;

namespace Dsh.SystemPrompt;

/// <summary>
/// Registry service for the prompt inputs assembled before each model step (ctx.systemPrompt).
/// Ordered prompt sections and tool-schema providers are registrations: registering returns the
/// exact Cordis effect disposer, and disposing it removes the contribution from later assemblies.
/// Registration and disposal emit <c>system-prompt/change</c>.
/// </summary>
public sealed class SystemPromptService : Service
{
    /// <summary>The service key this instance registers under.</summary>
    public const string ServiceKey = "systemPrompt";

    /// <summary>The tool registry's service key (see <see cref="ToolRuntime"/>).</summary>
    private const string ToolRegistryKey = "tools";

    private readonly Dictionary<string, PromptSection> _sections = new(StringComparer.Ordinal);
    private readonly List<Func<AssembleContext, ToolProviderResult>> _toolProviders = new();
    private readonly SystemPromptConfig _config;
    private readonly string[]? _toolOrder;

    /// <summary>
    /// Register the service and its built-ins: the harness identity opener (when enabled), the
    /// order-0 deployment persona, and the default tool-schema provider that projects the mounted
    /// tool registry's schemas into every assembly.
    /// </summary>
    public SystemPromptService(Context ctx, SystemPromptConfig? config = null)
        : base(ctx, ServiceKey)
    {
        _config = config ?? new SystemPromptConfig();
        _toolOrder = ToolOrdering.Validate(_config.ToolOrder);
        if (_config.IncludeHarnessIdentity)
        {
            RegisterSection(new PromptSection(
                "harness:identity",
                (int)SectionOrderName.HARNESS_IDENTITY,
                PromptText.Static(PromptConstants.HarnessIdentity)));
        }
        RegisterSection(new PromptSection(
            PromptConstants.PersonaSection,
            (int)SectionOrderName.DEPLOYMENT_PERSONA,
            PromptText.Static(_config.Persona)));
        RegisterToolProvider(_ => ProjectToolRegistry());
    }

    /// <summary>
    /// Register an ordered prompt section in the calling context. A duplicate name throws and
    /// leaks nothing; disposing the returned disposer removes the section from later assemblies.
    /// </summary>
    /// <returns>The exact Cordis effect disposer.</returns>
    public IDisposable RegisterSection(PromptSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return Ctx.Effect(() =>
        {
            if (_sections.ContainsKey(section.Name))
            {
                throw new InvalidOperationException($"prompt section \"{section.Name}\" is already registered");
            }
            _sections[section.Name] = section;
            EmitChange();
            return new ActionDisposer(() =>
            {
                _sections.Remove(section.Name);
                EmitChange();
            });
        }, "systemPrompt.section()");
    }

    /// <summary>
    /// Register a tool-schema provider in the calling context. Providers are evaluated per
    /// assembly; the default provider projects the mounted tool registry's schemas.
    /// </summary>
    /// <returns>The exact Cordis effect disposer.</returns>
    public IDisposable RegisterToolProvider(Func<AssembleContext, ToolProviderResult> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return Ctx.Effect(() =>
        {
            _toolProviders.Add(provider);
            EmitChange();
            return new ActionDisposer(() =>
            {
                _toolProviders.Remove(provider);
                EmitChange();
            });
        }, "systemPrompt.tools()");
    }

    /// <summary>Resolve the centrally owned placement of a repository prompt section.</summary>
    public int GetSectionOrder(SectionOrderName name) => (int)name;

    /// <summary>Resolve the centrally owned placement of a repository runtime context.</summary>
    public int GetContextOrder(ContextOrderName name) => (int)name;

    /// <summary>
    /// Assemble the registered sections and tool schemas: resolve section text against the
    /// context, canonicalize sections by (order, code-unit name), evaluate tool providers and
    /// detach their parameters, then apply the configured tool order.
    /// </summary>
    /// <returns>The post-assembly input; render with <see cref="RenderPrompt"/>.</returns>
    public Task<PromptAssembly> AssembleAsync(AssembleContext? context = null)
    {
        context ??= new AssembleContext();
        // Snapshot provider membership: a provider registered by an earlier provider applies to the
        // next assembly, not this one (TS parity).
        var providers = _toolProviders.ToArray();
        var collected = new List<ToolSchema>();
        var knownNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            var result = provider(context);
            var schemas = result.Schemas
                .Select(schema => new ToolSchema(schema.Name, schema.Description, schema.Parameters.Clone()))
                .ToArray();
            collected.AddRange(schemas);
            var acceptedNames = result.KnownNames ?? schemas.Select(schema => schema.Name).ToArray();
            foreach (var name in acceptedNames) knownNames.Add(name);
        }
        var sections = _sections.Values
            .OrderBy(section => section.Order)
            .ThenBy(section => section.Name, StringComparer.Ordinal)
            .Select(section => new AssembledSection(section.Name, section.Text.Resolve(context)))
            .ToArray();
        var tools = ToolOrdering.Order(collected, _toolOrder, knownNames);
        return Task.FromResult(new PromptAssembly(sections, tools));
    }

    /// <summary>
    /// Render an assembly: drop empty sections and join the rest with the configured separator.
    /// Variable interpolation arrives with prompt variables (later phase).
    /// </summary>
    public string RenderPrompt(PromptAssembly assembly)
        => PromptRendering.Render(assembly, _config.SectionSeparator);

    /// <summary>The default tool provider: the mounted tool registry's schemas (empty when no registry).</summary>
    private ToolProviderResult ProjectToolRegistry()
    {
        var tools = Ctx.Get<ToolRuntime>(ToolRegistryKey);
        return tools is null
            ? new ToolProviderResult(Array.Empty<ToolSchema>())
            : new ToolProviderResult(tools.Schemas());
    }

    private void EmitChange()
    {
        try
        {
            Ctx.Emit("system-prompt/change");
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"systemPrompt: system-prompt/change listener threw: {error.Message}");
        }
    }
}
