namespace Harness.SystemPrompt.Tests;

/// <summary>Zero-dependency console runner for the SystemPrompt port tests.</summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    /// <summary>Run all tests; exit 0 only when every test passes.</summary>
    public static async Task<int> Main()
    {
        await RunAsync("section: registers harness identity and the configured persona", SystemPromptTests.RegistersHarnessIdentityAndPersona);
        await RunAsync("section: persona-less deployment renders identity only", SystemPromptTests.RendersNoPersonaForAPersonaLessDeployment);
        await RunAsync("section: includeHarnessIdentity=false keeps only the persona", SystemPromptTests.CanOmitHarnessIdentity);
        await RunAsync("section: assembles sections in order with resolved text and collected tools", SystemPromptTests.AssemblesSectionsInOrder_WithResolvedTextAndCollectedTools);
        await RunAsync("section: equal orders break by code-unit name", SystemPromptTests.BreaksEqualSectionOrdersByCodeUnitName);
        await RunAsync("section: disposing the effect removes the section from later assemblies", SystemPromptTests.DisposeRemovesSectionFromLaterAssemblies);
        await RunAsync("section: context dispose removes the built-in sections", SystemPromptTests.ContextDisposeRemovesTheBuiltInSections);
        Run("section: duplicate registration throws without leaking", SystemPromptTests.DuplicateSectionRegistrationThrows_WithoutLeaking);
        await RunAsync("render: assembly output stability (pinned expected text)", SystemPromptTests.AssemblyOutputIsStable_PinnedExpectedText);
        await RunAsync("render: custom separator from config", SystemPromptTests.CustomSeparatorFromConfig_IsUsedForRendering);
        Run("render: empty sections are dropped", SystemPromptTests.RenderPromptDropsEmptySections);
        Run("orders: lookup matches the central allocation", SystemPromptTests.SectionOrderLookupMatchesCentralAllocation);
        Run("orders: repository placements are unique and at least ten apart", SystemPromptTests.SectionOrderNamesAreUniqueAndAtLeastTenApart);
        await RunAsync("tools: registry schemas appear in the assembled request", SystemPromptTests.ToolRegistrySchemasAppearInTheAssembledRequest);
        await RunAsync("tools: no registry yields no tools", SystemPromptTests.AssemblyWithoutToolRegistryHasNoTools);
        await RunAsync("tools: lexicographic order without a configured toolOrder", SystemPromptTests.ToolSchemasOrderLexicographically_WithoutConfiguredOrder);
        await RunAsync("tools: configured toolOrder with the rest entry applied", SystemPromptTests.ToolOrderAppliesConfiguredOrder_WithRestLexicographic);
        Run("tools: unknown configured tool fails assembly", SystemPromptTests.ToolOrderRejectsUnknownTool_AtAssembly);
        Run("tools: toolOrder shape violations fail at construction", SystemPromptTests.ToolOrderValidation_RejectsShapeViolations_AtConstruction);
        await RunAsync("tools: disposing a tool provider removes its schemas", SystemPromptTests.DisposingToolProviderRemovesItsSchemas);
        await RunAsync("section: text providers resolve per assembly", SystemPromptTests.SectionTextProvidersResolvePerAssembly);
        Run("events: system-prompt/change on register and dispose", SystemPromptTests.ChangeEventEmittedOnRegisterAndDispose);

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
