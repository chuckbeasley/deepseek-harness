using Harness.Cordis.Core;
using Harness.Cordis.Schemastery;

namespace Harness.Settings;

/// <summary>Minimal <see cref="IDisposable"/> built from an action; used for sync effect cleanups.</summary>
internal sealed class ActionDisposer : IDisposable
{
    private readonly Action _action;

    public ActionDisposer(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}

/// <summary>
/// Abstract settings service (ctx.settings): providers implement raw-document storage
/// (<c>LoadAsync</c>/<c>PersistAsync</c>) and the base class owns namespace registration,
/// resolution, validation, change detection, and the <c>settings/updated</c> commit event.
/// Reads are read-through to the last committed resolved value: an invalid stored section keeps
/// that namespace's last good value, and a committed change caps later reads.
/// </summary>
public abstract class SettingsProvider : Service
{
    /// <summary>The service key this instance registers under.</summary>
    public const string ServiceKey = "settings";

    private readonly Dictionary<SettingsNamespace, Registration> _registrations = new();
    private Dictionary<string, object?> _document = new();
    private readonly Dictionary<string, Task> _writeQueues = new();
    private readonly List<Task> _pendingTails = new();
    private bool _stopped;

    private sealed class Registration
    {
        public required SettingsNamespace Ns { get; init; }
        public required Schema Schema { get; init; }
        public required object? Base { get; init; }
        public required SettingsApplies Applies { get; init; }
        public Action<object?>? Validate { get; init; }
        public required object? Resolved { get; set; }
        public long Revision { get; set; }
        public List<Watcher> Watchers { get; } = new();
    }

    private sealed class Watcher
    {
        public required Func<object?, object?, Task> Callback { get; init; }
        public Task Tail { get; set; } = Task.CompletedTask;
        public bool Active { get; set; } = true;
    }

    private enum WriteMode
    {
        Merge,
        Replace,
        Mutate,
    }

    /// <summary>Register the service under the <c>settings</c> key.</summary>
    protected SettingsProvider(Context ctx)
        : base(ctx, ServiceKey)
    {
    }

    /// <summary>Whether <c>UpdateAsync</c>/<c>ReplaceAsync</c> may persist through this provider.</summary>
    public virtual bool Writable => false;

    /// <summary>Absolute path of the provider's user-editable document, when its storage is one local file.</summary>
    public virtual string? DocumentPath => null;

    /// <summary>
    /// Materialize an absent local document (creating it when needed) and return its path, or
    /// <c>null</c> for non-file storage (port of the TS <c>prepareDocument</c>).
    /// </summary>
    public virtual string? PrepareDocument() => null;

    /// <summary>Load the provider's document once and publish it when the service attaches (activates).</summary>
    protected abstract Task<Dictionary<string, object?>> LoadAsync();

    /// <summary>Durably store one namespace's merged user section.</summary>
    protected abstract Task PersistAsync(SettingsNamespace ns, Dictionary<string, object?> section);

    /// <summary>Load and publish the stored document on service activation.</summary>
    public override async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        Publish(await LoadAsync(), SettingsUpdateSource.Provider);
    }

    /// <summary>Refuse new writes and wait until every queued write and watcher invocation settles.</summary>
    public override async ValueTask StopAsync()
    {
        _stopped = true;
        var pending = _writeQueues.Values.Concat(_pendingTails).ToArray();
        foreach (var task in pending)
        {
            try
            {
                await task;
            }
            catch
            {
                // Failures already surfaced to their caller; teardown only drains.
            }
        }
    }

