using System.Text;
using Harness.Cordis.Core;

namespace Harness.Credentials;

/// <summary>
/// Fully resolved provider parameters; defaulting happens here (an explicit path wins, otherwise
/// the document lives under the harness home), never inline in a read path.
/// </summary>
public sealed record LocalCredentialsConfig(
    /// <summary>Provider-managed credentials file path; defaults to <c>&lt;dshHome&gt;/.credentials.env</c>.</summary>
    string? ManagedPath = null,
    /// <summary>Harness home used when <see cref="ManagedPath"/> is omitted; defaults to <c>$DSH_HOME</c> or <c>~/.dsh</c>.</summary>
    string? DshHome = null,
    /// <summary>Invocation-project <c>.env</c> fallback path; defaults to <c>&lt;cwd&gt;/.env</c>.</summary>
    string? ProjectEnvPath = null,
    /// <summary>User <c>.env</c> fallback path; defaults to <c>&lt;dshHome&gt;/.env</c>.</summary>
    string? UserEnvPath = null);

/// <summary>
/// Loud failure reading or parsing a credentials file. The message names the file and (for a
/// parse failure) the line and offending key — never a value, because a value in this document is
/// a secret.
/// </summary>
public sealed class CredentialsFileError : Exception
{
    /// <summary>Create the error; <paramref name="path"/> is the offending file.</summary>
    public CredentialsFileError(string message, string path)
        : base(message)
    {
        Path = path;
    }

    /// <summary>Create the error with a chained <paramref name="inner"/> cause.</summary>
    public CredentialsFileError(string message, string path, Exception? inner)
        : base(message, inner)
    {
        Path = path;
    }

    /// <summary>The offending file.</summary>
    public string Path { get; }
}

/// <summary>
/// Minimal dotenv parser shared by the managed store and the read-only fallback files: full-line
/// comments, blank lines, an optional <c>export </c> prefix, double-quoted values with the common
/// escapes, single-quoted literal values, and unquoted values (trailing whitespace trimmed). A
/// duplicate key is overwritten by its last occurrence. Diagnostics name the file, line, and key —
/// never a value.
/// </summary>
public static class DotenvParser
{
    /// <summary>
    /// Parse one <c>.env</c> document.
    /// </summary>
    /// <param name="text">the document text.</param>
    /// <param name="filename">absolute path, quoted in diagnostics.</param>
    /// <param name="rejectEmptyValues">when true, an empty value is a loud parse error (the managed
    /// store's contract); when false, empty values parse and resolution treats them as absent.</param>
    /// <returns>key-to-value entries, last occurrence winning.</returns>
    /// <exception cref="CredentialsFileError">on an unreadable shape: an invalid key, an
    /// unterminated quoted value, or (when requested) an empty value.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string text, string filename, bool rejectEmptyValues)
    {
        ArgumentNullException.ThrowIfNull(text);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var body = trimmed;
            if (body.StartsWith("export ", StringComparison.Ordinal)) body = body["export ".Length..].TrimStart();

            var equals = body.IndexOf('=');
            // A line without an assignment is not an entry; dotenv consumers skip it.
            if (equals < 0) continue;

            var key = body[..equals].Trim();
            if (!CredentialNames.IsCredentialRefName(key))
            {
                throw new CredentialsFileError(
                    $"credentials-local: invalid .env at {filename}: line {index + 1}: key \"{key}\" is not a POSIX identifier",
                    filename);
            }
            var rawValue = body[(equals + 1)..].TrimStart();
            var value = ParseValue(rawValue, key, filename, index + 1);
            if (rejectEmptyValues && value.Length == 0)
            {
                throw new CredentialsFileError(
                    $"credentials-local: invalid .env at {filename}: line {index + 1}: the value for \"{key}\" is empty; remove the key instead",
                    filename);
            }
            entries[key] = value;
        }
        return entries;
    }

    private static string ParseValue(string raw, string key, string filename, int line)
    {
        if (raw.Length == 0) return string.Empty;
        if (raw[0] == '"')
        {
            var builder = new StringBuilder();
            var index = 1;
            var closed = false;
            while (index < raw.Length)
            {
                var ch = raw[index];
                if (ch == '\\' && index + 1 < raw.Length)
                {
                    var next = raw[index + 1];
                    builder.Append(next switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => next,
                    });
                    index += 2;
                }
                else if (ch == '"')
                {
                    closed = true;
                    index++;
                    break;
                }
                else
                {
                    builder.Append(ch);
                    index++;
                }
            }
            if (!closed)
            {
                // The key name is quoted, never the value: this line is a secret the user meant to store.
                throw new CredentialsFileError(
                    $"credentials-local: invalid .env at {filename}: line {line}: the value for \"{key}\" has an unterminated double-quoted form",
                    filename);
            }
            return builder.ToString();
        }
        if (raw[0] == '\'')
        {
            var end = raw.IndexOf('\'', 1);
            if (end < 0)
            {
                throw new CredentialsFileError(
                    $"credentials-local: invalid .env at {filename}: line {line}: the value for \"{key}\" has an unterminated single-quoted form",
                    filename);
            }
            return raw[1..end];
        }
        return raw.TrimEnd();
    }
}

