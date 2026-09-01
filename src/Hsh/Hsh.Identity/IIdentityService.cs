using System.Text.Json.Serialization;
using Harness.Llm;

namespace Harness.Identity;

/// <summary>
/// The harness's user identity (port of the TS branded <c>UserId</c>): an opaque per-user key,
/// never a bare string. Only the anonymous identity provider is composed today, so this always
/// equals <see cref="AnonymousUserId"/>.
/// </summary>
[JsonConverter(typeof(StringIdJsonConverter<UserId>))]
public readonly record struct UserId(string Value) : IStringId
{
    public static implicit operator string(UserId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>
/// A harness-home-scoped anonymous user id: a random UUID persisted as a bare line in
/// <c>.anonymous-user-id</c> inside the harness home, never derived from the hostname, network
/// address, git remote, or any other identifying source. Scoped to the harness home, not the
/// machine: every process sharing one <c>$HSH_HOME</c> reports the same id, and deleting the file
/// mints a fresh identity on the next launch.
/// </summary>
[JsonConverter(typeof(StringIdJsonConverter<AnonymousUserId>))]
public readonly record struct AnonymousUserId(string Value) : IStringId
{
    public static implicit operator string(AnonymousUserId id) => id.Value;

    public static implicit operator UserId(AnonymousUserId id) => new(id.Value);

    public override string ToString() => Value;
}

/// <summary>
/// The identity capability surface (ctx.identity): a stable per-harness-home user id shared by
/// telemetry and feedback consumers. Reads are synchronous so boot-time and command consumers use
/// one API.
/// </summary>
public interface IIdentityService
{
    /// <summary>The harness user identity this home reports (currently the anonymous id).</summary>
    UserId UserId { get; }

    /// <summary>The anonymous flavor of <see cref="UserId"/>, minted and persisted once on first use.</summary>
    AnonymousUserId AnonymousUserId { get; }

    /// <summary>The resolved harness home this identity is scoped to (<c>$HSH_HOME</c> or the default <c>~/.hsh</c>).</summary>
    string Home { get; }
}
