namespace Dsh.AgentLoop;

/// <summary>Shared agent-loop scheduler defaults (port of the TS constants module).</summary>
public static class AgentLoopConstants
{
    /// <summary>Default maximum in-flight parallel-safe calls per agent step.</summary>
    public const int DefaultMaxParallelToolCalls = 10;

    /// <summary>The plugin name attributed to runtime-context snapshot messages.</summary>
    public const string RuntimeContextSource = "@deepseek-ai/dsh-system-prompt";

    /// <summary>The message logged when the retained runtime context empties.</summary>
    public const string ClearedRuntimeContext = "Current runtime context: none. Earlier runtime-context snapshots no longer apply.";
}
