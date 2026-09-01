using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.CordisRunner;

/// <summary>One declared read-only query of a Cordis inspect provider.</summary>
public sealed record CordisInspectMethod(
    string Name,
    string Description,
    JsonElement InputSchema,
    JsonElement OutputSchema);

/// <summary>One provider directory entry: identity, capability, and declared methods.</summary>
public sealed record CordisInspectProviderManifest(
    string Id,
    string Description,
    IReadOnlyList<CordisInspectMethod> Methods);

/// <summary>One provider directory row: the manifest plus the platform that executes it.</summary>
public sealed record CordisInspectProviderView(
    string Platform,
    string Id,
    string Description,
    IReadOnlyList<CordisInspectMethod> Methods);

/// <summary>
/// One local inspect provider (port of <c>HostCordisInspectProviderRegistration</c>): its
/// serializable manifest plus the query executor. The executor receives the validated method
/// name and input and returns the JSON data; the calling session scope is not bridged (the
/// ported tool registry has no agent-scoped layer, documented reduction).
/// </summary>
public sealed class CordisInspectProvider
{
    /// <summary>The provider and its explicit method directory.</summary>
    public required CordisInspectProviderManifest Manifest { get; init; }

    /// <summary>Execute one declared method over the validated input.</summary>
    public required Func<string, JsonElement?, JsonElement> Query { get; init; }
}

/// <summary>
/// Host registry for model-visible, read-only Cordis capability queries (port of the vendored
/// <c>CordisInspectRegistryService</c>). Client-platform queries are refused: the browser client
/// runner is not ported, so the client manifest is always empty and every client query settles
/// with the TS unregistered-provider vocabulary.
/// </summary>
public sealed class CordisInspect
{
    private readonly Dictionary<string, CordisInspectProvider> _providers = new(StringComparer.Ordinal);

    /// <summary>Register one host provider; returns the exact disposer that removes it.</summary>
    /// <exception cref="InvalidOperationException">when the provider id is already registered.</exception>
    public IDisposable Register(CordisInspectProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_providers.ContainsKey(provider.Manifest.Id))
        {
            throw new InvalidOperationException($"Host Cordis inspect provider \"{provider.Manifest.Id}\" is already registered");
        }
        _providers[provider.Manifest.Id] = provider;
        return new ActionDisposer(() =>
        {
            if (_providers.TryGetValue(provider.Manifest.Id, out var current) && ReferenceEquals(current, provider))
            {
                _providers.Remove(provider.Manifest.Id);
            }
        });
    }

    /// <summary>Return the complete known provider directory (host providers; the client half is empty).</summary>
    public IReadOnlyList<CordisInspectProviderView> List()
        => _providers.Values.Select(provider => new CordisInspectProviderView(
            "host", provider.Manifest.Id, provider.Manifest.Description, provider.Manifest.Methods)).ToArray();

    /// <summary>
    /// Execute one provider query on its owning platform. A non-host platform takes the client
    /// path: the port has no client manifest, so the provider is never registered there and the
    /// query settles with the TS unregistered vocabulary.
    /// </summary>
    /// <exception cref="InvalidOperationException">for an unknown provider, unknown method, or rejected input.</exception>
    public JsonElement Query(string platform, string providerId, string methodName, JsonElement? input)
    {
        if (platform != "host")
        {
            throw new InvalidOperationException($"Client Cordis inspect provider \"{providerId}\" is not registered");
        }
        var provider = _providers.GetValueOrDefault(providerId)
            ?? throw new InvalidOperationException($"Host Cordis inspect provider \"{providerId}\" is not registered");
        var method = provider.Manifest.Methods.FirstOrDefault(candidate => candidate.Name == methodName)
            ?? throw new InvalidOperationException($"Cordis inspect provider \"{providerId}\" has no method \"{methodName}\"");
        var validated = ValidateInput("Host", providerId, method, input);
        return provider.Query(methodName, validated);
    }

    /// <summary>
    /// Structural input validation against the method's input schema (the TS validates through
    /// the dsh-tools JSON-schema walker; the port checks the object shape and string fields the
    /// shipped providers declare, a documented reduction).
    /// </summary>
    private static JsonElement? ValidateInput(string platform, string provider, CordisInspectMethod method, JsonElement? input)
    {
        if (input is null || input.Value.ValueKind == JsonValueKind.Null) return null;
        if (input.Value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"{platform} Cordis inspect {provider}.{method.Name} rejected input: input must be a JSON object");
        }
        var schema = method.InputSchema;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject()) allowed.Add(property.Name);
        }
        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
        {
            foreach (var property in input.Value.EnumerateObject())
            {
                if (allowed.Contains(property.Name)) continue;
                throw new InvalidOperationException($"{platform} Cordis inspect {provider}.{method.Name} rejected input: input.{property.Name}: unknown property");
            }
        }
        foreach (var name in allowed)
        {
            if (!input.Value.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"{platform} Cordis inspect {provider}.{method.Name} rejected input: input.{name}: must be a string");
            }
        }
        return input;
    }
}

/// <summary>Disposable that runs one callback (the registry's registration disposer).</summary>
internal sealed class ActionDisposer(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;

    public void Dispose()
    {
        Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}