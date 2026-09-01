namespace Dsh.Web.Tests;

/// <summary>The turndown port reproduces the recorded corpus page and the oracle cases.</summary>
public static class TurndownTests
{
    private const string FixturePage =
        "<!doctype html>\n<html><head><title>Menu</title><style>.x{color:red}</style><script>ignored()</script></head>\n"
        + "<body>\n<h1>Caf&eacute; menu</h1>\n"
        + "<p>Prices include <strong>service &amp; <em>tax</em></strong> &mdash; updated daily.</p>\n"
        + "<ul><li>Espresso</li><li>Flat white</li></ul>\n"
        + "<table><thead><tr><th>Drink</th><th>Price</th></tr></thead><tbody><tr><td>Espresso</td><td>&euro;2</td></tr><tr><td>Flat white</td><td>&euro;3</td></tr></tbody></table>\n"
        + "<p>See <a href=\"https://fixture.invalid/specials\">today&rsquo;s specials</a>.</p>\n</body></html>\n";

    private const string FixtureExpected =
        "Menu\n\n# Café menu\n\nPrices include **service & _tax_** — updated daily.\n\n"
        + "-   Espresso\n-   Flat white\n\n"
        + "| Drink | Price |\n| --- | --- |\n| Espresso | €2  |\n| Flat white | €3  |\n\n"
        + "See [today’s specials](https://fixture.invalid/specials).";

    public static void Convert_MatchesTheRecordedFixturePage()
    {
        var converter = new TurndownConverter();
        var output = converter.Convert(FixturePage);
        Assert.Equal(FixtureExpected, output, "the corpus page converts byte-exact");
    }

    public static void Convert_CollapsesWhitespaceAcrossBlocks()
    {
        var converter = new TurndownConverter();
        var output = converter.Convert("<p>  a   b  </p><p>c</p>");
        Assert.Equal("a b\n\nc", output, "inner whitespace collapses to single spaces and blocks separate");
    }

    public static void Convert_RemovesNonVisibleContent()
    {
        var converter = new TurndownConverter();
        var output = converter.Convert("<p>keep</p><script>drop()</script><style>x{}</style><p style=\"display:none\">hidden</p><p>tail</p>");
        Assert.Equal("keep\n\ntail", output, "script, style, and display:none content is removed");
    }

    public static void Convert_EscapesMarkdownSyntax()
    {
        var converter = new TurndownConverter();
        var output = converter.Convert("<p>a *b* [c](d) _e_ `f`</p>");
        Assert.Equal("a \\*b\\* \\[c\\](d) \\_e\\_ \\`f\\`", output, "markdown syntax is escaped in text");
    }

    public static void Convert_EmphasisAndLinks()
    {
        var converter = new TurndownConverter();
        var output = converter.Convert("<p>See <a href=\"https://x.test/a\">the <em>page</em></a>.</p>");
        Assert.Equal("See [the _page_](https://x.test/a).", output, "links and emphasis render inline");
    }
}