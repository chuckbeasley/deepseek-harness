using System.Text.Json;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Code;

/// <summary>One programmatic tool dispatch opened inside a run_code run (the PTC record events).</summary>
public sealed record ToolCodeDispatchStartEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "tool/code-dispatch-start";

    /// <summary>The outermost run_code call id.</summary>
    public required string RootCallId { get; init; }

    /// <summary>The immediate parent call id (the run_code call).</summary>
    public required string ParentCallId { get; init; }

    /// <summary>The sub-call id ("{root}:code:{n}").</summary>
    public required string SubCallId { get; init; }

    /// <summary>The dispatched tool name.</summary>
    public required string Name { get; init; }

    /// <summary>The dispatched arguments (the parsed JSON object).</summary>
    public required JsonElement Arguments { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>One programmatic tool dispatch settled inside a run_code run.</summary>
public sealed record ToolCodeDispatchEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "tool/code-dispatch";

    /// <summary>The outermost run_code call id.</summary>
    public required string RootCallId { get; init; }

    /// <summary>The immediate parent call id (the run_code call).</summary>
    public required string ParentCallId { get; init; }

    /// <summary>The sub-call id ("{root}:code:{n}").</summary>
    public required string SubCallId { get; init; }

    /// <summary>The dispatched tool name.</summary>
    public required string Name { get; init; }

    /// <summary>The dispatched arguments (the parsed JSON object).</summary>
    public required JsonElement Arguments { get; init; }

    /// <summary>Whether the dispatched call failed.</summary>
    public required bool IsError { get; init; }

    /// <summary>The dispatched call's model-facing content.</summary>
    public required IReadOnlyList<ContentBlock> Content { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Register the tool/code-dispatch* event types into the session registry.</summary>
public static class CodeEventTypes
{
    /// <summary>Register both discriminators; idempotent per discriminator.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(ToolCodeDispatchStartEvent.EventTypeName, typeof(ToolCodeDispatchStartEvent));
        SessionEventTypes.Register(ToolCodeDispatchEvent.EventTypeName, typeof(ToolCodeDispatchEvent));
    }
}