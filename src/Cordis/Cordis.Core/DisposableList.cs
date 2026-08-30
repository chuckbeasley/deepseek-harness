namespace Cordis.Core;

/// <summary>
/// Ordered collection with removal by value (port of the vendored Cordis DisposableList).
/// <see cref="Clear"/> yields the items in reverse insertion order, which is the order a fiber
/// uses to unwind its effects.
/// </summary>
internal sealed class DisposableList<T> : IEnumerable<T>
{
    private readonly List<T> _items = new();

    /// <summary>Number of stored items.</summary>
    public int Count => _items.Count;

    /// <summary>Append an item and return a function that removes it.</summary>
    public Action Push(T value)
    {
        _items.Add(value);
        return () => _items.Remove(value);
    }

    /// <summary>Remove the first occurrence of an item; returns whether it was found.</summary>
    public bool Remove(T value) => _items.Remove(value);

    /// <summary>Item at a position (insertion order).</summary>
    public T this[int index] => _items[index];

    /// <summary>Remove the item at a position.</summary>
    public void RemoveAt(int index) => _items.RemoveAt(index);

    /// <summary>Index of the first occurrence of an item, or -1.</summary>
    public int IndexOf(T value) => _items.IndexOf(value);

    /// <summary>
    /// Clear the list; returns the items in reverse insertion order (the fiber unload order).
    /// </summary>
    public IReadOnlyList<T> Clear()
    {
        var reversed = _items.ToList();
        reversed.Reverse();
        _items.Clear();
        return reversed;
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Minimal <see cref="IDisposable"/> built from an action; used for sync effect cleanups.</summary>
internal sealed class DisposableAction : IDisposable
{
    private readonly Action _action;

    public DisposableAction(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}

