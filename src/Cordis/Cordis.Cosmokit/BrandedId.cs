namespace Harness.Cordis.Cosmokit;

/// <summary>
/// A nominal (branded) identifier wrapping a string, the C# equivalent of
/// <c>Branded&lt;B&gt;</c> from <c>@deepseek-ai/dsh-brand</c>. Two ids branded
/// with different marker types are distinct .NET types, so the compiler rejects
/// assigning one brand where another is expected even though every brand wraps
/// an ordinary string at runtime. Comparison, hashing, and formatting all
/// behave like the underlying string.
/// </summary>
/// <typeparam name="TBrand">A marker type naming the domain that owns the id,
/// typically an empty class such as <c>sealed class SessionId { }</c>.</typeparam>
public readonly struct BrandedId<TBrand> : IEquatable<BrandedId<TBrand>>
{
    private readonly string _value;

    /// <summary>Wraps <paramref name="value"/> without changing it.</summary>
    /// <param name="value">The string admitted by the domain that owns <typeparamref name="TBrand"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <c>null</c>.</exception>
    public BrandedId(string value)
    {
        _value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The underlying string value.</summary>
    public string Value => _value;

    /// <summary>Wraps a string with the brand carried by the return type, mirroring <c>brandString</c>.</summary>
    /// <param name="value">The string to brand.</param>
    /// <returns>A <see cref="BrandedId{TBrand}"/> wrapping <paramref name="value"/>.</returns>
    public static BrandedId<TBrand> From(string value) => new(value);

    /// <summary>Implicitly unwraps the id to its underlying string.</summary>
    public static implicit operator string(BrandedId<TBrand> id) => id._value;

    /// <summary>Implicitly wraps a plain string with the target brand.</summary>
    public static implicit operator BrandedId<TBrand>(string value) => new(value);

    /// <summary>Ordinal value equality.</summary>
    public static bool operator ==(BrandedId<TBrand> left, BrandedId<TBrand> right) => left.Equals(right);

    /// <summary>Ordinal value inequality.</summary>
    public static bool operator !=(BrandedId<TBrand> left, BrandedId<TBrand> right) => !left.Equals(right);

    /// <inheritdoc/>
    public bool Equals(BrandedId<TBrand> other) => StringComparer.Ordinal.Equals(_value, other._value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is BrandedId<TBrand> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);

    /// <inheritdoc/>
    public override string ToString() => _value;
}

/// <summary>Stateless factory for <see cref="BrandedId{TBrand}"/> values, mirroring <c>brandString</c>.</summary>
public static class Brand
{
    /// <summary>Wraps <paramref name="value"/> with the brand inferred from the call site.</summary>
    /// <param name="value">The string to brand.</param>
    /// <typeparam name="TBrand">The brand marker type.</typeparam>
    /// <returns>A <see cref="BrandedId{TBrand}"/> wrapping <paramref name="value"/>.</returns>
    public static BrandedId<TBrand> Of<TBrand>(string value) => new(value);
}
