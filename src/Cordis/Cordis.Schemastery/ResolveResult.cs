namespace Cordis.Schemastery;

/// <summary>
/// The result of one schema resolution: the normalized output plus an optional
/// adapted input. The adapted value mirrors the second element of the JS
/// <c>[output, adaptedInput?]</c> tuple returned by <c>Schema.resolve</c>.
/// </summary>
public readonly struct ResolveResult
{
    /// <summary>The normalized output value.</summary>
    public object? Value { get; }

    /// <summary>Whether an adapted input was produced.</summary>
    public bool HasAdapted { get; }

    /// <summary>The adapted input, when <see cref="HasAdapted"/> is <c>true</c>.</summary>
    public object? Adapted { get; }

    private ResolveResult(object? value, bool hasAdapted, object? adapted)
    {
        Value = value;
        HasAdapted = hasAdapted;
        Adapted = adapted;
    }

    /// <summary>Creates a result with no adapted input.</summary>
    public static ResolveResult Of(object? value) => new(value, false, null);

    /// <summary>Creates a result with an adapted input.</summary>
    public static ResolveResult OfAdapted(object? value, object? adapted) => new(value, true, adapted);
}
