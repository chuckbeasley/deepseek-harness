using Cordis.Core;
using Cordis.Cosmokit;
using System.Globalization;
using System.Text;

namespace Cordis.Logger.Console;

/// <summary>
/// Console log exporter (C# port of the vendored <c>logger-console</c>). Renders one line per
/// message as <c>[LEVEL] name message</c> with an optional local-time timestamp, mirroring the TS
/// <c>ConsoleExporter.render</c> layout and the Cordis.Core threshold rule: messages whose level
/// exceeds <see cref="IExporter.Level"/> are skipped. Constructing the exporter registers it with
/// the context logger as a fiber effect; context teardown removes it.
/// </summary>
public sealed class ConsoleExporter : IExporter
{
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _timeZone;
    private readonly ConsoleExporterConfig _config;
    private long _lastTs;

    /// <summary>
    /// Create the exporter, register it on the context logger, and start writing to
    /// <see cref="Out"/>. The registration is an effect on the current fiber, so context teardown
    /// (or the caller disposing the context) unregisters the exporter.
    /// </summary>
    /// <param name="ctx">the context whose logger receives messages.</param>
    /// <param name="config">exporter configuration; defaults are applied when omitted.</param>
    /// <param name="clock">clock used for the rendered timestamp and the diff seed (defaults to the system clock).</param>
    /// <param name="timeZone">zone the timestamp template is expanded in (defaults to the local zone, matching the TS local-time rendering).</param>
    public ConsoleExporter(Context ctx, ConsoleExporterConfig? config = null, TimeProvider? clock = null, TimeZoneInfo? timeZone = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _config = config ?? new ConsoleExporterConfig();
        _clock = clock ?? TimeProvider.System;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        _lastTs = _clock.GetUtcNow().ToUnixTimeMilliseconds(); // TS seeds the diff clock with Date.now()
        Out = System.Console.Out;
        ctx.Logger.Exporter(this);
    }

    /// <summary>The sink rendered lines are written to (defaults to standard output).</summary>
    public TextWriter Out { get; set; }

    /// <inheritdoc/>
    public LogLevel Level => _config.Level;

    /// <inheritdoc/>
    public void Export(LogMessage message)
    {
        Out.WriteLine(Render(message));
    }

    /// <summary>
    /// Render one message to a single line (port of the TS <c>render</c>): optional timestamp,
    /// <c>[LEVEL]</c> prefix, padded name label, and the formatted message with continuation
    /// indentation. The timestamp uses the render clock, matching the TS behavior of formatting
    /// the current time rather than the message time.
    /// </summary>
    /// <param name="message">the structured message to render.</param>
    /// <returns>the rendered line, including a trailing timestamp diff when configured.</returns>
    public string Render(LogMessage message)
    {
        var prefix = $"[{char.ToUpperInvariant(message.Type[0])}]";
        var space = new string(' ', _config.LabelMargin);
        var indent = prefix.Length + space.Length;
        var output = new StringBuilder();
        if (!string.IsNullOrEmpty(_config.TimeTemplate))
        {
            var time = Time.Template(_config.TimeTemplate, TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _timeZone).DateTime);
            indent += _config.TimeTemplate.Length;
            output.Append(_config.Colors ? AnsiColor.Wrap(time, 8, bold: false) : time);
        }
        var name = message.Name;
        var label = _config.Colors ? AnsiColor.Wrap(name, AnsiColor.NameCode(name), bold: true) : name;
        var pad = _config.LabelWidth + label.Length - name.Length;
        if (_config.LabelAlign == LabelAlignment.Right)
        {
            label = label.PadLeft(pad) + space + prefix + space;
            indent += _config.LabelWidth + space.Length;
        }
        else
        {
            label = prefix + space + label.PadRight(pad) + space;
        }
        output.Append(label);
        var body = MessageFormatter.Format(message, _config.MaxLength, _config.Colors);
        output.Append(body.Replace("\n", "\n" + new string(' ', indent)));
        if (_config.ShowDiff)
        {
            var diff = message.Ts - _lastTs;
            output.Append(" +").Append(Time.Format(diff));
        }
        _lastTs = message.Ts;
        return output.ToString();
    }
}
