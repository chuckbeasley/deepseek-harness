using Harness.Llm;
using Harness.Session;

namespace Harness.SessionQuery;

/// <summary>
/// First-party semantic text extraction (port of extraction.ts). Structural boundaries, raw stream
/// chunks, request envelopes, and unknown merge-extensible events contribute no text.
/// </summary>
public static class EventTextExtraction
{
    /// <summary>Extract searchable semantic text from one session event; an empty string when non-searchable.</summary>
    public static string ExtractEventText(SessionEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt switch
        {
            UserMessageEvent user => ContentText(user.Message.Content),
            AssistantMessageEvent assistant => ContentText(assistant.Message.Content),
            ToolCallEvent call => JoinText(new[] { call.Name, call.Arguments }),
            ToolResultEvent tool => JoinText(new[]
            {
                ContentText(tool.Message.Content),
                tool.Error?.Name ?? string.Empty,
                tool.Error?.Code ?? string.Empty,
            }),
            TurnEndEvent end => TurnEndText(end.Reason),
            _ => string.Empty,
        };
    }

    private static string TurnEndText(TurnEndReason reason) => reason switch
    {
        ErrorReason error => JoinText(new[] { "error", error.Failure.Message }),
        AbortedReason => "aborted",
        MaxTokensReason => "max-tokens",
        InterruptedReason => "interrupted",
        _ => string.Empty,
    };

    private static string ContentText(IReadOnlyList<ContentBlock> content)
    {
        var parts = new List<string>();
        foreach (var block in content) AddBlockText(block, parts);
        return JoinText(parts);
    }

    private static void AddBlockText(ContentBlock block, List<string> parts)
    {
        switch (block)
        {
            case TextBlock text:
                parts.Add(text.Text);
                break;
            case ToolCallBlock call:
                parts.Add(call.Name);
                parts.Add(call.Arguments);
                break;
            case ToolResultBlock result:
                foreach (var inner in result.Content) AddBlockText(inner, parts);
                break;
            // Reasoning and unknown blocks are not searchable.
        }
    }

    private static string JoinText(IEnumerable<string> parts)
        => string.Join("\n", parts.Select(part => part.Trim()).Where(part => part.Length > 0));
}
