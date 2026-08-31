using Cordis.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Dsh.Web.Host;

/// <summary>Deployment-varying web host config; no tunable is hardcoded.</summary>
public sealed record WebHostConfig(
    /// <summary>Listen address; loopback by default.</summary>
    string Host = "127.0.0.1",
    /// <summary>Listen port; the TS GUI default.</summary>
    int Port = 3080,
    /// <summary>Hub route prefix (the gateway hub lives under <c>/hub</c>).</summary>
    string HubPath = "/hub",
    /// <summary>Whether the loopback process-token fence gates the index and every API surface (the TS fence shape).</summary>
    bool AuthFence = true,
    /// <summary>Non-loopback authorities this deployment serves; each entry must be a bare
    /// <c>host</c> or <c>host:port</c> authority (validated loud at host start).</summary>
    IReadOnlyList<string>? TrustedHosts = null);

/// <summary>
/// The web host service (ctx.webHost): owns the Kestrel lifetime of the Phase-5 host — the
/// gateway hub and the mounted app shell. The Cordis context is published into the ASP.NET DI
/// container so endpoints resolve the ported seams directly; teardown stops the server and awaits
/// its shutdown.
/// </summary>
public sealed class WebHostService : Service
{
    private readonly WebHostConfig _config;
    private readonly Action<WebApplicationBuilder> _configure;
    private readonly Action<WebApplication> _map;
    private WebApplication? _app;

    /// <summary>Create and register the host under the <c>webHost</c> key.</summary>
    public WebHostService(
        Context ctx,
        WebHostConfig? config = null,
        Action<WebApplicationBuilder>? configure = null,
        Action<WebApplication>? map = null)
        : base(ctx, "webHost")
    {
        _config = config ?? new WebHostConfig();
        _configure = configure ?? (_ => { });
        _map = map ?? (_ => { });
    }

    /// <summary>The bound listen address once started, or <c>null</c> before start.</summary>
    public string? ListenUrl { get; private set; }

    /// <summary>The auth fence when the config enables it; the caller prints <see cref="WebAuthFence.AuthenticatedUrl"/>.</summary>
    public WebAuthFence? Fence { get; private set; }

    /// <summary>Start the Kestrel host: build, wire the hub and the mounted shell, listen.</summary>
    public override async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            // The web app's wwwroot (and published Blazor assets) live next to the dsh binary,
            // not in whatever directory the launcher happened to run from.
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls($"http://{_config.Host}:{_config.Port}");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new CordisLoggerProvider(Ctx));
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(Ctx);
        if (_config.AuthFence)
        {
            var trustedHosts = ResolveLanTrust(_config.Host, _config.TrustedHosts ?? Array.Empty<string>());
            foreach (var entry in trustedHosts)
            {
                // A malformed grant must fail the boot like the TS plugin load, not sit ignored
                // until requests 403 or quietly broaden.
                WebAuthFence.AssertTrustedAuthority(entry);
            }
            Fence = new WebAuthFence(signingSecret: ResolveSigningSecret(Ctx), trustedHosts: trustedHosts);
            builder.Services.AddSingleton(Fence);
        }
        var registry = Ctx.Get<DshRpcRegistry>("rpc");
        if (registry is not null) builder.Services.AddSingleton(registry);
        _configure(builder);
        var app = builder.Build();
        app.UseWebSockets();
        if (Fence is not null)
        {
            app.UseFence(Fence, _config.HubPath);
        }
        if (registry is not null)
        {
            app.MapGateway(registry);
            app.MapMux(registry, Ctx);
        }
        app.MapHub<DshRpcHub>(_config.HubPath);
        _map(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var address = app.Urls.First();
        ListenUrl = address;
        _app = app;
    }

    /// <summary>Stop the Kestrel host and await its shutdown.</summary>
    public override async ValueTask StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is not null)
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        await base.StopAsync();
    }

    /// <summary>
    /// Resolve one LAN-trust snapshot from the active server bind (port of the TS
    /// <c>resolveLanTrust</c>): binding the all-interfaces host derives the machine's non-loopback
    /// IPv4 literals as port-less trusted authorities — DNS rebinding needs an attacker-controlled
    /// name, while an IP-literal Host is safe on any port and an OS-assigned port is unknowable
    /// before bind. Explicitly configured entries follow the derived ones, in config order.
    /// </summary>
    /// <param name="bindHost">the active webHost bind host.</param>
    /// <param name="trustedHosts">the explicitly configured trustedHosts entries.</param>
    /// <returns>the fence authorities: derived LAN literals first, then the configured entries.</returns>
    public static IReadOnlyList<string> ResolveLanTrust(string bindHost, IReadOnlyList<string> trustedHosts)
    {
        ArgumentNullException.ThrowIfNull(trustedHosts);
        if (bindHost != "0.0.0.0") return trustedHosts;
        var addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(iface => iface.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(iface => iface.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !System.Net.IPAddress.IsLoopback(address.Address))
            .Select(address => address.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return addresses.Concat(trustedHosts).ToArray();
    }

    /// <summary>
    /// Resolve the cookie signing secret through the credentials seam when one is composed: read
    /// the <c>DSH_WEB_SESSION_SECRET</c> reference (the environment or the managed store), or
    /// create a fresh 32-byte value in the managed store, so cookies survive host restarts like
    /// the TS credential record. Without a credentials seam the fence falls back to a per-instance
    /// random secret (cookies die with the host).
    /// </summary>
    private static byte[]? ResolveSigningSecret(Context ctx)
    {
        const string secretRef = "DSH_WEB_SESSION_SECRET";
        var credentials = ctx.Get<Dsh.Credentials.ICredentialsService>("credentials");
        if (credentials is null) return null;
        var resolved = credentials.ResolveAsync(secretRef).GetAwaiter().GetResult();
        if (resolved is not null)
        {
            return WebAuthFence.DecodeSecret(resolved.Value)
                ?? throw new InvalidOperationException(
                    $"web: the {secretRef} credential must be a {WebAuthFence.SecretBytes}-byte base64url value");
        }
        var created = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(WebAuthFence.SecretBytes))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        credentials.SetAsync(secretRef, created).GetAwaiter().GetResult();
        return WebAuthFence.DecodeSecret(created)!;
    }
}

/// <summary>Route Cordis log messages into the ASP.NET logging pipeline.</summary>
internal sealed class CordisLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly Context _ctx;

    public CordisLoggerProvider(Context ctx)
    {
        _ctx = ctx;
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CordisLogger(_ctx, categoryName);

    public void Dispose()
    {
    }

    private sealed class CordisLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly Context _ctx;
        private readonly string _category;

        public CordisLogger(Context ctx, string category)
        {
            _ctx = ctx;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            switch (logLevel)
            {
                case LogLevel.Error:
                case LogLevel.Critical:
                    _ctx.Logger.Error($"[web] {_category}: {message}");
                    break;
                case LogLevel.Warning:
                    _ctx.Logger.Warn($"[web] {_category}: {message}");
                    break;
                case LogLevel.Debug:
                case LogLevel.Trace:
                    _ctx.Logger.Debug($"[web] {_category}: {message}");
                    break;
                default:
                    _ctx.Logger.Info($"[web] {_category}: {message}");
                    break;
            }
        }
    }
}
