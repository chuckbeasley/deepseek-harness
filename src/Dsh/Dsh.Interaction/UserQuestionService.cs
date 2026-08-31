using Cordis.Core;

namespace Dsh.Interaction;

/// <summary>
/// The user-questions capability seam (port of the TS <c>user-questions</c>): pause an agent tool
/// call until the human answers through a composed answerer on the <c>user-questions/ask</c>
/// waterfall. The model-facing consumer is the <c>ask_user_question</c> tool; a UI surface (the
/// web approval flow) answers on the waterfall. An ask with no answerer fails closed with
/// <see cref="UserQuestionError"/> code <c>UNAVAILABLE</c>; an aborted token settles
/// <c>ASK_ABORTED</c>.
/// </summary>
public sealed class UserQuestionService : Service
{
    /// <summary>The waterfall answerers compose on.</summary>
    public const string AskEvent = "user-questions/ask";

    /// <summary>Create and register the service as <c>userQuestions</c>.</summary>
    public UserQuestionService(Context ctx)
        : base(ctx, "userQuestions")
    {
    }

    /// <summary>
    /// Ask the composed answerer waterfall and wait for the human's answer.
    /// </summary>
    /// <param name="request">the questions and the abort token.</param>
    /// <returns>the answer chosen or typed by the human.</returns>
    /// <exception cref="UserQuestionError">code <c>ASK_ABORTED</c> when the token is already or
    /// becomes aborted, <c>EMPTY_QUESTIONS</c> when the request names none, or
    /// <c>UNAVAILABLE</c> when no answerer composes.</exception>
    public async Task<UserQuestionAnswer> AskAsync(UserQuestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CancellationToken is { IsCancellationRequested: true }) throw Aborted();
        if (request.Questions.Count == 0) throw new UserQuestionError("ask_user_question requires at least one question", "EMPTY_QUESTIONS");
        Task<UserQuestionAnswer> answer;
        try
        {
            answer = Ctx.Waterfall<Task<UserQuestionAnswer>>(AskEvent, new object?[] { request },
                () => throw new UserQuestionError("no user-questions answerer is composed", "UNAVAILABLE"));
        }
        catch (UserQuestionError)
        {
            throw;
        }
        catch (Exception)
        {
            throw new UserQuestionError("the user-questions answerer failed", "UNAVAILABLE");
        }
        if (request.CancellationToken is not { } token) return await answer;
        var tcs = new TaskCompletionSource<UserQuestionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => tcs.TrySetException(Aborted()));
        _ = answer.ContinueWith(completed =>
        {
            if (completed.IsFaulted) tcs.TrySetException(completed.Exception!.InnerException ?? completed.Exception);
            else if (completed.IsCanceled) tcs.TrySetException(Aborted());
            else tcs.TrySetResult(completed.Result);
        }, TaskScheduler.Default);
        return await tcs.Task;
    }

    private static UserQuestionError Aborted()
        => new("ask_user_question was aborted before the user answered", "ASK_ABORTED");
}
