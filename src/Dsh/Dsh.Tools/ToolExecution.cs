using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Tools;

/// <summary>Caller-supplied description of one tool call (identity, arguments, owning session).</summary>
public sealed record ToolExecutionInput(ToolCallId CallId, string Name, JsonElement Arguments, CancellationToken CancellationToken)
{
    /// <summary>The owning session; the tool appends durable events through it.</summary>
    public Dsh.Session.Session? Session { get; init; }
}

/// <summary>Runtime context handed to a tool body after the registry has accepted a call.</summary>
public sealed record ToolRunContext(ToolCallId CallId, string Name, JsonElement Arguments, CancellationToken CancellationToken)
{
    /// <summary>The owning session; the tool appends durable events through it.</summary>
    public Dsh.Session.Session? Session { get; init; }
}

/// <summary>The discriminated outcome of one tool call.</summary>
public abstract record ToolExecutionResult
{
    /// <summary>Whether the call failed.</summary>
    public abstract bool IsError { get; }

    /// <summary>The final model-facing content.</summary>
    public abstract IReadOnlyList<ContentBlock> Content { get; }
}

/// <summary>Successful canonical tool execution.</summary>
public sealed record ToolExecutionSuccess(JsonElement Value, IReadOnlyList<ContentBlock> Blocks) : ToolExecutionResult
{
    public override bool IsError => false;

    public override IReadOnlyList<ContentBlock> Content => Blocks;
}

/// <summary>Failed canonical tool execution; failures never carry a successful value.</summary>
public sealed record ToolExecutionFailure(ToolFailure Error, IReadOnlyList<ContentBlock> Blocks) : ToolExecutionResult
{
    public override bool IsError => true;

    public override IReadOnlyList<ContentBlock> Content => Blocks;
}

/// <summary>Canonical failure detail.</summary>
public sealed record ToolFailure(string Message, string? Name = null, string? Code = null);

/// <summary>Pre-dispatch decision: allow runs the call; deny materializes an error result.</summary>
public abstract record PreToolDecision
{
    /// <summary>"allow" or "deny".</summary>
    public abstract string Kind { get; }
}

/// <summary>Run the call.</summary>
public sealed record AllowDecision : PreToolDecision
{
    public override string Kind => "allow";
}

/// <summary>Materialize an error result.</summary>
public sealed record DenyDecision(string Reason) : PreToolDecision
{
    public override string Kind => "deny";
}

/// <summary>Post-dispatch decision: accept the result or block it into an error.</summary>
public abstract record PostToolDecision
{
    /// <summary>"accept" or "block".</summary>
    public abstract string Kind { get; }
}

/// <summary>Keep the dispatched result.</summary>
public sealed record AcceptDecision : PostToolDecision
{
    public override string Kind => "accept";
}

/// <summary>Turn the result into an error with corrective feedback.</summary>
public sealed record BlockDecision(IReadOnlyList<ContentBlock> Feedback) : PostToolDecision
{
    public override string Kind => "block";
}


