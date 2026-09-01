using Harness.Web.App.Slots;
using Microsoft.AspNetCore.Components;

namespace Harness.Ui.Sessions;

/// <summary>
/// The session-list contribution (port of the TS ui-session list): every live session with its
/// running/queued state, rendered into the shell's <c>sessions</c> slot. Selection travels
/// through the shared <see cref="Harness.Web.App.ShellState"/>, so the list and the transcript agree.
/// </summary>
public static class UiSessionsPlugin
{
    /// <summary>Register the session-list contribution; the returned disposer withdraws it.</summary>
    public static IDisposable Apply(SlotRegistry slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return slots.Register("sessions", 10, () => FragmentFor<SessionList>());
    }

    private static RenderFragment FragmentFor<TComponent>() where TComponent : IComponent
        => builder =>
        {
            builder.OpenComponent<TComponent>(0);
            builder.CloseComponent();
        };
}
