using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Cordis.Core;

/// <summary>
/// Options accepted by <c>On</c> and <c>Once</c> (port of the TS EventOptions).
/// </summary>
/// <param name="Prepend">add the listener before existing listeners for the same event.</param>
/// <param name="Global">receive the event regardless of context filter checks (Phase 0 has no scoped contexts).</param>
public sealed record EventOptions(bool Prepend = false, bool Global = false);

/// <summary>Registered listener record stored by the event service.</summary>
internal sealed class Hook
{
    public required Delegate Callback { get; init; }

    public bool Prepend { get; init; }

    public bool Global { get; init; }

    /// <summary>Once listeners remove themselves before their first invocation.</summary>
    public bool AutoRemove { get; init; }
}

/// <summary>
/// Event bus installed as <c>ctx.events</c> and mixed into every context (C# port of the vendored
/// Cordis EventsService).
///
/// Dispatch modes: <see cref="Emit"/> observes in registration order without awaiting listener
/// returns; <see cref="Waterfall{TResult}"/> composes listeners around an innermost
/// <c>next</c> continuation; <see cref="Parallel"/> awaits all listeners concurrently;
/// <see cref="Serial"/> awaits listeners in order until one bails; <see cref="Bail"/> runs
/// listeners in order until one returns a bail value. Listener delegates receive the dispatch
/// arguments flattened; waterfall listeners receive the <c>next</c> continuation as the final
/// argument. Listener registrations are effects on the owning fiber, so the context disposes them
/// on unload.
/// </summary>
public sealed class EventsService
{
    private readonly Context _ctx;
    private readonly Dictionary<string, List<Hook>> _hooks = new(StringComparer.Ordinal);

    /// <summary>Create the event bus for a context.</summary>
    public EventsService(Context ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
    }

    /// <summary>Number of registered listeners for one event (used to prove disposal).</summary>
    public int ListenerCount(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _hooks.TryGetValue(name, out var hooks) ? hooks.Count : 0;
    }

    /// <summary>
    /// Snapshot the listeners for one dispatch. Non-internal events first emit
    /// <c>internal/dispatch</c> with the mode, name, and raw args for diagnostics (port of the TS
    /// dispatch primitive minus the optional <c>this</c> binding, which scoped contexts will add).
    /// </summary>
    private List<Hook> ResolveHooks(string mode, string name, object?[] args)
    {
        if (!name.StartsWith("internal/", StringComparison.Ordinal))
        {
            _ctx.Emit("internal/dispatch", mode, name, args);
        }
        return _hooks.TryGetValue(name, out var hooks) ? hooks.ToList() : new List<Hook>();
    }

    /// <summary>
    /// Invoke one listener with the dispatch arguments, unwrapping the reflection wrapper so the
    /// listener's own exception (not a TargetInvocationException) reaches the dispatcher.
    /// </summary>
    private object? Invoke(string name, Hook hook, object?[] args)
    {
        if (hook.AutoRemove)
        {
            // Once listeners remove themselves before their first call; the current dispatch
            // snapshot keeps them visible for this dispatch (matches the TS self-disposing wrapper).
            if (_hooks.TryGetValue(name, out var hooks)) hooks.Remove(hook);
        }
        try
        {
            return hook.Callback.DynamicInvoke(args);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw; // unreachable
        }
    }

