using System.Text.Json.Serialization;
using Harness.Session;

namespace Harness.AgentLoop;

/// <summary>Durable retry-scheduling marker: one scheduled attempt for a failed model call (port of the TS llm/retry event).</summary>
public sealed record LlmRetryEvent : SessionEvent
{
    public const string EventTypeName = "llm/retry";

    public required string RetryId { get; init; }

    public required long Turn { get; init; }

    public required long Step { get; init; }

    public required string Provider { get; init; }

    public required string Mode { get; init; }

    public required string PolicyKey { get; init; }

    public required int Retry { get; init; }

    public int? MaxRetries { get; init; }

    public required long DelayMs { get; init; }

    public required LlmFailure Failure { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Durable retry-started marker: the scheduled attempt actually dispatched (port of the TS llm/retry-started event).</summary>
public sealed record LlmRetryStartedEvent : SessionEvent
{
    public const string EventTypeName = "llm/retry-started";

    public required string RetryId { get; init; }

    public required long Turn { get; init; }

    public required long Step { get; init; }

    public required int Retry { get; init; }

    public override string Type => EventTypeName;
}

/// <summary>Register the llm/retry event types into the session registry.</summary>
public static class LlmRetryEventTypes
{
    /// <summary>Register both markers; idempotent per discriminator.</summary>
    public static void Register()
    {
        SessionEventTypes.Register(LlmRetryEvent.EventTypeName, typeof(LlmRetryEvent));
        SessionEventTypes.Register(LlmRetryStartedEvent.EventTypeName, typeof(LlmRetryStartedEvent));
    }
}