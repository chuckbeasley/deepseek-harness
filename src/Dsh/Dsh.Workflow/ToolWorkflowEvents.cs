using Harness.Session;

namespace Harness.Workflow;

/// <summary>One agent() call established a published child run.</summary>
public sealed record ToolWorkflowAgentStartEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "tool-workflow/agent-start";

    /// <summary>The run's identity.</summary>
    public required string RunId { get; init; }

    /// <summary>The call's 1-based sequence number.</summary>
    public required long Seq { get; init; }

    /// <summary>The display label (the label option, or a prompt snippet).</summary>
    public required string Label { get; init; }

    /// <summary>The active phase at the call, when the script entered one.</summary>
    public string? Phase { get; init; }

    /// <summary>The published child session id.</summary>
    public required string ChildId { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>One agent() call settled.</summary>
public sealed record ToolWorkflowAgentEndEvent : SessionEvent
{
    /// <summary>The wire discriminator (also the session event-type registry key).</summary>
    public const string EventTypeName = "tool-workflow/agent-end";

    /// <summary>The run's identity.</summary>
    public required string RunId { get; init; }

    /// <summary>The call's 1-based sequence number, paired with the start event.</summary>
    public required long Seq { get; init; }

    /// <summary>The settlement outcome ("completed" for a clean child result).</summary>
    public required string Outcome { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Register the tool-workflow/* event types into the session registry (the start/end discriminators are declared on the workflow seam).</summary>
public static class WorkflowEventTypes
{
    /// <summary>Register all four record discriminators; idempotent per discriminator.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(ToolWorkflowRunStartEvent.EventTypeName, typeof(ToolWorkflowRunStartEvent));
        SessionEventTypes.Register(ToolWorkflowAgentStartEvent.EventTypeName, typeof(ToolWorkflowAgentStartEvent));
        SessionEventTypes.Register(ToolWorkflowAgentEndEvent.EventTypeName, typeof(ToolWorkflowAgentEndEvent));
        SessionEventTypes.Register(ToolWorkflowRunEndEvent.EventTypeName, typeof(ToolWorkflowRunEndEvent));
    }
}