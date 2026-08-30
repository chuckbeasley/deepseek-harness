namespace Dsh.Tools;

/// <summary>Thrown when the model requests a tool that is not registered.</summary>
public sealed class ToolNotFoundError : Exception
{
    public ToolNotFoundError(string toolName)
        : base($"unknown tool \"{toolName}\"")
    {
        Code = "UNKNOWN_TOOL";
    }

    /// <summary>Stable machine code.</summary>
    public string Code { get; }
}
