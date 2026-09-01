namespace Dsh.Attachment;

/// <summary>
/// The attachment capability Service Definition (port of the attachment seam of
/// packages/attachment/attachment): named file attachments ingested by path into provider-owned
/// storage, each with a generated opaque id, plus list/read/remove. The TS seam is image-focused
/// (base64 uploads, raster validation, normalization, content-addressed publication); this phase
/// ports the generic ingest/list/read/remove core over opaque files and defers image admission,
/// normalization, and request projection (named in the provider docs).
/// </summary>
public interface IAttachmentService
{
    /// <summary>
    /// Ingest a source file by path into attachment storage: the bytes are copied under the
    /// attachment root with a generated id and the file is registered. An absent source rejects
    /// with ATTACHMENT_NOT_FOUND, a directory with ATTACHMENT_NOT_REGULAR_FILE, and content above
    /// the configured byte limit with ATTACHMENT_TOO_LARGE.
    /// </summary>
    /// <param name="sourcePath">absolute or relative path of the file to ingest.</param>
    /// <param name="name">optional display name; defaults to the source file name. Never interpreted as a path.</param>
    /// <returns>the durable attachment reference.</returns>
    AttachmentRef Ingest(string sourcePath, string? name = null);

    /// <summary>The ingested attachments, in ingestion order (a snapshot).</summary>
    IReadOnlyList<AttachmentRef> List();

    /// <summary>
    /// Persist one image under its content-addressed sha256 id and parse its dimensions (port of
    /// the TS saveImage). The bytes must match <paramref name="mediaType"/> or the ingest refuses
    /// with IMAGE_TYPE_MISMATCH.
    /// </summary>
    /// <param name="data">the image bytes (PNG/JPEG/WebP/GIF).</param>
    /// <param name="mediaType">the declared media type the bytes must match.</param>
    /// <param name="name">the caller-suggested display name (sanitized).</param>
    /// <returns>the durable image reference with its parsed dimensions.</returns>
    Dsh.Llm.ImageAttachment SaveImage(byte[] data, string mediaType, string name);

    /// <summary>
    /// Read one attachment's stored bytes and verify they still match the recorded reference. A
    /// missing object rejects with ATTACHMENT_NOT_FOUND; a length mismatch with
    /// ATTACHMENT_CORRUPT.
    /// </summary>
    /// <param name="id">the attachment id from a reference.</param>
    /// <returns>the verified bytes and reference.</returns>
    AttachmentData Read(AttachmentId id);

    /// <summary>
    /// Remove one attachment: its object file is deleted and the registration is dropped. A missing
    /// object file is not a failure; returns whether the id was registered.
    /// </summary>
    /// <param name="id">the attachment id to remove.</param>
    /// <returns><c>true</c> when the id was registered and is now removed.</returns>
    bool Remove(AttachmentId id);
}
