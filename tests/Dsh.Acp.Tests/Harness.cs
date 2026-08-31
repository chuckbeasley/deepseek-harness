using System.IO.Pipelines;
using Cordis.Core;
using Dsh.Acp;
using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Sdk.Client;
using Dsh.Sdk.Protocol;
using Dsh.Session;
using Dsh.Session.Persistence;
using Dsh.Spike;
using Dsh.SystemPrompt;
using Dsh.Todo;
using Dsh.Tools;

namespace Dsh.Acp.Tests;

/// <summary>One in-process ACP server over a real transport, with the approval gate mounted.</summary>
internal sealed class Harness : IDisposable
{
    public required Context Ctx { get; init; }
    public required LlmRuntime Llm { get; init; }
    public required SessionStore Sessions { get; init; }
    public required Dsh.AgentLoop.AgentLoop Loop { get; init; }
    public required ApprovalService Approval { get; init; }
    public required JsonRpcLineTransport Client { get; init; }
    public required JsonRpcLineTransport Server { get; init; }
    private readonly Pipe[] _pipes;
    private readonly string _tempRoot;

    private Harness(Pipe[] pipes, string tempRoot)
    {
        _pipes = pipes;
        _tempRoot = tempRoot;
    }

    public static Harness Create(AcpHarnessOptions options)
    {
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var llm = new LlmRuntime(ctx);
        var tools = new ToolRuntime(ctx);
        _ = new SystemPromptService(ctx);
        _ = new AgentRegistry(ctx);
        var tempRoot = Path.Combine(Path.GetTempPath(), "dsh-acp-tests-" + Guid.NewGuid().ToString("N"));
        var persistence = new SessionPersistenceService(ctx, new PersistenceConfig { Root = tempRoot });
        _ = persistence.Attach(sessions);
        _ = new ApprovalService(ctx, ApprovalPolicy.Ask);
        llm.RegisterAdapter(new[] { MockLlmProvider.Provider }, new MockLlmProvider());
        if (options.SlowAdapter is not null)
        {
            llm.RegisterAdapter(new[] { SlowAdapter.Provider }, options.SlowAdapter);
        }
        _ = new TodoService(ctx, allowParallelInProgress: false);
        tools.Register(TodoTool.Definition(ctx, allowParallelInProgress: false));
        var loop = new Dsh.AgentLoop.AgentLoop(ctx);
        var pipes = new[] { new Pipe(), new Pipe() };
        var serverTransport = new JsonRpcLineTransport(pipes[0].Reader.AsStream(), pipes[1].Writer.AsStream());
        var clientTransport = new JsonRpcLineTransport(pipes[1].Reader.AsStream(), pipes[0].Writer.AsStream());
        _ = new AcpServer(ctx, serverTransport, options.Config);
        serverTransport.Start();
        clientTransport.Start();
        return new Harness(pipes, tempRoot)
        {
            Ctx = ctx,
            Llm = llm,
            Sessions = sessions,
            Loop = loop,
            Approval = ctx.Get<ApprovalService>("approval")!,
            Client = clientTransport,
            Server = serverTransport,
        };
    }

    public void Dispose()
    {
        Client.Close();
        Server.Close();
        foreach (var pipe in _pipes)
        {
            pipe.Writer.Complete();
            pipe.Reader.Complete();
        }
        Ctx.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception)
        {
            // best-effort cleanup
        }
    }
}

internal sealed record AcpHarnessOptions(AcpServerConfig Config, SlowAdapter? SlowAdapter = null);

/// <summary>A provider whose first stream stays open until cancellation (the deterministic cancel test).</summary>
internal sealed class SlowAdapter : ILlmAdapter
{
    public const string Provider = "slow";

    public const string Model = "slow-model";

    public async IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        yield return new BlockStart(0, "text");
        yield return new TextDelta(0, "slow ");
        await Task.Delay(TimeSpan.FromSeconds(60), ct);
        yield return new BlockEnd(0, new TextBlock("slow answer"));
        yield return new Finish(new Stop());
    }
}

/// <summary>The built dsh CLI entry and per-test runtime resolution (the profile e2e).</summary>
internal static class Runtime
{
    public static string CliPath { get; } = LoadCliPath();

    public static RuntimeProcessOptions Resolve(string dshHome, string cwd)
        => SdkLaunch.ResolveLaunch(new HarnessClientOptions
        {
            DshBin = CliPath,
            Profile = "acp",
            DshHome = dshHome,
            ProcessCwd = cwd,
        }, Environment.CurrentDirectory);

    private static string LoadCliPath()
    {
        var metadata = typeof(Runtime).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "DshCliPath");
        var path = metadata?.Value;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            throw new InvalidOperationException($"the test build did not locate the dsh CLI at \"{path}\"");
        }
        return path!;
    }
}
