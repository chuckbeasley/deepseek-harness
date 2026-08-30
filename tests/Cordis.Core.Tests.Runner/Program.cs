using Cordis.Core.Tests.Runner;

namespace Cordis.Core.Tests.Runner;

/// <summary>
/// Zero-dependency console assertion runner for the Cordis.Core Phase 0 port. Each entry runs one
/// scenario; failures are collected and the process exits non-zero when any assertion fails.
/// </summary>
internal static class Program
{
    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            Test("Events.Emit_CallsListenersInRegistrationOrder", EventsTests.Emit_CallsListenersInRegistrationOrder),
            Test("Events.Emit_DeliversPayloadToTypedListener", EventsTests.Emit_DeliversPayloadToTypedListener),
            Test("Events.Emit_ThrowingListener_PropagatesAndAbortsRemaining", EventsTests.Emit_ThrowingListener_PropagatesAndAbortsRemaining),
            Test("Events.Parallel_AwaitsEveryListenerAndAggregatesFailures", EventsTests.Parallel_AwaitsEveryListenerAndAggregatesFailures),
            Test("Events.Serial_AwaitsInOrderAndReturnsFirstBailValue", EventsTests.Serial_AwaitsInOrderAndReturnsFirstBailValue),
            Test("Events.Bail_StopsAtFirstBailValue", EventsTests.Bail_StopsAtFirstBailValue),
            Test("Events.Waterfall_ShortCircuit_ReturnsListenerValueWithoutCallingNext", EventsTests.Waterfall_ShortCircuit_ReturnsListenerValueWithoutCallingNext),
            Test("Events.Waterfall_NextReturns_PropagateThroughTheChain", EventsTests.Waterfall_NextReturns_PropagateThroughTheChain),
            Test("Events.Waterfall_Veto_StopsDownstreamTransformers", EventsTests.Waterfall_Veto_StopsDownstreamTransformers),
            Test("Events.Waterfall_DeliversEventArgumentsToListeners", EventsTests.Waterfall_DeliversEventArgumentsToListeners),
            Test("Events.Once_RemovesListenerAfterFirstInvocation", EventsTests.Once_RemovesListenerAfterFirstInvocation),
            Test("Events.On_DisposerRemovesTheRegistration", EventsTests.On_DisposerRemovesTheRegistration),
            Test("Events.Prepend_AddsListenerBeforeExisting", EventsTests.Prepend_AddsListenerBeforeExisting),
            Test("Fiber.DisposeAsync_UnwindsEffectsInReverseRegistrationOrder", FiberEffectTests.DisposeAsync_UnwindsEffectsInReverseRegistrationOrder),
            Test("Fiber.FailingCleanup_IsContainedAndDoesNotStarvePeers", FiberEffectTests.FailingCleanup_IsContainedAndDoesNotStarvePeers),
            Test("Fiber.EffectDisposer_IsSingleShot", FiberEffectTests.EffectDisposer_IsSingleShot),
            Test("Fiber.Effect_DisposedBeforeUnload_IsNotRunAgainAtContextDispose", FiberEffectTests.Effect_DisposedBeforeUnload_IsNotRunAgainAtContextDispose),
            Test("Fiber.Effect_OnDisposedContext_ThrowsInactiveEffect", FiberEffectTests.Effect_OnDisposedContext_ThrowsInactiveEffect),
            Test("Fiber.NestedEffect_UnwindsBeforeItsParent", FiberEffectTests.NestedEffect_UnwindsBeforeItsParent),
            Test("Fiber.EffectAsync_AwaitsAsyncCleanup", FiberEffectTests.EffectAsync_AwaitsAsyncCleanup),
            Test("Fiber.ContextDispose_UnwindsListenerRegistrations", FiberEffectTests.ContextDispose_UnwindsListenerRegistrations),
            Test("Service.Service_RegistersItselfAtItsKey", ServiceTests.Service_RegistersItselfAtItsKey),
            Test("Service.ContextDispose_UnregistersServiceAndRunsStopAsync", ServiceTests.ContextDispose_UnregistersServiceAndRunsStopAsync),
            Test("Service.Set_SameInstanceTwice_IsANoOp", ServiceTests.Set_SameInstanceTwice_IsANoOp),
            Test("Service.Set_DifferentInstanceUnderExistingKey_Throws", ServiceTests.Set_DifferentInstanceUnderExistingKey_Throws),
            Test("Service.Get_WrongType_Throws", ServiceTests.Get_WrongType_Throws),
            Test("Service.Get_MissingKey_ReturnsNull", ServiceTests.Get_MissingKey_ReturnsNull),
            Test("Service.Require_MissingKey_Throws", ServiceTests.Require_MissingKey_Throws),
            Test("Service.Service_Dispose_StopsAndUnregisters", ServiceTests.Service_Dispose_StopsAndUnregisters),
            Test("Service.Registry_TracksServiceKeysAndProvesDisposal", ServiceTests.Registry_TracksServiceKeysAndProvesDisposal),
            Test("Context.Logger_Warn_IsCapturedByTheRingBuffer", ContextTests.Logger_Warn_IsCapturedByTheRingBuffer),
            Test("Context.DisposeAsync_IsIdempotent", ContextTests.DisposeAsync_IsIdempotent),
            Test("Context.On_AfterDispose_ThrowsInactiveEffect", ContextTests.On_AfterDispose_ThrowsInactiveEffect),
            Test("Context.Set_AfterDispose_ThrowsInactiveEffect", ContextTests.Set_AfterDispose_ThrowsInactiveEffect),
            Test("Context.Emit_ReportsDispatchThroughInternalDispatch", ContextTests.Emit_ReportsDispatchThroughInternalDispatch),
            Test("Spike.SpikeSection7_Surface_CompilesAndRuns", SpikeContractTests.SpikeSection7_Surface_CompilesAndRuns),
            Test("Spike.On_AcceptsLambdaWithExplicitParameterTypes", SpikeContractTests.On_AcceptsLambdaWithExplicitParameterTypes),
        };

        int passed = 0;
        var failures = new List<string>();
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failures.Add($"{name}: {error.Message}");
                Console.WriteLine($"FAIL {name}: {error.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"== Cordis.Core.Tests.Runner: {passed} passed, {failures.Count} failed of {tests.Length} ==");
        foreach (var failure in failures)
        {
            Console.WriteLine($"  - {failure}");
        }
        return failures.Count == 0 ? 0 : 1;
    }

    private static (string Name, Func<Task> Run) Test(string name, Func<Task> run) => (name, run);

    private static (string Name, Func<Task> Run) Test(string name, Action run) => (name, () =>
    {
        run();
        return Task.CompletedTask;
    });
}