    /// <summary>
    /// Register a namespace schema and receive its owner scope. The registration is an effect on
    /// the calling context's fiber: disposing that fiber removes the namespace and its observers.
    /// An invalid stored section fails the registration itself — the earliest point where the
    /// schema can judge it.
    /// </summary>
    /// <param name="ns">Unique namespace; duplicate registration fails loud.</param>
    /// <param name="schema">Schema resolving this namespace's value.</param>
    /// <param name="options">Composition base layer, effect timing, and extra validation.</param>
    /// <returns>The owner scope for reads, observation, and updates.</returns>
    /// <exception cref="ArgumentException">when <paramref name="ns"/> is not a lowercase hyphenated identifier.</exception>
    public ISettingsScope<T> Register<T>(string ns, Schema schema, SettingsRegisterOptions<T>? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var parsed = SettingsNamespaces.Parse(ns);
        if (_registrations.ContainsKey(parsed))
        {
            throw new InvalidOperationException($"settings namespace \"{parsed}\" is already registered");
        }
        Action<object?>? validate = null;
        if (options?.Validate is not null)
        {
            var ownerValidate = options.Validate;
            validate = value => ownerValidate((T)value!);
        }
        var registration = new Registration
        {
            Ns = parsed,
            Schema = schema,
            Base = options?.Base,
            Applies = options?.Applies ?? SettingsApplies.Live,
            Validate = validate,
            Resolved = Resolve(schema, options?.Base, Section(parsed), validate),
        };
        Ctx.Effect(() =>
        {
            _registrations[parsed] = registration;
            return new ActionDisposer(() => _registrations.Remove(parsed));
        }, $"settings.register(\"{parsed}\")");
        return new SettingsScopeImpl<T>(registration, this);
    }

