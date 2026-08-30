using Cordis.Core;
using Dsh.Llm;

namespace Dsh.Tools;

/// <summary>
/// Tool registry and execution pipeline (ctx.tools). Registrations are effects: disposing the
/// context (or the returned disposer) unregisters the tool and emits <c>tools/change</c>.
/// Execution runs the guarded pipeline: <c>tools/pre-execute</c> (waterfall, allow|deny) then
/// <c>tools/execute</c> (waterfall around the body) then <c>tools/post-execute</c> (waterfall,
/// accept|block), then <c>tools/result</c> (observe-only emit).
/// </summary>
public sealed class ToolRuntime : Service
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.Ordinal);

    public ToolRuntime(Context ctx)
        : base(ctx, "tools")
    {
    }

    /// <summary>Register a tool; returns the exact disposer that unregisters it.</summary>
    /// <exception cref="InvalidOperationException">when the name is already registered.</exception>
    public IDisposable Register(ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (tool.Name.Length == 0)
        {
            throw new ArgumentException("tool name must be non-empty", nameof(tool));
        }
        return Ctx.Effect(() =>
        {
            if (_tools.ContainsKey(tool.Name))
            {
                throw new InvalidOperationException($"tool \"{tool.Name}\" is already registered");
            }
            _tools[tool.Name] = tool;
            EmitToolsChange();
            return new ActionDisposer(() =>
            {
                _tools.Remove(tool.Name);
                EmitToolsChange();
            });
        }, "tools.register()");
    }

    /// <summary>Look up a registered tool.</summary>
    public ToolDefinition? Get(string name) => _tools.GetValueOrDefault(name);

    /// <summary>Project registered tools onto the model-facing allowlist schema fields (name/description/parameters).</summary>
    public IReadOnlyList<ToolSchema> Schemas()
        => _tools.Values.Select(t => new ToolSchema(t.Name, t.Description, t.Parameters)).ToArray();

    /// <summary>
    /// Run one call through the guarded pipeline. An unknown tool reports
    /// <see cref="ToolNotFoundError"/> (code "UNKNOWN_TOOL"); tool and listener failures resolve
    /// as materialized error results.
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteAsync(ToolExecutionInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var exec = new ToolRunContext(input.CallId, input.Name, input.Arguments, ct) { Session = input.Session };

        var gate = await Ctx.Waterfall<Task<PreToolDecision>>(
            "tools/pre-execute",
            new object?[] { exec },
            () => Task.FromResult<PreToolDecision>(new AllowDecision()));
        if (gate is DenyDecision deny)
        {
            var content = new ContentBlock[] { new TextBlock($"Error: {deny.Reason}") };
            return new ToolExecutionFailure(new ToolFailure($"Error: {deny.Reason}"), content);
        }

        var tool = Get(input.Name) ?? throw new ToolNotFoundError(input.Name);

        var result = await Ctx.Waterfall<Task<ToolExecutionResult>>(
            "tools/execute",
            new object?[] { exec },
            () => DispatchBodyAsync(tool, exec));

        var post = await Ctx.Waterfall<Task<PostToolDecision>>(
            "tools/post-execute",
            new object?[] { exec, result },
            () => Task.FromResult<PostToolDecision>(new AcceptDecision()));
        if (post is BlockDecision block)
        {
            result = new ToolExecutionFailure(new ToolFailure("tool result blocked by post-execute policy"), block.Feedback);
        }

        EmitResult(exec, result);
        return result;
    }

    private async Task<ToolExecutionResult> DispatchBodyAsync(ToolDefinition tool, ToolRunContext exec)
    {
        try
        {
            var value = await tool.Execute(exec.Arguments, exec);
            var content = tool.Render is null
                ? new ContentBlock[] { new TextBlock(value.GetRawText()) }
                : tool.Render(exec.Arguments, value);
            return new ToolExecutionSuccess(value, content);
        }
        catch (Exception error)
        {
            return new ToolExecutionFailure(new ToolFailure(error.Message), new ContentBlock[] { new TextBlock($"Error: {error.Message}") });
        }
    }

    private void EmitToolsChange()
    {
        try
        {
            Ctx.Emit("tools/change");
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"tools: tools/change listener threw: {error.Message}");
        }
    }

    private void EmitResult(ToolRunContext exec, ToolExecutionResult result)
    {
        try
        {
            Ctx.Emit("tools/result", exec, result);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"tool \"{exec.Name}\": tools/result observer threw: {error.Message}");
        }
    }
}