/// <summary>
/// File-backed credentials provider over a provider-managed <c>.env</c> document, layered against
/// the environment by how much each layer is trusted:
/// <code>
/// inherited process environment      (read-only, wins)
/// &gt; managed &lt;dshHome&gt;/.credentials.env   (provider-managed, writable)
/// &gt; &lt;cwd&gt;/.env                    (read-only fallback)
/// &gt; &lt;dshHome&gt;/.env                   (read-only fallback)
/// </code>
/// The inherited environment wins because an explicit launch-time value is this run's intent; it
/// cannot be edited from inside, so it is visibly read-only rather than silently shadowing writes.
/// The managed file is read and parsed on every resolution and write — a changed credential
/// reaches the next operation without a restart, and an external edit is observed. An unreadable
/// or unparseable file fails loud (a document that exists but cannot be trusted is never treated
/// as "no credentials stored"). Secret values never appear in exception messages or logs: only
/// file paths, line numbers, and key names do. Port of
/// <c>@deepseek-ai/dsh-credentials-local</c> (no file watcher and no cross-process writer lock in
/// this port; resolution re-reads the files instead).
/// </summary>
public sealed class LocalCredentialsProvider : Service, ICredentialsService
{
    /// <summary>Source id of the inherited process environment layer.</summary>
    public const string EnvironmentSource = "env";

    /// <summary>Source id of the provider-managed file layer.</summary>
    public const string FileSource = "file";

    /// <summary>Source id of the invocation-project <c>.env</c> layer.</summary>
    public const string ProjectEnvSource = "project-env";

    /// <summary>Source id of the user <c>.env</c> layer.</summary>
    public const string UserEnvSource = "user-env";

    /// <summary>Basename of the credentials document inside the harness home.</summary>
    public const string DefaultCredentialsFilename = ".credentials.env";

    /// <summary>Environment variable naming the harness home.</summary>
    public const string DshHomeEnvVar = "DSH_HOME";

    private static readonly IReadOnlyDictionary<string, string> EmptyRefs = new Dictionary<string, string>();

    private readonly string _managedPath;
    private readonly string? _projectEnvPath;
    private readonly string? _userEnvPath;
    private readonly Func<string, string?> _environment;

