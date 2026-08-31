using System.Diagnostics;

namespace Dsh.Web.Host;

/// <summary>
/// The native-opener seam for the settings openers (port of the TS SettingsControllerInternals):
/// opening a directory or a text document through the platform desktop handler, and whether one
/// exists at all. Tests inject fakes; production uses the shell-open default.
/// </summary>
public sealed record SettingsOpeners(
    /// <summary>Open one directory through the platform handler (Explorer / xdg-open).</summary>
    Func<string, Task> OpenPath,
    /// <summary>Open one text document through the platform handler (the default editor).</summary>
    Func<string, Task> OpenTextFile,
    /// <summary>Whether a native opener exists in this deployment.</summary>
    bool CanOpen)
{
    /// <summary>The production opener: shell-open through the OS desktop handler.</summary>
    public static SettingsOpeners Default { get; } = new(
        OpenPath: path => OpenNativeAsync(path),
        OpenTextFile: path => OpenNativeAsync(path),
        CanOpen: true);

    private static Task OpenNativeAsync(string path)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("the native opener could not start");
        return Task.CompletedTask;
    }
}
