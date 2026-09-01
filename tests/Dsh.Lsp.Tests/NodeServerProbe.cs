namespace Dsh.Lsp.Tests;

/// <summary>Probe: the fixture provider reproduces the recorded corpus behavior without node.</summary>
public static class NodeServerProbe
{
    public static async Task FixtureProvider_ReproducesTheRecordedRender()
    {
        var provider = new FixtureLspProvider(
            new LspProviderId("fixture"),
            new Dictionary<string, string> { [".ts"] = "typescript" });
        var root = Path.Combine(Path.GetTempPath(), "dsh-lsp-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await provider.QueryAsync(new LspProviderQuery(
                LspOperation.GoToDefinition, "subject.ts", new LspPosition(0, 6), WorkspaceRoot: root, LanguageId: "typescript"))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result is LspLocationsResult, "the definition query returns locations");
            var locations = (LspLocationsResult)result;
            Assert.True(locations.Locations.Count == 2, "the fixture server returns two locations");
            var text = LspRender.FormatLocations(locations.Locations, locations.ResolvedWorkspaceUri, 1, 16000);
            Assert.Equal("subject.ts:1:7\n\u2026 1 more location omitted (limit 1).", text, "the render matches the recording");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
