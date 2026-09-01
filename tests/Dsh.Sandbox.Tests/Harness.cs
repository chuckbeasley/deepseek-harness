using Harness.Cordis.Core;
using Harness.Shell;

namespace Harness.Sandbox.Tests;

/// <summary>
/// One booted sandbox spine: context, the unsandboxed provider (ctx.sandbox), and a local shell
/// executor (ctx.shell) so the bash tool's result JSON/render round-trip can be exercised without
/// spawning any process.
/// </summary>
public sealed class Harness : IDisposable
{
    public required Context Ctx { get; init; }

    public required UnsandboxedSandboxProvider Sandbox { get; init; }

    public required LocalShellProvider Shell { get; init; }

    /// <summary>Boot the spine.</summary>
    public static Harness Create()
    {
        var ctx = new Context();
        var sandbox = new UnsandboxedSandboxProvider(ctx, new SandboxConfig());
        var shell = new LocalShellProvider(ctx);
        return new Harness { Ctx = ctx, Sandbox = sandbox, Shell = shell };
    }

    /// <summary>Dispose the context (unwinding every effect).</summary>
    public void Dispose() => Ctx.Dispose();
}