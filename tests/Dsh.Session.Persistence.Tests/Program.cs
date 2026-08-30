using System.Text.Json;
using Cordis.Core;
using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Session.Persistence.Tests;

/// <summary>Zero-dependency console runner for the Phase 2 session spine tests.</summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    /// <summary>Run all tests; exit 0 only when every test passes.</summary>
    public static int Main()
    {
        Run("persistence: append+replay round-trip produces identical events", PersistenceTests.AppendAndReplay_ProducesIdenticalEvents);
        Run("persistence: store attach persists every session append", PersistenceTests.StoreAttach_PersistsEverySessionAppend);
        Run("persistence: attach loads the stored log then persists new appends", PersistenceTests.Attach_LoadsStoredLog_ThenPersistsNewAppends);
        Run("persistence: hand-written pinned fixture replays", PersistenceTests.Replay_OfHandWrittenPinnedFixture_Works);
        Run("persistence: foreign format version is refused", PersistenceTests.ForeignFormatVersion_IsRefused);
        Run("persistence: batched flush buffers until flush", PersistenceTests.BatchedFlush_BuffersUntilFlush);

        Run("projection: stateOf returns the same reference until the fact moves", ProjectionTests.StateOf_ReturnsSameReference_UntilTheFactMoves);
        Run("projection: late registration folds the committed log", ProjectionTests.LateRegistration_FoldsTheCommittedLog);
        Run("projection: snapshot returns cropped views", ProjectionTests.Snapshot_ReturnsCroppedViews);
        Run("projection: host reader fails explicitly when registry absent", ProjectionTests.HostReader_FailsExplicitly_WhenRegistryAbsent);
        Run("projection: duplicate key registration fails loud", ProjectionTests.DuplicateKeyRegistration_FailsLoud);

        Run("titles: first user prompt becomes the title", TitlesTests.FirstUserPrompt_BecomesTitle);
        Run("titles: injected context is not the title", TitlesTests.InjectedContext_IsNotTheTitle);
        Run("titles: no provider fails explicit", TitlesTests.NoProvider_FailsExplicit);
        Run("titles: second provider registration fails loud", TitlesTests.SecondProvider_FailsLoud);
        Run("titles: no user prompt yields null", TitlesTests.NoUserPrompt_ReturnsNull);

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

/// <summary>Minimal assertion helpers.</summary>
public static class Assert
{
    /// <summary>Assert that <paramref name="condition"/> holds.</summary>
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"expected true: {message}");
    }

    /// <summary>Assert value equality.</summary>
    public static void Equal(object? expected, object? actual)
    {
        if (!Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
        }
    }

    /// <summary>Assert a non-null value.</summary>
    public static void NotNull(object? value, string message)
    {
        if (value is null) throw new InvalidOperationException($"expected non-null: {message}");
    }

    /// <summary>Assert a null value.</summary>
    public static void Null(object? value, string message)
    {
        if (value is not null) throw new InvalidOperationException($"expected null: {message}");
    }

    /// <summary>Assert reference equality.</summary>
    public static void Same(object? expected, object? actual)
    {
        if (!ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException("expected the same reference");
        }
    }

    /// <summary>Assert reference inequality.</summary>
    public static void NotSame(object? expected, object? actual)
    {
        if (ReferenceEquals(expected, actual))
        {
            throw new InvalidOperationException("expected a different reference");
        }
    }

    /// <summary>Assert that <paramref name="action"/> throws <typeparamref name="T"/>.</summary>
    public static void Throws<T>(Action action, string message) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"{message}: expected {typeof(T).Name}, got {error.GetType().Name}");
        }
        throw new InvalidOperationException($"{message}: expected {typeof(T).Name}, nothing was thrown");
    }
}

/// <summary>One context + store pair disposed together (test teardown unwinds live sessions).</summary>
internal sealed class TestScope : IDisposable
{
    public TestScope()
    {
        Ctx = new Context();
        Store = new SessionStore(Ctx);
    }

    public Context Ctx { get; }

    public SessionStore Store { get; }

    public void Dispose() => Ctx.Dispose();
}

/// <summary>Shared event-fixture builders.</summary>
internal static class TestEvents
{
    /// <summary>Fixed epoch-millis timestamp used by the pinned fixture and stamped expectations.</summary>
    public const long T = 1_700_000_000_000;

    /// <summary>One user-role prompt event with a direct human source.</summary>
    public static UserMessageEvent UserPrompt(string text, string messageId)
        => new()
        {
            Message = new UserMessage
            {
                Id = new MessageId(messageId),
                Content = new ContentBlock[] { new TextBlock(text) },
                Source = new UserSource(),
            },
            SurfaceOp = SurfaceOp.Append,
        };

    /// <summary>Canonical JSON of one event — the lossless storage-boundary comparison.</summary>
    public static string Canonical(SessionEvent evt) => JsonSerializer.Serialize<SessionEvent>(evt);
}
