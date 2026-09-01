namespace Harness.Cordis.Tests;

/// <summary>
/// Zero-dependency console test runner for the Phase 0 spike. The host sandbox
/// blocks <c>dotnet build</c>/<c>dotnet test</c> (MSBuild cannot spawn the C#
/// compiler with captured output), so tests run as a plain console app that
/// exits non-zero on any assertion failure.
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>Runs every registered test and returns the process exit code.</summary>
    public static int Main()
    {
        Console.WriteLine("Cordis Phase 0 spike - console assertions");
        Console.WriteLine();

        Run("Schema: valid object with typed fields normalizes", SchemaValidationTests.ValidObjectWithTypedFieldsNormalizes);
        Run("Schema: invalid field reports structured error", SchemaValidationTests.InvalidFieldReportsStructuredError);
        Run("Schema: missing required field throws with path", SchemaValidationTests.MissingRequiredFieldThrowsWithPath);
        Run("Schema: missing optional field is omitted from output", SchemaValidationTests.MissingOptionalFieldIsOmittedFromOutput);
        Run("Schema: default supplies missing value", SchemaValidationTests.DefaultSuppliesMissingValue);
        Run("Schema: nested schema paths are structured", SchemaValidationTests.NestedSchemaPathsAreStructured);
        Run("Schema: nested valid object passes", SchemaValidationTests.NestedValidObjectPasses);
        Run("Schema: array validates each element", SchemaValidationTests.ArrayValidatesEachElement);
        Run("Schema: array element error includes index", SchemaValidationTests.ArrayElementErrorIncludesIndex);
        Run("Schema: TryValidate returns structured errors without throwing", SchemaValidationTests.TryValidateReturnsStructuredErrorsWithoutThrowing);
        Run("Schema: union accepts any member and reports union type", SchemaValidationTests.UnionAcceptsAnyMemberAndReportsUnionType);
        Run("Schema: const accepts exact value", SchemaValidationTests.ConstAcceptsExactValue);
        Run("Schema: number range constraints enforce bounds", SchemaValidationTests.NumberRangeConstraintsEnforceBounds);
        Run("Schema: natural rejects negative and fractional values", SchemaValidationTests.NaturalRejectsNegativeAndFractionalValues);
        Run("Schema: pattern constrains strings", SchemaValidationTests.PatternConstrainsStrings);
        Run("Schema: autofix removes invalid property", SchemaValidationTests.AutofixRemovesInvalidProperty);
        Run("Schema: transform converts validated value", SchemaValidationTests.TransformConvertsValidatedValue);
        Run("Schema: intersect merges object outputs", SchemaValidationTests.IntersectMergesObjectOutputs);
        Run("Schema: dict validates values and paths", SchemaValidationTests.DictValidatesValuesAndPaths);
        Run("Schema: lazy supports recursive schemas", SchemaValidationTests.LazySupportsRecursiveSchemas);
        Run("Schema: ToString formats type strings", SchemaValidationTests.ToStringFormatsTypeStrings);
        Run("Schema: default value is cloned per validation", SchemaValidationTests.DefaultValueIsClonedPerValidation);
        Run("Schema: From infers primitive schemas", SchemaValidationTests.FromInfersPrimitiveSchemas);
        Run("Schema: object accepts POCO input", SchemaValidationTests.ObjectAcceptsPocoInput);

        Run("Brand: same brand round-trips", BrandedIdTests.SameBrandRoundTrips);
        Run("Brand: same brand distinct values are unequal", BrandedIdTests.SameBrandDistinctValuesAreUnequal);
        Run("Brand: string literal implicitly brands", BrandedIdTests.StringLiteralImplicitlyBrands);
        Run("Brand: different brands are distinct types", BrandedIdTests.DifferentBrandsAreDistinctTypes);
        Run("Brand: different brands have no runtime conversion", BrandedIdTests.DifferentBrandsHaveNoRuntimeConversion);

        Run("Cosmokit: ParseTime parses compact durations", CosmokitSmokeTests.ParseTimeParsesCompactDurations);
        Run("Cosmokit: Format renders compact durations", CosmokitSmokeTests.FormatRendersCompactDurations);
        Run("Cosmokit: ClampTimeout applies default and cap", CosmokitSmokeTests.ClampTimeoutAppliesDefaultAndCap);
        Run("Cosmokit: Deadline fires identifiable timeout reason", CosmokitSmokeTests.DeadlineFiresIdentifiableTimeoutReason);
        Run("Cosmokit: ItemRetainer keeps head and counts omissions", CosmokitSmokeTests.ItemRetainerKeepsHeadAndCountsOmissions);
        Run("Cosmokit: TextRetainer head/tail preserves UTF-8 boundaries", CosmokitSmokeTests.TextRetainerHeadTailPreservesUtf8Boundaries);
        Run("Cosmokit: ResolveHshHome honors environment override", CosmokitSmokeTests.ResolveHshHomeHonorsEnvironmentOverride);
        Run("Cosmokit: brand helpers, arrays, and strings work", CosmokitSmokeTests.BrandHelpersArraysAndStringsWork);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        if (_failed > 0)
        {
            foreach (var failure in Failures)
            {
                Console.WriteLine("  FAILED: " + failure);
            }
            return 1;
        }
        return 0;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"  PASS {name}");
        }
        catch (AssertionException ex)
        {
            _failed++;
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: unexpected {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: unexpected {ex.GetType().Name}: {ex.Message}");
        }
    }
}
