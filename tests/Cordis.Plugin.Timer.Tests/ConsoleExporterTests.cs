using Cordis.Core;
using Cordis.Logger.Console;

namespace Cordis.Plugin.Timer.Tests;

/// <summary>
/// Behavioral tests for the console exporter port: threshold filtering, effect-based
/// registration/disposal, and byte-for-byte render stability.
/// </summary>
public static class ConsoleExporterTests
{
    /// <summary>An exporter at the default Info threshold receives error and info, skipping warn and debug.</summary>
    public static async Task Exporter_RespectsLevelThreshold()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { TimeTemplate = null }) { Out = writer };
            ctx.Logger.Debug("debug-line");
            ctx.Logger.Info("info-line");
            ctx.Logger.Warn("warn-line");
            ctx.Logger.Error("error-line");
            var lines = Lines(writer);
            Assert.Equal(2, lines.Length, "info threshold keeps error+info only");
            Assert.Equal("[I] root info-line", lines[0]);
            Assert.Equal("[E] root error-line", lines[1]);
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>An exporter at the Debug threshold receives every level, in logging order.</summary>
    public static async Task Exporter_WithDebugLevel_ReceivesEverything()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { Level = LogLevel.Debug, TimeTemplate = null }) { Out = writer };
            ctx.Logger.Debug("debug-line");
            ctx.Logger.Info("info-line");
            ctx.Logger.Warn("warn-line");
            ctx.Logger.Error("error-line");
            var lines = Lines(writer);
            Assert.Equal(4, lines.Length);
            Assert.Equal("[D] root debug-line", lines[0]);
            Assert.Equal("[I] root info-line", lines[1]);
            Assert.Equal("[W] root warn-line", lines[2]);
            Assert.Equal("[E] root error-line", lines[3]);
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Disposing the registration returned by the logger removes the exporter.</summary>
    public static async Task Exporter_RegistrationDisposer_RemovesExporter()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            var exporter = new CollectingExporter(writer);
            var registration = ctx.Logger.Exporter(exporter);
            ctx.Logger.Info("one");
            registration.Dispose();
            ctx.Logger.Info("two");
            var text = writer.ToString();
            Assert.True(text.Contains("one", StringComparison.Ordinal), "first message exported");
            Assert.False(text.Contains("two", StringComparison.Ordinal), "disposed exporter must not receive further messages");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Context teardown unregisters a constructor-registered exporter.</summary>
    public static async Task Exporter_ContextDispose_UnregistersExporter()
    {
        var ctx = new Context();
        var writer = new StringWriter();
        _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { TimeTemplate = null }) { Out = writer };
        ctx.Logger.Info("before");
        await ctx.DisposeAsync();
        ctx.Logger.Info("after");
        var text = writer.ToString();
        Assert.True(text.Contains("before", StringComparison.Ordinal), "message before teardown exported");
        Assert.False(text.Contains("after", StringComparison.Ordinal), "exporter removed by teardown");
    }

    /// <summary>The default render is a stable plain-text line without a timestamp.</summary>
    public static async Task Exporter_FormatStability_PlainLine()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { TimeTemplate = null }) { Out = writer };
            ctx.Logger.Logger("test").Info("hello");
            Assert.Equal("[I] test hello", writer.ToString().TrimEnd('\r', '\n'));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>The default render prepends the local-time template before the level prefix.</summary>
    public static async Task Exporter_FormatStability_WithTimestamp()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            var clock = new FixedTimeProvider(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero));
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig(), clock, TimeZoneInfo.Utc) { Out = writer };
            ctx.Logger.Logger("test").Info("hello");
            Assert.Equal("2024-01-02 03:04:05 [I] test hello", writer.ToString().TrimEnd('\r', '\n'));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Multi-line messages indent continuation lines to the prefix width.</summary>
    public static async Task Exporter_MultiLineMessage_IndentsContinuation()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { TimeTemplate = null }) { Out = writer };
            ctx.Logger.Logger("test").Info("line1\nline2");
            Assert.Equal("[I] test line1\n    line2", writer.ToString().TrimEnd('\r', '\n'));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Long lines truncate to the configured maximum length with a trailing ellipsis.</summary>
    public static async Task Exporter_MaxLength_TruncatesPerLine()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { TimeTemplate = null, MaxLength = 8 }) { Out = writer };
            ctx.Logger.Logger("test").Info("abcdefghij");
            Assert.Equal("[I] test abcdefgh...", writer.ToString().TrimEnd('\r', '\n'));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>With showDiff enabled each line appends the compact duration since the previous message.</summary>
    public static async Task Exporter_ShowDiff_AppendsDuration()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { TimeTemplate = null, ShowDiff = true }) { Out = writer };
            ctx.Logger.Info("first");
            ctx.Logger.Info("second");
            var lines = Lines(writer);
            Assert.Equal(2, lines.Length);
            Assert.True(lines[0].Contains(" +", StringComparison.Ordinal), $"line 1 shows a diff: {lines[0]}");
            Assert.True(lines[0].EndsWith("ms", StringComparison.Ordinal), $"line 1 diff is a compact duration: {lines[0]}");
            Assert.True(lines[1].Contains(" +", StringComparison.Ordinal), $"line 2 shows a diff: {lines[1]}");
            Assert.True(lines[1].EndsWith("ms", StringComparison.Ordinal), $"line 2 diff is a compact duration: {lines[1]}");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Colors are opt-in; the default output carries no escape sequences.</summary>
    public static async Task Exporter_Colors_EmitsAnsiOnlyWhenEnabled()
    {
        var ctx = new Context();
        try
        {
            var plain = new StringWriter();
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { TimeTemplate = null }) { Out = plain };
            ctx.Logger.Logger("test").Info("plain");
            Assert.False(plain.ToString().Contains("\u001b", StringComparison.Ordinal), "plain output has no ANSI codes");

            var colored = new StringWriter();
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { TimeTemplate = null, Colors = true }) { Out = colored };
            ctx.Logger.Logger("test").Info("colored");
            var text = colored.ToString();
            Assert.True(text.Contains("\u001b[38;5;", StringComparison.Ordinal), "colored output uses 256-color codes");
            Assert.True(text.Contains("\u001b[0m", StringComparison.Ordinal), "colored output resets after the label");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Exception arguments render their full text (type, message, stack).</summary>
    public static async Task Exporter_Exception_RendersFullText()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            _ = new ConsoleExporter(ctx, new ConsoleExporterConfig { TimeTemplate = null }) { Out = writer };
            ctx.Logger.Logger("test").Error(new InvalidOperationException("boom"));
            var text = writer.ToString();
            Assert.True(text.Contains("InvalidOperationException: boom", StringComparison.Ordinal), $"exception text rendered: {text}");
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    /// <summary>Right-aligned labels sit before the level prefix, padded to the configured width.</summary>
    public static async Task Exporter_RightAlignedLabel_MatchesVendoredLayout()
    {
        var ctx = new Context();
        try
        {
            var writer = new StringWriter();
            var config = new ConsoleExporterConfig
            {
                TimeTemplate = null,
                LabelAlign = LabelAlignment.Right,
                LabelWidth = 6,
            };
            _ = new ConsoleExporter(ctx, config) { Out = writer };
            ctx.Logger.Logger("test").Info("hello");
            Assert.Equal("  test [I] hello", writer.ToString().TrimEnd('\r', '\n'));
        }
        finally
        {
            await ctx.DisposeAsync();
        }
    }

    private static string[] Lines(StringWriter writer)
    {
        return writer.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Exporter capturing rendered lines for registration/disposal assertions.</summary>
    private sealed class CollectingExporter : IExporter
    {
        private readonly TextWriter _writer;

        public CollectingExporter(TextWriter writer) => _writer = writer;

        public LogLevel Level => LogLevel.Debug;

        public void Export(LogMessage message) => _writer.WriteLine($"{message.Type}:{message.Name}:{string.Join(" ", message.Args)}");
    }

    /// <summary>Clock pinned to a fixed instant so rendered timestamps are deterministic.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
    }
}
