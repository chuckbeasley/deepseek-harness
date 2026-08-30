using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Guard;

/// <summary>
/// Advisory repeat-call detector config (port of the TS repeat-tool-reminder Config). Misconfiguration
/// fails loud at <see cref="RepeatToolReminderGuard"/> construction, never a silent fall-back: an
/// empty threshold list, a non-integer, a value below 2, or a duplicate throws. <c>Include</c>/<c>Exclude</c>
/// entries are <c>*</c>-wildcard predicates over tool names, not references to registry entries — a
/// pattern matching no currently registered tool is valid.
/// </summary>
public sealed record RepeatToolReminderConfig
{
    /// <summary>Consecutive-repeat counts that trigger a reminder (default [3, 5, 8]).</summary>
    public int[] Thresholds { get; init; } = new[] { 3, 5, 8 };

    /// <summary>Tool-name patterns to track; empty means every tool is tracked.</summary>
    public string[] Include { get; init; } = Array.Empty<string>();

    /// <summary>Tool-name patterns transparent to the chain (neither count nor reset).</summary>
    public string[] Exclude { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Maximum characters of canonical arguments quoted in the detailed reminder (default 500).
    /// Large payloads would otherwise ride into the next request unbounded; the cap bounds the
    /// reminder, never the detection (the chain key always compares the full canonical string).
    /// </summary>
    public int ArgumentsPreviewChars { get; init; } = 500;
}

/// <summary>
/// One agent's consecutive-repeat chain: the last tracked call's identity key and its run length
/// (port of the TS Chain). Chains are keyed by the live <see cref="Dsh.Session.Session"/> object — the port's
/// per-agent identity, since each published agent owns a distinct session — and dropped when the
/// session is disposed.
/// </summary>
internal sealed class RepeatChain
{
    /// <summary>Identity key of the last tracked call (name + canonical arguments).</summary>
    public string Key = "";

    /// <summary>Consecutive run length of that key.</summary>
    public int Count;
}

/// <summary>
/// Advisory per-agent repeat-call detector (port of the TS repeat-tool-reminder plugin). It
/// subscribes to <c>session/event</c> on the owner context, counts identical tool calls per agent
/// from the committed <c>tool/call</c> events, and injects a reminder user message into the
/// session log when a run length hits a configured threshold — enriching post-execute decisions
/// with logged model context without vetoing or rewriting calls. A user message resets the chain;
/// distinct calls reset it; excluded tools are transparent. The reminder texts and thresholds are
/// the TS originals verbatim.
/// </summary>
public sealed class RepeatToolReminderGuard : Service, IGuardService
{
    /// <summary>The stable guard name.</summary>
    public const string GuardName = "repeat-tool-reminder";

    /// <summary>The service key this guard registers under.</summary>
    public const string ServiceKey = "guard:repeat-tool-reminder";

    /// <summary>The gentle first-threshold reminder, keyed to <c>Thresholds[0]</c>, not a literal count.</summary>
    public const string GentleReminder =
        "You are repeating the exact same tool call with identical arguments. "
        + "Carefully analyze the previous result before calling again: if the task is "
        + "not complete, try a different approach or different arguments instead of "
        + "repeating the call.";

    private readonly int[] _thresholds;
    private readonly HashSet<int> _thresholdSet;
    private readonly Regex[] _include;
    private readonly Regex[] _exclude;
    private readonly int _argumentsPreviewChars;
    private readonly ConditionalWeakTable<Dsh.Session.Session, RepeatChain> _chains = new();

    /// <summary>
    /// Create and install the guard as <c>guard:repeat-tool-reminder</c>; validation fails loud here.
    /// </summary>
    /// <param name="ctx">the owner context whose <c>session/event</c> stream is observed.</param>
    /// <param name="config">the guard configuration; absent fields take the documented defaults.</param>
    public RepeatToolReminderGuard(Context ctx, RepeatToolReminderConfig? config = null)
        : base(ctx, ServiceKey)
    {
        try
        {
            var validated = config ?? new RepeatToolReminderConfig();
            _thresholds = ValidateThresholds(validated.Thresholds);
            _thresholdSet = new HashSet<int>(_thresholds);
            _include = (validated.Include ?? Array.Empty<string>()).Select(WildcardToRegex).ToArray();
            _exclude = (validated.Exclude ?? Array.Empty<string>()).Select(WildcardToRegex).ToArray();
            _argumentsPreviewChars = validated.ArgumentsPreviewChars;
            if (_argumentsPreviewChars < 1)
            {
                throw new ArgumentException(
                    $"repeat-tool-reminder: invalid argumentsPreviewChars {_argumentsPreviewChars} — must be an integer >= 1",
                    nameof(config));
            }
            Ctx.On("session/event", (Delegate)(Action<Dsh.Session.Session, SessionEvent>)Drive);
            Ctx.On("session/disposed", (Delegate)(Action<Dsh.Session.Session>)Drop);
        }
        catch
        {
            // Fail loud and leak nothing: unregister the service entry the base constructor just
            // created so a refused config leaves no half-installed guard behind.
            Ctx.Remove(ServiceKey);
            throw;
        }
    }

