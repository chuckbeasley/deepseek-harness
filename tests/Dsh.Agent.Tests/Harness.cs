using Harness.Cordis.Core;
using Harness.Llm;
using Harness.Session;

namespace Harness.Agent.Tests;

/// <summary>Shared boot and fixture helpers for the Agent port tests.</summary>
internal static class Harness
{
    /// <summary>Boot a context with the agent registry and session store services.</summary>
    public static (Context Ctx, AgentRegistry Registry, SessionStore Sessions) Boot()
    {
        var ctx = new Context();
        var registry = new AgentRegistry(ctx);
        var sessions = new SessionStore(ctx);
        return (ctx, registry, sessions);
    }

    /// <summary>One identified user-role message.</summary>
    public static UserMessage Msg(string id, string text) => new()
    {
        Id = new MessageId(id),
        Content = new ContentBlock[] { new TextBlock(text) },
        Source = new UserSource(),
    };

    /// <summary>Create a session and a live agent on it.</summary>
    public static Agent NewAgent(Context ctx, SessionStore sessions, string? id = null, AgentOptions? options = null, AgentConfig? config = null)
    {
        var session = sessions.Create(id is null ? null : new SessionId(id));
        return new Agent(ctx, session, options, config);
    }
}
