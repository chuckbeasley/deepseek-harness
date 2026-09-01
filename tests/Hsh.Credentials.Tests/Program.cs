namespace Harness.Credentials.Tests;

/// <summary>
/// Zero-dependency console test runner for the credentials capability seam. The host sandbox
/// blocks dotnet build/dotnet test (MSBuild cannot spawn the C# compiler with captured output), so
/// tests run as a plain console app that exits non-zero on any assertion failure. All file-backed
/// tests use disposable temp directories; no process environment is mutated.
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>Runs every registered test and returns the process exit code.</summary>
    public static int Main()
    {
        Console.WriteLine("Harness.Credentials - console assertions");
        Console.WriteLine();

        Run("Dotenv: comments and empty lines are skipped", DotenvParserTests.CommentsAndEmptyLines_AreSkipped);
        Run("Dotenv: double-quoted values preserve spaces", DotenvParserTests.DoubleQuotedValue_PreservesSpaces);
        Run("Dotenv: double-quoted values handle escapes", DotenvParserTests.DoubleQuotedValue_HandlesEscapes);
        Run("Dotenv: single-quoted values are literal", DotenvParserTests.SingleQuotedValue_IsLiteral);
        Run("Dotenv: export prefix is stripped", DotenvParserTests.ExportPrefix_IsStripped);
        Run("Dotenv: unquoted values trim trailing whitespace", DotenvParserTests.UnquotedValue_TrimsTrailingWhitespace);
        Run("Dotenv: duplicate keys keep the last occurrence", DotenvParserTests.DuplicateKey_LastOccurrenceWins);
        Run("Dotenv: inline # is part of the value", DotenvParserTests.InlineComment_IsPartOfTheValue);
        Run("Dotenv: invalid key fails loud naming the key, not the value", DotenvParserTests.InvalidKey_FailsLoud_NamingKeyNotValue);
        Run("Dotenv: unterminated double quote fails loud", DotenvParserTests.UnterminatedDoubleQuote_FailsLoud);
        Run("Dotenv: unterminated single quote fails loud", DotenvParserTests.UnterminatedSingleQuote_FailsLoud);
        Run("Dotenv: empty value rejected when requested", DotenvParserTests.EmptyValue_Rejected_WhenRequested);
        Run("Dotenv: empty value parses when not rejected", DotenvParserTests.EmptyValue_Parsed_WhenNotRejected);
        Run("Dotenv: line without equals is skipped", DotenvParserTests.LineWithoutEquals_IsSkipped);

        Run("Provider: environment wins over every file layer", LocalCredentialsProviderTests.Environment_WinsOverEveryFileLayer);
        Run("Provider: managed file wins over project .env", LocalCredentialsProviderTests.ManagedFile_WinsOverProjectEnv);
        Run("Provider: project .env is the fallback", LocalCredentialsProviderTests.ProjectEnv_IsTheFallback);
        Run("Provider: project .env ranks above user .env", LocalCredentialsProviderTests.ProjectEnv_RanksAboveUserEnv);
        Run("Provider: user .env used when no project layer", LocalCredentialsProviderTests.UserEnv_Used_WhenNoProjectLayer);
        Run("Provider: unconfigured resolves null", LocalCredentialsProviderTests.Unconfigured_ResolvesNull);
        Run("Provider: missing credential error names the key without any value", LocalCredentialsProviderTests.MissingCredential_ErrorNamesTheKey_WithoutAnyValue);
        Run("Provider: set round-trips through the managed file", LocalCredentialsProviderTests.Set_RoundTripsThroughTheManagedFile);
        Run("Provider: quoted value round-trips losslessly", LocalCredentialsProviderTests.Set_QuotedValue_RoundTripsLosslessly);
        Run("Provider: unset removes the entry", LocalCredentialsProviderTests.Unset_RemovesTheEntry);
        Run("Provider: unset of an absent reference is a no-op", LocalCredentialsProviderTests.Unset_AbsentReference_IsANoOp);
        Run("Provider: empty value set throws naming the key", LocalCredentialsProviderTests.Set_EmptyValue_ThrowsNamingTheKey);
        Run("Provider: shadowed set throws without writing", LocalCredentialsProviderTests.Set_ShadowedByEnvironment_ThrowsWithoutWriting);
        Run("Provider: set preserves unrelated entries", LocalCredentialsProviderTests.Set_PreservesUnrelatedEntries);
        Run("Provider: corrupt managed file fails loud without leaking values", LocalCredentialsProviderTests.CorruptManagedFile_FailsLoud_WithoutLeakingValues);
        Run("Provider: missing managed file is an empty store", LocalCredentialsProviderTests.MissingManagedFile_IsAnEmptyStore);
        Run("Provider: corrupt project .env fails loud too", LocalCredentialsProviderTests.CorruptProjectEnv_FailsLoudToo);
        Run("Provider: describe reports source and writability without values", LocalCredentialsProviderTests.Describe_ReportsSourceAndWritability_WithoutValues);
        Run("Provider: invalid reference names are rejected loudly", LocalCredentialsProviderTests.InvalidReferenceName_IsRejectedLoudly);
        Run("Provider: registered under the credentials key", LocalCredentialsProviderTests.Registered_UnderCredentialsKey);

        Run("Redaction: mask does not leak the secret", RedactionTests.Mask_DoesNotLeakTheSecret);
        Run("Redaction: short values are fully masked", RedactionTests.Mask_ShortValues_AreFullyMasked);
        Run("Redaction: redact removes secrets from text", RedactionTests.Redact_RemovesSecretsFromText);
        Run("Redaction: redact handles multiple secrets", RedactionTests.Redact_HandlesMultipleSecrets);
        Run("Redaction: redact skips empty secrets", RedactionTests.Redact_SkipsEmptySecrets);
        Run("Redaction: contains-secret detects secrets", RedactionTests.ContainsSecret_DetectsSecrets);

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
