using Harness.Cordis.Core;
using Harness.Cordis.Plugin.Include;
using IncludePlugin = Harness.Cordis.Plugin.Include.Include;

namespace Harness.Cordis.Plugin.Hmr;

/// <summary>
/// Watches exact config files and refreshes their target on change (C# port of the vendored Hmr
/// service's <c>registerConfig</c>, using <see cref="FileSystemWatcher"/> instead of chokidar).
///
/// Each registration watches the deepest existing ancestor directory of the config path and
/// matches change events against the full requested path, so a path under missing parents becomes
/// watchable as soon as the file appears (vendor/README item 9). Change events are coalesced
/// within one <see cref="HmrConfig.DebounceMs"/> window and refreshes run serially per
/// registration through a dirty-flag loop; a failed refresh is logged, broadcast through the
/// parallel <c>hmr/config-update-failed</c> event, and never stops the watcher. The registration
/// disposer closes the watcher and drains the in-flight refresh.
///
/// The vendored main watcher (module roots, partial reload of ESM dependency trees, externals
/// classification) is not ported: the loader imports plugin types from a catalog, not ESM modules,
/// so there is no module cache to reload. The vendored initial-scan refresh on registration is
/// also dropped: <see cref="FileSystemWatcher"/> has no scan event, and the target include applies
/// its own initial content (vendor/README item 12's patch-layer-applies-once case) through
/// <see cref="IncludePlugin.ApplyFileAsync"/> before registration.
/// </summary>
public sealed class Hmr : Service
{
    private readonly HmrConfig _config;
    private readonly string _baseDir;
    private readonly Logger _log;
    private readonly Dictionary<string, ConfigRegistration> _configs = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <summary>
    /// Create the HMR service on <paramref name="ctx"/>, registered under the <c>hmr</c> service
    /// key, with <paramref name="config"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">when <paramref name="config"/> has a negative <see cref="HmrConfig.DebounceMs"/>.</exception>
    public Hmr(Context ctx, HmrConfig config)
        : base(ctx, "hmr")
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (config.DebounceMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "debounce must be non-negative");
        }
        _baseDir = Path.GetFullPath(config.Base);
        _log = ctx.Logger.Logger("hmr");
    }

    /// <summary>The service config.</summary>
    public HmrConfig Config => _config;

    /// <summary>Base directory that relative config paths resolve against, absolute.</summary>
    public string BaseDir => _baseDir;

    /// <summary>
    /// Watch one exact config path and run <paramref name="refresh"/> when it is added, changed,
    /// or removed. The path resolves against <see cref="BaseDir"/>; the deepest existing ancestor
    /// directory is watched, and change events are matched against the full resolved path, so a
    /// path under missing parents becomes watchable once the file appears. Refreshes are coalesced
    /// and serialized per registration; failures are logged and broadcast, never propagated.
    /// </summary>
    /// <param name="filename">Config path, resolved against <see cref="BaseDir"/> when relative.</param>
    /// <param name="refresh">Refresh callback run serially on add, change, or unlink.</param>
    /// <returns>An asynchronous disposer that closes the watcher and drains the in-flight refresh.</returns>
    /// <exception cref="InvalidOperationException">when the resolved path is already registered or no ancestor directory exists.</exception>
    public Task<IAsyncDisposable> RegisterConfigAsync(string filename, Func<Task> refresh)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(refresh);
        var resolved = Path.GetFullPath(Path.Combine(_baseDir, filename));
        var target = FindWatchRoot(resolved);
        lock (_sync)
        {
            if (_configs.ContainsKey(target.Filename))
            {
                throw new InvalidOperationException($"config path already registered: {resolved}");
            }
            var registration = new ConfigRegistration(this, target.Filename, refresh);
            _configs[target.Filename] = registration;
            try
            {
                var watcher = CreateWatcher(target, registration);
                registration.Watcher = watcher;
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                _configs.Remove(target.Filename);
                registration.Watcher?.Dispose();
                throw;
            }
            return Task.FromResult<IAsyncDisposable>(new ConfigRegistrationDisposer(this, target.Filename));
        }
    }

    /// <summary>
    /// Watch the target include's config file and refresh it on change. Equivalent to
    /// <see cref="RegisterConfigAsync(string, Func{Task})"/> with <c>include.Filename</c> and
    /// <c>include.RefreshAsync</c>.
    /// </summary>
    public Task<IAsyncDisposable> RegisterConfigAsync(IncludePlugin include)
    {
        ArgumentNullException.ThrowIfNull(include);
        return RegisterConfigAsync(include.Filename, () => include.RefreshAsync());
    }

    /// <summary>
    /// Stop the service: close every registration's watcher and drain all in-flight refreshes.
    /// Runs once during teardown; individual registration disposers handle their own registration.
    /// </summary>
    public override async ValueTask StopAsync()
    {
        ConfigRegistration[] registrations;
        lock (_sync)
        {
            registrations = _configs.Values.ToArray();
            _configs.Clear();
        }
        foreach (var registration in registrations)
        {
            registration.Close();
        }
        await Task.WhenAll(registrations.Select(registration => registration.Running ?? Task.CompletedTask));
    }

    /// <summary>Close the registration and drain its in-flight refresh, if any.</summary>
    internal async Task RemoveConfigAsync(string watchFilename)
    {
        ConfigRegistration? registration;
        lock (_sync)
        {
            if (!_configs.TryGetValue(watchFilename, out registration)) return;
            _configs.Remove(watchFilename);
        }
        registration!.Close();
        var running = registration.Running;
        if (running is not null) await running;
    }

    private static (string Filename, string Root, int Depth) FindWatchRoot(string filename)
    {
        var root = Path.GetDirectoryName(filename)
            ?? throw new InvalidOperationException($"config path has no parent directory: {filename}");
        var depth = 0;
        while (true)
        {
            if (Directory.Exists(root)) break;
            if (File.Exists(root))
            {
                throw new InvalidOperationException($"config watch parent is not a directory: {root}");
            }
            var parent = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(parent) || parent == root)
            {
                throw new DirectoryNotFoundException($"no existing ancestor directory for config path {filename}");
            }
            root = parent;
            depth++;
        }
        var canonicalRoot = Path.GetFullPath(root);
        var suffix = Path.GetRelativePath(root, filename);
        return (Path.Combine(canonicalRoot, suffix), canonicalRoot, depth);
    }

    private static FileSystemWatcher CreateWatcher((string Filename, string Root, int Depth) target, ConfigRegistration registration)
    {
        var watcher = new FileSystemWatcher
        {
            Path = target.Root,
            // The vendored watcher limits recursion to the missing-parent depth; FileSystemWatcher
            // only offers all-or-nothing recursion, so a deep target over-watches its root and the
            // change handler filters by exact path (documented deviation).
            IncludeSubdirectories = target.Depth > 0,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
        };
        watcher.Created += (_, e) => registration.OnEvent(e.FullPath);
        watcher.Changed += (_, e) => registration.OnEvent(e.FullPath);
        watcher.Deleted += (_, e) => registration.OnEvent(e.FullPath);
        watcher.Renamed += (_, e) => registration.OnEvent(e.FullPath, e.OldFullPath);
        watcher.Error += (_, e) => registration.LogWatcherError(e.GetException());
        return watcher;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>One registered config watch and its serialized refresh state.</summary>
    private sealed class ConfigRegistration
    {
        private readonly Hmr _owner;
        private readonly object _sync = new();
        private bool _closed;

        public ConfigRegistration(Hmr owner, string targetFilename, Func<Task> refresh)
        {
            _owner = owner;
            TargetFilename = targetFilename;
            Refresh = refresh;
        }

        /// <summary>Full resolved path whose change events trigger a refresh.</summary>
        public string TargetFilename { get; }

        /// <summary>Refresh callback, run serially and never concurrently.</summary>
        public Func<Task> Refresh { get; }

        /// <summary>Watcher owning this registration; assigned once by the owner before enabling.</summary>
        public FileSystemWatcher? Watcher { get; set; }

        /// <summary>The in-flight refresh loop, or null when idle.</summary>
        public Task? Running { get; private set; }

        /// <summary>True when an event arrived while a refresh was in flight or coalescing.</summary>
        private bool Dirty { get; set; }

        /// <summary>
        /// Coalesce one change event: mark the refresh dirty and start the serialized loop when
        /// none is running. Closed registrations ignore events.
        /// </summary>
        public void OnEvent(string fullPath, string? oldFullPath = null)
        {
            if (!PathsEqual(fullPath, TargetFilename)
                && (oldFullPath is null || !PathsEqual(oldFullPath, TargetFilename)))
            {
                return;
            }
            lock (_sync)
            {
                if (_closed) return;
                Dirty = true;
                Running ??= RunLoopAsync();
            }
        }

        /// <summary>Stop accepting events and close the watcher; the in-flight loop drains.</summary>
        public void Close()
        {
            lock (_sync)
            {
                _closed = true;
            }
            if (Watcher is { } watcher)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
        }

        /// <summary>Log a watcher error (buffer overflow, path vanishing) and keep watching.</summary>
        public void LogWatcherError(Exception? error)
        {
            _owner._log.Warn(error?.Message ?? "file watcher error");
        }

        /// <summary>
        /// Serialized refresh loop: wait out one debounce window, run the refresh when no further
        /// events arrived, and repeat while events keep arriving. Failures are contained in
        /// <see cref="RefreshOnceAsync"/>, so a broken config never stops the watcher.
        /// </summary>
        private async Task RunLoopAsync()
        {
            try
            {
                do
                {
                    Dirty = false;
                    await Task.Delay(_owner._config.DebounceMs);
                    if (Dirty) continue;
                    await RefreshOnceAsync();
                } while (Dirty && !IsClosed());
            }
            finally
            {
                // Re-arm when an event arrived between the last dirty check and clearing the
                // running task; closed registrations stay closed.
                lock (_sync)
                {
                    Running = null;
                    if (Dirty && !_closed) Running = RunLoopAsync();
                }
            }
        }

        private async Task RefreshOnceAsync()
        {
            try
            {
                await Refresh();
            }
            catch (Exception error)
            {
                _owner._log.Warn($"config reload at {TargetFilename} failed");
                _owner._log.Warn(error.Message);
                try
                {
                    await _owner.Ctx.Parallel("hmr/config-update-failed", TargetFilename, error);
                }
                catch (Exception rejection)
                {
                    // Observer failures are contained: the failed refresh is already handled.
                    _owner._log.Warn(rejection.Message);
                }
            }
        }

        private bool IsClosed()
        {
            lock (_sync)
            {
                return _closed;
            }
        }
    }

    /// <summary>Async disposer returned by <see cref="RegisterConfigAsync(string, Func{Task})"/>.</summary>
    private sealed class ConfigRegistrationDisposer : IAsyncDisposable
    {
        private readonly Hmr _owner;
        private readonly string _watchFilename;
        private bool _disposed;

        public ConfigRegistrationDisposer(Hmr owner, string watchFilename)
        {
            _owner = owner;
            _watchFilename = watchFilename;
        }

        /// <summary>Close the watcher and drain the in-flight refresh; idempotent.</summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _owner.RemoveConfigAsync(_watchFilename);
        }
    }
}
