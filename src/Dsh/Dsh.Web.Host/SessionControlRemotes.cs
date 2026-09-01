using System.Text.Json;
using System.Threading.Channels;
using Harness.Cordis.Core;
using Harness.Agent;
using Harness.Jobs;
using Harness.Session;
using Harness.Session.Projection;

namespace Harness.Web.Host;

/// <summary>
/// The session control stream (port of the TS session-control feed): one baseline frame with the
/// per-session queues, jobs, and projections, then live queue and jobs deltas. Queue deltas are
/// driven by the port's own agent/inbox events â€” the C# session vocabulary does not carry the
/// durable <c>agent/inbox/spliced</c> event (the Inbox seam documents the deviation) â€” and each
/// queue frame carries the full current items, so frames are idempotent. Live projection deltas
/// are deferred: the port has no per-key projection-change events; the baseline carries the
/// consistent cut. Unowned jobs (no owner session) have no wire seat and are omitted.
/// </summary>
public static class SessionControlRemotes
{
    private static JsonSerializerOptions? _wireJson;
    private static int _wireJsonRevision = -1;

    /// <summary>The session-event serializer: the session log's polymorphic codecs, camel-cased for the wire; rebuilt when the event-type registry grows.</summary>
    private static JsonSerializerOptions WireJson()
    {
        var revision = Harness.Session.SessionEventTypes.Revision;
        if (_wireJson is null || _wireJsonRevision != revision)
        {
            _wireJson = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = Harness.Session.SessionEventTypes.CreateSerializerOptions().TypeInfoResolver,
            };
            _wireJsonRevision = revision;
        }
        return _wireJson;
    }

    /// <summary>
    /// The live control stream: subscribe first, then baseline, so mutations during the baseline
    /// read queue behind it instead of being lost; the subscription order is the frame order.
    /// </summary>
    public static RpcStreamMethod Control(
        Context ctx, SessionStore sessions, AgentRegistry agents, IJobsService? jobs, SessionProjectionRegistry? projections)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(agents);
        return new RpcStreamMethod("session/control", (_, ct) => ControlAsync(ctx, sessions, agents, jobs, projections, ct));
    }

    private static async IAsyncEnumerable<JsonElement> ControlAsync(
        Context ctx, SessionStore sessions, AgentRegistry agents, IJobsService? jobs, SessionProjectionRegistry? projections,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        using var inboxSubscriptions = SubscribeInbox(ctx, channel);
        using var jobsSubscription = jobs is null ? null : jobs.OnJobsChanged(ownerSession =>
        {
            // Unowned jobs (owner null) have no per-session wire seat.
            if (ownerSession is null) return;
            EmitFrame(ctx, channel, new
            {
                type = "jobs",
                sessionId = ownerSession,
                jobs = SessionJobs(jobs, ownerSession),
            });
        });
        using var projectionSubscription = projections is null ? null : SubscribeProjections(ctx, channel, projections, sessions, agents);
        channel.Writer.TryWrite(BaselineFrame(sessions, agents, jobs, projections));
        // Cancellation completes the channel, so the token-free read ends normally and the
        // stream ends quietly without a terminal frame (the TS cancel contract).
        using var cancel = ct.Register(() => channel.Writer.TryComplete());
        try
        {
            await foreach (var frame in channel.Reader.ReadAllAsync())
            {
                yield return frame;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>One consistent cut: per-session queues, jobs, and projection values.</summary>
    private static JsonElement BaselineFrame(
        SessionStore sessions, AgentRegistry agents, IJobsService? jobs, SessionProjectionRegistry? projections)
    {
        var queues = new Dictionary<string, object?>();
        var jobViews = new Dictionary<string, object?>();
        var projectionViews = new Dictionary<string, object?>();
        foreach (var agent in agents.List())
        {
            var sessionId = agent.Session.Id.Value;
            queues[sessionId] = QueueItems(agent);
            jobViews[sessionId] = jobs is null ? Array.Empty<object>() : SessionJobs(jobs, sessionId);
            if (projections is not null && sessions.Get(agent.Session.Id) is { } session)
            {
                var snapshot = projections.Snapshot(session);
                projectionViews[sessionId] = ProjectionView(snapshot);
            }
        }
        return JsonSerializer.SerializeToElement(new
        {
            type = "baseline",
            value = new
            {
                queues,
                jobs = jobViews,
                projections = projectionViews,
            },
        }, WireJson());
    }

    /// <summary>One session's queued items: next-turn prompts then next-step input, all placed <c>queued</c>.</summary>
    private static object[] QueueItems(Harness.Agent.Agent agent)
    {
        var items = new List<object>();
        foreach (var message in agent.Inbox.NextTurn) items.Add(QueuedItem(message));
        foreach (var message in agent.Inbox.NextStep) items.Add(QueuedItem(message));
        return items.ToArray();
    }

    private static object QueuedItem(Harness.Llm.UserMessage message)
        => new
        {
            id = message.Id.Value,
            placement = "queued",
            message = new
            {
                id = message.Id.Value,
                content = message.Content
                    .Select(block => JsonSerializer.SerializeToElement(block, WireJson()))
                    .ToArray(),
            },
        };

    /// <summary>One session's own jobs (unowned work has no wire seat).</summary>
    private static object[] SessionJobs(IJobsService jobs, string sessionId)
        => jobs.List(sessionId)
            .Where(snapshot => snapshot.OwnerSession == sessionId)
            .Select(JobView)
            .ToArray();

    private static object JobView(JobSnapshot snapshot)
    {
        var view = new Dictionary<string, object?>
        {
            ["id"] = snapshot.Id.Value,
            ["kind"] = snapshot.Kind,
            ["label"] = snapshot.Label,
            ["status"] = JobStatuses.WireName(snapshot.Status),
            ["startedAt"] = snapshot.StartedAt,
        };
        if (snapshot.Detail is not null) view["detail"] = snapshot.Detail;
        if (snapshot.FinishedAt is not null) view["finishedAt"] = snapshot.FinishedAt;
        return view;
    }

    /// <summary>Project one consistent cut; a unit whose view is not JSON-safe is omitted with a warning.</summary>
    private static Dictionary<string, object?> ProjectionView(ProjectionSnapshot snapshot)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in snapshot.Values)
        {
            try
            {
                values[pair.Key] = JsonSerializer.SerializeToElement(pair.Value, WireJson());
            }
            catch (Exception)
            {
                // A projection unit whose view is not JSON-safe cannot ride the wire; the other
                // keys still do (the wire admits JsonValue only).
            }
        }
        return new Dictionary<string, object?>
        {
            ["asOfSeq"] = snapshot.AsOfSeq,
            ["values"] = values,
        };
    }

    /// <summary>Subscribe the inbox events: every mutation re-derives the affected session's queue.</summary>
    private static IDisposable SubscribeInbox(Context ctx, Channel<JsonElement> channel)
    {
        void OnChange(Harness.Agent.Agent agent)
        {
            EmitFrame(ctx, channel, new
            {
                type = "queue",
                sessionId = agent.Session.Id.Value,
                items = QueueItems(agent),
            });
        }

        var disposers = new List<IDisposable>
        {
            ctx.On("agent/inbox/inserted", new Action<AgentInboxInsertedPayload>(payload => OnChange(payload.Agent))),
            ctx.On("agent/inbox/claimed", new Action<AgentInboxClaimedPayload>(payload => OnChange(payload.Agent))),
            ctx.On("agent/inbox/discarded", new Action<AgentInboxDiscardedPayload>(payload => OnChange(payload.Agent))),
        };
        return new CompositeDisposer(disposers);
    }

    /// <summary>
    /// Subscribe the session event stream: after every committed event, diff the session's
    /// projection cut against the last sent one and emit one frame per changed key (the TS
    /// SessionProjectionUpdate shape). Frames are full-value and idempotent; a unit whose view is
    /// not JSON-safe is omitted like the baseline omits it. The cut is seeded from the same state
    /// the baseline reads, so the first delta only carries real changes.
    /// </summary>
    private static IDisposable SubscribeProjections(
        Context ctx, Channel<JsonElement> channel, SessionProjectionRegistry projections,
        SessionStore sessions, AgentRegistry agents)
    {
        var lastCut = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var agent in agents.List())
        {
            if (sessions.Get(agent.Session.Id) is not { } session) continue;
            var snapshot = projections.Snapshot(session);
            var cut = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var pair in snapshot.Values)
            {
                try
                {
                    cut[pair.Key] = JsonSerializer.SerializeToElement(pair.Value, WireJson());
                }
                catch (Exception)
                {
                    // A projection unit whose view is not JSON-safe cannot ride the wire.
                }
            }
            lastCut[agent.Session.Id.Value] = cut;
        }
        var gate = new object();
        return ctx.On("session/event", new Action<Harness.Session.Session, SessionEvent>((session, _) =>
        {
            Dictionary<string, JsonElement> changed;
            long asOfSeq;
            lock (gate)
            {
                var snapshot = projections.Snapshot(session);
                asOfSeq = snapshot.AsOfSeq;
                if (!lastCut.TryGetValue(session.Id.Value, out var previous))
                {
                    previous = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    lastCut[session.Id.Value] = previous;
                }
                changed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var pair in snapshot.Values)
                {
                    JsonElement value;
                    try
                    {
                        value = JsonSerializer.SerializeToElement(pair.Value, WireJson());
                    }
                    catch (Exception)
                    {
                        // A projection unit whose view is not JSON-safe cannot ride the wire.
                        continue;
                    }
                    if (!previous.TryGetValue(pair.Key, out var prior) || !JsonElement.DeepEquals(prior, value))
                    {
                        previous[pair.Key] = value;
                        changed[pair.Key] = value;
                    }
                }
                // A key that left the cut (its unit unregistered) drops from the last cut; the TS
                // sends no removal frame for projections.
                foreach (var key in previous.Keys.Where(key => !snapshot.Values.ContainsKey(key)).ToArray())
                {
                    previous.Remove(key);
                }
            }
            foreach (var pair in changed)
            {
                EmitFrame(ctx, channel, new
                {
                    type = "projection",
                    sessionId = session.Id.Value,
                    key = pair.Key,
                    value = pair.Value,
                    seq = asOfSeq,
                });
            }
        }));
    }

    private static void EmitFrame(Context ctx, Channel<JsonElement> channel, object frame)
    {
        if (!channel.Writer.TryWrite(JsonSerializer.SerializeToElement(frame, WireJson())))
        {
            ctx.Logger.Warn("web: session/control frame dropped (stream closed)");
        }
    }

    private sealed class CompositeDisposer : IDisposable
    {
        private readonly List<IDisposable> _disposers;

        public CompositeDisposer(List<IDisposable> disposers)
        {
            _disposers = disposers;
        }

        public void Dispose()
        {
            foreach (var disposer in _disposers) disposer.Dispose();
            _disposers.Clear();
        }
    }
}
