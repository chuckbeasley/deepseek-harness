using Harness.Cordis.Core;
namespace Harness.Shell.Tests;

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
}

/// <summary>One booted shell composition: context, subprocess provider, shell executor, and the tool registry.</summary>
public sealed class ShellHarness : IDisposable
{
    public ShellHarness(string? shellPath = null)
    {
        Ctx = new Context();
        Subprocess = new global::Harness.Subprocess.LocalSubprocessProvider(Ctx);
        Shell = new global::Harness.Shell.LocalShellProvider(Ctx, new global::Harness.Shell.ShellConfig
        {
            ShellPath = shellPath ?? "cmd.exe",
            TimeoutMs = 120000,
        });
        Tools = new global::Harness.Tools.ToolRuntime(Ctx);
        var tool = global::Harness.Shell.ShellTools.Definition(Ctx);
        ToolRegistration = Tools.Register(tool);
    }

    public global::Harness.Cordis.Core.Context Ctx { get; }

    public global::Harness.Subprocess.LocalSubprocessProvider Subprocess { get; }

    public global::Harness.Shell.LocalShellProvider Shell { get; }

    public global::Harness.Tools.ToolRuntime Tools { get; }

    public IDisposable ToolRegistration { get; }

    public void Dispose() => Ctx.Dispose();
}
