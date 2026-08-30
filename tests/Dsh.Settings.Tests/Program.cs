namespace Dsh.Settings.Tests;

/// <summary>Zero-dependency console runner for the Settings port tests.</summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    /// <summary>Run all tests; exit 0 only when every test passes.</summary>
    public static async Task<int> Main()
    {
        await RunAsync("install: installSection attaches a default entry and notifies", SettingsTests.InstallSectionAttachesDefaultEntry);
        await RunAsync("write: update commits and reads are read-through", SettingsTests.UpdateCommitsAndGetReadsThrough);
        await RunAsync("write: validation rejects an invalid committed change keeping last good", SettingsTests.ValidationRejectsInvalidCommittedChangeKeepingLastGood);
        await RunAsync("write: a stale expectedRevision refuses with a conflict error", SettingsTests.StaleWriteRefusesWithConflictError);
        await RunAsync("provider: a publish with an invalid section keeps the last good value", SettingsTests.ProviderPublishKeepsLastGoodValue);
        await RunAsync("file: provider persists and reloads a section across instances", SettingsTests.FileProviderPersistsAndReloadsAcrossInstances);
        await RunAsync("redaction: secret values are masked in diagnostics", SettingsTests.RedactionMasksSecretValues);
        await RunAsync("file: an invalid stored section fails registration loud", SettingsTests.InvalidStoredSectionFailsRegistrationLoud);
        await RunAsync("namespace: a shape violation fails loud", SettingsTests.InvalidNamespaceFailsLoud);
        await RunAsync("file: an unsupported extension fails loud", SettingsTests.FileProviderRejectsUnsupportedExtension);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"  PASS {name}");
            _passed++;
        }
        catch (Exception error)
        {
            Console.WriteLine($"  FAIL {name}: {error.Message}");
            _failed++;
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            Console.WriteLine($"  PASS {name}");
            _passed++;
        }
        catch (Exception error)
        {
            Console.WriteLine($"  FAIL {name}: {error.Message}");
            _failed++;
        }
    }
}
