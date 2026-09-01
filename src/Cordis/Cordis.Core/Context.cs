namespace Harness.Cordis.Core;

/// <summary>
/// Root dependency container for Cordis plugins (C# port of the vendored Cordis Context).
///
/// The context is a repository of services (string-keyed with typed reads), an event bus with the
/// five dispatch modes, and the entry point for reversible effects. Service, listener, and effect
/// registrations are effect-backed, so <see cref="DisposeAsync"/> unwinds them in reverse
/// registration order with per-cleanup error containment.
/// </summary>
public sealed class Context : IDisposable, IAsyncDisposable
{
    private readonly Dictionary<string, object?> _services = new(StringComparer.Ordinal);
    private readonly Fiber _fiber;
    private readonly EventsService _events;
    private readonly LoggerService _logger;
    private readonly RegistryService _registry;

    /// <summary>Create the root context and install the built-in services.</summary>
    public Context()
    {
        _fiber = new Fiber(this);
        _events = new EventsService(this);
        _logger = new LoggerService(this);
        _registry = new RegistryService(this);
    }

    /// <summary>The root fiber that owns every effect registered on this context.</summary>
    public Fiber Fiber => _fiber;

    /// <summary>The event bus.</summary>
    public EventsService Events => _events;

    /// <summary>
    /// The logging service. Call <see cref="LoggerService.Logger"/> for a named facade, or use the
    /// severity methods directly (they log under the root fiber name, "root").
    /// </summary>
    public LoggerService Logger => _logger;

    /// <summary>Inspection surface over the services, listeners, and effects of this context.</summary>
    public RegistryService Registry => _registry;

    /// <summary>True once <see cref="DisposeAsync"/> has unwound the fiber.</summary>
    public bool IsDisposed => _fiber.State == FiberState.Disposed;

    /// <summary>Keys currently registered in the service repository, in registration order.</summary>
    internal IReadOnlyList<string> ServiceKeys => _services.Keys.ToList();

    // --- Service repository ---

