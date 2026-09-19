namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The shell's own rules: the header row, the collapsed rail, the page header, the Overview's tiles and
/// its lists (UX-5).
/// </summary>
/// <remarks>
/// <para>
/// A second part of <see cref="StylesheetTests" /> rather than a class of its own, so these use the same
/// comment-stripping reader the rest of the file's assertions do — and so the stylesheet is described in
/// one place while being edited in several. Everything here was found by measuring a real browser at
/// 390×844, 1000 px and 1440×900; none of it is visible to bUnit, which renders markup and not pixels.
/// </para>
/// </remarks>
public partial class StylesheetTests
{
    /// <summary>
    /// The rail's names are hidden the sr-only way and never with <c>display: none</c>.
    /// </summary>
    /// <remarks>
    /// This is the one in the file that is an accessibility bug rather than a layout one.
    /// <c>display: none</c> takes an element out of the accessibility tree as well as out of the layout,
    /// and these spans <em>are</em> the links' accessible names — the anchors have no other text — so a
    /// screen-reader user at 1000 px heard ten links called nothing at all. Clipping keeps the name and
    /// still collapses the rail to 56 px.
    /// </remarks>
    [Fact]
    public void The_collapsed_rail_keeps_the_names_of_its_links()
    {
        string block = MediaBlock("max-width: 1024px", ".ms-nav-link-text");

        Declaration(".ms-nav-link-text", "position", block).Should().Be(
            "absolute",
            "a nav label hidden with display:none is a link with no accessible name");
        Declaration(".ms-nav-link-text", "clip", block).Should().Be("rect(0, 0, 0, 0)");
        Declaration(".ms-sidebar-brand-text", "position", block).Should().Be("absolute");

        Rule(".ms-nav-link-text", block).Should().NotContain(
            "display: none",
            "the rule that took the name away must not come back");

        Declaration(".ms-nav-link", "position", block).Should().Be(
            "relative",
            "the clipped name is positioned against the link it names, not against the page");
    }

    /// <summary>
    /// A section heading names no control, so that one really does go away on a 56 px rail — and it is
    /// still its own rule, rather than being folded back in with the two that must not.
    /// </summary>
    [Fact]
    public void A_rail_section_heading_is_the_only_thing_the_collapse_removes()
    {
        string block = MediaBlock("max-width: 1024px", ".ms-nav-link-text");

        Declaration(".ms-sidebar-section", "display", block).Should().Be("none");
    }

    /// <summary>
    /// The header's own two regions never take a line each. Wrapping <em>here</em> is what made the
    /// phone header four rows deep: the left region took one, the chip another, and a long time-zone
    /// option two more.
    /// </summary>
    /// <remarks>
    /// This is about <c>.ms-header</c> and not about everything inside it. The scope selector within the
    /// left region does still wrap, deliberately — see
    /// <see cref="The_scope_pickers_keep_enough_width_to_be_read_on_a_phone" /> for the measurement that
    /// settled it. On the single-store, single-database host most people have there is one picker in
    /// there and the whole header is one 56 px row.
    /// </remarks>
    [Fact]
    public void The_phone_header_does_not_wrap()
    {
        string block = MediaBlock("max-width: 900px", ".ms-header-control-label");

        Declaration(".ms-header", "flex-wrap", block).Should().Be("nowrap");
        Declaration(".ms-header-left", "min-width", block).Should().Be(
            "0",
            "a flex item that may not wrap has to be allowed to shrink, or it overflows instead");
        Declaration(".ms-header-right", "flex-wrap", block).Should().Be("nowrap");
    }

    /// <summary>
    /// The scope pickers keep enough width to be read, and take a second row when three of them will
    /// not fit one — which is the whole reason they are not the ones that went behind a menu.
    /// </summary>
    /// <remarks>
    /// Measured, and it is why the header and the scope selector wrap differently. With
    /// <c>flex-wrap: nowrap</c> here as well, a host with more than one store and more than one database
    /// draws three pickers at 390 px and they came out 44, 52 and 35 px wide — about ten, eighteen and
    /// one pixel of text once a <c>select</c>'s own arrow and padding are paid for, which reads "In v",
    /// "loc v" and "v". The header did not overflow and the scope was unreadable, which is the same
    /// failure as hiding it behind a menu with extra steps. The floor puts them back at 120, 140 and
    /// 96 px on three rows.
    /// </remarks>
    [Fact]
    public void The_scope_pickers_keep_enough_width_to_be_read_on_a_phone()
    {
        string block = MediaBlock("max-width: 900px", ".ms-scope-selector");

        Declaration(".ms-scope-selector", "flex-wrap", block).Should().Be(
            "wrap",
            "three pickers squeezed onto one 390 px row have no readable text left in them");
        Declaration(".ms-scope-selector", "min-width", block).Should().Be("0");
        Declaration(".ms-scope-selector .ms-header-control", "min-width", block).Should().Be("0");

        Declaration(".ms-scope-selector .ms-header-select", "min-width", block).Should().Be(
            "6rem",
            "the floor is what makes the wrap happen rather than the pickers collapsing to their arrows");
    }

