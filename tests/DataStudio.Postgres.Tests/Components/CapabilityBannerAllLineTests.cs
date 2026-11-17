using Bunit;

using DataStudio.Components.Layout;

namespace DataStudio.Postgres.Tests.Components;

/// <summary>
/// The chip's module line "<c>PostgresCapabilities.All()</c> turns every capability of this module on" is true only while no read-only switch is on:
/// the module's own turns its capabilities off and says so in its own line, and the shell's draws no popover at all.
/// </summary>
public class CapabilityBannerAllLineTests
{
    private const string AllLine = "turns every capability of this module on";

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task The_all_line_is_drawn_only_while_no_read_only_switch_is_on(bool moduleReadOnly, bool shellReadOnly, bool drawn)
    {
        await using PostgresComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.ReadOnly = moduleReadOnly;
        context.ShellOptions.ReadOnly = shellReadOnly;

        IRenderedComponent<CapabilityBanner> banner = context.Render<CapabilityBanner>();

        banner.FindAll(".ms-popover-footnote").Select(static x => x.TextContent)
            .Where(static x => x.Contains(AllLine, StringComparison.Ordinal))
            .Should().HaveCount(drawn ? 1 : 0);

        if (moduleReadOnly && !shellReadOnly)
        {
            banner.FindAll(".ms-popover-footnote").Select(static x => x.TextContent).Should()
                .Contain(static x => x.Contains("PostgresOptions.ReadOnly is true, so every capability of this module is off.", StringComparison.Ordinal));
        }
    }
}
