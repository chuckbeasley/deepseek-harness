using System.Text.Json.Serialization;

namespace Dsh.Sandbox;

/// <summary>
/// File-effect policy for confined processes (port of the TS <c>SandboxMode</c> union).
/// <c>read-only</c> permits only required sinks such as <c>/dev/null</c>; <c>workspace-write</c>
/// also permits the workspace and a backend-defined temp area; <c>danger-full-access</c> bypasses
/// confinement. Network and process visibility are outside this vocabulary. <see cref="None"/> is
/// the port's unsandboxed mode — the facts the unsandboxed provider reports — and is NOT part of
/// the TS wire union.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SandboxMode
{
    /// <summary>"none" — the unsandboxed provider's mode (port-only; absent from the TS union).</summary>
    [JsonStringEnumMemberName("none")] None,

    /// <summary>"read-only" — only required sinks such as /dev/null are writable.</summary>
    [JsonStringEnumMemberName("read-only")] ReadOnly,

    /// <summary>"workspace-write" — the workspace root plus a backend-defined temp area are writable.</summary>
    [JsonStringEnumMemberName("workspace-write")] WorkspaceWrite,

    /// <summary>"danger-full-access" — confinement is bypassed.</summary>
    [JsonStringEnumMemberName("danger-full-access")] DangerFullAccess,
}

/// <summary>The exact wire spellings of <see cref="SandboxMode"/> — the strings markers and JSON use.</summary>
public static class SandboxModes
{
    /// <summary>The TS wire string for one mode.</summary>
    public static string WireName(SandboxMode mode) => mode switch
    {
        SandboxMode.None => "none",
        SandboxMode.ReadOnly => "read-only",
        SandboxMode.WorkspaceWrite => "workspace-write",
        SandboxMode.DangerFullAccess => "danger-full-access",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "unknown sandbox mode"),
    };
}

/// <summary>
/// Enforcement completeness for this host (port of the TS <c>SandboxEnforcement</c> union).
/// <c>partial</c> means an active backend or older kernel ABI cannot govern every promised file
/// effect; callers requiring an absolute boundary must not treat it as <c>full</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SandboxEnforcement
{
    /// <summary>"full" — every promised file effect is governed.</summary>
    [JsonStringEnumMemberName("full")] Full,

    /// <summary>"partial" — an active backend or older kernel ABI cannot govern every promised file effect.</summary>
    [JsonStringEnumMemberName("partial")] Partial,
}

/// <summary>
/// The complete file-effect policy resolved for one capability call (port of the TS
/// <c>SandboxExecutionPolicy</c>). The TS always carries the workspace root — even under modes
/// that do not consume it — so callers resolve policy once before choosing the enforcement path;
/// the port makes <see cref="WorkspaceRoot"/> optional so a policy under <c>none</c>/<c>read-only</c>
/// can be carried before a root exists (documented reduction). The TS <c>sessionId</c> field is not
/// ported: the port defers session-keyed backend state with the policy resolver service.
/// </summary>
public sealed record SandboxExecutionPolicy(
    /// <summary>The file-effect mode this execution runs under.</summary>
    SandboxMode Mode,
    /// <summary>Absolute root directory <c>workspace-write</c> may write under.</summary>
    string? WorkspaceRoot = null);

/// <summary>
/// Inputs that select the sandbox policy for one capability call (port of the TS
/// <c>SandboxPolicyRequest</c>). The TS carries the calling <c>Session</c> and resolves the
/// session's last override from its projection; the port defers session-based resolution (the
/// policy resolver service is a later wave) — the request carries only an approved mode override
/// and an explicit workspace root.
/// </summary>
public sealed record SandboxPolicyRequest(
    /// <summary>Explicit approved mode override, which outranks the deployment default.</summary>
    SandboxMode? Mode = null,
    /// <summary>Explicit workspace root for calls that cannot resolve a session cwd.</summary>
    string? WorkspaceRoot = null);

