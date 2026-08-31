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
    /// the result is always false today; the signature keeps the TS contract slot.
    /// </summary>
    public static async Task<bool> ExecuteAsync(
        Dsh.Agent.Agent agent, ToolRuntime tools, long turn, long step,
        IReadOnlyList<ToolCallBlock> toolCalls, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var session = agent.Session;
        var concluded = false;
        for (var index = 0; index < toolCalls.Count; index++)
        {
            if (ct.IsCancellationRequested)
            {
                for (var skipped = index; skipped < toolCalls.Count; skipped++)
                {
                    AppendSkipped(session, turn, step, toolCalls[skipped]);
                }
                return concluded;
            }
            var call = toolCalls[index];
            var callSeq = session.Append(new ToolCallEvent
            {
                Turn = turn, Step = step, CallId = call.Id, Name = call.Name, Arguments = call.Arguments,
            }).Seq;
            var input = new ToolExecutionInput(call.Id, call.Name, ParseArguments(call.Arguments), ct)
            {
                Session = session,
            };
            var result = await tools.ExecuteAsync(input, ct);
            AppendToolResult(session, turn, step, call, result, callSeq);
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

    /// <summary>Append a model-ordered result linked to its call event.</summary>
    private static void AppendToolResult(Dsh.Session.Session session, long turn, long step, ToolCallBlock call, ToolExecutionResult result, long callSeq)
    {
        session.Append(new ToolResultEvent
        {
            Turn = turn, Step = step,
            Message = ToolResultMessage.Create(call.Id, result.Content, result.IsError),
            Error = result is ToolExecutionFailure failure
                ? new ToolErrorInfo(failure.Error.Name ?? "Error", failure.Error.Code ?? "UNKNOWN")
                : null,
            Meta = result is ToolExecutionSuccess success ? success.Value : null,
            SurfaceOp = SurfaceOp.Append,
            SourceEventSeqs = new long[] { callSeq },
        });
    }

    /// <summary>Append the durable call/result pair for a model call skipped after cancellation.</summary>
    private static void AppendSkipped(Dsh.Session.Session session, long turn, long step, ToolCallBlock call)
    {
        var callSeq = session.Append(new ToolCallEvent
        {
            Turn = turn, Step = step, CallId = call.Id, Name = call.Name, Arguments = call.Arguments,
        }).Seq;
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
