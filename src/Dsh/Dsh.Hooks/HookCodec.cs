using System.Text.Json;

namespace Dsh.Hooks;

/// <summary>
/// Decode hook process outcomes for both dialects (port of codec.ts). Exit 0 may carry structured
/// JSON or plain stdout; exit 2 blocks with stderr as the reason; every other exit is a
/// non-blocking error. The legacy top-level decision is approve/block only; allow/deny/ask come
/// only from hookSpecificOutput.permissionDecision. Total: malformed JSON remains plain stdout.
/// </summary>
public static class HookCodec
{
    private const int BlockingExitCode = 2;

    /// <summary>
    /// Decode process output into a dialect-neutral hook outcome. When <paramref name="expectedEventName"/>
    /// is set, a missing or different hookSpecificOutput.hookEventName discards only its
    /// event-scoped fields; top-level fields and the claimed discriminator remain.
    /// </summary>
    public static HookOutput ParseHookOutput(int? exitCode, string stdout, string stderr, string? expectedEventName = null)
    {
        var trimmedErr = stderr.Trim();
        var trimmedOut = stdout.Trim();
        var output = new HookOutput
        {
            ExitCode = exitCode,
            Stderr = trimmedErr,
            Stdout = trimmedOut,
        };

        if (exitCode == BlockingExitCode)
        {
            output = output with { Decision = "block" };
            if (trimmedErr.Length > 0) output = output with { Reason = trimmedErr };
        }

        if (exitCode == 0 && trimmedOut.StartsWith('{'))
        {
            Dictionary<string, object?>? parsed = null;
            try
            {
                var document = JsonDocument.Parse(trimmedOut);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    parsed = ConvertObject(document.RootElement);
                }
            }
            catch (JsonException)
            {
                // Malformed JSON on a clean exit = no structured output (lenient, as the
                // reference engines are). The plain stdout remains the bridge's to use.
            }
            if (parsed is not null) output = ApplyStructured(output, parsed, expectedEventName);
        }

        return output;
    }

    private static HookOutput ApplyStructured(HookOutput output, Dictionary<string, object?> parsed, string? expectedEventName)
    {
        if (parsed.TryGetValue("continue", out var cont) && cont is bool contBool) output = output with { Continue = contBool };
        if (parsed.TryGetValue("stopReason", out var stop) && stop is string stopText) output = output with { StopReason = stopText };
        if (parsed.TryGetValue("systemMessage", out var sys) && sys is string sysText) output = output with { SystemMessage = sysText };

        if (parsed.TryGetValue("decision", out var topDecision) && topDecision is string topText && TopLevelDecisionOf(topText) is { } top)
        {
            output = output with { Decision = top };
        }
        if (parsed.TryGetValue("reason", out var topReason) && topReason is string topReasonText)
        {
            output = output with { Reason = topReasonText };
        }

        if (parsed.TryGetValue("hookSpecificOutput", out var hsoValue) && hsoValue is Dictionary<string, object?> hso)
        {
            if (hso.TryGetValue("hookEventName", out var eventName) && eventName is string eventText)
            {
                output = output with { HookEventName = eventText };
            }
            if (expectedEventName is not null && output.HookEventName != expectedEventName)
            {
                return output;
            }
            if (hso.TryGetValue("permissionDecision", out var permission) && permission is string permissionText
                && PermissionDecisionOf(permissionText) is { } permitted)
            {
                output = output with { Decision = permitted };
            }
            if (hso.TryGetValue("permissionDecisionReason", out var permissionReason) && permissionReason is string permissionReasonText)
            {
                output = output with { Reason = permissionReasonText };
            }
            if (hso.TryGetValue("additionalContext", out var context) && context is string contextText)
            {
                output = output with { AdditionalContext = contextText };
            }
            if (hso.TryGetValue("updatedInput", out var updated) && updated is Dictionary<string, object?> updatedMap)
            {
                output = output with { UpdatedInput = updatedMap };
            }
        }
        return output;
    }

    private static string? TopLevelDecisionOf(string value)
        => value is "approve" or "block" ? value : null;

    private static string? PermissionDecisionOf(string value)
        => value is "allow" or "deny" or "ask" ? value : null;

    private static Dictionary<string, object?> ConvertObject(JsonElement element)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = ConvertValue(property.Value);
        }
        return map;
    }

    private static object? ConvertValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ConvertObject(element),
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertValue).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
