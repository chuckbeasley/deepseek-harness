namespace Harness.Sdk.Client;

/// <summary>Internal generic process launch used by the transport and fake-runtime tests (the TS <c>RuntimeProcessOptions</c>).</summary>
public sealed record RuntimeProcessOptions(
    /// <summary>The child command (a .dll entry spawns via <c>dotnet</c>).</summary>
    string Command,
    /// <summary>Arguments after the command.</summary>
    IReadOnlyList<string> Args,
    /// <summary>Optional absolute child working directory; omission uses the parent working directory.</summary>
    string? WorkingDirectory,
    /// <summary>Materialize the complete child environment when the client starts its subprocess.</summary>
    Func<IReadOnlyDictionary<string, string>> Environment,
    /// <summary>Human-readable child description for diagnostics.</summary>
    string Description,
    /// <summary>Bound (ms) on the initial profile handshake.</summary>
    int InitializeTimeoutMs,
    /// <summary>Optional per-request timeout (ms); <c>null</c> waits indefinitely.</summary>
    int? RequestTimeoutMs,
    /// <summary>Bound (ms) on the protocol <c>shutdown</c> exchange inside close.</summary>
    int ShutdownTimeoutMs = 1000,
    /// <summary>Grace (ms) for the runtime's stdin-EOF quiesce during close.</summary>
    int DisposeEofGraceMs = 6000,
    /// <summary>Termination confirmation window (ms) after the forced kill during close.</summary>
    int DisposeGraceMs = 3000);

/// <summary>Resolve the public SDK launch configuration to one hsh subprocess (port of the TS <c>resolveHshLaunch</c>).</summary>
public static class SdkLaunch
{
    /// <summary>The default profile serving the SDK protocol.</summary>
    public const string DefaultProfile = "sdk";

    /// <summary>Default bound (ms) for a profile to answer the SDK initialize handshake.</summary>
    public const int DefaultInitializeTimeoutMs = 10_000;

    /// <summary>
    /// Resolve caller-relative filesystem inputs and construct canonical hsh argv. The default
    /// runtime entry is the current executable — the hsh CLI when the client is hosted in a hsh
    /// surface; other hosts pass <see cref="HarnessClientOptions.HshBin"/> explicitly (the
    /// documented deviation from the TS package-manifest resolution).
    /// </summary>
    /// <param name="options">public SDK launch options.</param>
    /// <param name="callerCwd">parent-process directory used for lexical resolution.</param>
    /// <returns>one generic subprocess spec for the JSON-RPC transport.</returns>
    public static RuntimeProcessOptions ResolveLaunch(HarnessClientOptions options, string callerCwd)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hshBin = options.HshBin is null ? Environment.ProcessPath : Path.GetFullPath(options.HshBin, callerCwd);
        if (hshBin is null)
        {
            throw new InvalidOperationException(
                "hsh SDK client cannot resolve the hsh executable: set HshBin or run inside a hsh surface");
        }
        var viaDotnet = hshBin.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var profile = options.Profile ?? DefaultProfile;
        var args = new List<string>();
        if (viaDotnet) args.Add(hshBin);
        args.Add("--profile");
        args.Add(profile);
        foreach (var patch in options.Patches ?? Array.Empty<string>())
        {
            args.Add("--patch");
            args.Add(Path.GetFullPath(patch, callerCwd));
        }
        var hshHome = options.HshHome is null ? null : Path.GetFullPath(options.HshHome, callerCwd);
        return new RuntimeProcessOptions(
            viaDotnet ? "dotnet" : hshBin,
            args,
            options.ProcessCwd is null ? null : Path.GetFullPath(options.ProcessCwd, callerCwd),
            () =>
            {
                var environment = options.Env is null
                    ? Environment.GetEnvironmentVariables()
                        .Cast<System.Collections.DictionaryEntry>()
                        .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value ?? string.Empty, StringComparer.Ordinal)
                    : new Dictionary<string, string>(options.Env, StringComparer.Ordinal);
                if (hshHome is not null) environment["HSH_HOME"] = hshHome;
                return environment;
            },
            $"hsh profile \"{profile}\"",
            options.InitializeTimeoutMs ?? DefaultInitializeTimeoutMs,
            options.RequestTimeoutMs,
            options.ShutdownTimeoutMs ?? 1000,
            options.DisposeEofGraceMs ?? 6000,
            options.DisposeGraceMs ?? 3000);
    }
}
