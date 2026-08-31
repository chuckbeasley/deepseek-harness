using System.Text.Json;
using Cordis.Core;

namespace Dsh.Web.Host;

/// <summary>
/// The directoryPicker remote methods (the Wave-1 stub the port spec names: native directory
/// pickers are deferred, so every verb answers <c>directory-picker/unavailable</c>). The wire
/// shape follows the TS DirectoryPickerController: <c>pick</c> needs the native capability,
/// <c>list</c>/<c>createDirectory</c> the browse capability, and the create name grammar is still
/// enforced before the capability refusal (the TS validates the request first).
/// </summary>
public static class DirectoryPickerRemotes
{
    /// <summary>Open the host OS chooser; no native backend is composed, so always unavailable.</summary>
    public static RpcMethod Pick(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("directoryPicker/pick", (_, _) => throw Unavailable("pick", "native"));
    }

    /// <summary>List one directory level; no browse backend is composed, so always unavailable.</summary>
    public static RpcMethod List(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("directoryPicker/list", (_, _) => throw Unavailable("list", "browse"));
    }

    /// <summary>Create one child directory; the name grammar is enforced, then the browse capability is refused.</summary>
    public static RpcMethod CreateDirectory(Context ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new RpcMethod("directoryPicker/createDirectory", (args, _) =>
        {
            var name = args is JsonElement element && element.TryGetProperty("name", out var nameValue)
                    && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString()
                : null;
            if (name is null
                || name.Trim().Length == 0
                || name == "."
                || name == ".."
                || name.Contains('/')
                || name.Contains('\\'))
            {
                throw new RpcBadRequestException("invalid payload for host.createDirectory");
            }
            throw Unavailable("createDirectory", "browse");
        });
    }

    /// <summary>The stub refusal: the deployment composes no directory-picker backend (the TS detail field rides verbatim).</summary>
    private static RpcDomainError Unavailable(string method, string kind)
        => new("directory-picker/unavailable",
            $"directoryPicker.{method} needs the {kind} capability; this deployment composes no directory-picker backend",
            JsonSerializer.SerializeToElement(new { capability = (string?)null }));
}
