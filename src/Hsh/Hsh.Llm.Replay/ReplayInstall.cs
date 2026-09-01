using System.Text.RegularExpressions;
using Harness.Llm;

namespace Harness.Llm.Replay;

/// <summary>
/// Handle returned by <see cref="ReplayInstall.Install"/>: removal plus the end-of-run consumption
/// check that turns silent fixture underruns into a crisp diagnostic at teardown.
/// </summary>
public sealed class ReplayHandle : IDisposable
{
    private readonly IDisposable _registration;
    private readonly Func<IReadOnlyList<string>> _problems;

    internal ReplayHandle(IDisposable registration, Func<IReadOnlyList<string>> problems)
    {
        _registration = registration;
        _problems = problems;
    }

    /// <summary>Throw unless every recorded script was bound and every bound cursor consumed its full entry list.</summary>
    public void AssertConsumed()
    {
        var problems = _problems();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "llm-replay: fixture not fully consumed — " + string.Join("; ", problems)
                + "; the scenario drove fewer model calls than recorded");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _registration.Dispose();
}

/// <summary>
/// Install per-session positional replay on an LLM runtime. A newly seen live session takes the
/// next ordered recorded script, then advances its own cursor synchronously at invocation time;
/// calls without a session id share one anonymous session. Port of
/// <c>@deepseek-ai/hsh-llm-replay</c>'s <c>installLlmReplay</c>.
/// </summary>
public static class ReplayInstall
{
    private const string AnonymousSession = "\0anon\0";
    private const string FromRequestOpen = "{{fromRequest:";
    private const string FromRequestClose = "}}";

    /// <summary>Install the replay adapter under <see cref="ReplayConfig.Provider"/> (default <c>deepseek-official</c>).</summary>
    public static ReplayHandle Install(LlmRuntime llm, ReplayConfig config)
    {
        ArgumentNullException.ThrowIfNull(llm);
        ArgumentNullException.ThrowIfNull(config);
        if (config.PaceMs < 0)
        {
            throw new InvalidOperationException($"llm-replay: paceMs must be a non-negative integer, got {config.PaceMs}");
        }
        var paceMs = config.PaceMs;
        var scripts = ReplayScript.LoadSessionScripts(config);
        var bound = new Dictionary<string, ScriptState>(StringComparer.Ordinal);
        var liveSessionIds = new string?[scripts.Count];
        var nextScript = 0;
        var scriptCount = scripts.Count;

        IAsyncEnumerable<StreamChunk> Replay(GenerateOptions options, CancellationToken ct)
        {
            var key = options.SessionId ?? AnonymousSession;
            if (!bound.TryGetValue(key, out var state))
            {
                if (nextScript >= scriptCount)
                {
                    return ThrowStream(new LlmError(
                        $"llm-replay: a model call arrived from an unrecorded session (#{nextScript + 1}); "
                        + $"the scenario recorded only {scriptCount} session(s) — re-record it", "REPLAY_UNRECORDED"));
                }
                var scriptIndex = nextScript;
                nextScript++;
                state = new ScriptState(scripts[scriptIndex].Entries);
                bound[key] = state;
                if (key != AnonymousSession) liveSessionIds[scriptIndex] = key;
            }
            var boundState = state;
            var index = boundState.Cursor++;
            var entry = index < boundState.Entries.Count ? boundState.Entries[index] : null;
            return Serve(entry, index, boundState.Entries.Count, options, ct, paceMs, liveSessionIds);
        }

        var provider = config.Provider ?? "deepseek-official";
        var registration = llm.RegisterAdapter(new[] { provider }, new ReplayAdapter(Replay, config.Models));
        return new ReplayHandle(registration, () =>
        {
            var problems = new List<string>();
            if (nextScript < scripts.Count)
            {
                problems.Add($"{scripts.Count - nextScript} recorded script(s) never bound to a live session");
            }
            foreach (var pair in bound)
            {
                if (pair.Value.Cursor < pair.Value.Entries.Count)
                {
                    var who = pair.Key == AnonymousSession ? "the anonymous session" : $"session {pair.Key}";
                    problems.Add($"{who} consumed {pair.Value.Cursor}/{pair.Value.Entries.Count} recorded call(s)");
                }
            }
            return problems;
        });
    }

