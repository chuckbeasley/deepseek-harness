namespace Dsh.SystemPrompt;

/// <summary>Centrally allocated repository prompt-section placements (sparse; at least ten apart).</summary>
public enum SectionOrderName
{
    HARNESS_IDENTITY = -1000,
    HARNESS_SOURCE = -900,
    WEB_SURFACE = -800,
    DEPLOYMENT_PERSONA = 0,
    PLAN_POLICY = 500,
    TEAM_POLICY = 600,
    PTC_ONLY = 800,
    FILE_REFERENCE = 900,
    TOOL_BASH = 1000,
    TOOL_PWSH = 1010,
    TOOL_READ = 1100,
    TOOL_WRITE = 1200,
    TOOL_EDIT = 1300,
    TOOL_GLOB = 1400,
    TOOL_GREP = 1500,
    TOOL_JOBS = 1600,
    TOOL_PTY = 1700,
    TOOL_WEB_SEARCH = 2000,
    TOOL_WEB_FETCH = 2100,
    TOOL_LSP = 2200,
    TOOL_SESSION_QUERY = 2300,
    TOOL_GOAL = 2400,
    TOOL_CORDIS = 2500,
    TOOL_WORKFLOW = 2600,
    TOOL_RALPH = 2700,
    TOOL_SUBAGENT = 2800,
    TOOL_REPORT = 2900,
    TOOLS_SDK = 5000,
    DELIVERABLE_FILE_REFERENCES = 9000,
    STRUCTURED_OUTPUT = 9900,
}

/// <summary>Centrally allocated repository runtime-context placements.</summary>
public enum ContextOrderName
{
    SANDBOX_POLICY = 110,
    APPROVAL_POLICY = 115,
    SUBAGENT_DELEGATION = 120,
}

/// <summary>Fixed prompt constants: the pinned harness opener, the persona slot, and the tool-order rest marker.</summary>
public static class PromptConstants
{
    /// <summary>
    /// The deployment persona's section name. Exported because a composition can replace this
    /// slot — an agent preset shadows the deployment's persona with its own, and both sides naming
    /// the same section is what makes the replacement work rather than duplicate.
    /// </summary>
    public const string PersonaSection = "deployment:persona";

    /// <summary>The fixed harness identity opener (pinned model-visible text).</summary>
    public const string HarnessIdentity = "You are an AI agent powered by DeepSeek Harness.";

    /// <summary>Reserved <see cref="SystemPromptConfig.ToolOrder"/> marker for unlisted tools.</summary>
    public const string ToolOrderRest = "<unlisted-tools>";
}
