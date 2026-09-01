using Harness.Cordis.Core;
using Harness.Cordis.Cosmokit;
using System.Text;

namespace Harness.Identity;

/// <summary>
/// Ambient seams for locating and minting the anonymous id; every field has a default (port of the
/// TS <c>AnonymousUserIdOptions</c>).
/// </summary>
public sealed record AnonymousIdentityOptions
{
    /// <summary>Environment mapping consulted for <c>DSH_HOME</c>; defaults to the process environment.</summary>
    public IDictionary<string, string?>? Env { get; init; }

    /// <summary>Explicit harness-home override, which has highest precedence over <c>$DSH_HOME</c>.</summary>
    public string? Home { get; init; }

    /// <summary>UUID generator; defaults to <see cref="Guid.NewGuid"/> (test hook).</summary>
    public Func<string>? UuidGenerator { get; init; }
}

/// <summary>
/// ctx.identity: the anonymous identity provider. The id is a random UUID persisted as a bare line
/// in <c>.anonymous-user-id</c> inside the harness home resolved by
/// <see cref="HomePaths.ResolveDshHome"/> (<c>$DSH_HOME</c> &gt; the default <c>~/.dsh</c>),
/// created once on first use and never derived from the hostname, network address, git remote, or
/// any other identifying source. It is scoped to the harness home, not the machine: every process
/// sharing one home reports the same id, and deleting the file mints a fresh identity on the next
/// launch.
///
/// Reads and writes are synchronous and happen once per provider instance. Unlike the TS package,
/// a corrupt id file fails loud at composition instead of minting a fresh id; a write failure on a
/// read-only home stays best-effort (the fresh id is kept in memory) so telemetry and feedback are
/// never blocked.
/// </summary>
public sealed class AnonymousIdentityProvider : Service, IIdentityService
{
    /// <summary>File inside the harness home storing the id: a bare UUID line, no wrapper format.</summary>
    public const string AnonymousUserIdFileName = ".anonymous-user-id";

    private readonly AnonymousUserId _anonymousId;

    /// <summary>
    /// Resolve the harness home, load or mint the id (corruption fails loud), and register the
    /// provider as the <c>identity</c> service.
    /// </summary>
    /// <param name="ctx">the context that owns the service.</param>
    /// <param name="options">home-location and UUID-generation seams.</param>
    /// <returns>the registered provider.</returns>
    /// <exception cref="InvalidOperationException">when the persisted id file is corrupt.</exception>
    public static AnonymousIdentityProvider Create(Context ctx, AnonymousIdentityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var home = HomePaths.ResolveDshHome(options?.Home, options?.Env);
        var id = LoadOrMint(Path.Combine(home, AnonymousUserIdFileName), options);
        return new AnonymousIdentityProvider(ctx, home, id);
    }

    private AnonymousIdentityProvider(Context ctx, string home, AnonymousUserId id)
        : base(ctx, "identity")
    {
        Home = home;
        _anonymousId = id;
    }

    /// <inheritdoc />
    public UserId UserId => _anonymousId;

    /// <inheritdoc />
    public AnonymousUserId AnonymousUserId => _anonymousId;

    /// <inheritdoc />
    public string Home { get; }

    /// <summary>Read a valid persisted id, or mint and persist a fresh one when the file is absent.</summary>
    /// <exception cref="InvalidOperationException">when the file exists with invalid content (corruption fails loud).</exception>
    private static AnonymousUserId LoadOrMint(string file, AnonymousIdentityOptions? options)
    {
        if (File.Exists(file))
        {
            var persisted = ReadPersistedId(file);
            if (persisted is not null) return persisted.Value;
            throw new InvalidOperationException(
                $"corrupt anonymous user id file \"{file}\": expected a UUID, got \"{File.ReadAllText(file).Trim()}\"");
        }
        var created = new AnonymousUserId((options?.UuidGenerator ?? DefaultUuidGenerator)());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            writer.Write(created.Value);
            writer.Write('\n');
        }
        catch (IOException) when (File.Exists(file))
        {
            // A CreateNew refusal (a concurrent first launch won) settles the race by rereading:
            // a valid winner is adopted; an invalid reread is corruption and fails loud.
            var winner = ReadPersistedId(file);
            if (winner is not null) return winner.Value;
            throw new InvalidOperationException(
                $"corrupt anonymous user id file \"{file}\": a concurrent writer left an invalid id");
        }
        catch (IOException)
        {
            // Best-effort persistence (TS parity): keep the fresh id in memory even when the home
            // is unwritable, so this run still reports a consistent id.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort persistence for a read-only home, same as above.
        }
        return created;
    }

    private static string DefaultUuidGenerator() => Guid.NewGuid().ToString("D");

    /// <summary>Read a valid persisted id from the file, or <c>null</c> when absent or invalid.</summary>
    private static AnonymousUserId? ReadPersistedId(string file)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        var value = text.Trim();
        return Guid.TryParseExact(value, "D", out _) ? new AnonymousUserId(value) : null;
    }
}