    /// <inheritdoc />
    public long? TimeoutMsFor(string toolName) => null;

    /// <inheritdoc />
    string IGuardService.Name => GuardName;

    /// <summary>
    /// Render the detailed later-threshold reminder naming the tool, the run length, and the
    /// canonical arguments.
    /// </summary>
    public static string DetailedReminder(string toolName, int count, string canonicalArguments)
        => "Repeated tool call detected:\n"
            + $"- tool: {toolName}\n"
            + $"- consecutive_calls: {count}\n"
            + $"- arguments: {canonicalArguments}\n"
            + "The repeated calls are not making progress. Do not call this tool with "
            + "these exact arguments again. Inspect the latest result and choose a "
            + "different action, different arguments, or finish the task if enough "
            + "evidence has been gathered.";

    /// <summary>
    /// Head-truncate the canonical arguments for quoting in the detailed reminder, marking how
    /// much was omitted. Bounds only the model-visible text — the chain key always uses the full
    /// canonical string.
    /// </summary>
    public static string PreviewArguments(string canonical, int cap)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        if (canonical.Length <= cap) return canonical;
        return $"{canonical[..cap]}… (+{canonical.Length - cap} more chars)";
    }

    /// <summary>
    /// Validate <paramref name="values"/> per the fail-loud contract and return them sorted
    /// ascending (the escalation rule reads <c>Thresholds[0]</c> as the gentle tier, so order is
    /// normalized here, once).
    /// </summary>
    public static int[] ValidateThresholds(int[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("repeat-tool-reminder: thresholds must not be empty", nameof(values));
        }
        foreach (var value in values)
        {
            if (value < 2)
            {
                throw new ArgumentException(
                    $"repeat-tool-reminder: invalid threshold {value} — every threshold must be an integer >= 2",
                    nameof(values));
            }
        }
        if (values.Distinct().Count() != values.Length)
        {
            throw new ArgumentException("repeat-tool-reminder: thresholds must not contain duplicates", nameof(values));
        }
        var sorted = (int[])values.Clone();
        Array.Sort(sorted);
        return sorted;
    }

    /// <summary>Compile one <c>*</c>-wildcard pattern to an anchored regex (every other regex metacharacter is matched literally).</summary>
    public static Regex WildcardToRegex(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var builder = new StringBuilder(pattern.Length + 2);
        foreach (var ch in pattern)
        {
            if ("|\\{}()[]^$+?.".Contains(ch)) builder.Append('\\');
            builder.Append(ch);
        }
        return new Regex("^" + builder.ToString().Replace("*", ".*") + "$", RegexOptions.Compiled);
    }

    /// <summary>Whether a tool participates in the chain (untracked calls are transparent: they neither count nor reset).</summary>
    public bool Tracked(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        if (_include.Length > 0 && !_include.Any(pattern => pattern.IsMatch(toolName))) return false;
        return !_exclude.Any(pattern => pattern.IsMatch(toolName));
    }

    /// <summary>Drive the guard's state from one committed session event.</summary>
    private void Drive(Dsh.Session.Session session, SessionEvent evt)
    {
        switch (evt)
        {
            case ToolCallEvent call:
                Observe(session, call);
                break;
            case UserMessageEvent { Message: { Source: UserSource } }:
                // A user interjection changes the context; repetition across it is not a loop.
                _chains.Remove(session);
                break;
        }
    }

    /// <summary>Drop one session's chain when its session is disposed (agent teardown).</summary>
    private void Drop(Dsh.Session.Session session) => _chains.Remove(session);

    /// <summary>
    /// Advance the calling session's chain for one committed tool call and inject the reminder,
    /// if this run length hits a configured threshold. Counting happens on the committed
    /// <c>tool/call</c> event, so calls that are later denied still draw the reminder — a model
    /// hammering a denied call is exactly the loop worth breaking.
    /// </summary>
    private void Observe(Dsh.Session.Session session, ToolCallEvent call)
    {
        if (!Tracked(call.Name)) return;
        var canonical = CanonicalJson.Canonicalize(call.Arguments);
        var key = $"{call.Name}\u0000{canonical}";
        var chain = _chains.GetValue(session, _ => new RepeatChain());
        var count = chain.Key == key ? chain.Count + 1 : 1;
        chain.Key = key;
        chain.Count = count;
        if (!_thresholdSet.Contains(count)) return;
        var text = count == _thresholds[0]
            ? GentleReminder
            : DetailedReminder(call.Name, count, PreviewArguments(canonical, _argumentsPreviewChars));
        session.Append(new UserMessageEvent
        {
            Message = Messages.CreateUserMessage(
                new ContentBlock[] { new TextBlock(text) },
                new PluginSource { Plugin = GuardName, Form = "notice" }),
            SurfaceOp = SurfaceOp.Append,
        });
    }
}
