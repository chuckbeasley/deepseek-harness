namespace Dsh.Hooks;

/// <summary>The folded outcome of every hook that matched one point.</summary>
public sealed record MergedHookOutcome(
    /// <summary>
    /// The most-restrictive permission decision across all hooks (<c>deny</c> &gt; <c>ask</c> &gt;
    /// <c>allow</c>), or <c>none</c> when no hook expressed one. <c>block</c>/<c>deny</c> both fold
    /// to <c>deny</c>; <c>approve</c>/<c>allow</c> both fold to <c>allow</c>.
    /// </summary>
    string Decision,
    /// <summary>Joined (<c>\n\n</c>) reasons from every blocking/denying hook, or <c>null</c>.</summary>
    string? Reason,
    /// <summary><c>true</c> when any hook asked to halt (<c>continue:false</c>).</summary>
    bool Stop,
    /// <summary>The first halting hook's <c>stopReason</c>, when one halted.</summary>
    string? StopReason,
    /// <summary>Every hook's <c>additionalContext</c>, in hook order (no joining — the bridge decides).</summary>
    IReadOnlyList<string> AdditionalContext,
    /// <summary>Every hook's <c>systemMessage</c>, in hook order.</summary>
    IReadOnlyList<string> SystemMessages);

/// <summary>
/// Merge matched hooks into one most-restrictive outcome (port of the TS <c>mergeHookOutputs</c>).
/// Permission precedence is <c>deny &gt; ask &gt; allow</c>; the first <c>continue:false</c> stop is
/// sticky; reasons for the winning rank are joined; and context and system messages accumulate in
/// hook order.
/// </summary>
public static class HookMerge
{
    /// <summary>
    /// Fold <paramref name="outputs"/> (the results of every hook that matched a point, in hook
    /// order) into one outcome by the precedence rules. An empty list yields a neutral outcome —
    /// the caller treats that as "no hook had anything to say".
    /// </summary>
    /// <param name="outputs">every matched hook's decoded output, in hook order.</param>
    /// <returns>the single folded outcome the bridge maps onto its extension point.</returns>
    public static MergedHookOutcome MergeHookOutputs(IReadOnlyList<HookOutput> outputs)
    {
        var maxRank = 0;
        // Keep reasons per rank so only objections explaining the winning decision surface.
        var reasonsByRank = new Dictionary<int, List<string>>();
        var stop = false;
        string? stopReason = null;
        var additionalContext = new List<string>();
        var systemMessages = new List<string>();

        foreach (var output in outputs)
        {
            var rank = Rank(output.Decision);
            if (rank > maxRank) maxRank = rank;
            if ((rank == 3 || rank == 2) && output.Reason is { Length: > 0 })
            {
                if (!reasonsByRank.TryGetValue(rank, out var list))
                {
                    list = new List<string>();
                    reasonsByRank[rank] = list;
                }
                list.Add(output.Reason);
            }
            if (output.Continue == false && !stop)
            {
                stop = true;
                if (output.StopReason is not null) stopReason = output.StopReason;
            }
            if (output.AdditionalContext is { Length: > 0 })
            {
                additionalContext.Add(output.AdditionalContext);
            }
            if (output.SystemMessage is { Length: > 0 })
            {
                systemMessages.Add(output.SystemMessage);
            }
        }

        reasonsByRank.TryGetValue(maxRank, out var reasons);
        return new MergedHookOutcome(
            DecisionForRank(maxRank),
            reasons is { Count: > 0 } ? string.Join("\n\n", reasons) : null,
            stop,
            stopReason,
            additionalContext,
            systemMessages);
    }

    /// <summary>Rank a single hook's decision for the deny&gt;ask&gt;allow precedence (higher = stricter).</summary>
    private static int Rank(string? decision) => decision switch
    {
        "deny" or "block" => 3,
        "ask" => 2,
        "approve" or "allow" => 1,
        _ => 0,
    };

    /// <summary>Collapse a ranked decision back to the merged vocabulary.</summary>
    private static string DecisionForRank(int maxRank) => maxRank switch
    {
        3 => "deny",
        2 => "ask",
        1 => "allow",
        _ => "none",
    };
}
