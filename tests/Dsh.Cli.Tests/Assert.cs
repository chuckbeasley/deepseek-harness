namespace Harness.Cli.Tests;

/// <summary>Test failure carrying one assertion message.</summary>
public sealed class AssertionException : Exception
{
    public AssertionException(string message)
        : base(message)
    {
    }
}

/// <summary>Zero-dependency console assertion helpers.</summary>
public static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{message} (expected \"{expected}\", got \"{actual}\")");
        }
    }

    public static void Sequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
    {
        if (expected.Count != actual.Count || !expected.SequenceEqual(actual))
        {
            throw new AssertionException($"{message} (expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}])");
        }
    }
}

/// <summary>Captures console output for one test and restores it on dispose.</summary>
public sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _out;
    private readonly TextWriter _error;

    public ConsoleCapture()
    {
        _out = Console.Out;
        _error = Console.Error;
        Console.SetOut(Out);
        Console.SetError(Error);
    }

    public StringWriter Out { get; } = new();

    public StringWriter Error { get; } = new();

    public void Dispose()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
    }
}

/// <summary>
/// One temp harness home for one test: DSH_HOME points at it and DEEPSEEK_API_KEY is cleared so
/// headless runs stay on the mock route. Both are restored on dispose.
/// </summary>
public sealed class TempDshHome : IDisposable
{
    private readonly string? _oldHome;
    private readonly string? _oldKey;

    public TempDshHome()
    {
        Dir = Path.Combine(Path.GetTempPath(), "dsh-cli-tests-" + Guid.NewGuid().ToString("N"));
        _oldHome = Environment.GetEnvironmentVariable("DSH_HOME");
        _oldKey = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        Directory.CreateDirectory(Dir);
        Environment.SetEnvironmentVariable("DSH_HOME", Dir);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);
    }

    public string Dir { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_HOME", _oldHome);
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", _oldKey);
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }
}
