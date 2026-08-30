namespace Dsh.Attachment;

/// <summary>
/// Opaque storage identifier of one immutable attachment (port of the TS <c>AttachmentId</c>
/// brand). A generated uuid, never a filesystem path or a caller-supplied name; consumers render it
/// but never parse it.
/// </summary>
public readonly record struct AttachmentId(string Value)
{
    public static implicit operator string(AttachmentId id) => id.Value;

    public override string ToString() => Value;
}

/// <summary>
/// One stored attachment's durable reference (port of the TS <c>ImageAttachmentRef</c> reduced to
/// the generic file facts): the opaque id, the display name stripped of local path information,
/// and the exact byte length.
/// </summary>
public sealed record AttachmentRef(AttachmentId Id, string Name, long Bytes);

/// <summary>Stored attachment bytes returned by <see cref="IAttachmentService.Read"/>.</summary>
public sealed record AttachmentData(AttachmentRef Ref, byte[] Content);