    /// <summary>
    /// Attach one optional-settings consumer to this provider. The consumer registers its
    /// composition entry as the base layer while this provider is present, then falls back to that
    /// entry if the provider detaches (unless the consumer's own fiber is unloading).
    /// </summary>
    /// <param name="owner">Consumer context whose unload suppresses fallback work.</param>
    /// <param name="ns">Consumer-owned settings namespace.</param>
    /// <param name="schema">Schema resolving the namespace.</param>
    /// <param name="entry">Composition entry used as the base and fallback value.</param>
    /// <param name="hooks">Source sink, change notification, and optional validation.</param>
    public void InstallSection<T>(Context owner, string ns, Schema schema, T entry, SettingsSectionHooks<T> hooks)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(hooks);
        var scope = Register(ns, schema, new SettingsRegisterOptions<T> { Base = entry, Validate = hooks.Validate });
        hooks.SetSource(() => scope.Get());
        Ctx.Effect(() =>
        {
            return new ActionDisposer(() =>
            {
                // Losing the provider leaves the consumer running; unloading the consumer does
                // not, so only the former needs fallback work.
                if (owner.Fiber.State is FiberState.Unloading or FiberState.Disposed) return;
                hooks.SetSource(() => entry);
                hooks.OnChange();
            });
        }, $"settings.installSection(\"{ns}\")");
        hooks.OnChange();
        scope.Watch((_, _) =>
        {
            if (owner.Fiber.State is FiberState.Unloading or FiberState.Disposed) return;
            hooks.OnChange();
        });
    }

    /// <summary>Read one registered namespace's resolved value, or <c>null</c> while unregistered.</summary>
    public object? Get(string ns)
    {
        var registration = _registrations.GetValueOrDefault(SettingsNamespaces.Parse(ns));
        return registration?.Resolved;
    }

    /// <summary>Merge a patch into one registered namespace's user layer, validate, persist, then commit and emit.</summary>
    /// <exception cref="SettingsConflictError">when the namespace moved past <paramref name="expectedRevision"/>.</exception>
    public Task UpdateAsync(string ns, object patch, long? expectedRevision = null)
        => WriteAsync(SettingsNamespaces.Parse(ns), patch, WriteMode.Merge, expectedRevision);

    /// <summary>Replace one registered namespace's user section wholesale, validate, persist, then commit and emit.</summary>
    /// <exception cref="SettingsConflictError">when the namespace moved past <paramref name="expectedRevision"/>.</exception>
    public Task ReplaceAsync(string ns, object section, long? expectedRevision = null)
        => WriteAsync(SettingsNamespaces.Parse(ns), section, WriteMode.Replace, expectedRevision);

    /// <summary>
    /// Apply ordered path-addressed edits to one registered namespace's user section, validate,
    /// persist, then commit and emit. Later ops observe earlier ones, so a caller holding only the
    /// redacted descriptor can name the field it means without restating the section.
    /// </summary>
    /// <exception cref="SettingsConflictError">when the namespace moved past <paramref name="expectedRevision"/>.</exception>
    public Task MutateAsync(string ns, IReadOnlyList<SettingsPathOp> ops, long? expectedRevision = null)
        => WriteAsync(SettingsNamespaces.Parse(ns), ops, WriteMode.Mutate, expectedRevision);

    /// <summary>
    /// Describe every registered namespace for configuration surfaces, including the composition
    /// base and raw user layers so a form can mark which fields the user overrode.
    /// </summary>
    /// <param name="options">Redaction switch; wire surfaces must redact.</param>
    /// <returns>One descriptor per registered namespace, in registration order.</returns>
    public IReadOnlyList<SettingsDescriptor> Describe(SettingsDescribeOptions? options = null)
    {
        var redact = options?.RedactSecrets == true;
        var result = new List<SettingsDescriptor>();
        foreach (var registration in _registrations.Values)
        {
            Dictionary<string, object?>? user;
            try
            {
                user = Section(registration.Ns);
            }
            catch
            {
                // A malformed stored section already warned at publish and kept the last good
                // resolved value; describing it as "no user layer" keeps this read total.
                user = null;
            }
            var baseLayer = registration.Base is null ? null : SettingsJson.DeepClone(registration.Base);
            var detachedUser = user is null ? null : SettingsJson.DeepClone(user);
            if (!redact)
            {
                result.Add(new SettingsDescriptor(registration.Ns, registration.Schema, registration.Resolved, registration.Revision, registration.Applies, baseLayer, detachedUser));
                continue;
            }
            var redactedValue = SettingsRedaction.RedactSecrets(registration.Schema, registration.Resolved);
            var redactedBase = baseLayer is null ? null : SettingsRedaction.RedactSecrets(registration.Schema, baseLayer).Value;
            var redactedUser = detachedUser is null ? null : SettingsRedaction.RedactSecrets(registration.Schema, detachedUser).Value;
            result.Add(new SettingsDescriptor(registration.Ns, registration.Schema, redactedValue.Value, registration.Revision, registration.Applies, redactedBase, redactedUser, redactedValue.Secrets));
        }
        return result;
    }

    /// <summary>
    /// Provider hook: commit a complete raw document observed in storage. Each registered namespace
    /// re-resolves; an invalid section keeps that namespace's last good value and warns, other
    /// namespaces still commit.
    /// </summary>
    /// <param name="doc">The detached raw document (unregistered sections preserved).</param>
    /// <param name="source">Change origin; defaults to provider.</param>
    protected void Publish(Dictionary<string, object?> doc, SettingsUpdateSource source = SettingsUpdateSource.Provider)
    {
        // Read every raw section BEFORE swapping the document, so the revision bump below compares
        // what was stored with what now is.
        var before = new Dictionary<SettingsNamespace, object?>();
        foreach (var registration in _registrations.Values)
        {
            try
            {
                before[registration.Ns] = Section(registration.Ns);
            }
            catch
            {
                before[registration.Ns] = null;
            }
        }
        _document = doc;
        foreach (var registration in _registrations.Values)
        {
            object? next;
            try
            {
                next = Resolve(registration.Schema, registration.Base, Section(registration.Ns), registration.Validate);
            }
            catch (Exception error)
            {
                Ctx.Logger.Warn($"settings: keeping last good \"{registration.Ns}\" after invalid stored section");
                Ctx.Logger.Warn(error.Message);
                continue;
            }
            BumpRevision(registration, before.GetValueOrDefault(registration.Ns), Section(registration.Ns));
            Commit(registration, next, source);
        }
    }

    /// <summary>Validate a write, then queue it on the namespace's serialized write chain.</summary>
    private Task WriteAsync(SettingsNamespace ns, object input, WriteMode mode, long? expectedRevision)
    {
        var verb = mode switch { WriteMode.Merge => "update", WriteMode.Replace => "replace", _ => "mutate" };
        var registration = _registrations.GetValueOrDefault(ns);
        if (registration is null)
        {
            throw new InvalidOperationException($"settings namespace \"{ns}\" is not registered");
        }
        if (_stopped)
        {
            throw new InvalidOperationException($"settings service is disposed: \"{ns}\" cannot be written");
        }
        if (!Writable)
        {
            throw new InvalidOperationException($"settings provider is read-only: \"{ns}\" cannot be updated in-process");
        }
        Dictionary<string, object?> snapshot;
        if (mode == WriteMode.Mutate)
        {
            if (input is not IReadOnlyList<SettingsPathOp> ops)
            {
                throw new ArgumentException($"settings {verb} for \"{ns}\" must be an array of path ops", nameof(input));
            }
            ValidateOps(ops, ns, verb);
            // The ops array is wrapped so one JSON-shape walk covers both the ops' values and
            // their paths, mirroring the TS write path.
            var wrapped = new Dictionary<string, object?>
            {
                ["ops"] = ops.Select(op => (object)WrapOp(op)).ToList(),
            };
            var cloned = SettingsJson.CloneJsonShaped(wrapped, (label, path) =>
                new ArgumentException($"settings {verb} for \"{ns}\" must contain only JSON-compatible data (found {label} at {path})"));
            snapshot = new Dictionary<string, object?>
            {
                ["ops"] = ((List<object?>)cloned["ops"]!).Cast<Dictionary<string, object?>>()
                    .Select(entry => (object)new SettingsPathOp(
                        (string)entry["op"]!,
                        ((List<object?>)entry["path"]!).Cast<string>().ToArray(),
                        entry.TryGetValue("value", out var value) ? value : null))
                    .ToList(),
            };
        }
        else
        {
            if (input is not Dictionary<string, object?> plain)
            {
                throw new ArgumentException($"settings {verb} for \"{ns}\" must be a plain object", nameof(input));
            }
            snapshot = SettingsJson.CloneJsonShaped(plain, (label, path) =>
                new ArgumentException($"settings {verb} for \"{ns}\" must contain only JSON-compatible data (found {label} at {path})"));
        }
        var previous = _writeQueues.TryGetValue(ns.Value, out var tail) ? tail : Task.CompletedTask;
        var run = previous.ContinueWith(
            _ => WriteQueuedAsync(ns, registration, mode, expectedRevision, snapshot),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
        _writeQueues[ns.Value] = run.ContinueWith(static _ => { });
        return run;
    }

    /// <summary>Wrap one op for the JSON-shape walk: op, path, and (for set) the value.</summary>
    private static Dictionary<string, object?> WrapOp(SettingsPathOp op)
    {
        var wrapped = new Dictionary<string, object?>
        {
            ["op"] = op.Op,
            ["path"] = op.Path.ToList(),
        };
        if (op.Op == "set") wrapped["value"] = op.Value;
        return wrapped;
    }

    /// <summary>Reject an op shape the typed surface cannot express (the TS TypeError checks).</summary>
    private static void ValidateOps(IReadOnlyList<SettingsPathOp> ops, SettingsNamespace ns, string verb)
    {
        foreach (var op in ops)
        {
            if (op is null || (op.Op != "set" && op.Op != "unset"))
            {
                throw new ArgumentException($"settings {verb} for \"{ns}\" ops must be {{op:'set'|'unset', path}}", nameof(ops));
            }
        }
    }

    /// <summary>
    /// Apply one path op to a detached section, returning the next section (port of the TS
    /// <c>applyPathOp</c>). The empty path addresses the section root: <c>unset</c> clears it,
    /// <c>set</c> replaces it with a plain object. A deeper <c>set</c> creates the intermediate
    /// objects it needs; an <c>unset</c> through an absent path is already satisfied.
    /// </summary>
    private static Dictionary<string, object?> ApplyPathOp(Dictionary<string, object?> section, SettingsPathOp op)
    {
        var head = op.Path.FirstOrDefault();
        var rest = op.Path.Skip(1).ToArray();
        if (head is null)
        {
            if (op.Op == "unset") return new Dictionary<string, object?>();
            if (op.Value is not Dictionary<string, object?> root)
            {
                throw new ArgumentException("settings mutate: setting the section root requires a plain object");
            }
            return new Dictionary<string, object?>(root);
        }
        if (rest.Length == 0)
        {
            var next = new Dictionary<string, object?>(section);
            if (op.Op == "set") next[head] = op.Value;
            else next.Remove(head);
            return next;
        }
        if (section.TryGetValue(head, out var child) && child is Dictionary<string, object?> childSection)
        {
            var next = new Dictionary<string, object?>(section);
            next[head] = ApplyPathOp(childSection, op with { Path = rest });
            return next;
        }
        if (op.Op == "unset") return section;
        var created = new Dictionary<string, object?>();
        var result = new Dictionary<string, object?>(section);
        result[head] = ApplyPathOp(created, op with { Path = rest });
        return result;
    }

    private async Task WriteQueuedAsync(
        SettingsNamespace ns,
        Registration registration,
        WriteMode mode,
        long? expectedRevision,
        Dictionary<string, object?> snapshot)
    {
        if (_stopped)
        {
            throw new InvalidOperationException($"settings service was disposed before the queued \"{ns}\" write ran");
        }
        if (!_registrations.TryGetValue(ns, out var current) || !ReferenceEquals(current, registration))
        {
            throw new InvalidOperationException($"settings namespace \"{ns}\" registration was disposed before the queued write ran");
        }
        // Every mode derives from the section as it stands NOW, at the front of the queue.
        var stored = Section(ns) ?? new Dictionary<string, object?>();
        if (expectedRevision is not null && expectedRevision != registration.Revision)
        {
            throw new SettingsConflictError(ns, expectedRevision.Value, registration.Revision);
        }
        var section = mode switch
        {
            WriteMode.Merge => (Dictionary<string, object?>)SettingsJson.MergeLayers(stored, snapshot)!,
            WriteMode.Replace => snapshot,
            _ => ((List<object?>)snapshot["ops"]!).Cast<SettingsPathOp>().Aggregate(stored, ApplyPathOp),
        };
        var next = Resolve(registration.Schema, registration.Base, section, registration.Validate);
        await PersistAsync(ns, section);
        // The write reached storage either way; the document cache must say so.
        _document[ns.Value] = section;
        if (_registrations.TryGetValue(ns, out var owner) && ReferenceEquals(owner, registration) && !_stopped)
        {
            BumpRevision(registration, stored, section);
            Commit(registration, next, SettingsUpdateSource.Update);
        }
    }

    /// <summary>Resolve one namespace value: schema defaults, then base, then the user layer.</summary>
    private object? Resolve(Schema schema, object? baseLayer, Dictionary<string, object?>? section, Action<object?>? validate)
    {
        // The merged candidate is untyped by construction; the schema call is the runtime
        // validation that admits it.
        var value = schema.Validate(SettingsJson.MergeLayers(baseLayer, section));
        // The owner's own check runs on the admitted value, so it sees defaults and the
        // composition base exactly as the owner will.
        validate?.Invoke(value);
        return value;
    }

    /// <summary>Read one namespace's raw user section, rejecting non-object sections.</summary>
    private Dictionary<string, object?>? Section(SettingsNamespace ns)
    {
        if (!_document.TryGetValue(ns.Value, out var section) || section is null) return null;
        if (section is not Dictionary<string, object?> plain)
        {
            throw new ArgumentException($"settings section \"{ns}\" must be an object of keys");
        }
        return plain;
    }

    /// <summary>Advance a namespace's revision when its RAW section changed, and announce it.</summary>
    private void BumpRevision(Registration registration, object? before, object? after)
    {
        if (SettingsJson.DeepEqual(before, after)) return;
        registration.Revision += 1;
        EmitDocumentUpdated(registration.Ns, registration.Revision);
    }

    /// <summary>Commit a resolved value when changed: swap, notify watchers, emit the event.</summary>
    private void Commit(Registration registration, object? next, SettingsUpdateSource source)
    {
        var prev = registration.Resolved;
        if (SettingsJson.DeepEqual(next, prev)) return;
        registration.Resolved = next;
        foreach (var watcher in registration.Watchers.ToArray())
        {
            // Serialize per watcher: invocations of one callback run one at a time in commit
            // order; failures are contained and logged.
            var segment = watcher.Tail
                .ContinueWith(
                    _ => watcher.Active && !_stopped ? watcher.Callback(next, prev) : Task.CompletedTask,
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap()
                .ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        Ctx.Logger.Warn($"settings: watcher for \"{registration.Ns}\" failed: {task.Exception?.GetBaseException().Message}");
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            watcher.Tail = segment;
            _pendingTails.Add(segment);
            _ = segment.ContinueWith(_ => _pendingTails.Remove(segment), TaskScheduler.Default);
        }
        try
        {
            Ctx.Emit("settings/updated", registration.Ns, next, prev, source);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"settings: a settings/updated listener for \"{registration.Ns}\" failed: {error.Message}");
        }
    }

    /// <summary>Contained fan-out of <c>settings/document-updated</c>.</summary>
    private void EmitDocumentUpdated(SettingsNamespace ns, long revision)
    {
        try
        {
            Ctx.Emit("settings/document-updated", ns, revision);
        }
        catch (Exception error)
        {
            Ctx.Logger.Warn($"settings: a settings/document-updated listener for \"{ns}\" failed: {error.Message}");
        }
    }

    private sealed class SettingsScopeImpl<T> : ISettingsScope<T>
    {
        private readonly Registration _registration;
        private readonly SettingsProvider _owner;

        public SettingsScopeImpl(Registration registration, SettingsProvider owner)
        {
            _registration = registration;
            _owner = owner;
        }

        public T Get() => (T)_registration.Resolved!;

        public IDisposable Watch(Action<T, T> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return _owner.AddWatcher(_registration, (next, prev) =>
            {
                callback((T)next!, (T)prev!);
                return Task.CompletedTask;
            });
        }

        public IDisposable WatchAsync(Func<T, T, Task> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);
            return _owner.AddWatcher(_registration, (next, prev) => callback((T)next!, (T)prev!));
        }

        public Task UpdateAsync(object patch, long? expectedRevision = null)
            => _owner.UpdateAsync(_registration.Ns.Value, patch, expectedRevision);

        public Task ReplaceAsync(object section, long? expectedRevision = null)
            => _owner.ReplaceAsync(_registration.Ns.Value, section, expectedRevision);

        public Task MutateAsync(IReadOnlyList<SettingsPathOp> ops, long? expectedRevision = null)
            => _owner.MutateAsync(_registration.Ns.Value, ops, expectedRevision);
    }

    private IDisposable AddWatcher(Registration registration, Func<object?, object?, Task> callback)
    {
        var watcher = new Watcher { Callback = callback };
        registration.Watchers.Add(watcher);
        return new ActionDisposer(() =>
        {
            watcher.Active = false;
            registration.Watchers.Remove(watcher);
        });
    }
}
