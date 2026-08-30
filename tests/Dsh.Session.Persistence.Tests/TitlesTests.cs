using Dsh.Llm;
using Dsh.Session;
using Dsh.Session.Titles;

namespace Dsh.Session.Persistence.Tests;

internal static class TitlesTests
{
    public static void FirstUserPrompt_BecomesTitle()
    {
        using var scope = new TestScope();
        var session = scope.Store.Create(new SessionId("title-1"));
        session.Append(new TurnStartEvent { Turn = 1 });
        session.Append(TestEvents.UserPrompt("Hello world", "msg-1"));
        var titles = new SessionTitleService(scope.Ctx);
        titles.RegisterProvider(new FirstPromptTitleProvider());
        Assert.Equal("Hello world", titles.TitleFor(session));
    }

    public static void InjectedContext_IsNotTheTitle()
    {
        using var scope = new TestScope();
        var session = scope.Store.Create(new SessionId("title-2"));
        // Plugin-injected context is user-role but is not a human prompt.
        session.Append(new UserMessageEvent
        {
            Message = new UserMessage
            {
                Id = new MessageId("msg-inject"),
                Content = new ContentBlock[] { new TextBlock("file changed") },
                Source = new PluginSource { Plugin = "watcher" },
            },
            SurfaceOp = SurfaceOp.Append,
        });
        session.Append(TestEvents.UserPrompt("real prompt", "msg-user"));
        var titles = new SessionTitleService(scope.Ctx);
        titles.RegisterProvider(new FirstPromptTitleProvider());
        Assert.Equal("real prompt", titles.TitleFor(session));
    }

    public static void NoProvider_FailsExplicit()
    {
        using var scope = new TestScope();
        var session = scope.Store.Create(new SessionId("title-3"));
        var titles = new SessionTitleService(scope.Ctx);
        Assert.Throws<InvalidOperationException>(
            () => titles.TitleFor(session),
            "deriving a title without a registered provider must fail explicitly");
    }

    public static void SecondProvider_FailsLoud()
    {
        using var scope = new TestScope();
        var titles = new SessionTitleService(scope.Ctx);
        titles.RegisterProvider(new FirstPromptTitleProvider());
        Assert.Throws<InvalidOperationException>(
            () => titles.RegisterProvider(new FirstPromptTitleProvider()),
            "a second session-title provider registration must fail loud");
    }

    public static void NoUserPrompt_ReturnsNull()
    {
        using var scope = new TestScope();
        var session = scope.Store.Create(new SessionId("title-4"));
        session.Append(new TurnStartEvent { Turn = 1 });
        session.Append(new TurnEndEvent { Turn = 1, Reason = new CompletedReason() });
        var titles = new SessionTitleService(scope.Ctx);
        titles.RegisterProvider(new FirstPromptTitleProvider());
        Assert.Null(titles.TitleFor(session), "a log with no user prompt must derive no title");
    }
}
