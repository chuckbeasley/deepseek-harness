namespace Dsh.AgentLoop;

/// <summary>
/// Request-reconstruction invariant for loop-built LLM calls (port of the TS invariant module).
/// A loop-built request is any <see cref="GenerateOptions"/> carrying a session id; the check
/// runs prepended on the <c>llm/stream</c> waterfall so a short-circuiting listener cannot
/// silence it, and fails the dispatch when the request diverges from the session log it claims.
/// </summary>
public static class AgentLoopInvariant
{
    /// <summary>
    /// Install the check on <paramref name="ctx"/>'s <c>llm/stream</c> waterfall; the returned
    /// disposer removes it.
    /// </summary>
    public static IDisposable Install(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.On("llm/stream",
            new Func<GenerateOptions, Func<IAsyncEnumerable<StreamChunk>>, IAsyncEnumerable<StreamChunk>>((options, next) =>
            {
                // Auxiliary calls (e.g. the compaction summarizer) ride the same session binding
                // but are not loop-assembled requests (the TS marks loop requests by object
                // identity; the purpose field is this port's marker).
                if (options.SessionId is null || options.Purpose is not null) return next();
                var sessions = ctx.Get<SessionStore>("sessions")
                    ?? throw new InvalidOperationException("agent-loop invariant: the \"sessions\" store is not mounted");
                var session = sessions.Get(new SessionId(options.SessionId))
                    ?? throw new InvalidOperationException($"agent-loop invariant: a loop-built request must carry a live session id, got \"{options.SessionId}\"");
                if (!session.Events.Any(evt => evt is StepStartEvent))
                {
                    throw new InvalidOperationException("agent-loop invariant: a loop-built request with no step/start in its session log");
                }
                var header = session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header).LastOrDefault()
                    ?? throw new InvalidOperationException("agent-loop invariant: a loop-built request with no request/header event in its session log");
                var expected = session.DeriveMessages();
                if (JsonSerializer.Serialize(options.Messages) != JsonSerializer.Serialize(expected))
                {
                    throw new InvalidOperationException($"agent-loop invariant: llm request for session \"{session.Id}\" diverges from the dispatch-time durable derivation (log-reconstruction desync)");
                }
                if (options.Model != header.Config.Model
                    || options.System != header.System
                    || options.Temperature != header.Config.Temperature
                    || options.MaxTokens != header.Config.MaxTokens
                    || JsonSerializer.Serialize(options.Tools ?? Array.Empty<ToolSchema>()) != JsonSerializer.Serialize(header.Tools ?? Array.Empty<ToolSchema>()))
                {
                    throw new InvalidOperationException($"agent-loop invariant: llm request for session \"{session.Id}\" diverges from the folded request header");
                }
                return next();
            }),
            new EventOptions { Prepend = true, Global = true });
    }
}
