namespace Dsh.Agent.Tests;

/// <summary>Zero-dependency console runner for the Agent and Scope port tests.</summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    /// <summary>Run all tests; exit 0 only when every test passes.</summary>
    public static int Main()
    {
        Run("registry: ctx service 'agents'", RegistryTests.RegistryIsService);
        Run("registry: register then dispose removes", RegistryTests.RegisterThenDisposeRemoves);
        Run("registry: context disposal removes", RegistryTests.ContextDisposalRemoves);
        Run("registry: duplicate register fails loud", RegistryTests.RegisterDuplicateThrows);
        Run("registry: created/disposed events", RegistryTests.CreatedAndDisposedEvents);
        Run("registry: foreign-context register fails loud", RegistryTests.RegisterOnForeignContextThrows);

        Run("status: initial status is idle", StatusTests.InitialStatusIsIdle);
        Run("status: transitions emit events", StatusTests.StatusTransitionsEmitEvents);
        Run("status: same status is a no-op", StatusTests.SameStatusIsNoOp);
        Run("status: step transitions emit events", StatusTests.StepTransitionsEmitEvents);
        Run("status: cancel signals and keeps first cause", StatusTests.CancelSignalsAndKeepsFirstCause);

        Run("inbox: claim returns queued messages in order", InboxTests.ClaimReturnsQueuedInOrder);
        Run("inbox: next-turn claim pops one queued turn", InboxTests.ClaimNextTurnPopsOneQueuedTurn);
        Run("inbox: claim on empty returns none", InboxTests.ClaimEmptyReturnsNone);
        Run("inbox: claim publishes claimed events", InboxTests.ClaimPublishesClaimedEvents);
        Run("inbox: inserted/discarded notifications", InboxTests.InsertedAndDiscardedNotifications);
        Run("inbox: duplicate identity fails loud", InboxTests.DuplicateIdentityThrows);
        Run("inbox: replace and remove", InboxTests.ReplaceAndRemove);
        Run("inbox: config cap rejects overflow", InboxTests.ConfigCapRejectsOverflow);

        Run("scope: effect disposes with its agent", ScopeTests.EffectDisposesWithAgent);
        Run("scope: effect disposes on registry context disposal", ScopeTests.EffectDisposesOnRegistryContextDisposal);
        Run("scope: scoped service removed with agent", ScopeTests.ScopedServiceRemovedWithAgent);
        Run("scope: scoped listener receives own agent only", ScopeTests.ScopedListenerReceivesOwnAgentOnly);

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
}
