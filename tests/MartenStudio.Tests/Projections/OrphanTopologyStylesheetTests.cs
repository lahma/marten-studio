using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// D26 for M4 and M7's section: <c>M47</c> is appended after 0.5's last section, <c>W5-API</c>, so nothing above it
/// changes, and it carries the rules the orphaned progression rows and the daemon topology use.
/// </summary>
public class OrphanTopologyStylesheetTests
{
    private static readonly Lazy<string> Stylesheet = new(() =>
        File.ReadAllText(RepositoryRoot.Combine("src/PostgresStudio/wwwroot/css/studio.css")).Replace("\r\n", "\n"));

    [Fact]
    public void M47_is_appended_after_W5_API()
    {
        int api = Stylesheet.Value.IndexOf("W5-API.", StringComparison.Ordinal);
        int section = Stylesheet.Value.IndexOf("M47.", StringComparison.Ordinal);

        api.Should().BePositive();
        section.Should().BeGreaterThan(api, "a later section is appended after the one before it (D26)");
    }

    [Theory]
    [InlineData(".ms-orphaned-progression {")]
    [InlineData(".ms-orphaned-progression-reason {")]
    [InlineData(".ms-orphaned-progression-statements {")]
    [InlineData(".ms-shard-node-elsewhere {")]
    [InlineData(".ms-daemon-topology {")]
    public void M47_carries_the_rules_the_screens_use(string rule)
    {
        int section = Stylesheet.Value.IndexOf("M47.", StringComparison.Ordinal);

        Stylesheet.Value.IndexOf(rule, StringComparison.Ordinal).Should().BeGreaterThan(section, "{0} belongs to M47", rule);
    }
}
