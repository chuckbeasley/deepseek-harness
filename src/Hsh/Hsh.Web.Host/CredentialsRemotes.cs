using System.Text.Json;
using Harness.Cordis.Core;
using Harness.Credentials;

namespace Harness.Web.Host;

/// <summary>
/// The credentials remote methods (port of the TS CredentialsController): the batched describe and
/// the set/unset writes over the reference half of ctx.credentials. The batch fan-out bound, the
/// field-by-field view projection, the reference-grammar guard, and the refusal mapping live here
/// — the seam itself carries none of the wire obligations. Secret values cross in one direction
/// only: no method returns one. The namespace stays registered without a provider, answering an
/// actionable <c>gateway/internal</c> like the TS controller.
/// </summary>
public static class CredentialsRemotes
{
    /// <summary>Fan-out bound on one remote describe batch (the TS MAX_DESCRIBE_REFS).</summary>
    public const int MaxDescribeRefs = 64;

    /// <summary>Describe several references for one configuration surface.</summary>
    public static RpcMethod Describe(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("credentials/describe", async (args, cancellationToken) =>
        {
            var refs = RefsArg(args);
            var credentials = Provider(ctx);
            var entries = new Dictionary<string, object?>();
            foreach (var reference in refs)
            {
                var info = await credentials.DescribeAsync(reference, cancellationToken);
                var view = new Dictionary<string, object?>
                {
                    ["configured"] = info.Configured,
                    ["writable"] = info.Writable,
                };
                if (info.Source is not null) view["source"] = info.Source;
                entries[reference] = view;
            }
            return JsonSerializer.SerializeToElement(entries);
        });
    }

    /// <summary>Store one value from a configuration surface; the value rides this direction only.</summary>
    public static RpcMethod Set(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("credentials/set", async (args, cancellationToken) =>
        {
            var (reference, value) = RefValueArg(args);
            var credentials = Provider(ctx);
            try
            {
                await credentials.SetAsync(reference, value, cancellationToken);
            }
            catch (Exception error)
            {
                throw new RpcDomainError("credential/rejected", error.Message,
                    JsonSerializer.SerializeToElement(new { @ref = reference }));
            }
            return null;
        });
    }

    /// <summary>Remove one reference from a configuration surface.</summary>
    public static RpcMethod Unset(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("credentials/unset", async (args, cancellationToken) =>
        {
            var reference = RefArg(args);
            var credentials = Provider(ctx);
            try
            {
                await credentials.UnsetAsync(reference, cancellationToken);
            }
            catch (Exception error)
            {
                throw new RpcDomainError("credential/rejected", error.Message,
                    JsonSerializer.SerializeToElement(new { @ref = reference }));
            }
            return null;
        });
    }

    /// <summary>Resolve the optional provider or report how to supply it.</summary>
    private static ICredentialsService Provider(Context ctx)
        => ctx.Get<ICredentialsService>("credentials")
            ?? throw new RpcDomainError(RpcErrorCodes.Internal,
                "credentials service is absent: this deployment does not mount a credential provider (e.g. LocalCredentialsProvider) in its composition");

    /// <summary>Parse and validate one grammar-conforming reference, rejecting malformed wire args.</summary>
    private static string RefArg(JsonElement? args)
    {
        var reference = args is JsonElement element && element.TryGetProperty("ref", out var value)
                && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        if (reference is null || !CredentialNames.IsCredentialRefName(reference))
        {
            throw new RpcBadRequestException("credentials methods require a ref matching [A-Za-z_][A-Za-z0-9_]*");
        }
        return reference;
    }

    /// <summary>Parse and validate the ref plus its non-empty value for a set write.</summary>
    private static (string Reference, string Value) RefValueArg(JsonElement? args)
    {
        var reference = RefArg(args);
        var value = args is JsonElement element && element.TryGetProperty("value", out var valueElement)
                && valueElement.ValueKind == JsonValueKind.String
            ? valueElement.GetString()
            : null;
        if (string.IsNullOrEmpty(value))
        {
            throw new RpcBadRequestException("credentials/set requires a non-empty value; use unset to remove a reference");
        }
        return (reference, value);
    }

    /// <summary>Parse and validate the describe batch: at most <see cref="MaxDescribeRefs"/> grammar-conforming names.</summary>
    private static string[] RefsArg(JsonElement? args)
    {
        if (args is not JsonElement element
            || !element.TryGetProperty("refs", out var refsValue)
            || refsValue.ValueKind != JsonValueKind.Array)
        {
            throw new RpcBadRequestException("credentials/describe requires a refs array");
        }
        var refs = new List<string>();
        foreach (var item in refsValue.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new RpcBadRequestException("credentials/describe refs must all be strings");
            }
            var reference = item.GetString()!;
            if (!CredentialNames.IsCredentialRefName(reference))
            {
                throw new RpcBadRequestException($"credential ref \"{reference}\" must match [A-Za-z_][A-Za-z0-9_]*");
            }
            refs.Add(reference);
        }
        if (refs.Count > MaxDescribeRefs)
        {
            throw new RpcBadRequestException($"credentials/describe accepts at most {MaxDescribeRefs} refs");
        }
        return refs.ToArray();
    }
}
