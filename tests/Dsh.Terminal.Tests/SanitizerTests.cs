using Dsh.Terminal;

namespace Dsh.Terminal.Tests;

/// <summary>Pure behavior of the terminal sanitizer: sequence stripping and prompt-marker recognition.</summary>
public static class SanitizerTests
{
    public static void StripsCsiSequences()
    {
        var sanitizer = new TerminalSanitizer();
        var output = sanitizer.Sanitize("\u001b[31mred\u001b[0m");
        Assert.True(output == "red", $"CSI sequences are stripped, got \"{output}\"");
    }

    public static void StripsOscSequences_AndDetectsThePromptMarker()
    {
        var sanitizer = new TerminalSanitizer();
        var output = sanitizer.Sanitize("\u001b]0;title\u0007text");
        Assert.True(output == "text", $"OSC sequences are stripped, got \"{output}\"");
        Assert.False(sanitizer.IsPromptMarkerPending, "an unrelated OSC is not a prompt marker");
    }

    public static void PromptMarker_WithBelTerminator_IsDetected()
    {
        var sanitizer = new TerminalSanitizer();
        _ = sanitizer.Sanitize("\u001b]133;D;\u0007");
        Assert.True(sanitizer.IsPromptMarkerPending, "the 133;D marker with BEL is recognized");
        Assert.True(sanitizer.TakePromptMarker(), "the marker is consumed once");
        Assert.False(sanitizer.IsPromptMarkerPending, "a consumed marker is not pending");
    }

    public static void PromptMarker_WithEscStTerminator_IsDetected()
    {
        var sanitizer = new TerminalSanitizer();
        _ = sanitizer.Sanitize("\u001b]133;D;\u001b\\");
        Assert.True(sanitizer.IsPromptMarkerPending, "the 133;D marker with ESC \\ is recognized");
    }

    public static void PromptMarker_SplitAcrossChunks_CarriesState()
    {
        var sanitizer = new TerminalSanitizer();
        _ = sanitizer.Sanitize("\u001b]1");
        _ = sanitizer.Sanitize("33;D;\u001b");
        _ = sanitizer.Sanitize("\\");
        Assert.True(sanitizer.IsPromptMarkerPending, "a marker split across chunks is recognized");
    }

    public static void NormalizesCrLfAndRemovesBel()
    {
        var sanitizer = new TerminalSanitizer();
        var output = sanitizer.Sanitize("a\r\nb\rc\u0007d");
        Assert.True(output == "a\nb\ncd", $"CRLF/CR normalize and BEL is removed, got \"{output}\"");
    }

    public static void DropsShortEscapes()
    {
        var sanitizer = new TerminalSanitizer();
        var output = sanitizer.Sanitize("x\u001bc\u001b7y");
        Assert.True(output == "xy", $"short escapes are dropped, got \"{output}\"");
    }

    public static void ResetClearsMarkerState()
    {
        var sanitizer = new TerminalSanitizer();
        _ = sanitizer.Sanitize("\u001b]133;D;\u0007");
        sanitizer.Reset();
        Assert.False(sanitizer.IsPromptMarkerPending, "reset clears the marker state");
    }
}
