namespace Dsh.Lsp.Tests;

/// <summary>The model-facing tool argument parsing and the pure renderers (mirrors the Wave 3 tool tests).</summary>
public static class ToolRenderTests
{
    public static Task ParseLspArgs_ConvertsOneBasedToZeroBased()
    {
        var input = ToolLsp.ParseLspArgs(new LspToolArgs("goToDefinition", "a.ts", 2, 7));
        Assert.Equal(LspOperation.GoToDefinition, input.Operation, "the operation maps");
        Assert.Equal("a.ts", input.FilePath, "the file path is verbatim");
        Assert.Equal(new LspPosition(1, 6), input.Position, "one-based coordinates convert to zero-based");
        return Task.CompletedTask;
    }

    public static Task ParseLspArgs_RejectsUnknownOperation()
    {
        var error = Assert.Throws<ArgumentException>(() => ToolLsp.ParseLspArgs(new LspToolArgs("bogus", "a.ts", 1, 1)));
        Assert.Contains("operation must be one of goToDefinition, findReferences, goToImplementation, hover", error.Message, "the unknown-operation message is exact");
        return Task.CompletedTask;
    }

    public static Task ParseLspArgs_RejectsEmptyFilePath()
    {
        var error = Assert.Throws<ArgumentException>(() => ToolLsp.ParseLspArgs(new LspToolArgs("hover", "   ", 1, 1)));
        Assert.Contains("file_path must be a non-empty string", error.Message, "an empty file_path is rejected");
        return Task.CompletedTask;
    }

    public static Task ParseLspArgs_RejectsNonPositiveCoordinates()
    {
        var lineError = Assert.Throws<ArgumentException>(() => ToolLsp.ParseLspArgs(new LspToolArgs("hover", "a.ts", 0, 1)));
        Assert.Contains("line must be a positive integer (one-based)", lineError.Message, "a zero line is rejected");
        var characterError = Assert.Throws<ArgumentException>(() => ToolLsp.ParseLspArgs(new LspToolArgs("hover", "a.ts", 1, 0)));
        Assert.Contains("character must be a positive integer (one-based)", characterError.Message, "a zero character is rejected");
        return Task.CompletedTask;
    }

    public static Task FormatLocations_GroupsByFileWithOneBasedEntries()
    {
        var locations = new[]
        {
            new LspLocation("file:///ws/a.ts", new LspRange(new LspPosition(0, 6), new LspPosition(0, 9))),
            new LspLocation("file:///ws/a.ts", new LspRange(new LspPosition(4, 1), new LspPosition(4, 3))),
            new LspLocation("file:///ws/b.ts", new LspRange(new LspPosition(2, 0), new LspPosition(2, 5))),
        };
        var text = LspRender.FormatLocations(locations, "file:///ws", 100, 16_000);
        Assert.Equal("a.ts:1:7\na.ts:5:2\nb.ts:3:1", text, "locations group by rendered path with one-based entries");
        return Task.CompletedTask;
    }

    public static Task FormatLocations_AppendsOmissionMarker_InsideCompleteCap()
    {
        var locations = Enumerable.Range(0, 5)
            .Select(i => new LspLocation("file:///ws/a.ts", new LspRange(new LspPosition(i, 0), new LspPosition(i, 1))))
            .ToArray();
        var text = LspRender.FormatLocations(locations, "file:///ws", 3, 16_000);
        Assert.Contains("… 2 more locations omitted (limit 3).", text, "the omission marker names the count and limit");
        var capped = LspRender.FormatLocations(locations, "file:///ws", 3, 30);
        Assert.Equal(30, capped.Length, "the complete cap bounds the rendered text");
        Assert.Contains("truncated", capped, "the truncation notice stays inside the cap");
        return Task.CompletedTask;
    }

    public static Task FormatLocations_NoResultsLine()
    {
        var text = LspRender.FormatLocations(Array.Empty<LspLocation>(), "file:///ws", 100, 16_000);
        Assert.Equal("No results.", text, "an empty locations result renders the no-results line");
        return Task.CompletedTask;
    }

    public static Task FormatHover_NullNoHoverInformation()
    {
        var text = LspRender.FormatHover(null, 16_000);
        Assert.Equal("No hover information.", text, "a null hover renders the no-hover line");
        return Task.CompletedTask;
    }

    public static Task FormatHover_TruncationMarkerInsideCap()
    {
        var text = LspRender.FormatHover(new LspHover(new string('x', 100)), 20);
        Assert.Equal(20, text.Length, "the complete cap bounds the rendered hover");
        Assert.Contains("hover truncated", text, "the hover truncation notice stays inside the cap");
        return Task.CompletedTask;
    }

    public static Task RenderUri_WorkspaceRelative_Inside_And_Absolute_Outside_And_Verbatim_NonFile()
    {
        // POSIX world.
        Assert.Equal("a.ts", LspRender.RenderUri("file:///ws/a.ts", "file:///ws"), "a target inside the workspace is workspace-relative");
        Assert.Equal("sub/b.ts", LspRender.RenderUri("file:///ws/sub/b.ts", "file:///ws"), "a nested target stays relative");
        Assert.Equal("/other/c.ts", LspRender.RenderUri("file:///other/c.ts", "file:///ws"), "a target outside the workspace is an absolute path");
        // Windows world.
        Assert.Equal("a.ts", LspRender.RenderUri("file:///C:/ws/a.ts", "file:///C:/ws"), "a Windows target inside the workspace is workspace-relative");
        Assert.Equal("C:/other/b.ts", LspRender.RenderUri("file:///C:/other/b.ts", "file:///C:/ws"), "a Windows target outside the workspace is an absolute path");
        // Non-file and malformed URIs stay verbatim.
        Assert.Equal("https://example.com/x", LspRender.RenderUri("https://example.com/x", "file:///ws"), "a non-file URI is verbatim");
        Assert.Equal("not a uri", LspRender.RenderUri("not a uri", "file:///ws"), "a malformed URI is verbatim");
        return Task.CompletedTask;
    }

    public static Task PresentLspCall_TitleCarriesOperationAndCursor()
    {
        var view = LspRender.PresentLspCall(new LspToolArgs("findReferences", "src/a.ts", 3, 9));
        Assert.Equal("generic", view.Card, "the card is generic");
        Assert.Equal("search", view.Kind, "the kind is search");
        Assert.Equal("LSP findReferences src/a.ts:3:9", view.Title, "the title carries the operation and one-based cursor");
        Assert.Equal(1, view.Locations.Count, "one focus location");
        Assert.Equal("src/a.ts", view.Locations[0].Path, "the focus path is the queried file");
        Assert.Equal(3, view.Locations[0].Line, "the focus line is the queried line");
        return Task.CompletedTask;
    }
}
