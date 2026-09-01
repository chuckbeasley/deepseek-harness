namespace Harness.Web.App;

/// <summary>
/// The shell's cross-component action bus (scoped per circuit): slot contributions publish
/// user gestures (new session, send) and the chat page handles them, so the composer and the
/// sidebar chrome never need to know the page's internals. Events are plain .NET events on the
/// scoped instance; the page subscribes in <c>OnInitialized</c> and unsubscribes on dispose.
/// </summary>
public sealed class ShellBus
{
    /// <summary>Raised when the user asks for a fresh session (the sidebar action).</summary>
    public event Action? NewSessionRequested;

    /// <summary>Raised when the user submits one message from the composer.</summary>
    public event Action<string>? SendRequested;

    /// <summary>Publish the new-session gesture.</summary>
    public void RequestNewSession() => NewSessionRequested?.Invoke();

    /// <summary>Publish one submitted message.</summary>
    public void RequestSend(string text) => SendRequested?.Invoke(text);
}