    /// <summary>
    /// Section 5 hides every <c>.ms-header-control-label</c> below 900 px, which is right for the pickers
    /// still in the header row and wrong for the two inside the popover, where the label is the only
    /// thing that says which select is which. It is undone there rather than narrowed up here.
    /// </summary>
    [Fact]
    public void The_labels_inside_the_preferences_popover_are_painted_at_every_width()
    {
        Declaration(".ms-preferences-popover .ms-header-control-label", "position").Should().Be("static");
        Declaration(".ms-preferences-popover .ms-header-control-label", "clip").Should().Be("auto");
    }

    /// <summary>
    /// The popover is anchored to its own button. Without this it would inherit
    /// <c>.ms-popover:popover-open</c>'s anchor, which is the capability chip's, and open under the wrong
    /// control — or under nothing at all when the chip is a read-only span with no anchor name.
    /// </summary>
    [Fact]
    public void The_preferences_popover_is_anchored_to_the_gear_and_not_to_the_capability_chip()
    {
        Declaration(".ms-preferences-button", "anchor-name").Should().Be("--ms-preferences-anchor");
        Declaration(".ms-popover.ms-preferences-popover:popover-open", "position-anchor")
            .Should().Be("--ms-preferences-anchor");
    }

    /// <summary>
    /// A page's actions drop below its title when there is no room for both. On the projections screen at
    /// 390 px the freshness caption and the Refresh button were drawn across the word "Projections".
    /// </summary>
    [Fact]
    public void A_page_headers_actions_wrap_below_the_title_rather_than_over_it()
    {
        Declaration(".ms-page-header", "flex-wrap").Should().Be("wrap");
        Declaration(".ms-page-header", "gap").Should().Contain("var(--ms-space-");

        Declaration(".ms-page-title", "flex").Should().Be(
            "1 1 auto",
            "the title takes what is left and gives it up first");
        Declaration(".ms-page-title", "min-width").Should().Be("0");
    }

    /// <summary>
    /// Six tiles on one row. The Overview's six numbers are one thought, and a 200 px track fitted five
    /// of them at 1200 px of content and left the sixth alone underneath.
    /// </summary>
    /// <remarks>
    /// The arithmetic is the assertion's reason rather than its subject: six 160 px tracks and five
    /// 16 px gaps are 1040 px, so a content column of about 1100 px and up holds them all.
    /// </remarks>
    [Fact]
    public void Six_tiles_fit_one_row_on_a_desktop()
    {
        List<string> grids = Matches(".ms-stat-grid");
        grids.Should().HaveCount(2, "one rule for the grid and one for the two-column layout below 1024 px");
        grids.Should().Contain(
            x => x.Contains("repeat(auto-fit, minmax(160px, 1fr))", StringComparison.Ordinal),
            "a 200px track fitted five of the Overview's six tiles and orphaned the sixth");

        string collapsed = MediaBlock("max-width: 1024px", ".ms-stat-grid");
        Declaration(".ms-stat-grid", "grid-template-columns", collapsed).Should().Be(
            "repeat(2, 1fr)",
            "two columns below 1024 px is the rule the narrow layout is built on, and it stays");
    }

    /// <summary>
    /// The Overview's list rows are two lines on a phone: a sequence and a type, then an id that can be a
    /// 36-character GUID or a whole SQL statement, with the timestamp beside it.
    /// </summary>
    /// <remarks>
    /// Both halves are asserted, in the two <c>640px</c> blocks that hold them, because the basis below
    /// is worthless on its own: a flex line only breaks in a container that is allowed to wrap, and a
    /// merge that dropped <c>.ms-overview-list-item { flex-wrap: wrap }</c> would leave the id squeezed
    /// onto one clipped line with every assertion about its basis still passing.
    /// </remarks>
    [Fact]
    public void An_overview_lists_id_takes_its_own_line_on_a_phone()
    {
        Declaration(".ms-overview-list-item", "flex-wrap", MediaBlock("max-width: 640px", ".ms-overview-list-item"))
            .Should().Be("wrap", "the basis below can only break a line in a container that wraps");

        string block = MediaBlock("max-width: 640px", ".ms-overview-list-stream");

        Declaration(".ms-overview-list-stream", "flex", block).Should().Be(
            "1 1 calc(100% - 4.5rem)",
            "nearly the whole line, less the measured 52px the timestamp beside it needs - and still "
            + "wider than the 106px the sequence and the type leave of the first one, so the break happens");
        Declaration(".ms-overview-list-stream", "min-width", block).Should().Be("0");
        Declaration(".ms-overview-list-stream", "overflow-wrap", block).Should().Be(
            "anywhere",
            "the activity list's target is a SQL statement, which has no break opportunity of its own");
    }

    /// <summary>
    /// The anti-vacuity half for the readers above: <see cref="MediaBlock" /> throws when it finds
    /// nothing, so a test that asserts over the wrong block fails rather than passing quietly — but a
    /// selector that no longer exists would take its assertions with it. These are the blocks the shell
    /// is laid out by, named once so their disappearance is a failure of its own.
    /// </summary>
    [Theory]
    [InlineData("max-width: 1024px", ".ms-nav-link-text")]
    [InlineData("max-width: 900px", ".ms-header-control-label")]
    [InlineData("max-width: 900px", ".ms-scope-selector")]
    [InlineData("max-width: 640px", ".ms-overview-list-item")]
    [InlineData("max-width: 640px", ".ms-overview-list-stream")]
    public void The_shell_still_has_the_media_block_each_assertion_above_reads(string condition, string marker)
    {
        MediaBlock(condition, marker).Should().Contain(marker);
    }
}