    /// <summary>
    /// Register a service under a stable key. The registration is an effect: the entry is removed
    /// when the context disposes, and a <see cref="Service"/> value is stopped before removal.
    /// Registering the same instance twice is a no-op; registering a different instance under an
    /// existing key throws (fail loud, mirroring Cordis's single-provider rule).
    /// </summary>
    /// <exception cref="InvalidOperationException">when the key is already held by another instance.</exception>
    /// <exception cref="CordisError">with code <see cref="CordisErrorCode.INACTIVE_EFFECT"/> on a disposed context.</exception>
    public void Set<T>(string key, T service) where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(service);
        if (_services.TryGetValue(key, out var existing))
        {
            if (ReferenceEquals(existing, service)) return;
            throw new InvalidOperationException($"service '{key}' is already registered");
        }
        _fiber.RegisterEffect(() =>
        {
            _services[key] = service;
            return () =>
            {
                if (ReferenceEquals(_services[key], service)) _services.Remove(key);
                return service is Service svc ? svc.StopOnceAsync() : ValueTask.CompletedTask;
            };
        }, $"ctx.set(\"{key}\")");
    }

    /// <summary>
    /// Read a service from the repository. Returns <c>null</c> when the key is not registered, and
    /// throws when the registered value is not assignable to <typeparamref name="T"/>
    /// (misconfiguration fails loud).
    /// </summary>
    public T? Get<T>(string key) where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_services.TryGetValue(key, out var value) || value is null) return null;
        if (value is T typed) return typed;
        throw new InvalidOperationException(
            $"service at key '{key}' is of type {value.GetType().FullName}, not {typeof(T).FullName}");
    }

    /// <summary>
    /// Read a service from the repository, throwing when it is absent (fail loud).
    /// </summary>
    public T Require<T>(string key) where T : class
    {
        return Get<T>(key) ?? throw new InvalidOperationException($"required service '{key}' is not registered");
    }

    /// <summary>
    /// Remove a service entry when it still holds <paramref name="expected"/>; used by
    /// <see cref="Service.DisposeAsync"/>. The fiber-level registration effect stays registered and
    /// is a no-op when it later runs.
    /// </summary>
    internal void RemoveService(string name, Service expected)
    {
        if (_services.TryGetValue(name, out var current) && ReferenceEquals(current, expected))
        {
            _services.Remove(name);
        }
    }

    /// <summary>
    /// Remove a service entry by key. The plugin disposer contract removes what a plugin body
    /// registered: Cordis removes fiber-scoped services on fiber disposal, and the loader's
    /// entry disposer does it explicitly in the port.
    /// </summary>
    /// <returns>true when an entry was removed.</returns>
    public bool Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _services.Remove(key);
    }

    // --- Effects ---

    /// <summary>
    /// Register an effect on the current fiber: <paramref name="execute"/> runs immediately and
    /// returns the disposer that releases its resources. The returned disposer is single-shot; the
    /// effect is also disposed when the context disposes, in reverse registration order, with
    /// per-effect error containment.
    /// </summary>
    /// <exception cref="CordisError">with code <see cref="CordisErrorCode.INACTIVE_EFFECT"/> on a disposed or unloading fiber.</exception>
    public IDisposable Effect(Func<IDisposable?> execute, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return _fiber.Effect(execute, label);
    }

    /// <summary>Async-teardown variant of <see cref="Effect"/>; the returned disposer is awaitable.</summary>
    public IAsyncDisposable EffectAsync(Func<IAsyncDisposable?> execute, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return _fiber.EffectAsync(execute, label);
    }

    // --- Events (mixed onto ctx like the TS context proxy) ---

    /// <inheritdoc cref="EventsService.On(string, Delegate, EventOptions?)"/>
    public IDisposable On(string name, Delegate listener, EventOptions? options = null) => _events.On(name, listener, options);

    /// <inheritdoc cref="EventsService.On(string, Action, EventOptions?)"/>
    public IDisposable On(string name, Action listener, EventOptions? options = null) => _events.On(name, listener, options);

    /// <inheritdoc cref="EventsService.On{T}(string, Action{T}, EventOptions?)"/>
    public IDisposable On<T>(string name, Action<T> listener, EventOptions? options = null) => _events.On(name, listener, options);

    /// <inheritdoc cref="EventsService.Once(string, Delegate, EventOptions?)"/>
    public IDisposable Once(string name, Delegate listener, EventOptions? options = null) => _events.Once(name, listener, options);

    /// <inheritdoc cref="EventsService.Emit(string, object?[])"/>
    public void Emit(string name, params object?[] args) => _events.Emit(name, args);

    /// <inheritdoc cref="EventsService.Parallel(string, object?[])"/>
    public Task Parallel(string name, params object?[] args) => _events.Parallel(name, args);

    /// <inheritdoc cref="EventsService.Serial(string, object?[])"/>
    public Task<object?> Serial(string name, params object?[] args) => _events.Serial(name, args);

    /// <inheritdoc cref="EventsService.Bail(string, object?[])"/>
    public object? Bail(string name, params object?[] args) => _events.Bail(name, args);

    /// <inheritdoc cref="EventsService.Waterfall{TResult}(string, object?[], Func{TResult})"/>
    public TResult Waterfall<TResult>(string name, object?[] args, Func<TResult> next) => _events.Waterfall(name, args, next);

    /// <summary>
    /// Dispatch a waterfall event with no event arguments; the chain receives only the
    /// <c>next</c> continuation.
    /// </summary>
    public TResult Waterfall<TResult>(string name, Func<TResult> next) => _events.Waterfall(name, Array.Empty<object?>(), next);

    // --- Disposal ---

    /// <summary>
    /// Unwind every effect (service entries, listeners, effect disposers) in reverse registration
    /// order. Cleanup failures are contained: each failing cleanup is logged via <see cref="Logger"/>
    /// and the remaining effects still run. Idempotent.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        return _fiber.UnloadAsync();
    }

    /// <summary>Synchronous form of <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
