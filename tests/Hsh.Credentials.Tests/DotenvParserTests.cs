using Harness.Credentials;

namespace Harness.Credentials.Tests;

/// <summary>Parser behavior of <see cref="DotenvParser"/>: comments, blank lines, quoted values, precedence, and loud failures.</summary>
public static class DotenvParserTests
{
    private const string FileName = "C:\\virtual\\credentials.env";

    private static IReadOnlyDictionary<string, string> Parse(string text, bool rejectEmptyValues = false)
        => DotenvParser.Parse(text, FileName, rejectEmptyValues);

    public static void CommentsAndEmptyLines_AreSkipped()
    {
        var entries = Parse("# a comment\n\n   \nKEY=value\n# trailing comment\n");
        Assert.Equal(1, entries.Count);
        Assert.Equal("value", entries["KEY"]);
    }

    public static void DoubleQuotedValue_PreservesSpaces()
    {
        Assert.Equal("hello world", Parse("KEY=\"hello world\"\n")["KEY"]);
    }

    public static void DoubleQuotedValue_HandlesEscapes()
    {
        Assert.Equal("a\"b\\c", Parse("KEY=\"a\\\"b\\\\c\"\n")["KEY"]);
        Assert.Equal("line1\nline2", Parse("KEY=\"line1\\nline2\"\n")["KEY"]);
    }

    public static void SingleQuotedValue_IsLiteral()
    {
        Assert.Equal("a b", Parse("KEY='a b'\n")["KEY"]);
        Assert.Equal(@"a\nb", Parse("KEY='a\\nb'\n")["KEY"]);
    }

    public static void ExportPrefix_IsStripped()
    {
        Assert.Equal("value", Parse("export KEY=value\n")["KEY"]);
    }

    public static void UnquotedValue_TrimsTrailingWhitespace()
    {
        Assert.Equal("value", Parse("KEY=value   \n")["KEY"]);
    }

    public static void DuplicateKey_LastOccurrenceWins()
    {
        var entries = Parse("A=1\nA=2\n");
        Assert.Equal(1, entries.Count);
        Assert.Equal("2", entries["A"]);
    }

    public static void InlineComment_IsPartOfTheValue()
    {
        // dotenv values may contain '#'; only full-line comments are stripped.
        Assert.Equal("value # not a comment", Parse("KEY=value # not a comment\n")["KEY"]);
    }

    public static void InvalidKey_FailsLoud_NamingKeyNotValue()
    {
        var error = Assert.Throws<CredentialsFileError>(() => Parse("1BAD=sekret\n"));
        Assert.True(error.Message.Contains("line 1", StringComparison.Ordinal), error.Message);
        Assert.True(error.Message.Contains("1BAD", StringComparison.Ordinal), error.Message);
        Assert.False(error.Message.Contains("sekret", StringComparison.Ordinal), error.Message);
    }

    public static void UnterminatedDoubleQuote_FailsLoud()
    {
        var error = Assert.Throws<CredentialsFileError>(() => Parse("KEY=\"never closed\n"));
        Assert.True(error.Message.Contains("line 1", StringComparison.Ordinal), error.Message);
        Assert.True(error.Message.Contains("KEY", StringComparison.Ordinal), error.Message);
    }

    public static void UnterminatedSingleQuote_FailsLoud()
    {
        var error = Assert.Throws<CredentialsFileError>(() => Parse("KEY='never closed\n"));
        Assert.True(error.Message.Contains("line 1", StringComparison.Ordinal), error.Message);
    }

    public static void EmptyValue_Rejected_WhenRequested()
    {
        var error = Assert.Throws<CredentialsFileError>(() => Parse("KEY=\n", rejectEmptyValues: true));
        Assert.True(error.Message.Contains("KEY", StringComparison.Ordinal), error.Message);
        Assert.True(error.Message.Contains("empty", StringComparison.Ordinal), error.Message);
    }

    public static void EmptyValue_Parsed_WhenNotRejected()
    {
        var entries = Parse("KEY=\n", rejectEmptyValues: false);
        Assert.Equal(string.Empty, entries["KEY"]);
    }

    public static void LineWithoutEquals_IsSkipped()
    {
        var entries = Parse("JUST_A_NAME\nKEY=value\n");
        Assert.Equal(1, entries.Count);
        Assert.Equal("value", entries["KEY"]);
    }
}
