using Dsh.Web.App;
using Dsh.Web.App.Slots;
using Microsoft.AspNetCore.Components;

namespace Dsh.Ui.Chat;

/// <summary>
/// The composer contribution (port of the TS ui-chat composer): the message input and submit
/// button, rendered into the shell's <c>chat.composer</c> slot. Submission publishes the text
/// through <see cref="ShellBus"/>; the chat page owns the loop turn. The form keeps the shell's
/// <c>dsh-input-row</c> class so the interactive smoke and styles stay stable.
/// </summary>
public static class UiChatPlugin
{
    /// <summary>Register the composer contribution; the returned disposer withdraws it.</summary>
    public static IDisposable Apply(SlotRegistry slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        return slots.Register("chat.composer", 10, () => FragmentFor<ChatComposer>());
    }

    private static RenderFragment FragmentFor<TComponent>() where TComponent : IComponent
        => builder =>
        {
            builder.OpenComponent<TComponent>(0);
            builder.CloseComponent();
        };
}
