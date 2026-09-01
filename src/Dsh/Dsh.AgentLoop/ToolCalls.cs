namespace Dsh.AgentLoop;

/// <summary>
/// Schedules one assistant step's tool calls in model order (port of the TS tool-calls module).
/// The C# port executes calls serially — dispatch overlap, bounded parallel pools, and
/// exclusive-barrier reclassification arrive with a later phase — while keeping the TS abort
/// contract: a cancellation before a call starts records a synthetic error result for it and
/// every later call so replay stays valid, and already-committed results remain in model order.
/// The deployment-wide cap is resolved and validated at the AgentLoop configuration boundary.
/// </summary>
public static class ToolCallScheduler
{
    /// <summary>
    /// Execute the calls in model order. The C# tool vocabulary carries no concludesTurn yet, so
    /// the result is always false today; the signature keeps the TS contract slot. All
    /// <c>tool/call</c> events are logged up front in model order (the TS dispatch pass), then the
    /// calls run and their results append in model order — the recorded corpus interleaves
    /// parallel dispatches exactly this way. The deployment-wide cap is resolved and validated at
    /// the AgentLoop configuration boundary.
    /// </summary>
    public static async Task<bool> ExecuteAsync(
        Dsh.Agent.Agent agent, ToolRuntime tools, long turn, long step,
        IReadOnlyList<ToolCallBlock> toolCalls, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var session = agent.Session;
        var callSeqs = new long[toolCalls.Count];
        for (var index = 0; index < toolCalls.Count; index++)
        {
            var call = toolCalls[index];
            callSeqs[index] = session.Append(new ToolCallEvent
            {
                Turn = turn, Step = step, CallId = call.Id, Name = call.Name, Arguments = call.Arguments,
            }).Seq;
        }
        var concluded = false;
        for (var index = 0; index < toolCalls.Count; index++)
        {
            if (ct.IsCancellationRequested)
            {
                for (var skipped = index; skipped < toolCalls.Count; skipped++)
                {
                    AppendSkipped(session, turn, step, toolCalls[skipped], callSeqs[skipped]);
                }
                return concluded;
            }
            var call = toolCalls[index];
            var input = new ToolExecutionInput(call.Id, call.Name, ParseArguments(call.Arguments), ct)
            {
                Session = session,
            };
            var result = await tools.ExecuteAsync(input, ct);
            result = ApplySpillPolicy(agent, call, result);
            AppendToolResult(session, turn, step, call, result, callSeqs[index]);
        }
        return concluded;
    }

    /// <summary>Parse model arguments, preserving invalid JSON as text and mapping empty input to an empty object.</summary>
    private static JsonElement ParseArguments(string raw)
    {
        if (raw.Length == 0) return JsonDocument.Parse("{}").RootElement.Clone();
        try
        {
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(raw);
        }
    }

    /// <summary>
    /// Apply the snapshot-run result-retention policy (port of the spill-policy post-execute arm):
    /// a plain-text accepted result larger than $DSH_SNAPSHOT_SPILL_MAX_BYTES is spilled to the
    /// session spill store and replaced with the bounded preview + notice. Best-effort: no policy
    /// env, no spill service, a non-text result, or a storage failure keeps the original. The
    /// product bundles adopt the policy row at cutover; the env is the snapshot config channel.
    /// </summary>
    private static ToolExecutionResult ApplySpillPolicy(Dsh.Agent.Agent agent, ToolCallBlock call, ToolExecutionResult result)
    {
        if (result is not ToolExecutionSuccess success) return result;
        if (call.Name == "read") return result; // avoid a read -> spill -> read-again loop
        var envCap = Environment.GetEnvironmentVariable("DSH_SNAPSHOT_SPILL_MAX_BYTES");
        if (envCap is null || !int.TryParse(envCap, out var cap) || cap < 0) return result;
        var spill = agent.Owner.Get<Dsh.Spill.ISpillService>("spill");
        if (spill is null) return result;
        var blocks = success.Blocks;
        if (blocks.Count == 0 || blocks.Any(block => block is not TextBlock)) return result;
        var text = string.Concat(blocks.Cast<TextBlock>().Select(block => block.Text));
        var replaced = Dsh.Spill.SpillPolicy.Replacement(text, agent.Session.Id.Value, $"{call.Name}.txt", spill, cap);
        if (replaced is null) return result;
        return success with { Blocks = new ContentBlock[] { new TextBlock(replaced) } };
    }

    /// <summary>Append a model-ordered result linked to its call event.</summary>
    private static void AppendToolResult(Dsh.Session.Session session, long turn, long step, ToolCallBlock call, ToolExecutionResult result, long callSeq)
    {
        session.Append(new ToolResultEvent
        {
            Turn = turn, Step = step,
            Message = ToolResultMessage.Create(call.Id, result.Content, result.IsError),
            // A failure with neither a stable name nor code (e.g. a post-execute block) records no
            // error identity, exactly like the recorded fixtures.
            Error = result is ToolExecutionFailure { Error: { Name: not null } or { Code: not null } } failure
                ? new ToolErrorInfo(failure.Error.Name ?? "Error", failure.Error.Code ?? "UNKNOWN")
                : null,
            Meta = result is ToolExecutionSuccess success ? success.Meta : null,
            SurfaceOp = SurfaceOp.Append,
            SourceEventSeqs = new long[] { callSeq },
        });
    }

    /// <summary>Append the durable result for a model call skipped after cancellation (its call event is already logged).</summary>
    private static void AppendSkipped(Dsh.Session.Session session, long turn, long step, ToolCallBlock call, long callSeq)
    {
        var content = new ContentBlock[] { new TextBlock("Error: tool call aborted before dispatch") };
        session.Append(new ToolResultEvent
        {
            Turn = turn, Step = step,
            Message = ToolResultMessage.Create(call.Id, content, isError: true),
            Error = new ToolErrorInfo("AbortError", "TOOL_ABORTED_BEFORE_DISPATCH"),
            SurfaceOp = SurfaceOp.Append,
            SourceEventSeqs = new long[] { callSeq },
        });
    }
}
