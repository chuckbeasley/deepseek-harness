using System.Text.Json;

namespace Harness.Acp;

/// <summary>Caller-correctable session configuration failure.</summary>
public sealed class AcpModelConfigError : Exception
{
    /// <summary>Create the configuration failure.</summary>
    /// <param name="message">the protocol-safe detail.</param>
    public AcpModelConfigError(string message) : base(message)
    {
    }
}

/// <summary>
/// Project and mutate one session's provider/model selection through ACP config options
/// (reduced port of the TS <c>AcpModelControl</c>): the ported AgentOptions are fixed at agent
/// creation and the LLM seam has no model catalog, so the option state advertises the session's
/// exact route as the only choice and accepts no mutation. Model catalogs, reasoning efforts,
/// and per-turn selection pins await the port's catalog and loop selection-ref seams.
/// </summary>
public sealed class AcpModelControl
{
    private const string ModelConfigId = "model";

    private readonly string _provider;
    private readonly string _model;

    /// <summary>Create the control over the session's fixed route.</summary>
    /// <param name="provider">the provider route, or <c>null</c> when the deployment configured none.</param>
    /// <param name="model">the model id, or <c>null</c> when the deployment configured none.</param>
    public AcpModelControl(string? provider, string? model)
    {
        _provider = provider ?? "";
        _model = model ?? "";
    }

    /// <summary>Whether the session has a concrete selection to advertise.</summary>
    public bool HasSelection => _provider.Length > 0 && _model.Length > 0;

    /// <summary>Return the complete standard config-option state.</summary>
    /// <returns>the model option (empty when the session has no selection).</returns>
    public IReadOnlyList<SessionConfigOption> Options()
    {
        if (!HasSelection) return Array.Empty<SessionConfigOption>();
        var value = ModelValue(_provider, _model);
        return new[]
        {
            new SessionConfigOption(ModelConfigId, "Model", "model", "select", value,
                new[]
                {
                    new SessionConfigGroup(_provider, _provider, new[] { new SessionConfigChoice(value, _model) }),
                }),
        };
    }

    /// <summary>Set one advertised option and return the complete resulting option state.</summary>
    /// <param name="configId">the standard option id.</param>
    /// <param name="value">the opaque selected value returned by a previous option state.</param>
    /// <returns>the complete resulting option state; the only accepted value is the session's
    /// current route (mutations await the loop selection-ref seam).</returns>
    public IReadOnlyList<SessionConfigOption> Set(string configId, JsonElement value)
    {
        if (configId != ModelConfigId)
        {
            throw new AcpModelConfigError($"unknown session config option: {configId}");
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new AcpModelConfigError($"{configId} requires a select value");
        }
        var selected = value.GetString()!;
        if (!HasSelection || selected != ModelValue(_provider, _model))
        {
            throw new AcpModelConfigError($"unknown model option: {selected}");
        }
        return Options();
    }

    /// <summary>The opaque ACP selector value carrying the full route identity.</summary>
    public static string ModelValue(string provider, string model)
        => JsonSerializer.Serialize(new[] { provider, model });
}
