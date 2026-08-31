using System.Text.Json;
using Cordis.Core;
using Dsh.Settings;

namespace Dsh.Web.Host;

/// <summary>
/// The settings remote methods (port of the TS SettingsController): the redacted describe and the
/// update/replace writes. Every read is redacted, and every provider refusal is classified as
/// <c>settings/conflict</c> (stale revision) or <c>settings/rejected</c> (anything else). The
/// settings/mutate path ops, canOpenAgentPresetDirectory, openSettingsDocument, and
/// openAgentPresetDirectory methods are deferred: the path-op model and the native openers are not
/// ported. The namespaces stay registered without a provider, answering an actionable
/// <c>gateway/internal</c> like the TS controller.
/// </summary>
public static class SettingsRemotes
{
    /// <summary>The redacted namespace catalog for configuration surfaces.</summary>
    public static RpcMethod Describe(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("settings/describe", (_, _) =>
        {
            var settings = Provider(ctx);
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["writable"] = settings.Writable,
                ["hasDocument"] = settings.DocumentPath is not null,
                ["namespaces"] = settings
                    .Describe(new SettingsDescribeOptions(RedactSecrets: true))
                    .Select(NamespaceView)
                    .ToArray(),
            }));
        });
    }

    /// <summary>Merge a patch into one namespace's stored user section and answer its new redacted view.</summary>
    public static RpcMethod Update(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("settings/update", (args, _) => WriteAsync(ctx, args, WriteKind.Update));
    }

    /// <summary>Replace one namespace's stored user section wholesale and answer its new redacted view.</summary>
    public static RpcMethod Replace(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("settings/replace", (args, _) => WriteAsync(ctx, args, WriteKind.Replace));
    }

    private static async Task<JsonElement?> WriteAsync(Context ctx, JsonElement? args, WriteKind kind)
    {
        var settings = Provider(ctx);
        var verb = kind == WriteKind.Update ? "update" : "replace";
        var ns = args is JsonElement element && element.TryGetProperty("ns", out var nsValue)
                && nsValue.ValueKind == JsonValueKind.String
            ? nsValue.GetString()
            : null;
        if (string.IsNullOrEmpty(ns))
        {
            throw new RpcBadRequestException($"settings/{verb} requires a non-empty ns");
        }
        var section = args is JsonElement argsElement && argsElement.TryGetProperty(kind == WriteKind.Update ? "patch" : "section", out var sectionValue)
                && sectionValue.ValueKind == JsonValueKind.Object
            ? sectionValue
            : default;
        if (section.ValueKind != JsonValueKind.Object)
        {
            throw new RpcBadRequestException($"settings/{verb} requires a {(kind == WriteKind.Update ? "patch" : "section")} object");
        }
        var expectedRevision = LongArg(args, "expectedRevision");
        try
        {
            var input = SettingsWireValues.FromJsonElement(section);
            if (kind == WriteKind.Update)
            {
                await settings.UpdateAsync(ns, input!, expectedRevision);
            }
            else
            {
                await settings.ReplaceAsync(ns, input!, expectedRevision);
            }
        }
        catch (Exception error)
        {
            throw Rejected(ns, error);
        }
        var descriptor = settings.Describe(new SettingsDescribeOptions(RedactSecrets: true))
            .FirstOrDefault(candidate => candidate.Ns.Value == ns)
            ?? throw new RpcDomainError(RpcErrorCodes.Internal,
                $"settings namespace \"{ns}\" was disposed after the {verb}");
        return JsonSerializer.SerializeToElement(NamespaceView(descriptor));
    }

    /// <summary>Project one redacted descriptor onto its wire view, omitting absent layers.</summary>
    private static Dictionary<string, object?> NamespaceView(SettingsDescriptor descriptor)
    {
        var view = new Dictionary<string, object?>
        {
            ["ns"] = descriptor.Ns.Value,
            ["schema"] = descriptor.Schema.ToJson(),
            ["value"] = JsonSerializer.SerializeToElement(descriptor.Value),
            ["applies"] = descriptor.Applies == SettingsApplies.Live ? "live" : "restart",
            ["secrets"] = (descriptor.Secrets ?? Array.Empty<SettingsSecret>())
                .Select(secret => (object)new Dictionary<string, object?>
                {
                    ["path"] = secret.Path,
                    ["set"] = secret.Set,
                })
                .ToList(),
            ["revision"] = descriptor.Revision,
        };
        if (descriptor.Base is not null) view["base"] = JsonSerializer.SerializeToElement(descriptor.Base);
        if (descriptor.User is not null) view["user"] = JsonSerializer.SerializeToElement(descriptor.User);
        return view;
    }

    /// <summary>Classify one seam refusal: a stale writer is its own outcome, anything else is rejected.</summary>
    private static RpcDomainError Rejected(string ns, Exception error)
    {
        if (error is SettingsConflictError conflict)
        {
            return new RpcDomainError("settings/conflict", conflict.Message,
                JsonSerializer.SerializeToElement(new { ns, expected = conflict.Expected, actual = conflict.Actual }));
        }
        return new RpcDomainError("settings/rejected", error.Message,
            JsonSerializer.SerializeToElement(new { ns }));
    }

    /// <summary>Resolve the optional provider or report how to supply it.</summary>
    private static SettingsProvider Provider(Context ctx)
        => ctx.Get<SettingsProvider>("settings")
            ?? throw new RpcDomainError(RpcErrorCodes.Internal,
                "settings service is absent: this deployment does not mount a settings provider (e.g. FileSettingsProvider) in its composition");

    private static long? LongArg(JsonElement? args, string key)
        => args is JsonElement element
            && element.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : null;

    private enum WriteKind
    {
        Update,
        Replace,
    }
}
