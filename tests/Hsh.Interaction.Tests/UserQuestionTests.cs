using Harness.Cordis.Core;
using Harness.Interaction;

namespace Harness.Interaction.Tests;

/// <summary>
/// The user-questions seam and its model-facing consumer: validation, the answerer waterfall, the
/// fail-closed vocabulary, and the ask_user_question tool definition.
/// </summary>
public static class UserQuestionTests
{
    private static readonly UserQuestionItem Question = new("q1", "Proceed?");

    public static void EmptyQuestions_Reject()
    {
        var ctx = new Context();
        try
        {
            var questions = new UserQuestionService(ctx);
            var error = Assert.ThrowsAny<UserQuestionError>(
                () => questions.AskAsync(new UserQuestionRequest(Array.Empty<UserQuestionItem>())),
                "no questions must be refused");
            Assert.Equal("EMPTY_QUESTIONS", error.Code, "the stable code");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void NoAnswerer_FailsClosed()
    {
        var ctx = new Context();
        try
        {
            var questions = new UserQuestionService(ctx);
            var error = Assert.ThrowsAny<UserQuestionError>(
                () => questions.AskAsync(new UserQuestionRequest(new[] { Question })),
                "no answerer must fail closed");
            Assert.Equal("UNAVAILABLE", error.Code, "the stable code");
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void TheAnswererWaterfall_AnswersTheAsk()
    {
        var ctx = new Context();
        try
        {
            var questions = new UserQuestionService(ctx);
            var registration = ctx.On("user-questions/ask",
                new Func<UserQuestionRequest, Func<Task<UserQuestionAnswer>>, Task<UserQuestionAnswer>>((request, next) =>
                    Task.FromResult(new UserQuestionAnswer(new[] { new UserQuestionAnswerItem("q1", new[] { "Yes" }) }))));
            try
            {
                var answer = questions.AskAsync(new UserQuestionRequest(new[] { Question })).GetAwaiter().GetResult();
                Assert.Equal("q1", answer.Answers.Single().Id, "the answer echoes the question id");
                Assert.Equal(1, answer.Answers.Single().Selected.Count, "the selected label rides back");
            }
            finally
            {
                registration.Dispose();
            }
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void AnAbortedAsk_SettlesAskAborted()
    {
        var ctx = new Context();
        try
        {
            var questions = new UserQuestionService(ctx);
            var gate = new TaskCompletionSource<UserQuestionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ctx.On("user-questions/ask",
                new Func<UserQuestionRequest, Func<Task<UserQuestionAnswer>>, Task<UserQuestionAnswer>>((request, next) => gate.Task));
            try
            {
                using var cts = new CancellationTokenSource();
                var ask = questions.AskAsync(new UserQuestionRequest(new[] { Question }, CancellationToken: cts.Token));
                cts.Cancel();
                var error = Assert.ThrowsAny<UserQuestionError>(
                    () => { ask.GetAwaiter().GetResult(); return Task.CompletedTask; },
                    "the abort settles the ask");
                Assert.Equal("ASK_ABORTED", error.Code, "the stable code");
            }
            finally
            {
                registration.Dispose();
            }
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public static void AskUserTool_RegistersItsDefinition()
    {
        var ctx = new Context();
        try
        {
            _ = new UserQuestionService(ctx);
            var definition = AskUserTool.Definition(ctx);
            Assert.Equal("ask_user_question", definition.Name, "the tool name");
            Assert.True(definition.Parameters.TryGetProperty("questions", out _), "the schema carries the questions array");
            Assert.True(definition.OutputSchema.GetProperty("properties").TryGetProperty("answers", out _), "the output schema carries answers");
        }
        finally
        {
            ctx.Dispose();
        }
    }
}
