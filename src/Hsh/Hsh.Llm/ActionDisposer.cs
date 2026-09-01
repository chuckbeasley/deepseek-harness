namespace Harness.Llm;

/// <summary>Minimal <see cref="IDisposable"/> built from an action; used for sync effect cleanups.</summary>
public sealed class ActionDisposer : IDisposable
{
    private readonly Action _action;

    public ActionDisposer(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}
