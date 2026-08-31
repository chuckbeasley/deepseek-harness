namespace Dsh.Web.App;

/// <summary>
/// The shell's shared viewing state (scoped per circuit): the selected session entry. The chat
/// page and the session-list contribution read and change it through the same scope, so a click
/// in the list and the transcript always agree. The store remains the data owner; this is
/// selection-only presentation state.
/// </summary>
public sealed class ShellState
{
    private Store.WebSessionEntry? _selected;

    /// <summary>Raised after every selection change; the transcript and list re-render through it.</summary>
    public event Action? Changed;

    /// <summary>The selected session entry, or <c>null</c> when none is selected.</summary>
    public Store.WebSessionEntry? Selected => _selected;

    /// <summary>Select one entry (or <c>null</c> to clear the selection).</summary>
    public void Select(Store.WebSessionEntry? entry)
    {
        _selected = entry;
        Changed?.Invoke();
    }
}
