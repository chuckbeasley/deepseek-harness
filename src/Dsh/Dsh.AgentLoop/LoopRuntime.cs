namespace Harness.AgentLoop;

/// <summary>
/// The loop's resolved runtime dependencies (port of the TS inject list). Resolution fails loud
/// at the factory boundary: a missing service aborts agent creation instead of failing at the
/// first model request.
/// </summary>
/// <param name="Llm">the llm service.</param>
/// <param name="Tools">the tool registry.</param>
/// <param name="SystemPrompt">the system-prompt assembly service.</param>
/// <param name="Sessions">the live session store.</param>
public sealed record LoopRuntime(LlmRuntime Llm, ToolRuntime Tools, SystemPromptService SystemPrompt, SessionStore Sessions)
{
    /// <summary>Resolve every required dependency from the context.</summary>
    public static LoopRuntime Resolve(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new LoopRuntime(
            ctx.Get<LlmRuntime>("llm") ?? throw new InvalidOperationException("agentLoop requires the \"llm\" service"),
            ctx.Get<ToolRuntime>("tools") ?? throw new InvalidOperationException("agentLoop requires the \"tools\" service"),
            ctx.Get<SystemPromptService>("systemPrompt") ?? throw new InvalidOperationException("agentLoop requires the \"systemPrompt\" service"),
            ctx.Get<SessionStore>("sessions") ?? throw new InvalidOperationException("agentLoop requires the \"sessions\" store"));
    }
}

/// <summary>One contributed runtime-context part: dynamic system-prompt input for the next step.</summary>
public sealed record RuntimeContextPart(string Text, IReadOnlyList<Harness.Llm.NamedSection> Sections);
