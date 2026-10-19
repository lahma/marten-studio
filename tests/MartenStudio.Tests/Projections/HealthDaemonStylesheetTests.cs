using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// D26 for H2's section: <c>HEALTH-2</c> is appended after <c>HEALTH-1</c>, so nothing above it changes, and it carries the
/// rules the projections screen's banners, Explain, trend arrow and rebuilding badges use.
/// </summary>
public class HealthDaemonStylesheetTests
{
    private static readonly Lazy<string> Stylesheet = new(() =>
        File.ReadAllText(RepositoryRoot.Combine("src/MartenStudio/wwwroot/css/marten-studio.css")).Replace("\r\n", "\n"));

    [Fact]
    public void HEALTH_2_is_appended_after_HEALTH_1()
    {
        int first = Stylesheet.Value.IndexOf("HEALTH-1.", StringComparison.Ordinal);
        int second = Stylesheet.Value.IndexOf("HEALTH-2.", StringComparison.Ordinal);

        first.Should().BePositive();
        second.Should().BeGreaterThan(first, "a later section is appended after the one before it (D26)");
    }

    [Theory]
    [InlineData(".ms-health-banner {")]
    [InlineData(".ms-high-water-explanation {")]
    [InlineData(".ms-high-water-statement {")]
    [InlineData(".ms-shard-trend {")]
    [InlineData(".ms-shard-trend-growing {")]
    [InlineData(".ms-shard-eta {")]
    [InlineData(".ms-rebuild-drain-note {")]
    public void HEALTH_2_carries_the_rules_the_screen_uses(string rule)
    {
        int section = Stylesheet.Value.IndexOf("HEALTH-2.", StringComparison.Ordinal);

        Stylesheet.Value.IndexOf(rule, StringComparison.Ordinal).Should().BeGreaterThan(section, "{0} belongs to HEALTH-2", rule);
    }
}
