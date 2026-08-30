namespace Dsh.Preset.Tests;

/// <summary>Zero-dependency console test runner.</summary>
public static class Program
{
    private static readonly (string Name, Action Run)[] Suites = new (string, Action)[]
    {
        ("discovery lists presets from a temp root and skips non-id directories", PresetTests.DiscoveryListsPresetsFromATempRoot),
        ("discovery reports a missing composition as a broken row", PresetTests.MissingCompositionIsReportedBroken),
        ("discovery reports a malformed composition as a broken row", PresetTests.MalformedCompositionIsReportedBroken),
        ("discovery reports a wrong-shaped composition as a broken row", PresetTests.WrongShapedCompositionIsReportedBroken),
        ("resolution composes loader rows through the Include patch layers", PresetTests.ResolveComposesLayersThroughPatches),
        ("resolution composes group rows recursively into loader rows", PresetTests.ResolveComposesGroupRows),
        ("resolution of an unknown preset fails loud", PresetTests.ResolveUnknownPresetFailsLoud),
        ("resolution of a broken preset fails loud with the discovery reason", PresetTests.ResolveBrokenPresetFailsLoud),
        ("resolution of a missing preset id fails loud", PresetTests.ResolveEmptyIdFailsLoud),
        ("an absent root yields no presets instead of throwing", PresetTests.AbsentRootYieldsNoPresets),
        ("the persona provider registers its text as a system-prompt section", PersonaTests.RegistersPersonaTextAsASection),
        ("disposing the persona registration removes the section", PersonaTests.DisposeRemovesTheSection),
        ("empty persona text registers an empty section that drops at render", PersonaTests.EmptyTextDropsAtRender),
        ("a duplicate persona registration fails loud and leaks nothing", PersonaTests.DuplicateRegistrationFailsLoud),
    };

    public static int Main()
    {
        var passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in Suites)
        {
            try
            {
                run();
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
                Console.WriteLine($"FAIL  {name}: {error.Message}");
            }
        }
        Console.WriteLine($"{passed} passed, {failures.Count} failed");
        return failures.Count == 0 ? 0 : 1;
    }
}
