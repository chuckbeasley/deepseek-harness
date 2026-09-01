using System.Text.Json;
using Harness.Llm;
using Harness.Session;
using Harness.Tools;

namespace Harness.Spike;

/// <summary>
/// Straight-line one-turn driver (spike-design.md section 6): turn/start -> step/start ->
/// user/message -> request/header + request/context -> stream (through the llm/stream waterfall) ->
/// assistant/chunk* + assistant/message -> tool/call -> tools.ExecuteAsync (the tool appends its
/// own durable events inside its body) -> tool/result -> step/end; a second step feeds the tool
/// result back with no new user message; turn/end. The full agent-loop (inbox and its
/// agent/inbox/spliced events, agent/* events, the parallel scheduler) is Phase 2+ scope.
/// </summary>
public static class HeadlessTurnDriver
{
    /// <summary>Fixed system prompt for the smoke (pinned fixture literal).</summary>
    public const string SystemPrompt = "You are the Dsh port spike assistant.";

    public static async Task RunOneTurnAsync(Harness.Session.Session session, LlmRuntime llm, ToolRuntime tools, UserMessage userMessage)
    {
        const long turn = 1;
        const long step = 1;

        session.Append(new TurnStartEvent { Turn = turn });

        // Step 1
        session.Append(new StepStartEvent { Turn = turn, Step = step });
        session.Append(new UserMessageEvent { Message = userMessage, SurfaceOp = SurfaceOp.Append });
        var schemas = tools.Schemas();
        session.Append(new RequestHeaderEvent
        {
            Header = new EpochHeader
            {
                Config = new LlmCallConfig(MockLlmProvider.Provider, MockLlmProvider.Model),
                System = SystemPrompt,
                Tools = schemas,
            },
            Reason = RequestHeaderReason.Initial,
        });
        session.Append(new RequestContextEvent { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model });

        var request = new GenerateOptions(
            MockLlmProvider.Provider, MockLlmProvider.Model,
            session.DeriveMessages(), System: SystemPrompt, Tools: schemas);

        var assembler = new BlockAssembler();
        var chunkSeqs = new List<long>();
        await foreach (var chunk in llm.Stream(request, CancellationToken.None))
        {
            chunkSeqs.Add(session.Append(new AssistantChunkEvent { Turn = turn, Step = step, Chunk = chunk }).Seq);
            assembler.Push(chunk);
        }

        var assistant = new AssistantMessage
        {
            Id = new MessageId("msg-assistant-1"),
            Content = assembler.Blocks(),
            Source = new ModelSource { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model },
        };
        session.Append(new AssistantMessageEvent
        {
            Turn = turn, Step = step, Message = assistant,
            SurfaceOp = SurfaceOp.Append, SourceEventSeqs = chunkSeqs,
        });

        var toolCall = (ToolCallBlock)assembler.Blocks().Single(b => b is ToolCallBlock);
        var callSeq = session.Append(new ToolCallEvent
        {
            Turn = turn, Step = step, CallId = toolCall.Id, Name = toolCall.Name, Arguments = toolCall.Arguments,
        }).Seq;

        var execInput = new ToolExecutionInput(toolCall.Id, toolCall.Name, ParseArguments(toolCall.Arguments), CancellationToken.None)
        {
            Session = session,
        };
        var result = tools.ExecuteAsync(execInput, CancellationToken.None).GetAwaiter().GetResult();

        session.Append(new ToolResultEvent
        {
            Turn = turn, Step = step,
            Message = new ToolResultMessage
            {
                Id = new MessageId("msg-tool-1"),
                Content = new ContentBlock[] { new ToolResultBlock(toolCall.Id, result.Content, result.IsError) },
                Source = new ToolSource { CallId = toolCall.Id },
            },
            Error = result is ToolExecutionFailure failure
                ? new ToolErrorInfo(failure.Error.Name ?? "Error", failure.Error.Code ?? "UNKNOWN")
                : null,
            SurfaceOp = SurfaceOp.Append,
            SourceEventSeqs = new long[] { callSeq },
        });
        session.Append(new StepEndEvent { Turn = turn, Step = step });

        // Step 2: the tool result owes the model another request; the derived history now
        // includes the tool result and no new user message enters the log.
        const long step2 = 2;
        session.Append(new StepStartEvent { Turn = turn, Step = step2 });
        var request2 = new GenerateOptions(
            MockLlmProvider.Provider, MockLlmProvider.Model,
            session.DeriveMessages(), System: SystemPrompt, Tools: schemas);
        var assembler2 = new BlockAssembler();
        var chunkSeqs2 = new List<long>();
        await foreach (var chunk in llm.Stream(request2, CancellationToken.None))
        {
            chunkSeqs2.Add(session.Append(new AssistantChunkEvent { Turn = turn, Step = step2, Chunk = chunk }).Seq);
            assembler2.Push(chunk);
        }
        var assistant2 = new AssistantMessage
        {
            Id = new MessageId("msg-assistant-2"),
            Content = assembler2.Blocks(),
            Source = new ModelSource { Provider = MockLlmProvider.Provider, Model = MockLlmProvider.Model },
        };
        session.Append(new AssistantMessageEvent
        {
            Turn = turn, Step = step2, Message = assistant2,
            SurfaceOp = SurfaceOp.Append, SourceEventSeqs = chunkSeqs2,
        });
        session.Append(new StepEndEvent { Turn = turn, Step = step2 });

        session.Append(new TurnEndEvent { Turn = turn, Reason = new CompletedReason() });
    }

    private static JsonElement ParseArguments(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}