    /// <summary>
    /// Dispatch an event synchronously, ignoring listener return values (fire-and-forget).
    /// Listeners run in registration order; a throwing listener aborts the remaining listeners and
    /// the exception propagates to the caller — containment is the caller's choice (the harness
    /// wraps observer events per listener). Failures of async listeners are logged, never thrown.
    /// </summary>
    public void Emit(string name, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var hook in ResolveHooks("emit", name, args))
        {
            var result = Invoke(name, hook, args);
            ObserveAsyncResult(result);
        }
    }

    private void ObserveAsyncResult(object? result)
    {
        Task? task = result switch
        {
            Task t => t,
            ValueTask vt => vt.AsTask(),
            _ => null,
        };
        if (task is null) return;
        _ = ObserveTaskAsync(task);
    }

    private async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception error)
        {
            _ctx.Logger.Error(error);
        }
    }

    /// <summary>
    /// Dispatch an event, running all listeners concurrently and awaiting each. Every listener
    /// runs; failures are aggregated into an <see cref="AggregateException"/>.
    /// </summary>
    public async Task Parallel(string name, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(name);
        var hooks = ResolveHooks("parallel", name, args);
        var failures = new List<Exception>();
        await Task.WhenAll(hooks.Select(async hook =>
        {
            try
            {
                var result = Invoke(name, hook, args);
                await AwaitResultAsync(result);
            }
            catch (Exception error)
            {
                lock (failures) failures.Add(error);
            }
        }));
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    /// <summary>
    /// Dispatch an event, awaiting listeners in registration order until one returns a bail value
    /// (non-null and not false, see <see cref="IsBailed"/>). Returns the first bail value, or
    /// <c>null</c> when none bailed.
    /// </summary>
    public async Task<object?> Serial(string name, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var hook in ResolveHooks("serial", name, args))
        {
            var result = await UnwrapResultAsync(Invoke(name, hook, args));
            if (IsBailed(result)) return result;
        }
        return null;
    }

    /// <summary>
    /// Dispatch an event synchronously, running listeners in registration order until one returns
    /// a bail value (non-null and not false, see <see cref="IsBailed"/>). Returns the first bail
    /// value, or <c>null</c> when none bailed. Listener promises are NOT awaited — a returned
    /// promise is itself a bail value.
    /// </summary>
    public object? Bail(string name, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var hook in ResolveHooks("bail", name, args))
        {
            var result = Invoke(name, hook, args);
            if (IsBailed(result)) return result;
        }
        return null;
    }

    /// <summary>
    /// Dispatch an event whose listeners receive a <c>next</c> continuation as their final
    /// argument (around-middleware, port of the TS waterfall).
    ///
    /// Listeners run outermost-first in registration order. Calling <c>next()</c> delegates to the
    /// remaining chain and returns the downstream result; returning without calling <c>next()</c>
    /// short-circuits the chain, including the innermost behavior. Values propagate through
    /// <c>next()</c>'s return value. Listener resolution is a per-dispatch snapshot, and
    /// <paramref name="next"/> is the innermost built-in behavior, invoked with no arguments
    /// (capture event arguments via closure).
    /// </summary>
    /// <typeparam name="TResult">the event's result type; every listener and <paramref name="next"/> share it.</typeparam>
    /// <param name="name">the event name.</param>
    /// <param name="args">the event arguments delivered to every listener.</param>
    /// <param name="next">the innermost behavior; runs only when the chain reaches it.</param>
    /// <returns>the outermost listener's return value.</returns>
    public TResult Waterfall<TResult>(string name, object?[] args, Func<TResult> next)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(next);
        var remaining = ResolveHooks("waterfall", name, args);

        TResult Next()
        {
            if (remaining.Count > 0)
            {
                var hook = remaining[0];
                remaining.RemoveAt(0);
                var listenerArgs = new object?[args.Length + 1];
                Array.Copy(args, listenerArgs, args.Length);
                listenerArgs[^1] = (Func<TResult>)Next;
                return (TResult)Invoke(name, hook, listenerArgs)!;
            }
            return next();
        }

        return Next();
    }

    /// <summary>
    /// Register an event listener owned by the current fiber; the registration is an effect, so
    /// the context removes the listener on dispose. The listener delegate's signature must match
    /// the event: it receives the dispatch arguments flattened (waterfall listeners additionally
    /// receive the <c>next</c> continuation as the final argument).
    /// </summary>
    /// <param name="name">the event name to listen for.</param>
    /// <param name="listener">the listener delegate.</param>
    /// <param name="options">placement and filtering options.</param>
    /// <returns>a disposer removing the listener; single-shot, no-op when already removed.</returns>
    /// <exception cref="CordisError">with code <see cref="CordisErrorCode.INACTIVE_EFFECT"/> when the fiber is disposed.</exception>
    public IDisposable On(string name, Delegate listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(listener);
        _ctx.Fiber.AssertActive();
        return RegisterHook(name, new Hook
        {
            Callback = listener,
            Prepend = options?.Prepend ?? false,
            Global = options?.Global ?? false,
        }, $"ctx.on(\"{name}\")");
    }

    /// <summary>Register a no-argument listener (equivalent to <see cref="On(string, Delegate, EventOptions?)"/>).</summary>
    public IDisposable On(string name, Action listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return On(name, (Delegate)listener, options);
    }

    /// <summary>
    /// Register a single-payload listener: receives the one dispatch argument as <typeparamref name="T"/>.
    /// </summary>
    public IDisposable On<T>(string name, Action<T> listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return On(name, (Delegate)new Action<object?>(payload => listener((T)payload!)), options);
    }

    /// <summary>
    /// Register a listener that disposes itself after its first invocation. Removing it before
    /// any dispatch is also supported.
    /// </summary>
    public IDisposable Once(string name, Delegate listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(listener);
        _ctx.Fiber.AssertActive();
        return RegisterHook(name, new Hook
        {
            Callback = listener,
            Prepend = options?.Prepend ?? false,
            Global = options?.Global ?? false,
            AutoRemove = true,
        }, $"ctx.once(\"{name}\")");
    }

    private IDisposable RegisterHook(string name, Hook hook, string label)
    {
        return _ctx.Fiber.Effect(() =>
        {
            var hooks = GetOrCreate(name);
            if (hook.Prepend) hooks.Insert(0, hook);
            else hooks.Add(hook);
            return new DisposableAction(() => hooks.Remove(hook));
        }, label);
    }

    private List<Hook> GetOrCreate(string name)
    {
        if (!_hooks.TryGetValue(name, out var hooks))
        {
            hooks = new List<Hook>();
            _hooks[name] = hooks;
        }
        return hooks;
    }

    /// <summary>
    /// Return whether an event result should stop a bail-style dispatch: true unless the value is
    /// <c>null</c> or <c>false</c> (port of the TS <c>isBailed</c>).
    /// </summary>
    public static bool IsBailed(object? value) => value is not null && value is not false;

    private static async Task AwaitResultAsync(object? result)
    {
        switch (result)
        {
            case Task task:
                await task;
                break;
            case ValueTask valueTask:
                await valueTask;
                break;
            default:
                // Boxed ValueTask<T> does not match `is ValueTask`; await through AsTask().
                var type = result?.GetType();
                if (type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    var asTask = type.GetMethod(nameof(ValueTask.AsTask), Type.EmptyTypes)?.Invoke(result, null) as Task;
                    if (asTask is not null) await asTask;
                }
                break;
        }
    }

    private static async Task<object?> UnwrapResultAsync(object? value)
    {
        if (value is Task task)
        {
            await task;
            return GetTaskResult(task);
        }
        if (value is ValueTask valueTask)
        {
            await valueTask;
            return null;
        }
        var type = value?.GetType();
        if (type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = type.GetMethod(nameof(ValueTask.AsTask), Type.EmptyTypes)?.Invoke(value, null) as Task;
            if (asTask is not null)
            {
                await asTask;
                return GetTaskResult(asTask);
            }
        }
        return value;
    }

    private static object? GetTaskResult(Task task)
    {
        var type = task.GetType();
        if (!type.IsGenericType) return null;
        return type.GetProperty(nameof(Task<object>.Result))?.GetValue(task);
    }
}



