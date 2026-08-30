using Dsh.Session;

namespace Dsh.Session.Titles;

/// <summary>Generates a session title from the session log.</summary>
public interface ISessionTitleProvider
{
    /// <summary>Derive the session title, or <c>null</c> when the log holds no title-worthy content.</summary>
    /// <param name="session">the session whose log is read.</param>
    string? Generate(Session session);
}
