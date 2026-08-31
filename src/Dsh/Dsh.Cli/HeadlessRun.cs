using Cordis.Core;
using Cordis.Plugin.Loader;

namespace Dsh.Cli;

/// <summary>
/// The headless one-shot app row: run one task through the real agent loop and exit. The provider
/// route follows <c>DEEPSEEK_API_KEY</c> — set, the real DeepSeek adapter; unset, the mock route.
/// The final assistant message's text prints to stdout, then <c>appExit</c> exits the process.
/// </summary>
public sealed class HeadlessRun : ILoaderPlugin
{
    /// <summary>The session id the headless run publishes.</summary>
    public const string SessionIdValue = "session-headless";

    /// <inheritdoc />
    public ValueTask<IDisposable?> ApplyAsync(Cordis.Core.Context ctx, object? config)
    {
        var args = ctx.Get<CmdlineArgs>("cmdlineArgs") ?? new CmdlineArgs(Array.Empty<string>());
        var task = string.Join(' ', args.Args);
        if (task.Trim().Length == 0)
        {
            Console.Error.WriteLine("dsh: headless needs a task argument (dsh --profile headless \"task\")");
            Exit(ctx, 1);
            return ValueTask.FromResult<IDisposable?>(null);
        }
        var loop = ctx.Get<Dsh.AgentLoop.AgentLoop>("agentLoop")
            ?? throw new InvalidOperationException("dsh: headless requires the \"agentLoop\" row");
        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        // A snapshot run follows the recorded fixture's route; otherwise the key decides
        // between the real DeepSeek adapter and the keyless mock route.
        var provider = Dsh.Llm.Replay.SnapshotEnv.Provider
            ?? (string.IsNullOrEmpty(key) ? Dsh.Spike.MockLlmProvider.Provider : "deepseek");
        var model = Dsh.Llm.Replay.SnapshotEnv.Model
            ?? (string.IsNullOrEmpty(key) ? Dsh.Spike.MockLlmProvider.Model : "deepseek-chat");
        var sessionId = new Dsh.Session.SessionId(SessionIdValue);
        var message = new Dsh.Llm.UserMessage
        {
            Id = new Dsh.Llm.MessageId(Guid.NewGuid().ToString("N")),
            Content = new Dsh.Llm.ContentBlock[] { new Dsh.Llm.TextBlock(task) },
            Source = new Dsh.Llm.UserSource(),
        };

        // The session is created and the turn starts only after the loader settles
        // (appReady.Commit), so later rows (e.g. the snapshot replay and policy-baseline rows)
        // are mounted before the session exists and the first model call runs; Main blocks on
        // the appExit signal.
        var ready = ctx.Get<AppReady>("appReady")
            ?? throw new InvalidOperationException("dsh: headless requires the appReady launcher fact");
        ready.OnReady(() =>
        {
            var handle = loop.Create(sessionId, new Dsh.Agent.AgentOptions { Provider = provider, Model = model });
            var driver = loop.GetLoop(sessionId)
                ?? throw new InvalidOperationException("dsh: headless published no loop");
            driver.Send(message, Dsh.Agent.InboxTarget.NextTurn, wakeup: true);
            _ = Task.Run(async () =>
            {
                try
                {
                    await driver.WhenIdleAsync();
                    var last = handle.Agent.Session.Events.OfType<Dsh.Session.AssistantMessageEvent>().LastOrDefault();
                    if (last is null)
                    {
                        Console.Error.WriteLine("dsh: the turn produced no assistant message");
                        Exit(ctx, 1);
                        return;
                    }
                    var textBlocks = last.Message.Content.OfType<Dsh.Llm.TextBlock>().ToArray();
                    if (textBlocks.Length == 0)
                    {
                        Console.Out.WriteLine("[the final assistant message carries no text]");
                    }
                    else
                    {
                        foreach (var block in textBlocks) Console.Out.WriteLine(block.Text);
                    }
                    Exit(ctx, 0);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine($"dsh: headless run failed: {error.Message}");
                    Exit(ctx, 1);
                }
            });
        });
        return ValueTask.FromResult<IDisposable?>(null);
    }

    private static void Exit(Cordis.Core.Context ctx, int code)
    {
        var exit = ctx.Get<AppExit>("appExit")
            ?? throw new InvalidOperationException("dsh: headless requires the appExit launcher fact");
        exit.Exit(code);
    }
}
