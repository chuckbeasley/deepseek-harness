using System.Globalization;
using System.Text.Json;
using Cordis.Core;
using Dsh.Workspace;

namespace Dsh.Web.Host;

/// <summary>
/// The workspace remote methods (port of the TS WorkspaceController): the create command over the
/// ported identity/root lifecycle. The rename, delete, insertBefore, insertSessionBefore,
/// archiveSession, and follow methods are deferred with the durable WorkspaceRegistry they sit on
/// (the ported seam holds one current workspace and no session accounting). The namespace stays
/// registered without a provider, answering an actionable <c>gateway/internal</c> like the TS
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
            var workspace = Provider(ctx);
            var path = args is JsonElement element && element.TryGetProperty("path", out var pathValue)
                    && pathValue.ValueKind == JsonValueKind.String
                ? pathValue.GetString()
                : null;
            if (string.IsNullOrEmpty(path))
            {
                throw new RpcBadRequestException("workspace/create requires a path string");
            }
            try
            {
                var before = workspace.Current;
                var opened = workspace.Open(path);
                return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
                {
                    workspace = WorkspaceView(opened),
                    created = before is null,
                }));
            }
            catch (Exception error)
            {
                throw new RpcDomainError("workspace/invalid-path",
                    $"cannot create a Workspace at \"{path}\": {error.Message}",
                    JsonSerializer.SerializeToElement(new { path }));
            }
        });
    }

    /// <summary>Project one workspace onto its wire view; session accounting is deferred with the registry.</summary>
    private static object WorkspaceView(Dsh.Workspace.Workspace workspace)
        => new
        {
            workspaceId = workspace.Id.Value,
            path = workspace.Root,
            title = workspace.Title,
            sessionIds = Array.Empty<string>(),
            createdAt = workspace.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            updatedAt = workspace.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        };

    /// <summary>Resolve the optional provider or report how to supply it.</summary>
    private static IWorkspaceService Provider(Context ctx)
        => ctx.Get<IWorkspaceService>("workspace")
            ?? throw new RpcDomainError(RpcErrorCodes.Internal,
                "workspace service is absent: this deployment does not mount a workspace provider (e.g. LocalWorkspaceProvider) in its composition");
}
