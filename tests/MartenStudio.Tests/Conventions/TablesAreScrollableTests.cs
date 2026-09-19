using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Every data table in <c>src/</c> is inside a scroll region.
/// </summary>
/// <remarks>
/// <para>
/// <c>.ms-table</c> is <c>width: 100%</c> and <c>.ms-main</c> is <c>overflow-x: hidden</c>, so a table
/// wider than the content column is not scrolled - it is <em>cut off</em>, with no scrollbar and no way
/// at all to reach the columns on the right. That was the state of every table in the studio but the
/// documents list, and on the event-types screen it hid four of six columns at 1440px. The fix is a
/// wrapper, which means the rule is "somebody remembered the wrapper" - exactly the kind of rule that
/// comes back the first time a screen grows a table and nobody thinks about a phone.
/// </para>
/// <para>
/// So it is scanned rather than reviewed. A page test can only assert the tables that page renders in
/// the state that test puts it in; this one walks the shipped markup and fails on any table at all.
/// </para>
/// <para>
/// A wrapper is any of the three spellings <c>marten-studio.css</c>'s own selector lists recognise: the
/// <c>&lt;TableScroll&gt;</c> component, an element carrying <c>ms-table-scroll</c> - which is what that
/// component renders, and what the query results grid puts on the scroll container it already had rather
/// than nesting a second one inside it - and an element carrying <c>ms-table-wrap</c>, the documents
/// list's own container. The rule is about the behaviour those classes carry, so recognising fewer of
/// them than the stylesheet does would fail a table that scrolls perfectly well.
/// </para>
/// <para>
/// The scan is over markup rather than over rendered output, so it reads tags with a stack: Razor will
/// not compile unbalanced markup, which is what makes that sound. Razor and HTML comments are blanked
/// first, so prose that draws a table - including this rule, quoted in a component's own doc comment -
/// does not fail the build. That blanking is what makes a scanner capable of being silently vacuous,
/// which is why <see cref="The_scanner_finds_a_bare_table_and_ignores_a_wrapped_one" /> checks it
/// against known-good and known-bad markup before the real scan is trusted.
/// </para>
/// </remarks>
public class TablesAreScrollableTests
{
    /// <summary>
    /// Elements that never close, so a start tag for one must not be pushed onto the stack.
    /// </summary>
    /// <remarks>
    /// Written out rather than inferred: an element wrongly treated as open swallows every close tag
    /// after it and would eventually pop a wrapper that is still open, which fails a table that is
    /// perfectly well wrapped.
    /// </remarks>
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source",
        "track", "wbr",
    };

    /// <summary>
    /// One start or end tag. The alternation in the attribute part is what lets an attribute value hold
    /// a <c>&gt;</c> - <c>@(a &gt; b)</c> in a Blazor attribute is ordinary - without ending the tag.
    /// </summary>
    private static readonly Regex Tag = new(
        @"<(?<close>/?)(?<name>[A-Za-z][A-Za-z0-9._:-]*)(?<attributes>(?:""[^""]*""|'[^']*'|[^>""'])*)(?<selfClose>/?)>",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex ClassAttribute = new(
        @"\bclass\s*=\s*""(?<value>[^""]*)""",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex RazorComment = new(@"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(5));
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(5));

    [Fact]
    public void No_data_table_in_src_is_outside_a_scroll_region()
    {
        string[] files = [.. SourceScanner.ShippedSourceFiles()
            .Where(x => x.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))];

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        List<string> offenders = [];
        int scanned = 0;

        foreach (string file in files)
        {
            string markup = File.ReadAllText(file);
            scanned += CountDataTables(markup);

            foreach (string table in UnwrappedTables(markup))
            {
                offenders.Add(SourceScanner.Relative(file) + ": " + table);
            }
        }

        scanned.Should().BeGreaterThan(10,
            "the studio ships about twenty data tables; finding almost none means the scan stopped seeing them");

        offenders.Should().BeEmpty(
            "a table outside a scroll region is clipped rather than scrolled on any viewport narrower "
            + "than it - wrap it in <TableScroll Label=\"...\">. Offenders: " + string.Join("; ", offenders));
    }

    /// <summary>The anti-vacuity check: the scanner has to be able to fail, and to stop failing.</summary>
    [Theory]
    // A bare table, which is the whole point.
    [InlineData("<table class=\"ms-table\"><tbody></tbody></table>", 1)]
    [InlineData("<div class=\"ms-page\"><table class=\"ms-table ms-doc-meta-table\"></table></div>", 1)]
    // Wrapped, in each of the two accepted ways.
    [InlineData("<TableScroll Label=\"Things\"><table class=\"ms-table\"></table></TableScroll>", 0)]
    [InlineData("<div class=\"ms-table-wrap\"><table class=\"ms-table ms-doc-table\"></table></div>", 0)]
    [InlineData("<div class=\"ms-query-grid-scroll ms-table-scroll\" role=\"region\"><table class=\"ms-table ms-query-grid\"></table></div>", 0)]
    [InlineData("<TableScroll Label=\"@Label(x)\"><div><table class=\"ms-table\"></table></div></TableScroll>", 0)]
    // The region closed before the table opened, which is what a second table added to a page looks
    // like when nobody wrapped it.
    [InlineData("<TableScroll Label=\"A\"><table class=\"ms-table\"></table></TableScroll>\n<table class=\"ms-table\"></table>", 1)]
    // Both halves of a pair.
    [InlineData("<table class=\"ms-table\"></table><table class=\"ms-table\"></table>", 2)]
    // Prose is prose, in both comment syntaxes.
    [InlineData("@* never write <table class=\"ms-table\"> on its own *@\n<p>ok</p>", 0)]
    [InlineData("<!-- <table class=\"ms-table\"></table> -->", 0)]
    // A void element inside the region must not swallow the region's own close tag.
    [InlineData("<TableScroll Label=\"A\"><br><img src=\"x\"><table class=\"ms-table\"></table></TableScroll>", 0)]
    // A self-closing component likewise.
    [InlineData("<TableScroll Label=\"A\"><LoadingSpinner /><table class=\"ms-table\"></table></TableScroll>", 0)]
    // A table that is not a studio data table is not this rule's business.
    [InlineData("<table><tr><td>layout</td></tr></table>", 0)]
    public void The_scanner_finds_a_bare_table_and_ignores_a_wrapped_one(string markup, int expected)
    {
        UnwrappedTables(markup).Should().HaveCount(expected);
    }

    /// <summary>And the count the real scan guards itself with can see a table at all.</summary>
    [Theory]
    [InlineData("<table class=\"ms-table\"></table>", 1)]
    [InlineData("<TableScroll Label=\"A\"><table class=\"ms-table ms-query-grid\"></table></TableScroll>", 1)]
    [InlineData("<table class=\"other\"></table>", 0)]
    [InlineData("@* <table class=\"ms-table\"> *@", 0)]
    public void The_scanner_counts_the_data_tables_it_walked_past(string markup, int expected)
    {
        CountDataTables(markup).Should().Be(expected);
    }

    /// <summary>Every studio data table in this markup that no scroll region encloses.</summary>
    /// <param name="markup">A <c>.razor</c> file's text.</param>
    /// <returns>The offending start tags, as they were written.</returns>
    private static List<string> UnwrappedTables(string markup) => WalkTables(markup, wrappedToo: false);

    /// <summary>How many studio data tables this markup has, wrapped or not.</summary>
    /// <param name="markup">A <c>.razor</c> file's text.</param>
    /// <returns>The count.</returns>
    private static int CountDataTables(string markup) => WalkTables(markup, wrappedToo: true).Count;

    /// <summary>
    /// Walks the markup's tags, keeping a stack of the elements that are open, and reports the data
    /// tables found.
    /// </summary>
    /// <param name="markup">A <c>.razor</c> file's text.</param>
    /// <param name="wrappedToo">Whether to report a table that <em>is</em> inside a scroll region.</param>
    /// <returns>The start tags reported, as they were written.</returns>
    private static List<string> WalkTables(string markup, bool wrappedToo)
    {
        string source = HtmlComment.Replace(RazorComment.Replace(markup, string.Empty), string.Empty);

        List<string> found = [];
        List<(string Name, bool IsRegion)> open = [];

        foreach (Match tag in Tag.Matches(source))
        {
            string name = tag.Groups["name"].Value;

            if (tag.Groups["close"].Value.Length > 0)
            {
                // Pop back to the matching name. A mismatch means the tag walk has lost the thread -
                // Razor would not have compiled it - so leaving the stack alone is the safe direction:
                // it can only report a table as wrapped that is, never one that is not.
                int index = open.FindLastIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    open.RemoveRange(index, open.Count - index);
                }

                continue;
            }

            bool selfClosing = tag.Groups["selfClose"].Value.Length > 0 || VoidElements.Contains(name);

            if (string.Equals(name, "table", StringComparison.OrdinalIgnoreCase))
            {
                if (IsDataTable(tag.Groups["attributes"].Value))
                {
                    bool inRegion = open.Exists(static x => x.IsRegion);
                    if (wrappedToo || !inRegion)
                    {
                        found.Add(tag.Value);
                    }
                }
            }

            if (!selfClosing)
            {
                open.Add((name, IsScrollRegion(name, tag.Groups["attributes"].Value)));
            }
        }

        return found;
    }

    /// <summary>Whether this start tag opens one of the three accepted scroll regions.</summary>
    /// <param name="name">The element or component name.</param>
    /// <param name="attributes">Everything between the name and the closing angle bracket.</param>
    /// <returns>Whether a table inside it is scrollable.</returns>
    private static bool IsScrollRegion(string name, string attributes) =>
        string.Equals(name, "TableScroll", StringComparison.Ordinal)
        || HasClassStartingWith(attributes, "ms-table-scroll")
        || HasClassStartingWith(attributes, "ms-table-wrap");

    /// <summary>Whether a <c>&lt;table&gt;</c> is one of the studio's own data tables.</summary>
    /// <param name="attributes">Everything between the name and the closing angle bracket.</param>
    /// <returns>Whether this rule applies to it.</returns>
    private static bool IsDataTable(string attributes) => HasClassStartingWith(attributes, "ms-table");

    /// <summary>Whether the tag's <c>class</c> attribute has a token beginning with <paramref name="prefix" />.</summary>
    /// <param name="attributes">Everything between the name and the closing angle bracket.</param>
    /// <param name="prefix">The class-name prefix to look for.</param>
    /// <returns>Whether a token matches.</returns>
    private static bool HasClassStartingWith(string attributes, string prefix)
    {
        Match match = ClassAttribute.Match(attributes);
        if (!match.Success)
        {
            return false;
        }

        foreach (string token in match.Groups["value"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
