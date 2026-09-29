namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The UX-7 section: the last real-browser pass before 0.3.0, fixed in the stylesheet where no markup test can
/// see it.
/// </summary>
public partial class StylesheetTests
{
    /// <summary>
    /// The capability popover was 420 px wide, and every option's name - the thing a host has to type - ran past
    /// its edge and was cut off. It is as wide as its longest line up to 40rem, within the viewport, and a name
    /// that still does not fit wraps.
    /// </summary>
    [Fact]
    public void The_capability_popover_shows_every_option_name_whole()
    {
        LastDeclaration(".ms-popover.ms-capability-popover", "max-width")
            .Should().Contain("40rem").And.Contain("100vw", "never wider than the viewport, which keeps phones working");

        Declaration(".ms-capability-popover .ms-capability-option", "overflow-wrap").Should().Be("anywhere");
        Declaration(".ms-capability-popover .ms-capability-option", "min-width").Should().Be("0",
            "a grid item will not shrink below its content without it, and so would never wrap");

        Declaration(".ms-popover.ms-capability-popover:popover-open", "margin-left").Should().Be("var(--ms-space-md)",
            "on a phone it fills the space left of the chip, and sat flush against the screen's edge");
    }

    /// <summary>The Relationships title and the sentence under it are one block: the page's gap, and no margin on top of it.</summary>
    [Fact]
    public void The_relationships_description_sits_under_its_title()
    {
        LastDeclaration(".ms-rel-page > .ms-page-header", "margin-bottom").Should().Be("0");
    }

    /// <summary>
    /// UX-7 comes after UX-6, as D26 has every UX section appended in order: its popover width re-declares
    /// <c>.ms-popover</c>'s, and a rule placed before the one it overrides does nothing.
    /// </summary>
    [Fact]
    public void The_final_polish_follows_the_database_polish()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "wwwroot", "css", "marten-studio.css"));

        int section = css.IndexOf("UX-7. Final polish", StringComparison.Ordinal);
        section.Should().BePositive();

        css.IndexOf("UX-6. Database browser polish", StringComparison.Ordinal).Should().BePositive()
            .And.BeLessThan(section);
        css.IndexOf(".ms-popover.ms-capability-popover", StringComparison.Ordinal).Should().BeGreaterThan(section);
    }
}
