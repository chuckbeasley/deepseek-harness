using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Preset;
using Harness.Settings;

namespace Harness.Web.Host;

/// <summary>
/// The settings remote methods (port of the TS SettingsController): the redacted describe, the
/// update/replace/mutate writes, and the document/preset openers. Every read is redacted, and
/// every provider refusal is classified as <c>settings/conflict</c> (stale revision) or
/// <c>settings/rejected</c> (anything else). The namespaces stay registered without a provider,
/// answering an actionable <c>gateway/internal</c> like the TS controller. The preset opener
/// refuses a <c>system</c>-trust preset with <c>agent-preset/read-only</c> — a preset shipping
/// with the deployment is not authorable (the TS read-only refusal); the default spine root is
/// user-authored, and a deployment may mount a system root through the preset row's trust config.
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

    /// <summary>Apply ordered path-addressed edits and answer the namespace's new redacted view.</summary>
    public static RpcMethod Mutate(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("settings/mutate", (args, _) => WriteAsync(ctx, args, WriteKind.Mutate));
    }

    /// <summary>Whether this deployment can open an authored Agent preset directory natively.</summary>
    public static RpcMethod CanOpenAgentPresetDirectory(Context ctx, SettingsOpeners? openers = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var native = openers ?? SettingsOpeners.Default;
        return new RpcMethod("settings/canOpenAgentPresetDirectory", (_, _) =>
            Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(native.CanOpen)));
    }

    /// <summary>Materialize the provider-owned settings document and open it in a native text editor.</summary>
    public static RpcMethod OpenSettingsDocument(Context ctx, SettingsOpeners? openers = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var native = openers ?? SettingsOpeners.Default;
        return new RpcMethod("settings/openSettingsDocument", async (_, ct) =>
        {
            var settings = Provider(ctx);
            if (ct.IsCancellationRequested)
            {
                throw new RpcDomainError(RpcErrorCodes.Cancelled, "settings document open was aborted");
            }
            string? path;
            try
            {
                path = settings.PrepareDocument();
            }
            catch (Exception error)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new RpcDomainError(RpcErrorCodes.Cancelled, "settings document preparation was aborted");
                }
                throw new RpcDomainError(RpcErrorCodes.Internal, $"settings document preparation failed: {error.Message}");
            }
            if (path is null)
            {
                throw new RpcDomainError(RpcErrorCodes.Internal, "settings provider has no local document to open");
            }
            if (ct.IsCancellationRequested)
            {
                throw new RpcDomainError(RpcErrorCodes.Cancelled, "settings document open was aborted");
            }
            try
            {
                await native.OpenTextFile(path);
            }
            catch (Exception error)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new RpcDomainError(RpcErrorCodes.Cancelled, "settings document open was aborted");
                }
                throw new RpcDomainError(RpcErrorCodes.Internal, $"path open failed: {error.Message}");
            }
            return JsonSerializer.SerializeToElement(new { opened = true });
        });
    }

    /// <summary>Open one user-authored Agent preset directory or return its path when no native opener exists.</summary>
    public static RpcMethod OpenAgentPresetDirectory(Context ctx, SettingsOpeners? openers = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var native = openers ?? SettingsOpeners.Default;
        return new RpcMethod("settings/openAgentPresetDirectory", async (args, ct) =>
        {
            var agentPreset = args is JsonElement element && element.TryGetProperty("agentPreset", out var presetValue)
                    && presetValue.ValueKind == JsonValueKind.String
                ? presetValue.GetString()
                : null;
            if (string.IsNullOrEmpty(agentPreset))
            {
                throw new RpcBadRequestException("agent preset id must not be empty");
            }
            var presets = ctx.Get<IPresetService>("preset");
            if (presets is null)
            {
                throw new RpcDomainError("agent-preset/not-found",
                    "this deployment composes no agent presets",
                    JsonSerializer.SerializeToElement(new { agentPreset, available = Array.Empty<string>() }));
            }
            ComposedPreset preset;
            try
            {
                preset = presets.Resolve(agentPreset);
            }
            catch (Exception error)
            {
                var available = presets.Discover().Select(candidate => candidate.Id).ToArray();
                throw new RpcDomainError("agent-preset/not-found", error.Message,
                    JsonSerializer.SerializeToElement(new { agentPreset, available }));
            }
            if (preset.Trust != PresetTrust.User)
            {
                // A preset shipping with the deployment is not authorable: it belongs to the
                // deployment, and opening its directory for editing would invite a browser
                // rewrite of the shipped set (the TS read-only refusal).
                throw new RpcDomainError("agent-preset/read-only",
                    $"agent-presets: preset \"{preset.Id}\" cannot be written: it ships with the deployment",
                    JsonSerializer.SerializeToElement(new { agentPreset = preset.Id, reason = "it ships with the deployment" }));
            }
            var directory = Path.GetDirectoryName(preset.CompositionPath) ?? preset.CompositionPath;
            if (!native.CanOpen)
            {
                return JsonSerializer.SerializeToElement(new { opened = false, path = directory });
            }
            try
            {
                await native.OpenPath(directory);
            }
            catch (Exception error)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new RpcDomainError(RpcErrorCodes.Cancelled, "path open was aborted");
                }
                throw new RpcDomainError(RpcErrorCodes.Internal, $"path open failed: {error.Message}");
            }
            return JsonSerializer.SerializeToElement(new { opened = true });
        });
    }

    private static async Task<JsonElement?> WriteAsync(Context ctx, JsonElement? args, WriteKind kind)
    {
        var settings = Provider(ctx);
        var verb = kind switch { WriteKind.Update => "update", WriteKind.Replace => "replace", _ => "mutate" };
        var ns = args is JsonElement element && element.TryGetProperty("ns", out var nsValue)
                && nsValue.ValueKind == JsonValueKind.String
            ? nsValue.GetString()
            : null;
        if (string.IsNullOrEmpty(ns))
        {
            throw new RpcBadRequestException($"settings/{verb} requires a non-empty ns");
        }
        var expectedRevision = LongArg(args, "expectedRevision");
        object? input;
        if (kind == WriteKind.Mutate)
        {
            input = OpsArg(args);
        }
        else
        {
            var section = args is JsonElement argsElement && argsElement.TryGetProperty(kind == WriteKind.Update ? "patch" : "section", out var sectionValue)
                    && sectionValue.ValueKind == JsonValueKind.Object
                ? sectionValue
                : default;
            if (section.ValueKind != JsonValueKind.Object)
            {
                throw new RpcBadRequestException($"settings/{verb} requires a {(kind == WriteKind.Update ? "patch" : "section")} object");
            }
            input = SettingsWireValues.FromJsonElement(section);
        }
        try
        {
            switch (kind)
            {
                case WriteKind.Update:
                    await settings.UpdateAsync(ns, input!, expectedRevision);
                    break;
                case WriteKind.Replace:
                    await settings.ReplaceAsync(ns, input!, expectedRevision);
                    break;
                default:
                    await settings.MutateAsync(ns, (IReadOnlyList<Harness.Settings.SettingsPathOp>)input!, expectedRevision);
                    break;
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

    /// <summary>
    /// Parse and validate the mutate ops array: every op is <c>{op:'set'|'unset', path: string[]}</c>
    /// with a value for <c>set</c>. The seam re-validates the shape (the TS controller and seam
    /// both guard it); wire-level violations refuse before the write.
    /// </summary>
    private static Harness.Settings.SettingsPathOp[] OpsArg(JsonElement? args)
    {
        if (args is not JsonElement element
            || !element.TryGetProperty("ops", out var opsValue)
            || opsValue.ValueKind != JsonValueKind.Array)
        {
            throw new RpcBadRequestException("settings/mutate requires an ops array");
        }
        var ops = new List<Harness.Settings.SettingsPathOp>();
        foreach (var item in opsValue.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("op", out var opValue)
                || opValue.ValueKind != JsonValueKind.String
                || (opValue.GetString() != "set" && opValue.GetString() != "unset")
                || !item.TryGetProperty("path", out var pathValue)
                || pathValue.ValueKind != JsonValueKind.Array)
            {
                throw new RpcBadRequestException("settings/mutate ops must be {op:'set'|'unset', path: string[]}");
            }
            var path = new List<string>();
            foreach (var part in pathValue.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.String)
                {
                    throw new RpcBadRequestException("settings/mutate op paths must be arrays of strings");
                }
                path.Add(part.GetString()!);
            }
            object? value = null;
            if (opValue.GetString() == "set")
            {
                if (!item.TryGetProperty("value", out var valueElement))
                {
                    throw new RpcBadRequestException("settings/mutate set ops require a value");
                }
                value = SettingsWireValues.FromJsonElement(valueElement);
            }
            ops.Add(new Harness.Settings.SettingsPathOp(opValue.GetString()!, path, value));
        }
        return ops.ToArray();
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
        Mutate,
    }
}
