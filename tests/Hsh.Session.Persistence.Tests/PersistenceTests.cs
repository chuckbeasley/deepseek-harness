using Harness.Llm;
using Harness.Session;
using Harness.Session.Persistence;

namespace Harness.Session.Persistence.Tests;

internal static class PersistenceTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hsh-session-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    public static void AppendAndReplay_ProducesIdenticalEvents()
    {
        var root = TempRoot();
        try
        {
            using var scope = new TestScope();
            var persistence = new SessionPersistenceService(scope.Ctx, new PersistenceConfig { Root = root });
            var id = new SessionId("s1");
            var session = scope.Store.Create(id);
            using (persistence.Attach(session))
            {
                session.Append(new TurnStartEvent { Turn = 1 });
                session.Append(TestEvents.UserPrompt("Record your plan.", "msg-user-1"));
                session.Append(new TurnEndEvent { Turn = 1, Reason = new CompletedReason() });
            }
            var stored = persistence.Load(id);
            Assert.NotNull(stored, "a stored log must exist after attached appends");
            Assert.Equal(0, stored!.Header.Version);
            Assert.Equal(id, stored.Header.Id);
            Assert.Equal(3, stored.Events.Count);
            for (var index = 0; index < 3; index++)
            {
                var replayed = stored.Events[index];
                var original = session.Events[index];
                Assert.Equal(original.Type, replayed.Type);
                Assert.Equal(original.Seq, replayed.Seq);
                Assert.Equal(TestEvents.Canonical(original), TestEvents.Canonical(replayed));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void StoreAttach_PersistsEverySessionAppend()
    {
        var root = TempRoot();
        try
        {
            using var scope = new TestScope();
            var persistence = new SessionPersistenceService(scope.Ctx, new PersistenceConfig { Root = root });
            using (persistence.Attach(scope.Store))
            {
                var a = scope.Store.Create(new SessionId("a"));
                var b = scope.Store.Create(new SessionId("b"));
                a.Append(new TurnStartEvent { Turn = 1 });
                b.Append(new TurnStartEvent { Turn = 1 });
                a.Append(TestEvents.UserPrompt("hello", "msg-a-1"));
            }
            Assert.Equal(2, persistence.Load(new SessionId("a"))!.Events.Count);
            Assert.Equal(1, persistence.Load(new SessionId("b"))!.Events.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void Attach_LoadsStoredLog_ThenPersistsNewAppends()
    {
        var root = TempRoot();
        try
        {
            var id = new SessionId("s2");
            SessionEvent[] firstEvents;
            // Phase 1: persist a log under the id.
            {
                using var scope = new TestScope();
                var persistence = new SessionPersistenceService(scope.Ctx, new PersistenceConfig { Root = root });
                var session = scope.Store.Create(id);
                using (persistence.Attach(session))
                {
                    session.Append(new TurnStartEvent { Turn = 1 });
                    session.Append(TestEvents.UserPrompt("first prompt", "msg-first"));
                }
                firstEvents = session.Events.ToArray();
            }
            // Phase 2: a fresh lifecycle over the same id loads the stored log on attach.
            StoredSession? loaded = null;
            using (var scope = new TestScope())
            {
                var persistence = new SessionPersistenceService(scope.Ctx, new PersistenceConfig { Root = root });
                var session = scope.Store.Create(id);
                using (persistence.Attach(session, stored => loaded = stored))
                {
                    Assert.NotNull(loaded, "the stored log must load on attach");
                    Assert.Equal(2, loaded!.Events.Count);
                    Assert.Equal(TestEvents.Canonical(firstEvents[0]), TestEvents.Canonical(loaded.Events[0]));
                    Assert.Equal(TestEvents.Canonical(firstEvents[1]), TestEvents.Canonical(loaded.Events[1]));
                    session.Append(new TurnEndEvent { Turn = 1, Reason = new CompletedReason() });
                }
                var final = persistence.Load(id);
                Assert.NotNull(final, "the log must include the post-attach append");
                Assert.Equal(3, final!.Events.Count);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void Replay_OfHandWrittenPinnedFixture_Works()
    {
        var root = TempRoot();
        try
        {
            var id = new SessionId("session-pinned");
            Directory.CreateDirectory(Path.Combine(root, JsonlFormat.EncodeSegment(id.Value)));
            var fixture = Path.Combine(AppContext.BaseDirectory, "pinned-session.jsonl");
            Assert.True(File.Exists(fixture), $"fixture missing at {fixture}");
            File.Copy(fixture, JsonlFormat.LogPath(root, id));
            using var scope = new TestScope();
            var persistence = new SessionPersistenceService(scope.Ctx, new PersistenceConfig { Root = root });
            var stored = persistence.Load(id);
            Assert.NotNull(stored, "the pinned fixture must replay");
            Assert.Equal(id, stored!.Header.Id);
            Assert.Equal(0, stored.Header.Version);
            Assert.Equal(TestEvents.T, stored.Header.CreatedAtMs);
            Assert.Equal(6, stored.Events.Count);
            var expected = new SessionEvent[]
            {
                new TurnStartEvent { Id = "evt-0", Seq = 0, TimeMs = TestEvents.T, Turn = 1 },
                new UserMessageEvent
                {
                    Id = "evt-1", Seq = 1, TimeMs = TestEvents.T,
                    Message = new UserMessage
                    {
                        Id = new MessageId("msg-user-1"),
                        Content = new ContentBlock[] { new TextBlock("Explain the JSONL session log format.") },
                        Source = new UserSource(),
                    },
                    SurfaceOp = SurfaceOp.Append,
                },
                new StepStartEvent { Id = "evt-2", Seq = 2, TimeMs = TestEvents.T, Turn = 1, Step = 1 },
                new AssistantChunkEvent { Id = "evt-3", Seq = 3, TimeMs = TestEvents.T, Turn = 1, Step = 1, Chunk = new TextDelta(0, "One event ") },
                new AssistantMessageEvent
                {
                    Id = "evt-4", Seq = 4, TimeMs = TestEvents.T, Turn = 1, Step = 1,
                    Message = new AssistantMessage
                    {
                        Id = new MessageId("msg-assistant-1"),
                        Content = new ContentBlock[] { new TextBlock("One event per line.") },
                        Source = new ModelSource { Provider = "mock", Model = "mock-1" },
                    },
                    SurfaceOp = SurfaceOp.Append,
                    SourceEventSeqs = new long[] { 3 },
                },
                new TurnEndEvent { Id = "evt-5", Seq = 5, TimeMs = TestEvents.T, Turn = 1, Reason = new CompletedReason() },
            };
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index].Type, stored.Events[index].Type);
                Assert.Equal((long)index, stored.Events[index].Seq);
                Assert.Equal(TestEvents.Canonical(expected[index]), TestEvents.Canonical(stored.Events[index]));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void ForeignFormatVersion_IsRefused()
    {
        var root = TempRoot();
        try
        {
            var id = new SessionId("future");
            Directory.CreateDirectory(Path.Combine(root, JsonlFormat.EncodeSegment(id.Value)));
            File.WriteAllText(
                JsonlFormat.LogPath(root, id),
                "{\"type\":\"session\",\"version\":999,\"id\":\"future\",\"createdAt\":1700000000000}\n");
            using var scope = new TestScope();
            var persistence = new SessionPersistenceService(scope.Ctx, new PersistenceConfig { Root = root });
            Assert.Throws<SessionFormatUnsupportedException>(
                () => persistence.Load(id),
                "a foreign format version must be refused before any event decoding");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static void BatchedFlush_BuffersUntilFlush()
    {
        var root = TempRoot();
        try
        {
            using var scope = new TestScope();
            // A 60 s flush interval keeps the background timer from firing during the test.
            var persistence = new SessionPersistenceService(
                scope.Ctx,
                new PersistenceConfig { Root = root, FlushMode = FlushMode.Batched, BatchDelayMs = 60_000 });
            var id = new SessionId("batched");
            var session = scope.Store.Create(id);
            using (persistence.Attach(session))
            {
                session.Append(new TurnStartEvent { Turn = 1 });
                session.Append(TestEvents.UserPrompt("buffered", "msg-buffered"));
            }
            Assert.True(!File.Exists(persistence.LogPath(id)), "batched appends must not hit disk before flush");
            persistence.Flush();
            var stored = persistence.Load(id);
            Assert.NotNull(stored, "a flush must materialize the buffered log");
            Assert.Equal(2, stored!.Events.Count);
            Assert.Equal(TestEvents.Canonical(session.Events[1]), TestEvents.Canonical(stored.Events[1]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
