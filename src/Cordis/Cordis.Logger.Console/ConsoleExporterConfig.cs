using Harness.Cordis.Core;
namespace Harness.Cordis.Logger.Console;

/// <summary>Horizontal placement of the logger-name label (port of the TS <c>label.align</c>).</summary>
public enum LabelAlignment
{
    /// <summary>Label sits after the level prefix: <c>[I] name message</c>.</summary>
    Left,

    /// <summary>Label sits before the level prefix, padded to <see cref="ConsoleExporterConfig.LabelWidth"/>.</summary>
    Right,
}

/// <summary>
/// Configuration for <see cref="ConsoleExporter"/> (port of the TS <c>ConsoleExporter.Config</c>).
/// All rendering knobs are config fields with the vendored defaults.
/// </summary>
public sealed record ConsoleExporterConfig
{
    /// <summary>
    /// Highest message level this exporter receives; messages with a larger level are skipped
    /// (the Harness.Cordis.Core threshold rule). Default <see cref="LogLevel.Info"/>, the vendored
    /// default threshold.
    /// </summary>
    public LogLevel Level { get; init; } = LogLevel.Info;

    /// <summary>
    /// Timestamp template rendered before each line; <c>null</c> or empty disables the timestamp
    /// (TS default <c>"yyyy-MM-dd hh:mm:ss "</c>, expanded in local time).
    /// </summary>
    public string? TimeTemplate { get; init; } = "yyyy-MM-dd hh:mm:ss ";

    /// <summary>Append a compact duration since the previous message (TS <c>showDiff</c>, default false).</summary>
    public bool ShowDiff { get; init; }

    /// <summary>
    /// Emit ANSI colors for the timestamp, the level prefix, and the logger-name label. Disabled
    /// by default so the output is plain-text-safe; the TS Node build auto-detects terminal color
    /// support instead.
    /// </summary>
    public bool Colors { get; init; }

    /// <summary>Maximum characters per rendered line before truncation with <c>...</c> (TS default 10240).</summary>
    public int MaxLength { get; init; } = 10240;

    /// <summary>Target width of the logger-name label (TS <c>label.width</c>, default 0).</summary>
    public int LabelWidth { get; init; }

    /// <summary>Spaces between the label and the message (TS <c>label.margin</c>, default 1).</summary>
    public int LabelMargin { get; init; } = 1;

    /// <summary>Label placement (TS <c>label.align</c>, default left).</summary>
    public LabelAlignment LabelAlign { get; init; } = LabelAlignment.Left;
}
