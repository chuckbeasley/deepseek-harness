using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Dsh.Web.App.Slots;

/// <summary>
/// One UI plugin contribution to a named slot (port of the ui-slots registration): the slot name,
/// the render order, and the fragment factory.
/// </summary>
public sealed class SlotRegistration
{
    internal SlotRegistration(string slot, int order, Func<RenderFragment> factory, int sequence)
    {
        Slot = slot;
        Order = order;
        Factory = factory;
        Sequence = sequence;
    }

    /// <summary>The slot this contribution renders into.</summary>
    public string Slot { get; }

    /// <summary>Render order; lower renders first.</summary>
    public int Order { get; }

    /// <summary>The fragment factory (a component type or inline markup).</summary>
    public Func<RenderFragment> Factory { get; }

    /// <summary>Registration-order tiebreak for stable rendering.</summary>
    internal int Sequence { get; }
}

/// <summary>
/// The slot registry (port of the ui-slots registry): named slots with ordered contributions.
/// Registrations are scoped to the component tree; disposal withdraws the exact contribution.
/// </summary>
public sealed class SlotRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<SlotRegistration>> _slots = new(StringComparer.Ordinal);
    private int _sequence;

    /// <summary>
    /// Register one contribution. One slot may host many contributions, ordered by
    /// <see cref="SlotRegistration.Order"/>.
    /// </summary>
    /// <returns>the disposer that withdraws the contribution.</returns>
    public IDisposable Register(string slot, int order, Func<RenderFragment> factory)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(factory);
        var registration = new SlotRegistration(slot, order, factory, ++_sequence);
        lock (_gate)
        {
            if (!_slots.TryGetValue(slot, out var list))
            {
                list = new List<SlotRegistration>();
                _slots[slot] = list;
            }
            list.Add(registration);
            list.Sort((left, right) => left.Order != right.Order
                ? left.Order.CompareTo(right.Order)
                : left.Sequence.CompareTo(right.Sequence));
        }
        return new ActionDisposer(() =>
        {
            lock (_gate)
            {
                if (_slots.TryGetValue(slot, out var current))
                {
                    current.Remove(registration);
                    if (current.Count == 0) _slots.Remove(slot);
                }
            }
        });
    }

    /// <summary>Every registered slot name, in first-registration order.</summary>
    public IReadOnlyList<string> Names()
    {
        lock (_gate) return _slots.Keys.ToArray();
    }

    /// <summary>Ordered contributions for one slot (empty when none are registered).</summary>
    public IReadOnlyList<SlotRegistration> Get(string slot)
    {
        lock (_gate) return _slots.TryGetValue(slot, out var list) ? list.ToArray() : Array.Empty<SlotRegistration>();
    }
}

/// <summary>Minimal <see cref="IDisposable"/> built from an action; used for sync cleanups.</summary>
internal sealed class ActionDisposer : IDisposable
{
    private readonly Action _action;

    public ActionDisposer(Action action)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
    }

    public void Dispose() => _action();
}
