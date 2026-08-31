using Dsh.Web.App;
using Dsh.Web.App.Slots;
using Microsoft.AspNetCore.Components;

namespace Dsh.Ui.Sidebar;

/// <summary>
/// The sidebar chrome contribution (port of the TS ui-sidebar shell controls): the brand row and
/// the New Session action, rendered into the shell's <c>sidebar</c> slot. The action publishes the
/// new-session gesture through <see cref="ShellBus"/>; the chat page owns the reaction.
/// </summary>
public static class UiSidebarPlugin
{
    /// <summary>Register the sidebar contributions; the returned disposer withdraws them.</summary>
    public static IDisposable Apply(SlotRegistry slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var disposers = new List<IDisposable>
        {
            slots.Register("sidebar", 10, () => FragmentFor<BrandRow>()),
            slots.Register("sidebar", 20, () => FragmentFor<NewSessionButton>()),
        };
        return new CompositeDisposer(disposers.ToArray());
    }

    private static RenderFragment FragmentFor<TComponent>() where TComponent : IComponent
        => builder =>
        {
            builder.OpenComponent<TComponent>(0);
            builder.CloseComponent();
        };

    private sealed class CompositeDisposer : IDisposable
    {
        private readonly IDisposable[] _disposers;

        public CompositeDisposer(IDisposable[] disposers)
        {
            _disposers = disposers;
        }

        public void Dispose()
        {
            foreach (var disposer in _disposers) disposer.Dispose();
        }
    }
}