    /// <summary>
    /// Create and register the credentials service under the <c>credentials</c> key.
    /// <paramref name="environment"/> is injectable so tests serve a fixed environment with zero
    /// process mutation; it defaults to the process environment.
    /// </summary>
    public LocalCredentialsProvider(Context ctx, LocalCredentialsConfig? config = null, Func<string, string?>? environment = null)
        : base(ctx, "credentials")
    {
        var cfg = config ?? new LocalCredentialsConfig();
        var dshHome = cfg.DshHome
            ?? Environment.GetEnvironmentVariable(DshHomeEnvVar)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        _managedPath = cfg.ManagedPath ?? Path.Combine(dshHome, DefaultCredentialsFilename);
        _projectEnvPath = cfg.ProjectEnvPath ?? Path.Combine(Environment.CurrentDirectory, ".env");
        _userEnvPath = cfg.UserEnvPath ?? Path.Combine(dshHome, ".env");
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The resolved provider-managed file path.</summary>
    public string ManagedPath => _managedPath;

    /// <inheritdoc />
    public Task<ResolvedCredential?> ResolveAsync(string reference, CancellationToken cancellationToken = default)
    {
        CredentialNames.ValidateReference(reference);

        var inherited = _environment(reference);
        if (!string.IsNullOrEmpty(inherited))
        {
            return Task.FromResult<ResolvedCredential?>(new ResolvedCredential(inherited, EnvironmentSource));
        }
        if (LoadManaged().TryGetValue(reference, out var stored) && stored.Length > 0)
        {
            return Task.FromResult<ResolvedCredential?>(new ResolvedCredential(stored, FileSource));
        }
        var fallback = LoadFallback(reference);
        return Task.FromResult<ResolvedCredential?>(fallback);
    }

    /// <inheritdoc />
    public async Task<ResolvedCredential> RequireAsync(string reference, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
        return resolved ?? throw new CredentialMissingError(reference);
    }

    /// <inheritdoc />
    public Task<CredentialInfo> DescribeAsync(string reference, CancellationToken cancellationToken = default)
    {
        CredentialNames.ValidateReference(reference);

        // Only the inherited environment is unwritable: it is the one layer this process cannot
        // edit. A fallback .env value is writable in the sense that matters — storing a key
        // replaces it as the effective one.
        if (!string.IsNullOrEmpty(_environment(reference)))
        {
            return Task.FromResult(new CredentialInfo(true, EnvironmentSource, false));
        }
        if (LoadManaged().TryGetValue(reference, out var stored) && stored.Length > 0)
        {
            return Task.FromResult(new CredentialInfo(true, FileSource, true));
        }
        var project = TryReadFallback(_projectEnvPath);
        if (project is not null && project.TryGetValue(reference, out var value) && value.Length > 0)
        {
            return Task.FromResult(new CredentialInfo(true, ProjectEnvSource, true));
        }
        var user = TryReadFallback(_userEnvPath);
        if (user is not null && user.TryGetValue(reference, out value) && value.Length > 0)
        {
            return Task.FromResult(new CredentialInfo(true, UserEnvSource, true));
        }
        return Task.FromResult(new CredentialInfo(false, null, true));
    }

    /// <inheritdoc />
    public Task SetAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        CredentialNames.ValidateReference(reference);
        if (value.Length == 0)
        {
            throw new ArgumentException($"credentials-local: an empty value cannot be stored for \"{reference}\"; use unset", nameof(value));
        }
        AssertUnshadowed(reference, "set");
        WriteManaged(reference, value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UnsetAsync(string reference, CancellationToken cancellationToken = default)
    {
        CredentialNames.ValidateReference(reference);
        AssertUnshadowed(reference, "unset");
        WriteManaged(reference, null);
        return Task.CompletedTask;
    }

    /// <summary>Reject a write the inherited environment would shadow into apparent no-effect.</summary>
    private void AssertUnshadowed(string reference, string verb)
    {
        if (!string.IsNullOrEmpty(_environment(reference)))
        {
            throw new InvalidOperationException(
                $"credentials-local: \"{reference}\" is supplied read-only by the launching environment, so {verb} would be"
                + " shadowed; unset it in the shell you start dsh from instead");
        }
    }

    /// <summary>
    /// Reconcile against the on-disk document, patch one reference, and write the result
    /// atomically. An unreadable or unparseable document throws before any write, so a write can
    /// never clobber a document this provider could not understand.
    /// </summary>
    private void WriteManaged(string reference, string? value)
    {
        var current = LoadManaged();
        var entries = new Dictionary<string, string>(current, StringComparer.Ordinal);
        if (value is null)
        {
            if (!entries.Remove(reference)) return; // removing an absent reference is a no-op
        }
        else
        {
            entries[reference] = value;
        }
        var directory = Path.GetDirectoryName(_managedPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        WriteAtomic(_managedPath, RenderDotenv(entries));
    }

    private static string RenderDotenv(IReadOnlyDictionary<string, string> entries)
    {
        var builder = new StringBuilder();
        foreach (var pair in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append('=').Append(QuoteValue(pair.Value)).Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>Quote a value when it needs quoting so the round trip is lossless.</summary>
    private static string QuoteValue(string value)
    {
        if (value.Length == 0
            || value.Any(ch => char.IsWhiteSpace(ch) || ch == '#' || ch == '"' || ch == '\''))
        {
            return "\"" + value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
        }
        return value;
    }

    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The managed document as parsed right now; an absent file is an empty store.</summary>
    private IReadOnlyDictionary<string, string> LoadManaged()
    {
        var text = TryReadFile(_managedPath);
        return text is null ? EmptyRefs : DotenvParser.Parse(text, _managedPath, rejectEmptyValues: true);
    }

    /// <summary>The layered fallback value for one reference: project <c>.env</c> above user <c>.env</c>.</summary>
    private ResolvedCredential? LoadFallback(string reference)
    {
        var project = TryReadFallback(_projectEnvPath);
        if (project is not null && project.TryGetValue(reference, out var value) && value.Length > 0)
        {
            return new ResolvedCredential(value, ProjectEnvSource);
        }
        var user = TryReadFallback(_userEnvPath);
        if (user is not null && user.TryGetValue(reference, out value) && value.Length > 0)
        {
            return new ResolvedCredential(value, UserEnvSource);
        }
        return null;
    }

    /// <summary>Read and parse a read-only fallback <c>.env</c>; a missing file is null, any other failure is loud.</summary>
    private IReadOnlyDictionary<string, string>? TryReadFallback(string? path)
    {
        if (path is null) return null;
        var text = TryReadFile(path);
        return text is null ? null : DotenvParser.Parse(text, path, rejectEmptyValues: false);
    }

    /// <summary>Read a file as UTF-8; a missing file is null, any other read failure is loud and names the path only.</summary>
    private static string? TryReadFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CredentialsFileError($"credentials-local: cannot read {path}", path, error);
        }
    }
}

