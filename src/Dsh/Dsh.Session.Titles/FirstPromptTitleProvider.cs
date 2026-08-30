using Dsh.Llm;
using Dsh.Session;

namespace Dsh.Session.Titles;

/// <summary>
/// The sole session-title provider: the first user prompt becomes the title. The first
/// user/message event produced by a direct human prompt (<see cref="UserSource"/>) supplies the
/// text; injected context and model/tool messages never title a session.
/// </summary>
public sealed class FirstPromptTitleProvider : ISessionTitleProvider
{
    /// <summary>
    /// Derive the title from the first direct human prompt's text blocks.
    /// </summary>
    /// <param name="session">the session whose log is read.</param>
    /// <returns>the trimmed first prompt text, or <c>null</c> when the log holds no user prompt.</returns>
    public string? Generate(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        foreach (var evt in session.Events)
        {
            if (evt is not UserMessageEvent user || user.Message.Source is not UserSource) continue;
            var text = string.Concat(user.Message.Content.OfType<TextBlock>().Select(block => block.Text));
            var title = text.Trim();
            return title.Length == 0 ? null : title;
        }
        return null;
    }
}
