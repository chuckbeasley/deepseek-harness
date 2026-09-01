using System.Text.Json;

namespace Harness.Lsp.Tests;

/// <summary>The pure protocol translation layer (mirrors translate.spec.ts).</summary>
public static class TranslateTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    public static Task RequestMethod_MapsEachOperation()
    {
        Assert.Equal("textDocument/definition", LspTranslate.RequestMethod(LspOperation.GoToDefinition), "goToDefinition maps");
        Assert.Equal("textDocument/references", LspTranslate.RequestMethod(LspOperation.FindReferences), "findReferences maps");
        Assert.Equal("textDocument/implementation", LspTranslate.RequestMethod(LspOperation.GoToImplementation), "goToImplementation maps");
        Assert.Equal("textDocument/hover", LspTranslate.RequestMethod(LspOperation.Hover), "hover maps");
        return Task.CompletedTask;
    }

    public static Task SupportsOperation_ReadsProviderSlotBooleanAndOptionsForms()
    {
        var caps = Json("{\"definitionProvider\":true,\"referencesProvider\":{\"workDoneProgress\":true},\"implementationProvider\":false}");
        Assert.True(LspTranslate.SupportsOperation(caps, LspOperation.GoToDefinition), "boolean true supports");
        Assert.True(LspTranslate.SupportsOperation(caps, LspOperation.FindReferences), "options object supports");
        Assert.False(LspTranslate.SupportsOperation(caps, LspOperation.GoToImplementation), "boolean false does not support");
        Assert.False(LspTranslate.SupportsOperation(caps, LspOperation.Hover), "an absent slot does not support");
        return Task.CompletedTask;
    }

    public static Task SupportsTransientOpen_LegacyEnums()
    {
        Assert.True(LspTranslate.SupportsTransientOpen(Json("1")), "Full (1) implies open/close");
        Assert.True(LspTranslate.SupportsTransientOpen(Json("2")), "Incremental (2) implies open/close");
        Assert.False(LspTranslate.SupportsTransientOpen(Json("0")), "None (0) does not");
        Assert.False(LspTranslate.SupportsTransientOpen(null), "absent does not");
        return Task.CompletedTask;
    }

    public static Task SupportsTransientOpen_OptionsForms()
    {
        Assert.True(LspTranslate.SupportsTransientOpen(Json("{\"openClose\":true}")), "openClose:true supports");
        Assert.False(LspTranslate.SupportsTransientOpen(Json("{\"openClose\":false,\"change\":2}")), "openClose:false does not");
        return Task.CompletedTask;
    }

    public static Task SupportsTransientOpen_RequiresExplicitOpenClose()
    {
        Assert.False(LspTranslate.SupportsTransientOpen(Json("{\"change\":1}")), "change alone does not imply open/close");
        Assert.False(LspTranslate.SupportsTransientOpen(Json("{\"change\":2}")), "change alone does not imply open/close");
        Assert.False(LspTranslate.SupportsTransientOpen(Json("{}")), "an empty options object does not");
        return Task.CompletedTask;
    }

    public static Task NegotiatePositionEncoding_DefaultsOmittedToUtf16()
    {
        Assert.Equal("utf-16", LspTranslate.NegotiatePositionEncoding(null), "omitted defaults to utf-16");
        Assert.Equal("utf-16", LspTranslate.NegotiatePositionEncoding("utf-16"), "utf-16 is accepted");
        return Task.CompletedTask;
    }

    public static Task NegotiatePositionEncoding_RejectsOtherEncodings()
    {
        var error = Assert.Throws<InvalidOperationException>(() => LspTranslate.NegotiatePositionEncoding("utf-8"));
        Assert.Contains("unsupported position encoding", error.Message, "the rejection names the encoding");
        return Task.CompletedTask;
    }

    public static Task NormalizeLocations_NullAndMissing()
    {
        Assert.Equal(0, LspTranslate.NormalizeLocations(Json("null")).Count, "JSON null is the only no-result value");
        var error = Assert.Throws<LspError>(() => LspTranslate.NormalizeLocations(null));
        Assert.Equal("LSP_MALFORMED_RESPONSE", error.Code, "a missing result is malformed");
        Assert.Contains("LSP navigation result was missing", error.Message, "the missing-result message is exact");
        return Task.CompletedTask;
    }

    public static Task NormalizeLocations_MapsASingleLocation()
    {
        var locations = LspTranslate.NormalizeLocations(Json("{\"uri\":\"file:///a\",\"range\":{\"start\":{\"line\":1,\"character\":2},\"end\":{\"line\":1,\"character\":5}}}"));
        Assert.Equal(1, locations.Count, "one location");
        Assert.Equal("file:///a", locations[0].Uri, "the uri maps");
        Assert.Equal(new LspRange(new LspPosition(1, 2), new LspPosition(1, 5)), locations[0].Range, "the range maps verbatim");
        return Task.CompletedTask;
    }

    public static Task NormalizeLocations_MapsAnArray()
    {
        var locations = LspTranslate.NormalizeLocations(Json("[{\"uri\":\"file:///a\",\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}}},{\"uri\":\"file:///b\",\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}}}]"));
        Assert.Equal(2, locations.Count, "two locations");
        Assert.Equal("file:///a", locations[0].Uri, "the first uri maps");
        Assert.Equal("file:///b", locations[1].Uri, "the second uri maps");
        return Task.CompletedTask;
    }

    public static Task NormalizeLocations_MapsLocationLinks()
    {
        var locations = LspTranslate.NormalizeLocations(Json("[{\"targetUri\":\"file:///c\",\"targetSelectionRange\":{\"start\":{\"line\":1,\"character\":2},\"end\":{\"line\":1,\"character\":5}},\"targetRange\":{\"start\":{\"line\":9,\"character\":9},\"end\":{\"line\":9,\"character\":9}}}]"));
        Assert.Equal(1, locations.Count, "one link maps to one location");
        Assert.Equal("file:///c", locations[0].Uri, "targetUri maps");
        Assert.Equal(new LspRange(new LspPosition(1, 2), new LspPosition(1, 5)), locations[0].Range, "targetSelectionRange maps; targetRange is ignored");
        return Task.CompletedTask;
    }

    public static Task NormalizeLocations_RejectsNonObjectEntry()
    {
        var error = Assert.Throws<LspError>(() => LspTranslate.NormalizeLocations(Json("[42]")));
        Assert.Contains("non-object", error.Message, "the non-object message is exact");
        Assert.Equal("LSP_MALFORMED_RESPONSE", error.Code, "the code is LSP_MALFORMED_RESPONSE");
        return Task.CompletedTask;
    }

    public static Task NormalizeLocations_RejectsNeitherLocationNorLink()
    {
        Assert.Throws<LspError>(() => LspTranslate.NormalizeLocations(Json("[{\"nope\":true}]")));
        var rangeError = Assert.Throws<LspError>(() => LspTranslate.NormalizeLocations(Json("[{\"uri\":\"file:///a\",\"range\":\"nope\"}]")));
        Assert.Contains("neither a Location nor a LocationLink", rangeError.Message, "a non-object range is not a Location");
        var nullError = Assert.Throws<LspError>(() => LspTranslate.NormalizeLocations(Json("[{\"uri\":\"file:///a\",\"range\":{\"start\":null,\"end\":null}}]")));
        Assert.Contains("neither a Location", nullError.Message, "null positions are not a Location");
        return Task.CompletedTask;
    }

    public static Task NormalizeLocations_RejectsNegativeAndFractionalCoordinates()
    {
        var negative = Assert.Throws<LspError>(() => LspTranslate.NormalizeLocations(Json("[{\"uri\":\"file:///a\",\"range\":{\"start\":{\"line\":-1,\"character\":0},\"end\":{\"line\":0,\"character\":1}}}]")));
        Assert.Equal("LSP_MALFORMED_RESPONSE", negative.Code, "negative coordinates are malformed");
        var fractional = Assert.Throws<LspError>(() => LspTranslate.NormalizeLocations(Json("[{\"uri\":\"file:///a\",\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":1.5,\"character\":5}}}]")));
        Assert.Equal("LSP_MALFORMED_RESPONSE", fractional.Code, "fractional coordinates are malformed");
        return Task.CompletedTask;
    }

    public static Task NormalizeHover_NullAndMissing()
    {
        Assert.True(LspTranslate.NormalizeHover(Json("null")) is null, "JSON null is no hover");
        var missing = Assert.Throws<LspError>(() => LspTranslate.NormalizeHover(null));
        Assert.Contains("LSP hover result was missing", missing.Message, "the missing-result message is exact");
        Assert.Equal("LSP_MALFORMED_RESPONSE", missing.Code, "the code is LSP_MALFORMED_RESPONSE");
        return Task.CompletedTask;
    }

    public static Task NormalizeHover_MarkupContentKeepsRange()
    {
        var hover = LspTranslate.NormalizeHover(Json("{\"contents\":{\"kind\":\"markdown\",\"value\":\"# H\"},\"range\":{\"start\":{\"line\":1,\"character\":2},\"end\":{\"line\":1,\"character\":5}}}"));
        Assert.Equal("# H", hover!.Contents, "the MarkupContent value is the contents");
        Assert.Equal(new LspRange(new LspPosition(1, 2), new LspPosition(1, 5)), hover.Range, "the range is kept");
        return Task.CompletedTask;
    }

    public static Task NormalizeHover_MarkedStringForms()
    {
        Assert.Equal("plain text", LspTranslate.NormalizeHover(Json("{\"contents\":\"plain text\"}"))!.Contents, "a bare string is verbatim");
        Assert.Equal("```ts\nconst x = 1\n```", LspTranslate.NormalizeHover(Json("{\"contents\":{\"language\":\"ts\",\"value\":\"const x = 1\"}}"))!.Contents, "a language-tagged object is a fenced block");
        Assert.Equal("a\n\n```ts\nb\n```", LspTranslate.NormalizeHover(Json("{\"contents\":[\"a\",{\"language\":\"ts\",\"value\":\"b\"}]}"))!.Contents, "an array joins with one blank line");
        Assert.True(LspTranslate.NormalizeHover(Json("{\"contents\":{\"kind\":\"plaintext\",\"value\":\"\"}}")) is null, "empty contents become null");
        return Task.CompletedTask;
    }

    public static Task NormalizeHover_RejectsMalformedPayloads()
    {
        var nonStringValue = Assert.Throws<LspError>(() => LspTranslate.NormalizeHover(Json("{\"contents\":{\"kind\":\"markdown\",\"value\":42}}")));
        Assert.Equal("LSP_MALFORMED_RESPONSE", nonStringValue.Code, "a non-string MarkupContent value is malformed");
        var notObject = Assert.Throws<LspError>(() => LspTranslate.NormalizeHover(Json("42")));
        Assert.Contains("was not an object", notObject.Message, "a non-object payload is rejected");
        var weirdContents = Assert.Throws<LspError>(() => LspTranslate.NormalizeHover(Json("{\"contents\":{\"weird\":true}}")));
        Assert.Contains("were not MarkupContent", weirdContents.Message, "unrecognized contents are rejected");
        var numberContents = Assert.Throws<LspError>(() => LspTranslate.NormalizeHover(Json("{\"contents\":42}")));
        Assert.Contains("were not MarkupContent", numberContents.Message, "a number contents is rejected");
        var badMember = Assert.Throws<LspError>(() => LspTranslate.NormalizeHover(Json("{\"contents\":[\"ok\",{\"language\":\"ts\",\"value\":42}]}")));
        Assert.Equal("LSP_MALFORMED_RESPONSE", badMember.Code, "a malformed array member is malformed");
        var nullMember = Assert.Throws<LspError>(() => LspTranslate.NormalizeHover(Json("{\"contents\":[null]}")));
        Assert.Equal("LSP_MALFORMED_RESPONSE", nullMember.Code, "a null array member is malformed");
        var noContents = Assert.Throws<LspError>(() => LspTranslate.NormalizeHover(Json("{\"range\":{\"start\":{\"line\":0,\"character\":0},\"end\":{\"line\":0,\"character\":1}}}")));
        Assert.Contains("no contents", noContents.Message, "a missing contents field is rejected");
        var malformedRange = Assert.Throws<LspError>(() => LspTranslate.NormalizeHover(Json("{\"contents\":\"x\",\"range\":{\"start\":{\"line\":1}}}")));
        Assert.Contains("malformed range", malformedRange.Message, "a malformed range is rejected, never dropped");
        return Task.CompletedTask;
    }
}