/// <summary>
/// Sandbox facts for one run, present iff a sandboxing executor handled it (port of the TS
/// <c>ShellSandboxInfo</c>). Facts are reported independently of process exit status so callers can
/// distinguish command failures from policy denials and runner failures. Serializes to the TS wire
/// shape: <c>mode</c> and <c>denied</c> are always present; <c>enforcement</c> and
/// <c>runnerFailed</c> are omitted when absent.
/// </summary>
public sealed record ShellSandboxInfo(
    /// <summary>The mode the command actually ran under.</summary>
    [property: JsonPropertyName("mode")] SandboxMode Mode,
    /// <summary>Whether the sandbox denied a file operation.</summary>
    [property: JsonPropertyName("denied")] bool Denied,
    /// <summary>How completely the selected runner enforced the requested mode.</summary>
    [property: JsonPropertyName("enforcement"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SandboxEnforcement? Enforcement = null,
    /// <summary>Whether the sandbox runner failed before the command could run.</summary>
    [property: JsonPropertyName("runnerFailed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? RunnerFailed = null);

/// <summary>
/// Stable, machine-routable codes for sandbox failures (port of the TS error-code vocabulary).
/// Carried on <see cref="SandboxError"/>; retry/permission/UI layers branch on the code without
/// parsing messages.
/// </summary>
public static class SandboxErrorCodes
{
    /// <summary>A requested confining mode has no usable backend on this host (the TS <c>SANDBOX_UNAVAILABLE</c> code).</summary>
    public const string Unavailable = "SANDBOX_UNAVAILABLE";
}

/// <summary>
/// Typed sandbox failure (port of the TS <c>SandboxUnavailableError</c>): a message plus a stable
/// <see cref="Code"/> from <see cref="SandboxErrorCodes"/>. Denials are NOT errors — they are the
/// <see cref="SandboxEscalation.SandboxDenialMarker"/> text and <see cref="ShellSandboxInfo.Denied"/>.
/// </summary>
public sealed class SandboxError : Exception
{
    /// <summary>Create a sandbox failure with its stable code.</summary>
    public SandboxError(string message, string code, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Stable machine code (see <see cref="SandboxErrorCodes"/>).</summary>
    public string Code { get; }

    /// <summary>
    /// Fail-closed error for a requested confining mode with no usable backend — verbatim TS text,
    /// so a consumer recognizes the refusal identically. Only confining modes produce it:
    /// <c>danger-full-access</c> and <c>none</c> never fail unavailable.
    /// </summary>
    public static SandboxError Unavailable(SandboxMode mode, string? detail = null)
    {
        var text = $"sandbox mode \"{SandboxModes.WireName(mode)}\" is requested but no sandbox backend is usable on this host; "
            + "refusing to run the command unconfined. Install bubblewrap or run a Landlock-enforcing kernel (Linux), ensure "
            + "sandbox-exec is usable (macOS), or ensure the ACL restricted-token runner can start (Windows) — otherwise switch "
            + "the consumer to danger-full-access."
            + (detail is null ? string.Empty : $" Runner failure: {detail}");
        return new SandboxError(text, SandboxErrorCodes.Unavailable);
    }
}

/// <summary>
/// The escalation vocabulary shared by every sandbox-enforcing tool family (port of the TS
/// <c>escalation.ts</c>): the strictly-wider ladder, the closed target vocabulary, the
/// argument-pairing validation a tool schema cannot express, and the model-facing markers. The
/// approval-channel choreography (<c>approveEscalation</c>) is deferred with the interaction seam;
/// the port carries the vocabulary only.
/// </summary>
public static class SandboxEscalation
{
    /// <summary>
    /// The strictly-wider table: what a call whose effective mode is the key may escalate TO.
    /// Checked at execution, never baked into a tool schema — schemas are registry-global while
    /// the effective mode is per-call truth.
    /// </summary>
    public static readonly IReadOnlyDictionary<SandboxMode, IReadOnlyList<SandboxMode>> WiderModes =
        new Dictionary<SandboxMode, IReadOnlyList<SandboxMode>>
        {
            [SandboxMode.ReadOnly] = new[] { SandboxMode.WorkspaceWrite, SandboxMode.DangerFullAccess },
            [SandboxMode.WorkspaceWrite] = new[] { SandboxMode.DangerFullAccess },
        };

    /// <summary>
    /// The closed escalation-target vocabulary — every mode a call could ever escalate TO
    /// (<c>read-only</c> is the floor; nothing escalates to it).
    /// </summary>
    public static readonly SandboxMode[] EscalationTargets = { SandboxMode.WorkspaceWrite, SandboxMode.DangerFullAccess };

    /// <summary>
    /// Validate the escalation argument pairing a tool schema cannot express:
    /// <c>sandbox_permissions</c> and <c>justification</c> travel together — an approval prompt
    /// without a reason, or a reason driving nothing, is a malformed ask — and the justification
    /// must be a non-empty sentence. Messages are verbatim TS text.
    /// </summary>
    public static void ValidateEscalationArgs(string? sandboxPermissions, string? justification)
    {
        if (sandboxPermissions is not null && justification is null)
        {
            throw new ArgumentException("invalid escalation: sandbox_permissions requires a justification");
        }
        if (justification is not null && sandboxPermissions is null)
        {
            throw new ArgumentException("invalid escalation: justification is only valid together with sandbox_permissions");
        }
        if (justification is not null && justification.Trim().Length == 0)
        {
            throw new ArgumentException("invalid justification: expected a non-empty sentence");
        }
    }

    /// <summary>
    /// The model-facing denial marker — the one vocabulary both enforcing families teach and
    /// report, so the model recognizes a policy denial identically.
    /// </summary>
    public static string SandboxDenialMarker(SandboxMode mode)
        => $"[sandbox: file access denied under {SandboxModes.WireName(mode)} mode]";

    /// <summary>
    /// The same-turn escalation hint that rides a denial when the composition advertises the
    /// escalation fields — the nudge lives at the decision point so the sanctioned retry does not
    /// depend on the model recalling the tool description.
    /// </summary>
    public static string EscalationHintMarker(string subject)
        => $"[sandbox: escalation available — retry this exact {subject} once with sandbox_permissions (the narrowest wider mode that suffices) + justification; the approval prompt asks the user]";
}

/// <summary>
/// The writable-root derivation shared by every enforcement dialect that expresses a mode as a
/// canonical allow-list (port of the TS <c>roots.ts</c>): <c>workspace-write</c> means "the
/// workspace root plus the platform temp areas".
/// </summary>
public static class SandboxRoots
{
    /// <summary>
    /// Resolve a granted root to the path the enforcement layer actually compares: canonical
    /// (symlinks resolved), because both Seatbelt filters and the fs fence's containment check
    /// match resolved paths — an as-spelled grant would match nothing. Returns the spelling as-is
    /// when resolution fails — a missing root matches nothing until it exists; inventing a
    /// fallback would grant a path the caller never named.
    /// </summary>
    public static string CanonicalPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var target = File.ResolveLinkTarget(full, returnFinalTarget: true);
            return target?.FullName ?? full;
        }
        catch (Exception)
        {
            // The path (or a prefix) is missing or unreadable: keep the as-spelled form.
            return path;
        }
    }

    /// <summary>
    /// The roots one confined execution may WRITE under — the mode's meaning as a canonical,
    /// deduplicated allow-list. <c>read-only</c> allows nothing; <c>workspace-write</c> allows the
    /// policy's workspace root, the host <c>/tmp</c>, and the per-user platform temp dir
    /// (<c>Path.GetTempPath()</c> — the real temp area for mkstemp-family tools). A policy without
    /// a workspace root contributes only the temp areas.
    /// </summary>
    public static IReadOnlyList<string> WritableRoots(SandboxExecutionPolicy policy)
    {
        if (policy.Mode != SandboxMode.WorkspaceWrite)
        {
            return Array.Empty<string>();
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var root in new[] { policy.WorkspaceRoot, "/tmp", Path.GetTempPath() })
        {
            if (root is null) continue;
            var canonical = CanonicalPath(root);
            if (seen.Add(canonical)) roots.Add(canonical);
        }
        return roots;
    }
}
