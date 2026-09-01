using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Cordis.Core;
using Harness.Session;
using Harness.Workspace;

namespace Harness.Web.Host;

/// <summary>
/// The workspace remote methods (port of the TS WorkspaceController commands + follow feed), all
/// over the durable registry. Command failures map the seam codes to the TS wire codes:
/// <c>workspace/not-found</c>, <c>workspace/name-conflict</c>, <c>workspace/move-invalid</c>, and
/// <c>session/not-found</c> for archive requests naming an unknown session. The namespace stays
/// registered without a registry, answering an actionable <c>gateway/internal</c> like the TS
/// controller.
/// </summary>
public static class WorkspaceRemotes
{
    /// <summary>Create or idempotently resolve one workspace over an existing directory.</summary>
    public static RpcMethod Create(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("workspace/create", (args, _) =>
        {
            var registry = Provider(ctx);
            var path = StringArg(args, "path")
                ?? throw new RpcBadRequestException("workspace/create requires a path string");
            try
            {
                var existing = registry.ResolveByPath(path);
                if (existing is not null)
                {
                    return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                    {
                        workspace = WorkspaceView(existing),
                        created = false,
                    }));
                }
                var created = registry.Create(path);
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    workspace = WorkspaceView(created),
                    created = true,
                }));
            }
            catch (WorkspaceError error)
            {
                // The TS wraps every non-Remote create failure as workspace/invalid-path.
                throw new RpcDomainError("workspace/invalid-path",
                    $"cannot create a Workspace at \"{path}\": {error.Message}",
                    JsonSerializer.SerializeToElement(new { path }));
            }
        });
    }

    /// <summary>Rename one workspace to a unique non-blank title.</summary>
    public static RpcMethod Rename(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("workspace/rename", (args, _) =>
        {
            var registry = Provider(ctx);
            var workspaceId = IdArg(args, "workspaceId");
            var title = StringArg(args, "title");
            if (string.IsNullOrWhiteSpace(title))
            {
                throw new RpcBadRequestException("Workspace rename requires a non-blank title");
            }
            try
            {
                var renamed = registry.Rename(workspaceId, title);
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { workspace = WorkspaceView(renamed) }));
            }
            catch (WorkspaceError error)
            {
                throw MapRename(error, workspaceId, title);
            }
        });
    }

    /// <summary>Remove one workspace registration while retaining files and sessions.</summary>
    public static RpcMethod Delete(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("workspace/delete", (args, _) =>
        {
            var registry = Provider(ctx);
            var workspaceId = IdArg(args, "workspaceId");
            try
            {
                if (!registry.Delete(workspaceId))
                {
                    throw new RpcDomainError("workspace/not-found",
                        $"Workspace \"{workspaceId}\" not found",
                        JsonSerializer.SerializeToElement(new { workspaceId = workspaceId.Value }));
                }
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { deleted = true }));
            }
            catch (WorkspaceError error)
            {
                throw MapIdError(error, workspaceId);
            }
        });
    }

    /// <summary>Move one workspace within the durable display order.</summary>
    public static RpcMethod InsertBefore(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("workspace/insertBefore", (args, _) =>
        {
            var registry = Provider(ctx);
            var workspaceId = IdArg(args, "workspaceId");
            WorkspaceId? beforeId = args is JsonElement element && element.TryGetProperty("beforeWorkspaceId", out var beforeValue)
                    && beforeValue.ValueKind == JsonValueKind.String
                ? new WorkspaceId(beforeValue.GetString()!)
                : null;
            try
            {
                var ids = registry.InsertBefore(workspaceId, beforeId);
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    workspaceIds = ids.Select(id => id.Value).ToArray(),
                }));
            }
            catch (WorkspaceError error)
            {
                // The TS maps an invalid order move to workspace/not-found naming the moved id.
                throw MapIdError(error, workspaceId);
            }
        });
    }

    /// <summary>Move one accounted session within a workspace's membership order.</summary>
    public static RpcMethod InsertSessionBefore(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("workspace/insertSessionBefore", (args, _) =>
        {
            var registry = Provider(ctx);
            var workspaceId = IdArg(args, "workspaceId");
            var sessionId = SessionIdArg(args, "sessionId");
            SessionId? beforeSessionId = args is JsonElement element && element.TryGetProperty("beforeSessionId", out var beforeValue)
                    && beforeValue.ValueKind == JsonValueKind.String
                ? new SessionId(beforeValue.GetString()!)
                : null;
            try
            {
                var workspace = registry.InsertSessionBefore(workspaceId, sessionId, beforeSessionId);
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { workspace = WorkspaceView(workspace) }));
            }
            catch (WorkspaceError error)
            {
                if (error.Code == WorkspaceErrorCodes.MoveInvalid)
                {
                    var details = new Dictionary<string, object?>
                    {
                        ["workspaceId"] = workspaceId.Value,
                        ["sessionId"] = sessionId.Value,
                    };
                    if (beforeSessionId is { } before) details["beforeSessionId"] = before.Value;
                    throw new RpcDomainError("workspace/move-invalid", error.Message, JsonSerializer.SerializeToElement(details));
                }
                throw MapIdError(error, workspaceId);
            }
        });
    }

    /// <summary>Hide one known session from workspace grouping surfaces.</summary>
    public static RpcMethod ArchiveSession(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("workspace/archiveSession", (args, _) =>
        {
            var registry = Provider(ctx);
            var sessionId = SessionIdArg(args, "sessionId");
            try
            {
                registry.ArchiveSession(sessionId);
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    archivedSessionIds = registry.ArchivedSessionIds.Select(id => id.Value).ToArray(),
                }));
            }
            catch (WorkspaceError error)
            {
                if (error.Code == WorkspaceErrorCodes.UnknownSession)
                {
                    throw new RpcDomainError("session/not-found", error.Message,
                        JsonSerializer.SerializeToElement(new { sessionId = sessionId.Value }));
                }
                throw;
            }
        });
    }

    /// <summary>
    /// The live follow feed: one baseline frame (every workspace + the archive set), then upsert,
    /// remove, order, and archived deltas from the registry events.
    /// </summary>
    public static RpcStreamMethod Follow(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcStreamMethod("workspace/follow", (_, ct) => FollowAsync(ctx, ct));
    }

    private static async IAsyncEnumerable<JsonElement> FollowAsync(
        Context ctx, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var registry = Provider(ctx);
        var channel = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        // Subscribe first, then baseline, so mutations during the baseline read queue behind it.
        using var cancel = ct.Register(() => channel.Writer.TryComplete());
        using var upserted = ctx.On<Harness.Workspace.Workspace>("workspace/upserted",
            workspace => Emit(ctx, channel, new { type = "upsert", workspace = WorkspaceView(workspace) }));
        using var removed = ctx.On<WorkspaceId>("workspace/removed",
            id => Emit(ctx, channel, new { type = "remove", workspaceId = id.Value }));
        using var order = ctx.On<string[]>("workspace/order",
            ids => Emit(ctx, channel, new { type = "order", workspaceIds = ids }));
        using var archived = ctx.On<string[]>("workspace/archived",
            ids => Emit(ctx, channel, new { type = "archived", archivedSessionIds = ids }));
        channel.Writer.TryWrite(JsonSerializer.SerializeToElement(new
        {
            type = "baseline",
            value = new
            {
                items = registry.List().Select(WorkspaceView).ToArray(),
                archivedSessionIds = registry.ArchivedSessionIds.Select(id => id.Value).ToArray(),
            },
        }));
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

    private static void Emit(Context ctx, Channel<JsonElement> channel, object frame)
    {
        if (!channel.Writer.TryWrite(JsonSerializer.SerializeToElement(frame)))
        {
            ctx.Logger.Warn("web: workspace/follow frame dropped (stream closed)");
        }
    }

    /// <summary>Project one workspace onto its wire view with its accounted session membership.</summary>
    private static object WorkspaceView(Harness.Workspace.Workspace workspace)
        => new
        {
            workspaceId = workspace.Id.Value,
            path = workspace.Root,
            title = workspace.Title,
            sessionIds = workspace.SessionIdsOrEmpty.Select(id => id.Value).ToArray(),
            createdAt = workspace.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            updatedAt = workspace.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };

    /// <summary>Map a rename refusal: the TS name-conflict carries the proposed title; a missing workspace is not-found.</summary>
    private static RpcDomainError MapRename(WorkspaceError error, WorkspaceId workspaceId, string title)
        => error.Code switch
        {
            WorkspaceErrorCodes.NameConflict => new RpcDomainError("workspace/name-conflict", error.Message,
                JsonSerializer.SerializeToElement(new { name = title })),
            WorkspaceErrorCodes.NotFound => new RpcDomainError("workspace/not-found", error.Message,
                JsonSerializer.SerializeToElement(new { workspaceId = workspaceId.Value })),
            _ => new RpcDomainError(RpcErrorCodes.Internal, error.Message),
        };

    /// <summary>Map a workspace-id command refusal: every seam code the TS routes to not-found lands there.</summary>
    private static RpcDomainError MapIdError(WorkspaceError error, WorkspaceId workspaceId)
        => error.Code switch
        {
            WorkspaceErrorCodes.NotFound or WorkspaceErrorCodes.OrderInvalid => new RpcDomainError("workspace/not-found", error.Message,
                JsonSerializer.SerializeToElement(new { workspaceId = workspaceId.Value })),
            _ => new RpcDomainError(RpcErrorCodes.Internal, error.Message),
        };

    /// <summary>Resolve the optional registry or report how to supply it.</summary>
    private static WorkspaceRegistry Provider(Context ctx)
        => ctx.Get<WorkspaceRegistry>("workspaceRegistry")
            ?? throw new RpcDomainError(RpcErrorCodes.Internal,
                "workspace registry service is absent: this deployment does not mount a workspace registry in its composition");

    private static string? StringArg(JsonElement? args, string key)
        => args is JsonElement element && element.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static WorkspaceId IdArg(JsonElement? args, string key)
        => new(StringArg(args, key) ?? throw new RpcBadRequestException($"workspace methods require a {key} string"));

    private static SessionId SessionIdArg(JsonElement? args, string key)
        => new(StringArg(args, key) ?? throw new RpcBadRequestException($"workspace methods require a {key} string"));
}
