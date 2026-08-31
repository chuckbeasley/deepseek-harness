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
    string HubPath = "/hub");

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

    /// <summary>Start the Kestrel host: build, wire the hub and the mounted shell, listen.</summary>
    public override async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.WebHost.UseUrls($"http://{_config.Host}:{_config.Port}");
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new CordisLoggerProvider(Ctx));
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(Ctx);
        var registry = Ctx.Get<DshRpcRegistry>("rpc");
        if (registry is not null) builder.Services.AddSingleton(registry);
        _configure(builder);
        var app = builder.Build();
        app.UseWebSockets();
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
