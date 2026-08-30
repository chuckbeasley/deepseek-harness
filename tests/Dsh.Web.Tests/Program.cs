namespace Dsh.Web.Tests;

/// <summary>
/// Zero-dependency console test runner for the web capability seam. The host sandbox blocks
/// dotnet build/dotnet test (MSBuild cannot spawn the C# compiler with captured output), so tests
/// run as a plain console app that exits non-zero on any assertion failure. All fetch traffic goes
/// through the fake HttpMessageHandler — no network is ever touched.
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly List<string> Failures = new();

    /// <summary>Runs every registered test and returns the process exit code.</summary>
    public static int Main()
    {
        Console.WriteLine("Dsh.Web - console assertions");
        Console.WriteLine();

        Run("WebRuntime: registered under the web key", WebRuntimeTests.Registered_UnderWebKey);
        Run("WebRuntime: duplicate search provider throws WEB_DUPLICATE_PROVIDER", WebRuntimeTests.DuplicateSearchProvider_ThrowsDuplicate);
        Run("WebRuntime: duplicate fetch provider throws WEB_DUPLICATE_PROVIDER", WebRuntimeTests.DuplicateFetchProvider_ThrowsDuplicate);
        Run("WebRuntime: configured missing provider throws CONFIGURED_MISSING", WebRuntimeTests.ConfiguredMissing_Throws);
        Run("WebRuntime: configured unavailable provider throws CONFIGURED_UNAVAILABLE", WebRuntimeTests.ConfiguredUnavailable_Throws);
        Run("WebRuntime: no provider throws WEB_PROVIDER_UNAVAILABLE", WebRuntimeTests.NoProvider_ThrowsUnavailable);
        Run("WebRuntime: multiple usable providers throw AMBIGUOUS", WebRuntimeTests.MultipleUsable_ThrowsAmbiguous);
        Run("WebRuntime: single usable provider auto-selects", WebRuntimeTests.SingleUsable_AutoSelects);
        Run("WebRuntime: configured id wins over other usable providers", WebRuntimeTests.ConfiguredId_WinsOverOtherUsable);
        Run("WebRuntime: search result is capped to maxResults", WebRuntimeTests.SearchResult_IsCapped_ToMaxResults);
        Run("WebRuntime: search result uncapped when maxResults omitted or larger", WebRuntimeTests.SearchResult_Uncapped_WhenMaxResultsOmittedOrLarger);
        Run("WebRuntime: disposing a registration unregisters the provider", WebRuntimeTests.DisposingRegistration_UnregistersProvider);
        Run("WebRuntime: fetch resolves through the provider", WebRuntimeTests.Fetch_ResolvesThroughProvider);

        Run("HttpWebProvider: request shape pins method, URL, and headers", HttpWebProviderTests.RequestShape_PinsMethodUrlAndHeaders);
        Run("HttpWebProvider: HTTP 404 is a result, not an error", HttpWebProviderTests.Http404_IsAResult_NotAnError);
        Run("HttpWebProvider: HTTP 429 is a result carrying the status", HttpWebProviderTests.Http429_IsAResult_CarryingStatus);
        Run("HttpWebProvider: HTTP 500 is a result carrying the status", HttpWebProviderTests.Http500_IsAResult_CarryingStatus);
        Run("HttpWebProvider: html content classifies html", HttpWebProviderTests.HtmlContent_ClassifiesHtml);
        Run("HttpWebProvider: json content classifies text", HttpWebProviderTests.JsonContent_ClassifiesText);
        Run("HttpWebProvider: unsupported content type throws", HttpWebProviderTests.UnsupportedContentType_Throws);
        Run("HttpWebProvider: unsupported charset throws", HttpWebProviderTests.UnsupportedCharset_Throws);
        Run("HttpWebProvider: declared Content-Length over cap rejects", HttpWebProviderTests.DeclaredContentLength_OverCap_RejectsImmediately);
        Run("HttpWebProvider: stream past cap is truncated, not rejected", HttpWebProviderTests.Stream_GrowingPastCap_IsTruncated_NotRejected);
        Run("HttpWebProvider: exactly-at-cap body is not flagged truncated", HttpWebProviderTests.Stream_ExactlyAtCap_IsNotFlaggedTruncated);
        Run("HttpWebProvider: decoded body over char cap is truncated", HttpWebProviderTests.DecodedBody_OverCharCap_IsTruncated);
        Run("HttpWebProvider: non-http scheme throws WEB_INVALID_URL", HttpWebProviderTests.NonHttpScheme_ThrowsInvalidUrl);
        Run("HttpWebProvider: credentials in URL throw WEB_BLOCKED_URL", HttpWebProviderTests.CredentialsInUrl_ThrowsBlocked);
        Run("HttpWebProvider: overlong URL throws WEB_INVALID_URL", HttpWebProviderTests.OverlongUrl_ThrowsInvalidUrl);
        Run("HttpWebProvider: same-origin redirect is followed", HttpWebProviderTests.SameOriginRedirect_IsFollowed);
        Run("HttpWebProvider: cross-origin redirect is blocked", HttpWebProviderTests.CrossOriginRedirect_IsBlocked);
        Run("HttpWebProvider: redirect budget is enforced", HttpWebProviderTests.RedirectBudget_IsEnforced);
        Run("HttpWebProvider: redirect without Location throws", HttpWebProviderTests.RedirectWithoutLocation_ThrowsProviderError);
        Run("HttpWebProvider: pre-cancelled token throws WEB_ABORTED", HttpWebProviderTests.PreCancelled_ThrowsAborted);
        Run("HttpWebProvider: transport failure maps to WEB_PROVIDER_ERROR", HttpWebProviderTests.TransportFailure_ThrowsProviderError);
        Run("HttpWebProvider: timeout maps to WEB_FETCH_TIMEOUT", HttpWebProviderTests.Timeout_ThrowsFetchTimeout);
        Run("HttpWebProvider: declared charset is decoded", HttpWebProviderTests.CharsetDeclaration_IsDecoded);

        Run("WebTools: web_fetch executes through ToolRuntime", WebToolsTests.FetchTool_ExecutesThroughToolRuntime);
        Run("WebTools: web_fetch renders a non-200 status", WebToolsTests.FetchTool_Non200Status_RendersStatus);
        Run("WebTools: web_fetch appends the truncation footer", WebToolsTests.FetchTool_ProviderTruncation_AppendsFooter);
        Run("WebTools: web_fetch output cap bounds the complete output", WebToolsTests.FetchTool_OutputCap_BoundsCompleteOutput);
        Run("WebTools: web_fetch blank URL fails loud", WebToolsTests.FetchTool_BlankUrl_FailsLoud);
        Run("WebTools: web_search fails loud with no search provider", WebToolsTests.SearchTool_FailsLoud_WithNoSearchProvider);
        Run("WebTools: web_search carries WEB_PROVIDER_UNAVAILABLE", WebToolsTests.SearchTool_ProviderError_CarriesCode);
        Run("WebTools: web_search single query executes through ToolRuntime", WebToolsTests.SearchTool_SingleQuery_ExecutesThroughToolRuntime);
        Run("WebTools: web_search multiple queries merge round-robin and dedupe", WebToolsTests.SearchTool_MultipleQueries_MergeRoundRobinAndDedupe);
        Run("WebTools: web_search multi-query merge caps at maxResults", WebToolsTests.SearchTool_MultiQuery_MergeCapsAtMaxResults);
        Run("WebTools: web_search no results renders the notice", WebToolsTests.SearchTool_NoResults_RendersNotice);
        Run("WebTools: web_search empty queries fails loud", WebToolsTests.SearchTool_EmptyQueries_FailsLoud);
        Run("WebTools: web_search too many queries fails loud", WebToolsTests.SearchTool_TooManyQueries_FailsLoud);
        Run("WebTools: web_search provider failure fails the call", WebToolsTests.SearchTool_ProviderFailure_FailsTheCall);
        Run("WebTools: HtmlToText strips markup and decodes entities", WebToolsTests.HtmlToText_StripsMarkupAndDecodesEntities);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        if (_failed > 0)
        {
            foreach (var failure in Failures)
            {
                Console.WriteLine("  FAILED: " + failure);
            }
            return 1;
        }
        return 0;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"  PASS {name}");
        }
        catch (AssertionException ex)
        {
            _failed++;
            Failures.Add($"{name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _failed++;
            Failures.Add($"{name}: unexpected {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: unexpected {ex.GetType().Name}: {ex.Message}");
        }
    }
}
