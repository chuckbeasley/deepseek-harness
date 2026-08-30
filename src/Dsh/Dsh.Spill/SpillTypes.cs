namespace Dsh.Spill;

/// <summary>
/// One registered spill file (port of the TS <c>SpillRef</c> reduced to its local facts): the
/// absolute file path — the locator consumers render, never parse — and the exact UTF-8 byte
/// length of the content at claim/register time.
/// </summary>
public sealed record SpillFile(string Path, long Bytes);
