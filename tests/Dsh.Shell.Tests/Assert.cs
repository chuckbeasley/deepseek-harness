using Cordis.Core;
namespace Dsh.Shell.Tests;

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
        Subprocess = new Dsh.Subprocess.LocalSubprocessProvider(Ctx);
        Shell = new Dsh.Shell.LocalShellProvider(Ctx, new Dsh.Shell.ShellConfig
        {
            ShellPath = shellPath ?? "cmd.exe",
            TimeoutMs = 120000,
        });
        Tools = new Dsh.Tools.ToolRuntime(Ctx);
        var tool = Dsh.Shell.ShellTools.Definition(Ctx);
        ToolRegistration = Tools.Register(tool);
    }

    public Cordis.Core.Context Ctx { get; }

    public Dsh.Subprocess.LocalSubprocessProvider Subprocess { get; }

    public Dsh.Shell.LocalShellProvider Shell { get; }

    public Dsh.Tools.ToolRuntime Tools { get; }

    public IDisposable ToolRegistration { get; }

    public void Dispose() => Ctx.Dispose();
}
