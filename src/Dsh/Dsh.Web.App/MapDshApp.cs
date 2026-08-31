using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Dsh.Web.App;

/// <summary>
/// The Blazor shell wiring (the Phase-5 web app): mounts the Razor components with server
/// interactivity, the slot registry, and the web session store onto a host application. The
/// shell's components consume the ported seams directly (Blazor Server renders on the host), and
/// the RPC gateway serves remote clients.
/// </summary>
public static class DshWebApp
{
    /// <summary>Mount the shell onto the host application.</summary>
    /// <param name="app">the host application (after the gateway hub is mapped).</param>
    public static void MapDshApp(this WebApplication app)
    {
        app.UseStaticFiles();
        app.UseAntiforgery();
        app.Use(async (http, next) =>
        {
            // Negotiate once per request so the prerendered shell and the circuit that replaces
            // it can pin the same language (the page persists it across the boundary).
            http.Items[WebLocale.ItemsKey] = WebLocale.Negotiate(http.Request.Headers.AcceptLanguage);
            await next();
        });
        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();
    }

    /// <summary>Register the shell services: Razor components, the slot registry, the web session store.</summary>
    public static IServiceCollection AddDshApp(this IServiceCollection services)
    {
        services.AddRazorComponents()
            .AddInteractiveServerComponents();
        services.AddHttpContextAccessor();
        services.AddSingleton<Slots.SlotRegistry>();
        services.AddSingleton<Store.WebSessionStore>();
        services.AddScoped<LocaleScope>();
        services.AddScoped(sp => new WebLocale(sp.GetRequiredService<LocaleScope>()));
        return services;
    }
}
