using System.Text.Json;

namespace Harness.Tui;

/// <summary>Deployment choices of one TUI run.</summary>
public sealed record TuiOptions
{
    /// <summary>Whether tool calls open an approval dialog before execution (default: true).</summary>
    public bool ApproveTools { get; init; } = true;

    /// <summary>Whether the run exits by itself after a short scripted turn (the smoke path).</summary>
    public bool Smoke { get; init; }
}

/// <summary>
/// The interactive terminal UI: a transcript pane fed live from the session log, an input line
/// that drives the real agent loop, a todo panel projected from todo/write events, tool-call
/// disclosure through the transcript, and an approval dialog on the tools/pre-execute waterfall.
/// </summary>
public static class TuiApp
{
    private sealed class UiState
    {
        public required Context Ctx { get; init; }
        public required LoopAgent Driver { get; init; }
        public required TranscriptModel Transcript { get; init; }
        public required TodoModel Todos { get; init; }
        public required TextView TranscriptView { get; init; }
        public required ListView TodoView { get; init; }
        public required string TempRoot { get; init; }
        public required object ApproveGate { get; init; }
    }

    /// <summary>Boot the spine and run the interactive UI until the operator quits (F9).</summary>
    public static int Run(string[] args)
    {
        var options = ParseArgs(args);
        var state = Boot(options);
        try
        {
            if (options.Smoke || Console.IsOutputRedirected || Console.IsInputRedirected)
            {
                // Headless run: the fake driver renders to an in-memory screen.
                Application.Init(new FakeDriver());
            }
            else
            {
                Application.Init();
            }
            var top = BuildWindow(state);
            WireModels(state);
            if (options.Smoke)
            {
                // Scripted smoke: one canned task with tool approvals auto-approved, then exit.
                var task = new UserMessage
                {
                    Id = new MessageId("msg-tui-smoke-1"),
                    Content = new ContentBlock[] { new TextBlock("Record a two-item plan as todos.") },
                    Source = new UserSource(),
                };
                state.Driver.Send(task, InboxTarget.NextTurn, wakeup: true);
                Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(100), _ =>
                {
                    if (state.Driver.IsRunning) return true;
                    Application.RequestStop();
                    return false;
                });
            }
            Application.Run(top);
            Application.Shutdown();
            return 0;
        }
        finally
        {
            state.Ctx.Dispose();
            if (Directory.Exists(state.TempRoot))
            {
                Directory.Delete(state.TempRoot, recursive: true);
            }
        }
    }

    /// <summary>Boot the spine: mock route without a key, the real DeepSeek provider with one.</summary>
    private static UiState Boot(TuiOptions options)
    {
        var ctx = new Context();
        var sessions = new SessionStore(ctx);
        var llm = new LlmRuntime(ctx);
        var tools = new ToolRuntime(ctx);
        var systemPrompt = new SystemPromptService(ctx);
        var agents = new AgentRegistry(ctx);
        var tempRoot = Path.Combine(Path.GetTempPath(), "hsh-tui-" + Guid.NewGuid().ToString("N"));
        var persistence = new SessionPersistenceService(ctx, new PersistenceConfig { Root = tempRoot });
        var todoService = new TodoService(ctx, allowParallelInProgress: false);
        tools.Register(TodoTool.Definition(ctx, allowParallelInProgress: false));
        persistence.Attach(sessions);
        var loop = new Harness.AgentLoop.AgentLoop(ctx);

        var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        var provider = MockLlmProvider.Provider;
        var model = MockLlmProvider.Model;
        if (!string.IsNullOrEmpty(key))
        {
            provider = "deepseek";
            model = "deepseek-chat";
            llm.RegisterAdapter(new[] { provider }, new DeepSeekAdapter(new DeepSeekConfig { ApiKey = key }));
        }
        else
        {
            llm.RegisterAdapter(new[] { provider }, new MockLlmProvider());
        }

        var handle = loop.Create(new SessionId("session-tui"), new AgentOptions { Provider = provider, Model = model });
        var driver = loop.GetLoop(new SessionId("session-tui"))
            ?? throw new InvalidOperationException("tui: no loop published");

        var transcript = new TranscriptModel();
        var todos = new TodoModel();
        transcript.Reset(handle.Agent.Session.Events);
        todos.Reset(handle.Agent.Session.Events);

        var transcriptView = new TextView { ReadOnly = true, Width = Dim.Fill(), Height = Dim.Fill(), Text = RenderTranscript(transcript) };
        var todoView = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        todoView.SetSource(RenderTodos(todos));

        var state = new UiState
        {
            Ctx = ctx,
            Driver = driver,
            Transcript = transcript,
            Todos = todos,
            TranscriptView = transcriptView,
            TodoView = todoView,
            TempRoot = tempRoot,
            ApproveGate = new object(),
        };

        if (options.ApproveTools)
        {
            ctx.On("tools/pre-execute", new Func<ToolRunContext, Func<Task<PreToolDecision>>, Task<PreToolDecision>>(async (exec, _) =>
            {
                var pending = new PendingApproval(exec.Name, exec.Arguments);
                Application.MainLoop.Invoke(() => ShowApprovalDialog(state, pending));
                var outcome = await pending.Outcome;
                return outcome == ApprovalOutcome.Approved
                    ? new AllowDecision()
                    : new DenyDecision("denied by the operator in the TUI");
            }));
        }

        return state;
    }

    /// <summary>Build the three-pane window: transcript, input line, and the todo panel.</summary>
    private static Window BuildWindow(UiState state)
    {
        var window = new Window("hsh - DeepSeek Harness (F9 quits)")
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };
        window.KeyPress += args =>
        {
            if (args.KeyEvent.Key == Key.F9) Application.RequestStop();
        };

        var transcriptFrame = new FrameView("transcript")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(30),
            Height = Dim.Fill(1),
        };
        transcriptFrame.Add(state.TranscriptView);
        window.Add(transcriptFrame);

        var todoFrame = new FrameView("todos")
        {
            X = Pos.Right(transcriptFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        todoFrame.Add(state.TodoView);
        window.Add(todoFrame);

        var input = new TextField
        {
            X = 0,
            Y = Pos.Bottom(transcriptFrame),
            Width = Dim.Fill(),
            Height = 1,
        };
        input.KeyPress += args =>
        {
            if (args.KeyEvent.Key != Key.Enter) return;
            var text = input.Text?.ToString() ?? string.Empty;
            if (text.Trim().Length == 0) return;
            input.Text = string.Empty;
            var message = new UserMessage
            {
                Id = new MessageId(Guid.NewGuid().ToString("D")),
                Content = new ContentBlock[] { new TextBlock(text) },
                Source = new UserSource(),
            };
            state.Driver.Send(message, InboxTarget.NextTurn, wakeup: true);
        };
        window.Add(input);
        return window;
    }

    /// <summary>Subscribe the session log to the view models and re-render on the UI thread.</summary>
    private static void WireModels(UiState state)
    {
        state.Ctx.On("session/event", (Delegate)(Action<Harness.Session.Session, SessionEvent>)((_, evt) =>
        {
            state.Transcript.Apply(evt);
            if (evt is TodoWriteEvent todo) state.Todos.Apply(todo);
            Application.MainLoop.Invoke(() => Refresh(state));
        }));
    }

    private static void Refresh(UiState state)
    {
        state.TranscriptView.Text = RenderTranscript(state.Transcript);
        state.TodoView.SetSource(RenderTodos(state.Todos));
    }

    private static List<string> RenderTodos(TodoModel model)
        => model.Rows.Select(row => $"{row.Status,-11} {row.Content}").ToList();

    private static string RenderTranscript(TranscriptModel model)
        => string.Join(Environment.NewLine, model.Rows.Select(RenderRow)) + Environment.NewLine;

    private static string RenderRow(TranscriptRow row) => row.Kind switch
    {
        TranscriptRowKind.User => $"> {row.Text}",
        TranscriptRowKind.Assistant => row.Text,
        TranscriptRowKind.Tool => $"  [tool] {row.Text}{(row.IsError ? "  (error)" : string.Empty)}",
        _ => row.Text,
    };

    /// <summary>Open one modal approval dialog for a pending tool call.</summary>
    private static void ShowApprovalDialog(UiState state, PendingApproval pending)
    {
        var arguments = pending.Arguments.ValueKind == JsonValueKind.Undefined
            ? string.Empty
            : pending.Arguments.ToString();
        var approve = new Button("Approve");
        var deny = new Button("Deny");
        approve.Clicked += ApproveHandler;
        deny.Clicked += DenyHandler;
        var dialog = new Dialog(
            $"Approve tool call: {pending.ToolName}",
            width: 70,
            height: Math.Min(20, 5 + arguments.Split('\n').Length + 2),
            approve,
            deny);

        void ApproveHandler()
        {
            pending.Decide(ApprovalOutcome.Approved);
            Application.RequestStop();
        }

        void DenyHandler()
        {
            pending.Decide(ApprovalOutcome.Denied);
            Application.RequestStop();
        }
        var body = new TextView
        {
            Text = arguments,
            ReadOnly = true,
            X = 1,
            Y = 1,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
        };
        dialog.Add(body);
        Application.Run(dialog);
    }

    private static TuiOptions ParseArgs(string[] args)
    {
        var options = new TuiOptions();
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--smoke":
                    options = options with { Smoke = true, ApproveTools = false };
                    break;
                case "--approve-tools":
                    options = options with { ApproveTools = true };
                    break;
                case "--no-approve-tools":
                    options = options with { ApproveTools = false };
                    break;
                default:
                    throw new ArgumentException($"hsh tui: unknown argument {arg}");
            }
        }
        return options;
    }
}
