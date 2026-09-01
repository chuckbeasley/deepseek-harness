namespace Harness.Cordis.Schemastery;

/// <summary>
/// A non-throwing validation outcome: either a successful normalized value or
/// the structured errors collected from the first failure. Schemastery throws
/// on the first error (mirroring the JS port), so a failure carries one error.
/// </summary>
public sealed class SchemaValidationResult
{
    /// <summary>Whether validation succeeded.</summary>
    public bool IsValid { get; }

    /// <summary>The normalized value when <see cref="IsValid"/> is <c>true</c>.</summary>
    public object? Value { get; }

    /// <summary>The structured errors (empty on success).</summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    private SchemaValidationResult(bool isValid, object? value, IReadOnlyList<ValidationError> errors)
    {
        IsValid = isValid;
        Value = value;
        Errors = errors;
    }

    /// <summary>Creates a successful result.</summary>
    public static SchemaValidationResult Success(object? value) => new(true, value, Array.Empty<ValidationError>());

    /// <summary>Creates a failed result carrying <paramref name="errors"/>.</summary>
    public static SchemaValidationResult Failure(IReadOnlyList<ValidationError> errors) => new(false, null, errors);
}
