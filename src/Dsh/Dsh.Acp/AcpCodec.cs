using Dsh.Session;

namespace Dsh.Acp;

/// <summary>Pure translation between the harness lifecycle and the automation-only ACP wire (port of the TS <c>codec.ts</c>).</summary>
public static class AcpCodec
{
    /// <summary>
    /// Map a harness turn ending to ACP's terminal reason vocabulary. <c>cancelled</c> is reserved
    /// for explicit client cancellation (<c>session/cancel</c>) and disposal, both settled out of
    /// band; a turn aborted by a hook or another owner is ordinary quiescence and reports
    /// <c>end_turn</c>.
    /// </summary>
    /// <param name="reason">the harness turn outcome.</param>
    /// <returns>the closest legal ACP stop reason.</returns>
    public static string TurnEndToStopReason(TurnEndReason reason) => reason switch
    {
        CompletedReason => "end_turn",
        MaxTokensReason => "max_tokens",
        InterruptedReason => "cancelled",
        _ => "end_turn",
    };
}
