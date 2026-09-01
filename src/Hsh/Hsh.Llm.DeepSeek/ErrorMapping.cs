using System.Text.RegularExpressions;

namespace Harness.Llm.DeepSeek;

/// <summary>
/// Map DeepSeek HTTP and API-error facts to stable provider-neutral harness codes. Ports the TS
/// <c>httpErrorCode</c> and the context-window/quota classifiers from hsh-llm's error module.
/// </summary>
internal static class ErrorMapping
{
    private static readonly Regex StructuredContextOverflow = new(
        @"(?:^|[^a-z0-9])context[\s_-](?:length|window)[\s_-](?:exceed(?:ed|s)?|overflow(?:ed)?|limit[\s_-]exceeded)(?:$|[^a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MaxContextLength = new(
        @"\b(?:maximum|max)(?:\s+(?:allowed|supported))?\s+context\s+(?:length|window)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TooLargeForContext = new(
        @"\b(?:request|prompt|input|messages?)\s+(?:is\s+|are\s+)?too\s+(?:large|long)\s+for\s+(?:(?:this|the)\s+)?(?:model(?:'s)?\s+)?context(?:\s+window)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TooLongForModel = new(
        @"\b(?:input|prompt|request)\s+(?:is\s+)?too\s+(?:long|large)\s+for\s+(?:this|the)\s+model\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExceedsModelContext = new(
        @"\b(?:input|prompt|request|messages?)\b.{0,40}\b(?:exceed(?:s|ed)?|overflows?|is\s+larger\s+than)\b.{0,40}\b(?:the\s+)?(?:model(?:'s)?\s+)?context(?:\s+(?:length|window))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InsufficientQuota = new(
        @"\binsufficient[\s_-]+(?:quota|balance|credits?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuotaUsageLimitExceeded = new(
        @"\b(?:quota|usage[\s_-]+limit)[\s_-]+(?:exceeded|exhausted|reached)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExceededCurrentQuota = new(
        @"\bexceed(?:ed|s)?[\s_-]+(?:(?:your|the)[\s_-]+)?(?:current[\s_-]+)?quota\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BalanceCreditsExhausted = new(
        @"\b(?:balance|credits?)[\s_-]+(?:exhausted|depleted)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OutOfCreditsBudget = new(
        @"\bout[\s_-]+of[\s_-]+(?:credits?|budget)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Map an HTTP status and parsed provider error to the harness error code.</summary>
    internal static string HttpErrorCode(int status, WireError? error)
    {
        if (status == 401 || status == 403) return "AUTH";
        if (status == 413) return "INVALID_REQUEST";
        var detail = JoinDetail(error);
        if (IsQuotaExceededError(detail)) return "QUOTA";
        if (status == 429) return "RATE_LIMIT";
        if (status == 400)
        {
            if (IsContextWindowExceededError(detail)) return "CONTEXT_WINDOW_EXCEEDED";
            return "INVALID_REQUEST";
        }
        if (status >= 500) return "SERVER";
        return $"HTTP_{status}";
    }

    /// <summary>Recognize provider wording that identifies a request exceeding the model context window.</summary>
    internal static bool IsContextWindowExceededError(string detail)
        => StructuredContextOverflow.IsMatch(detail)
        || MaxContextLength.IsMatch(detail)
        || TooLargeForContext.IsMatch(detail)
        || TooLongForModel.IsMatch(detail)
        || ExceedsModelContext.IsMatch(detail);

    /// <summary>Recognize provider wording that identifies exhausted account quota rather than a transient rate limit.</summary>
    internal static bool IsQuotaExceededError(string detail)
        => InsufficientQuota.IsMatch(detail)
        || QuotaUsageLimitExceeded.IsMatch(detail)
        || ExceededCurrentQuota.IsMatch(detail)
        || BalanceCreditsExhausted.IsMatch(detail)
        || OutOfCreditsBudget.IsMatch(detail);

    /// <summary>Join provider code/type/message into one classifier input, skipping empty fields.</summary>
    private static string JoinDetail(WireError? error)
    {
        var body = error?.Error;
        if (body is null) return string.Empty;
        return string.Join(" ", new[] { body.Code, body.Type, body.Message }.Where(field => !string.IsNullOrEmpty(field)));
    }
}
