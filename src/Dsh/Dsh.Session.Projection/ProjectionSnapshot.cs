namespace Dsh.Session.Projection;

/// <summary>
/// One consistent read cut over every registered client-visible unit for one session.
/// <see cref="AsOfSeq"/> is the shared watermark — the seq of the last event every value reflects
/// (-1 for an empty log).
/// </summary>
/// <param name="AsOfSeq">seq of the last event the values reflect; -1 for an empty log.</param>
/// <param name="Values">whole current client value per selected key.</param>
public sealed record ProjectionSnapshot(long AsOfSeq, IReadOnlyDictionary<string, object?> Values);
