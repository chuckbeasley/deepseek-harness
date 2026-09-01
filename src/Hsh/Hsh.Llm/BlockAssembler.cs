using System.Text.Json;

namespace Harness.Llm;

/// <summary>
/// Incremental chunk-to-message assembler: the single canonical assembly algorithm used by the
/// agent loop to build an assistant message from a chunk stream while logging the raw chunks for
/// replay fidelity. Tolerant of delta-only protocols (no block-start/end); deltas arriving for an
/// index already closed by block-end are ignored (malformed stream).
/// </summary>
public sealed class BlockAssembler
{
    private sealed record PartialBlock(
        string BlockType,
        string Text,
        ToolCallId? ToolCallId,
        string? ToolCallName,
        string ToolCallArguments,
        ContentBlock? Block);

    private readonly Dictionary<int, PartialBlock> _partials = new();
    private readonly List<int> _order = new();
    private TokenUsage? _usage;
    private FinishReason? _finish;
    private ReplayEnvelope? _replayState;

    /// <summary>Feed one chunk into the assembly state, in stream order.</summary>
    public void Push(StreamChunk chunk)
    {
        switch (chunk)
        {
            case BlockStart start:
                if (!_partials.ContainsKey(start.Index))
                {
                    _order.Add(start.Index);
                    _partials[start.Index] = NewPartial(start.BlockType);
                }
                break;
            case TextDelta delta:
                AccumulateText(delta.Index, "text", delta.Text);
                break;
            case ReasoningDelta delta:
                AccumulateText(delta.Index, "reasoning", delta.Text);
                break;
            case ToolCallDelta delta:
            {
                var partial = Ensure(delta.Index, "tool-call");
                if (partial.Block is not null) break; // closed by block-end; ignore stragglers
                _partials[delta.Index] = partial with
                {
                    ToolCallId = delta.Id,
                    ToolCallName = delta.Name ?? partial.ToolCallName,
                    ToolCallArguments = partial.ToolCallArguments + delta.ArgumentsDelta,
                };
                break;
            }
            case BlockEnd end:
            {
                var partial = Ensure(end.Index, end.Block.BlockType);
                if (partial.Block is not null) break; // first close wins
                _partials[end.Index] = partial with { Block = end.Block };
                break;
            }
            case UsageChunk usage:
                _usage = usage.Usage;
                break;
            case Finish finish:
                _finish = finish.Reason;
                _replayState = finish.ReplayState;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(chunk), chunk, "unknown stream chunk");
        }
    }

    /// <summary>
    /// Assemble all blocks seen so far, in stream order; max-token truncation drops tool calls
    /// that cannot be executed safely; an open block assembles from its accumulated deltas.
    /// </summary>
    public IReadOnlyList<ContentBlock> Blocks() => Assembled().Blocks;

    /// <summary>
    /// Assemble the prefix an interrupted stream can safely finalize: closed and open text /
    /// reasoning blocks with non-whitespace content, in stream order. Tool calls are omitted.
    /// </summary>
    public IReadOnlyList<ContentBlock> InterruptedBlocks()
    {
        var result = new List<ContentBlock>();
        foreach (var index in _order)
        {
            var partial = _partials[index];
            var type = partial.Block?.BlockType ?? partial.BlockType;
            if (type is not ("text" or "reasoning")) continue;
            var block = Assemble(partial, index);
            if (block is TextBlock { Text.Length: > 0 } text && text.Text.Trim().Length > 0)
            {
                result.Add(text);
            }
            else if (block is ReasoningBlock { Text.Length: > 0 } reasoning && reasoning.Text.Trim().Length > 0)
            {
                result.Add(reasoning);
            }
        }
        return result;
    }

    /// <summary>Usage from the usage chunk; null until one arrives.</summary>
    public TokenUsage? Usage => _usage;

    /// <summary>Finish reason from the finish chunk; <see cref="Stop"/> when the stream ended without one.</summary>
    public FinishReason Finish => _finish ?? new Stop();

    /// <summary>Replay metadata from the terminal finish chunk, pruned in step with <see cref="Blocks"/>.</summary>
    public ReplayEnvelope? ReplayState => Assembled().Replay;

    /// <summary>The assembled assistant message over <see cref="Blocks"/>.</summary>
    public Message Message(MessageSource? source = null)
        => new AssistantMessage
        {
            Id = new MessageId(Guid.NewGuid().ToString("D")),
            Content = Blocks(),
            Source = source ?? new PluginSource { Plugin = "hsh-llm/assembler" },
        };

    private void AccumulateText(int index, string blockType, string text)
    {
        var partial = Ensure(index, blockType);
        if (partial.Block is not null) return; // closed by block-end; ignore stragglers
        _partials[index] = partial with { Text = partial.Text + text };
    }

    private PartialBlock Ensure(int index, string blockType)
    {
        if (!_partials.TryGetValue(index, out var partial))
        {
            partial = NewPartial(blockType);
            _partials[index] = partial;
            _order.Add(index);
        }
        return partial;
    }

    private static PartialBlock NewPartial(string blockType)
        => new(blockType, string.Empty, null, null, string.Empty, null);

    private ContentBlock Assemble(PartialBlock partial, int index)
    {
        if (partial.Block is not null) return partial.Block;
        return partial.BlockType switch
        {
            "text" => new TextBlock(partial.Text),
            "reasoning" => new ReasoningBlock(partial.Text),
            "tool-call" => new ToolCallBlock(
                partial.ToolCallId ?? new ToolCallId($"call-{index}"),
                partial.ToolCallName ?? string.Empty,
                partial.ToolCallArguments),
            _ => throw new InvalidOperationException($"cannot assemble incomplete block of type \"{partial.BlockType}\""),
        };
    }

    private (IReadOnlyList<ContentBlock> Blocks, ReplayEnvelope? Replay) Assembled()
    {
        var all = _order.Select(index => Assemble(_partials[index], index)).ToList();
        var keep = new bool[all.Count];
        var blocks = new List<ContentBlock>();
        for (var i = 0; i < all.Count; i++)
        {
            keep[i] = !(Finish is MaxTokens && all[i] is ToolCallBlock);
            if (keep[i]) blocks.Add(all[i]);
        }
        var envelope = _replayState;
        if (envelope is null || envelope.Blocks is null) return (blocks, envelope);
        if (envelope.Blocks.Length != all.Count) return (blocks, null);
        var pruned = new List<JsonElement>();
        for (var i = 0; i < keep.Length; i++)
        {
            if (keep[i]) pruned.Add(envelope.Blocks[i]);
        }
        return (blocks, pruned.Count == all.Count ? envelope : envelope with { Blocks = pruned.ToArray() });
    }
}


