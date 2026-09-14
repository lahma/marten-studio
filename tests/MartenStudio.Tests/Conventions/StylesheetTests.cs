using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Rules in the one stylesheet that no rendered markup can prove, and that are quiet when they break.
/// </summary>
/// <remarks>
/// <para>
/// bUnit renders markup, not pixels: nothing in a component test notices that an <c>h1</c> grew a black
/// box after every navigation, that an id column wraps a GUID over four lines, or that five uppercase
/// labels no longer fit a 390 px header. Those were all found by looking at a real browser, and the
/// smallest thing that stops them coming back is an assertion that the rule is still in the file.
/// </para>
/// <para>
/// These are deliberately assertions about <em>rules</em> — a selector and the one declaration that
/// carries the fix — rather than about the file's text, so reformatting the stylesheet does not fail
/// them and deleting a rule does. Comments are stripped before anything is parsed, so a class named only
/// in prose counts as absent, which is what "no markup uses it" means.
/// </para>
/// </remarks>
public partial class StylesheetTests
{
    private const string StylesheetPath = "src/MartenStudio/wwwroot/css/marten-studio.css";

    /// <summary>The stylesheet with its comments removed, which is the thing these tests are about.</summary>
    private static readonly Lazy<string> Css = new(static () =>
        CommentExpression().Replace(
            File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "wwwroot", "css", "marten-studio.css")),
            string.Empty));

    /// <summary>
    /// <c>&lt;FocusOnNavigate Selector="h1" /&gt;</c> focuses the page heading after every navigation,
    /// which is right for a screen reader and drew the user agent's own focus ring — a hard black box
    /// round the title — for everybody else.
    /// </summary>
    /// <remarks>
    /// Two rules, because whether a programmatic focus counts as <c>:focus-visible</c> is the browser's
    /// decision and turns on whether the visitor arrived by keyboard. The quiet one alone would take the
    /// ring away from keyboard users; the ring alone would leave the box for everybody else.
    /// </remarks>
    [Fact]
    public void The_focused_page_heading_is_quiet_unless_the_focus_is_a_keyboard_focus()
    {
        Declaration(".ms-studio h1:focus", "outline").Should().Be(
            "none",
            "FocusOnNavigate puts focus on the heading after every navigation, and the user agent's own ring is a black box");

        Declaration(".ms-studio h1:focus-visible", "outline").Should().Contain(
            "var(--ms-color-primary)",
            "a keyboard focus still gets the same ring every other focusable thing in the studio has");
    }

    /// <summary>
    /// Both rules are scoped to <c>.ms-studio</c>. An embeddable component that restyled the host's own
    /// headings would be the failure this project exists not to have (AGENTS.md hard rule 3).
    /// </summary>
    [Fact]
    public void The_heading_rules_never_reach_outside_the_studio()
    {
        foreach (string selector in Selectors())
        {
            if (selector.Contains("h1", StringComparison.Ordinal))
            {
                selector.Should().StartWith(".ms-", $"'{selector}' would style the host's own markup");
            }
        }
    }

    /// <summary>
    /// The heading's focus ring uses the colour token the rest of the stylesheet uses. There is no
    /// <c>--ms-focus-ring</c> token and this deliberately did not invent one: every focus ring in the file
    /// is <c>2px solid var(--ms-color-primary)</c>, and a second spelling would be one more thing to keep
    /// in step.
    /// </summary>
    [Fact]
    public void Nothing_invented_a_focus_ring_token()
    {
        Css.Value.Should().NotContain("--ms-focus-ring");
    }

    /// <summary>
    /// The phone header. The labels are hidden by width rather than deleted, so the desktop header keeps
    /// them and the accessibility tree keeps them everywhere.
    /// </summary>
    [Fact]
    public void The_header_labels_are_visually_hidden_below_900_pixels()
    {
        string block = MediaBlock("max-width: 900px", ".ms-header-control-label");

        Declaration(".ms-header-control-label", "position", block).Should().Be("absolute");
        Declaration(".ms-header-control-label", "clip", block).Should().Be("rect(0, 0, 0, 0)");

        block.Should().Contain(
            ".ms-header {",
            "the header itself has to be allowed to wrap, or hiding the labels only makes the rows narrower");
    }

    /// <summary>
    /// Id cells. A GUID is 36 characters with no natural break point, so a cell that lets one wrap made
    /// every row of the documents list about 70 px tall.
    /// </summary>
    [Fact]
    public void An_id_is_one_line_clipped_rather_than_four_lines_wrapped()
    {
        Declaration(".ms-id-cell", "white-space").Should().Be("nowrap");

        string text = Rule(".ms-id-text");
        text.Should().Contain("text-overflow: ellipsis;", "the clip has to read as a clip");
        text.Should().Contain("max-width:", "an unconstrained id column pushes every other column off the table");
        text.Should().Contain("var(--ms-font-mono)");
    }

    /// <summary>
    /// The id rules reach the two shapes an id cell has in this codebase: the documents list's own
    /// <c>ms-id-text</c> anchor, and the Events tables' plain <c>&lt;a&gt;</c> child of the cell.
    /// </summary>
    [Fact]
    public void An_id_cells_bare_anchor_is_styled_without_being_asked()
    {
        Rule(".ms-id-text").Should().Contain(".ms-id-cell > a");
    }

    /// <summary>
    /// Three classes for a rebuild dialog that was rewritten into <c>ConfirmDialog</c> and no longer uses
    /// them. Dead CSS is not free: it is what somebody greps for, finds, and then styles against.
    /// </summary>
    [Theory]
    [InlineData(".ms-rebuild-dialog")]
    [InlineData(".ms-rebuild-summary")]
    [InlineData(".ms-rebuild-input")]
    public void No_rule_survives_for_a_class_no_markup_uses(string selector)
    {
        Matches(selector).Should().BeEmpty($"nothing renders '{selector}' any more");
    }

    /// <summary>
    /// The stylesheet still folds: one rule per selector, so a reader who greps for a class finds the one
    /// place it is defined. Checked over the selectors this packet introduced rather than over the whole
    /// file, where several selectors are legitimately repeated inside media queries.
    /// </summary>
    [Theory]
    [InlineData(".ms-id-cell")]
    [InlineData(".ms-id-text")]
    [InlineData(".ms-doc-sort-hint")]
    [InlineData(".ms-write-refusal")]
    [InlineData(".ms-studio h1:focus")]
    [InlineData(".ms-studio h1:focus-visible")]
    public void A_new_selector_is_defined_exactly_once(string selector)
    {
        Matches(selector).Should().HaveCount(1, $"'{selector}' should be defined in one place");
    }

    /// <summary>Every selector the stylesheet declares a rule for, trimmed, one entry per selector.</summary>
    private static IEnumerable<string> Selectors()
    {
        foreach (Match match in RuleExpression().Matches(Css.Value))
        {
            foreach (string candidate in match.Groups["selectors"].Value.Split(','))
            {
                string trimmed = candidate.Trim();
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }
    }

    /// <summary>Every rule block whose selector list contains <paramref name="selector" />.</summary>
    private static List<string> Matches(string selector, string? source = null)
    {
        List<string> blocks = [];

        foreach (Match match in RuleExpression().Matches(source ?? Css.Value))
        {
            foreach (string candidate in match.Groups["selectors"].Value.Split(','))
            {
                if (string.Equals(candidate.Trim(), selector, StringComparison.Ordinal))
                {
                    blocks.Add(match.Value);
                    break;
                }
            }
        }

        return blocks;
    }

    /// <summary>The single rule block for <paramref name="selector" />, or a failure that says so.</summary>
    private static string Rule(string selector, string? source = null)
    {
        List<string> blocks = Matches(selector, source);

        return blocks.Count switch
        {
            1 => blocks[0],
            0 => throw new InvalidOperationException($"{StylesheetPath} has no rule for '{selector}'."),
            _ => throw new InvalidOperationException(
                $"{StylesheetPath} has {blocks.Count} rules for '{selector}'; the stylesheet should fold."),
        };
    }

    /// <summary>The value of one declaration in <paramref name="selector" />'s rule, without its semicolon.</summary>
    private static string Declaration(string selector, string property, string? source = null)
    {
        string rule = Rule(selector, source);
        Match match = Regex.Match(
            rule,
            @"(?m)^\s*" + Regex.Escape(property) + @"\s*:\s*(?<value>[^;]+);",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        if (!match.Success)
        {
            throw new InvalidOperationException($"The rule for '{selector}' declares no '{property}'.");
        }

        return match.Groups["value"].Value.Trim();
    }

    /// <summary>
    /// The body of the <c>@media</c> block with the given condition that mentions <paramref name="marker" />.
    /// </summary>
    /// <remarks>
    /// Braces are matched by counting rather than by a regular expression: a media query contains rules,
    /// which contain braces of their own, and a lazy match would stop at the first one.
    /// </remarks>
    private static string MediaBlock(string condition, string marker)
    {
        string css = Css.Value;
        int search = 0;

        while (true)
        {
            int start = css.IndexOf("@media (" + condition + ")", search, StringComparison.Ordinal);
            if (start < 0)
            {
                throw new InvalidOperationException(
                    $"{StylesheetPath} has no '@media ({condition})' block mentioning '{marker}'.");
            }

            int open = css.IndexOf('{', start);
            int depth = 0;
            int index = open;

            for (; index < css.Length; index++)
            {
                if (css[index] == '{')
                {
                    depth++;
                }
                else if (css[index] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
            }

            string body = css[open..Math.Min(index + 1, css.Length)];
            if (body.Contains(marker, StringComparison.Ordinal))
            {
                return body;
            }

            search = index + 1;
        }
    }

    /// <summary>One CSS rule: a selector list that is not an at-rule, and its declarations.</summary>
    [GeneratedRegex(@"(?<selectors>[^{}@;]+?)\{(?<body>[^{}]*)\}", RegexOptions.Singleline, 5000)]
    private static partial Regex RuleExpression();

    /// <summary>A <c>/* … */</c> comment, which is prose rather than a rule.</summary>
    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, 5000)]
    private static partial Regex CommentExpression();
}
