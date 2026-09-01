using System.Runtime.CompilerServices;
using Harness.Cordis.Core;
using Harness.Agent;
using Harness.AgentLoop;
using Harness.Hooks;
using Harness.Llm;
using Harness.Session;
using Harness.Session.Persistence;
using Harness.Shell;
using Harness.Spike;
using Harness.Subprocess;
using Harness.SystemPrompt;
using Harness.Todo;
using Harness.Tools;

namespace Harness.Hooks.Tests;

/// <summary>One disposable temp directory used for hook configs, captures, and the persistence root.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hsh-hooks-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // best-effort cleanup
        }
    }
}

/// <summary>One in-process loop harness with the shell seam and a request-capturing mock adapter.</summary>
internal sealed class Harness : IDisposable
{
    public required Context Ctx { get; init; }
    public required SessionStore Sessions { get; init; }
    public required global::Harness.AgentLoop.AgentLoop Loop { get; init; }
    public required CapturingAdapter Llm { get; init; }
    public required TempDir Temp { get; init; }

    public static Harness Create()
    {
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var llm = new LlmRuntime(ctx);
        var tools = new ToolRuntime(ctx);
        _ = new SystemPromptService(ctx);
        _ = new AgentRegistry(ctx);
        var temp = new TempDir();
        var persistence = new SessionPersistenceService(ctx, new PersistenceConfig { Root = temp.Path });
        _ = persistence.Attach(sessions);
        _ = new LocalSubprocessProvider(ctx);
        _ = new LocalShellProvider(ctx, new ShellConfig { TimeoutMs = 15_000 });
        var capturing = new CapturingAdapter(new MockLlmProvider());
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, capturing);
        _ = new TodoService(ctx, allowParallelInProgress: false);
        tools.Register(TodoTool.Definition(ctx, allowParallelInProgress: false));
        var loop = new global::Harness.AgentLoop.AgentLoop(ctx);
        return new Harness
        {
            Ctx = ctx,
            Sessions = sessions,
            Loop = loop,
            Llm = capturing,
            Temp = temp,
        };
    }

    public void Dispose()
    {
        Ctx.Dispose();
        Temp.Dispose();
    }

    /// <summary>Create one mock-route session and run one prompt to idle.</summary>
    public global::Harness.Session.Session RunTurn(string sessionId, string prompt)
    {
        var handle = Loop.Create(new SessionId(sessionId), new AgentOptions { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });
        var driver = Loop.GetLoop(new SessionId(sessionId))!;
        driver.Followup(new UserMessage
        {
            Id = new MessageId(Guid.NewGuid().ToString("N")),
            Content = new ContentBlock[] { new TextBlock(prompt) },
            Source = new UserSource(),
        });
        driver.WhenIdleAsync().GetAwaiter().GetResult();
        return handle.Agent.Session;
    }
}

/// <summary>A request-capturing adapter delegating to the inner mock.</summary>
internal sealed class CapturingAdapter : ILlmAdapter
{
    private readonly ILlmAdapter _inner;

    public CapturingAdapter(ILlmAdapter inner)
    {
        _inner = inner;
    }

    public List<GenerateOptions> Requests { get; } = new();

    public async IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        await foreach (var chunk in _inner.StreamAsync(request, ct))
        {
            yield return chunk;
        }
    }
}

/// <summary>Write .cmd helper scripts that capture stdin and echo one fixed decision JSON.</summary>
internal static class HookScripts
{
    /// <summary>Write a helper that captures stdin to <paramref name="capturePath"/> and echoes <paramref name="json"/>.</summary>
    public static string WriteCaptureEcho(string dir, string capturePath, string json)
    {
        var path = Path.Combine(dir, "hook.cmd");
        var script = "@echo off\r\nfindstr /r \".*\" > \"" + capturePath + "\"\r\necho " + json + "\r\n";
        File.WriteAllText(path, script);
        return path;
    }

    /// <summary>Write a helper that echoes <paramref name="json"/> and exits 2 (the blocking contract).</summary>
    public static string WriteBlockingEcho(string dir, string json)
    {
        var path = Path.Combine(dir, "block.cmd");
        File.WriteAllText(path, "@echo off\r\necho " + json + "\r\nexit /b 2\r\n");
        return path;
    }

    /// <summary>Write one Claude Code hooks.json with a single PreToolUse matcher group.</summary>
    public static string WriteClaudePreTool(string dir, string hookCommand, string matcher, string point = "PreToolUse")
    {
        var path = Path.Combine(dir, "hooks.json");
        var hooks = new Dictionary<string, object>
        {
            [point] = new object[]
            {
                new { matcher, hooks = new object[] { new { type = "command", command = hookCommand } } },
            },
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { hooks }));
        return path;
    }

    /// <summary>Write one Codex hooks.json with a single event group.</summary>
    public static string WriteCodexConfig(string dir, string eventName, string hookCommand, string matcher)
    {
        var path = Path.Combine(dir, "codex-hooks.json");
        var hooks = new Dictionary<string, object>
        {
            [eventName] = new object[]
            {
                new { matcher, hooks = new object[] { new { type = "command", command = hookCommand } } },
            },
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { hooks }));
        return path;
    }
}