    private static async IAsyncEnumerable<StreamChunk> Serve(
        ReplayEntry? entry,
        int index,
        int entryCount,
        GenerateOptions options,
        CancellationToken ct,
        int paceMs,
        string?[] liveSessionIds)
    {
        if (entry is null)
        {
            throw new LlmError(
                $"llm-replay: script exhausted — session requested model call #{index + 1} "
                + $"but its script has only {entryCount}; re-record the scenario", "REPLAY_EXHAUSTED");
        }
        var resolved = ResolveScriptedEntry(entry, options.Messages, liveSessionIds);
        await foreach (var chunk in ReplayEntryStream(resolved, ct, paceMs).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<StreamChunk> ReplayEntryStream(ReplayEntry entry, CancellationToken ct, int paceMs)
    {
        switch (entry)
        {
            case ChunksEntry chunks:
                foreach (var chunk in chunks.Chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    if (paceMs > 0) await Task.Delay(paceMs, ct).ConfigureAwait(false);
                    yield return chunk;
                }
                yield break;
            case ThrowEntry thrown:
                foreach (var chunk in thrown.Chunks)
                {
                    ct.ThrowIfCancellationRequested();
                    if (paceMs > 0) await Task.Delay(paceMs, ct).ConfigureAwait(false);
                    yield return chunk;
                }
                throw new LlmError(thrown.Message, thrown.Code);
            case HangEntry hang:
                yield return new BlockStart(0, "text");
                yield return new TextDelta(0, "partial");
                if (hang.ReadyFile is not null) File.WriteAllText(hang.ReadyFile, "");
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => completion.TrySetResult());
                await completion.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                yield break;
            default:
                throw new InvalidOperationException($"llm-replay: unknown replay entry kind {entry.GetType().Name}");
        }
    }

    private static async IAsyncEnumerable<StreamChunk> ThrowStream(LlmError error)
    {
        throw error;
        yield break;
    }

    private sealed class ScriptState(IReadOnlyList<ReplayEntry> entries)
    {
        public IReadOnlyList<ReplayEntry> Entries { get; } = entries;

        public int Cursor { get; set; }
    }

    /// <summary>Resolve typed recorded-session tokens and <c>{{fromRequest:&lt;pattern&gt;}}</c> placeholders in one scripted entry.</summary>
    internal static ReplayEntry ResolveScriptedEntry(ReplayEntry entry, IReadOnlyList<Message> messages, string?[] liveSessionIds)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(entry, ReplayScript.FixtureJson);
        if (!json.Contains("{{"))
        {
            return entry;
        }
        var leaves = new List<string>();
        CollectMessageStrings(messages, leaves);
        var corpus = string.Join('\n', leaves);
        var resolved = SubstituteText(json, corpus, liveSessionIds);
        return System.Text.Json.JsonSerializer.Deserialize<ReplayEntry>(resolved, ReplayScript.FixtureJson)!;
    }

    /// <summary>Substitute on the serialized JSON text so placeholders inside any nesting resolve uniformly.</summary>
    private static string SubstituteText(string text, string corpus, string?[] liveSessionIds)
    {
        if (!text.Contains("{{")) return text;
        // Session tokens first: {{session:N}} maps to the live session bound at the same corpus index.
        // Live ids are UUIDs, so the raw replacement stays JSON-safe inside a string literal.
        var withSessions = Regex.Replace(text, @"\{\{session:([1-9]\d*)\}\}", match =>
        {
            var ordinal = int.Parse(match.Groups[1].Value);
            var live = liveSessionIds[ordinal - 1];
            if (live is null)
            {
                throw new InvalidOperationException(
                    $"llm-replay: session token {{{{session:{ordinal}}}}} was used before that recorded session bound");
            }
            return live;
        });
        if (!withSessions.Contains(FromRequestOpen)) return withSessions;
        var result = new System.Text.StringBuilder();
        var cursor = 0;
        while (true)
        {
            var open = withSessions.IndexOf(FromRequestOpen, cursor, StringComparison.Ordinal);
            if (open == -1)
            {
                result.Append(withSessions, cursor, withSessions.Length - cursor);
                return result.ToString();
            }
            var close = withSessions.IndexOf(FromRequestClose, open + FromRequestOpen.Length, StringComparison.Ordinal);
            if (close == -1)
            {
                throw new InvalidOperationException("llm-replay: fromRequest placeholder is unterminated");
            }
            while (close + FromRequestClose.Length < withSessions.Length
                && withSessions[close + FromRequestClose.Length] == '}')
            {
                close++;
            }
            var pattern = withSessions.Substring(open + FromRequestOpen.Length, close - (open + FromRequestOpen.Length));
            result.Append(withSessions, cursor, open - cursor);
            result.Append(EscapeJsonString(ResolveFromRequest(pattern, corpus)));
            cursor = close + FromRequestClose.Length;
        }
    }

    /// <summary>JSON-escape one replacement value for insertion inside a string literal.</summary>
    private static string EscapeJsonString(string value)
    {
        var encoded = System.Text.Json.JsonSerializer.Serialize(value);
        return encoded.Substring(1, encoded.Length - 2);
    }

    private static string ResolveFromRequest(string pattern, string corpus)
    {
        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"llm-replay: fromRequest has an invalid pattern {System.Text.Json.JsonSerializer.Serialize(pattern)}: {error.Message}");
        }
        Match last = null!;
        foreach (Match match in regex.Matches(corpus))
        {
            last = match;
        }
        if (last is null)
        {
            throw new InvalidOperationException(
                $"llm-replay: fromRequest pattern {System.Text.Json.JsonSerializer.Serialize(pattern)} matched nothing in the request");
        }
        return last.Groups[1].Success ? last.Groups[1].Value : last.Value;
    }

    private static void CollectMessageStrings(IReadOnlyList<Message> messages, List<string> leaves)
    {
        foreach (var message in messages)
        {
            foreach (var block in message.Content)
            {
                CollectBlockStrings(block, leaves);
            }
        }
    }

    private static void CollectBlockStrings(ContentBlock block, List<string> leaves)
    {
        switch (block)
        {
            case TextBlock text:
                leaves.Add(text.Text);
                break;
            case ReasoningBlock reasoning:
                leaves.Add(reasoning.Text);
                break;
            case ToolCallBlock toolCall:
                leaves.Add(toolCall.Id.Value);
                leaves.Add(toolCall.Name);
                leaves.Add(toolCall.Arguments);
                break;
            case ToolResultBlock toolResult:
                leaves.Add(toolResult.ToolCallId.Value);
                foreach (var inner in toolResult.Content) CollectBlockStrings(inner, leaves);
                break;
        }
    }
}