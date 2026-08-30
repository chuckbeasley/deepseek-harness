namespace Dsh.Context;

/// <summary>
/// Workspace-instruction contributor (port of the agent-instructions baseline rendering): an
/// injected instruction text is rendered as a workspace-context section. The TS plugin's file
/// discovery, digest caching, and dynamic reconciliation machinery is deferred (named, not
/// ported); the injected text plays the role of the loaded AGENTS.md chain, and an empty
/// instruction set contributes nothing.
/// </summary>
public sealed class AgentInstructionsContributor : IContextContributor
{
    /// <summary>The contributor's stable key.</summary>
    public const string DefaultKey = "agent-instructions";

    private readonly string _instructions;
    private readonly string? _source;

    /// <summary>Create the contributor over the injected instruction text.</summary>
    /// <param name="instructions">the instruction text; an empty value contributes nothing.</param>
    /// <param name="source">optional source label rendered in the "Instructions from:" heading.</param>
    public AgentInstructionsContributor(string instructions, string? source = null)
    {
        _instructions = instructions ?? throw new ArgumentNullException(nameof(instructions));
        _source = source;
    }

    /// <inheritdoc />
    public string Key => DefaultKey;

    /// <summary>The injected instruction text.</summary>
    public string Instructions => _instructions;

    /// <inheritdoc />
    public Task<ContextSection?> ContributeAsync(Dsh.Session.Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (_instructions.Length == 0) return Task.FromResult<ContextSection?>(null);
        var heading = _source is null ? "Instructions from: agent-instructions" : $"Instructions from: {_source}";
        return Task.FromResult<ContextSection?>(new ContextSection(Key, $"{heading}\n\n{_instructions}"));
    }
}
