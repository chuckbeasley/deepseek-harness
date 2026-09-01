namespace Harness.Cordis.Core;

/// <summary>Numeric severity of a log message (port of the TS LoggerLevel); higher is more verbose.</summary>
public enum LogLevel
{
    /// <summary>Fatal/error severity.</summary>
    Error = 0,

    /// <summary>Informational severity.</summary>
    Info = 1,

    /// <summary>Warning severity.</summary>
    Warn = 2,

    /// <summary>Debug severity.</summary>
    Debug = 3,
}

/// <summary>
/// Minimal logging surface for containment diagnostics (Phase 0; Microsoft.Extensions.Logging is a
/// Phase 1 decision).
/// </summary>
public interface ILogger
{
    /// <summary>Log at debug severity.</summary>
    void Debug(string message);

    /// <summary>Log at info severity.</summary>
    void Info(string message);

    /// <summary>Log at warn severity.</summary>
    void Warn(string message);

    /// <summary>Log at error severity.</summary>
    void Error(string message);

    /// <summary>Log an exception at error severity.</summary>
    void Error(Exception exception);
}

/// <summary>
/// Structured log record delivered to exporters (port of the TS Logger Message).
/// </summary>
/// <param name="Sn">per-service message sequence number.</param>
/// <param name="Ts">Unix epoch milliseconds of the message.</param>
/// <param name="Name">logger name.</param>
/// <param name="Type">severity category: "error", "info", "warn", or "debug".</param>
/// <param name="Level">numeric severity.</param>
/// <param name="Args">raw message arguments (a string, an exception, or another object).</param>
public sealed record LogMessage(int Sn, long Ts, string Name, string Type, LogLevel Level, IReadOnlyList<object?> Args);

/// <summary>Sink that receives structured log messages (port of the TS Logger Exporter).</summary>
public interface IExporter
{
    /// <summary>
    /// Highest message level this exporter receives; messages with a larger level are skipped
    /// (port of the TS threshold rule, including its naming: a higher level is more verbose).
    /// </summary>
    LogLevel Level { get; }

    /// <summary>Receive one log message.</summary>
    void Export(LogMessage message);
}

/// <summary>Named logger facade bound to one <see cref="LoggerService"/>.</summary>
public sealed class Logger : ILogger
{
    private readonly LoggerService _service;

    internal Logger(LoggerService service, string name)
    {
        _service = service;
        Name = name;
    }

    /// <summary>The logger name shown with each message.</summary>
    public string Name { get; }

    /// <inheritdoc cref="ILogger.Debug"/>
    public void Debug(string message) => _service.Log(LogLevel.Debug, Name, message);

    /// <inheritdoc cref="ILogger.Info"/>
    public void Info(string message) => _service.Log(LogLevel.Info, Name, message);

    /// <inheritdoc cref="ILogger.Warn"/>
    public void Warn(string message) => _service.Log(LogLevel.Warn, Name, message);

    /// <inheritdoc cref="ILogger.Error(string)"/>
    public void Error(string message) => _service.Log(LogLevel.Error, Name, message);

    /// <inheritdoc cref="ILogger.Error(Exception)"/>
    public void Error(Exception exception) => _service.Log(LogLevel.Error, Name, exception);
}

/// <summary>
/// Built-in logging service installed as <c>ctx.logger</c> (C# port of the vendored Cordis
/// LoggerService). Call <see cref="Logger"/> for a named facade or use the severity methods
/// directly (they log under the root fiber name). A ring-buffer exporter captures the last
/// <see cref="BufferSize"/> messages; additional exporters register as effects.
/// </summary>
public sealed class LoggerService : ILogger
{
    private readonly Context _ctx;
    private readonly List<IExporter> _exporters = new();
    private readonly List<LogMessage> _buffer = new();
    private int _sn;
    private readonly string _defaultName;

    internal LoggerService(Context ctx)
    {
        _ctx = ctx;
        _defaultName = "root"; // the root fiber name, per the TS fiber-derived default
        _exporters.Add(new BufferExporter(this));
    }

    /// <summary>Ring-buffer size of the default exporter (TS default: 1000).</summary>
    public int BufferSize { get; set; } = 1000;

    /// <summary>Messages captured by the default ring-buffer exporter, oldest first.</summary>
    public IReadOnlyList<LogMessage> Buffer => _buffer;

    /// <summary>Create a named logger facade.</summary>
    public Logger Logger(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new Logger(this, name);
    }

    /// <summary>
    /// Register an exporter owned by the current fiber; disposing the returned disposer (or the
    /// context) removes it.
    /// </summary>
    public IDisposable Exporter(IExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        return _ctx.Fiber.Effect(() =>
        {
            _exporters.Add(exporter);
            return new DisposableAction(() => _exporters.Remove(exporter));
        }, "ctx.logger.exporter()");
    }

    /// <inheritdoc cref="ILogger.Debug"/>
    public void Debug(string message) => Log(LogLevel.Debug, _defaultName, message);

    /// <inheritdoc cref="ILogger.Info"/>
    public void Info(string message) => Log(LogLevel.Info, _defaultName, message);

    /// <inheritdoc cref="ILogger.Warn"/>
    public void Warn(string message) => Log(LogLevel.Warn, _defaultName, message);

    /// <inheritdoc cref="ILogger.Error(string)"/>
    public void Error(string message) => Log(LogLevel.Error, _defaultName, message);

    /// <inheritdoc cref="ILogger.Error(Exception)"/>
    public void Error(Exception exception) => Log(LogLevel.Error, _defaultName, exception);

    internal void Log(LogLevel level, string name, object? arg)
    {
        var message = new LogMessage(
            ++_sn,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            name,
            TypeName(level),
            level,
            new[] { arg });
        foreach (var exporter in _exporters)
        {
            if (exporter.Level < level) continue; // TS: skip when the exporter threshold is below the message level
            exporter.Export(message);
        }
    }

    private static string TypeName(LogLevel level) => level switch
    {
        LogLevel.Error => "error",
        LogLevel.Info => "info",
        LogLevel.Warn => "warn",
        _ => "debug",
    };

    private sealed class BufferExporter : IExporter
    {
        private readonly LoggerService _service;

        public BufferExporter(LoggerService service) => _service = service;

        public LogLevel Level => LogLevel.Debug;

        public void Export(LogMessage message)
        {
            _service._buffer.Add(message);
            if (_service._buffer.Count > _service.BufferSize)
            {
                _service._buffer.RemoveRange(0, _service._buffer.Count - _service.BufferSize);
            }
        }
    }
}

