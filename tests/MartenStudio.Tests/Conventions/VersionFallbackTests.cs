using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// <c>Directory.Build.props</c>' <c>VersionPrefix</c> is the version a plain <c>dotnet build</c> or <c>dotnet run</c> gets -
/// the Fallout build passes CHANGELOG.md's own on the command line - and so the release tag a health finding's docs link
/// points at (<c>HealthCheckInfo.DocsRootFor</c>). It still said 0.4.0 through 0.5, so a sample started with
/// <c>dotnet run</c> linked every finding to 0.4.0's docs (0.6's UI sweep). CHANGELOG.md stays the version authority; this
/// holds the fallback to it.
/// </summary>
public partial class VersionFallbackTests
{
    [Fact]
    public void The_fallback_version_is_the_changelogs_top_version()
    {
        string first = File.ReadLines(RepositoryRoot.Combine("CHANGELOG.md")).First();
        Match heading = ChangelogHeading().Match(first);
        heading.Success.Should().BeTrue("CHANGELOG.md's first line is its version header, '# 0.6.0' (AGENTS.md, Build)");

        MatchCollection prefixes = VersionPrefix().Matches(File.ReadAllText(RepositoryRoot.Combine("Directory.Build.props")));
        prefixes.Should().ContainSingle("Directory.Build.props sets the one fallback");

        prefixes[0].Groups["version"].Value.Should().Be(
            heading.Groups["core"].Value,
            "a new CHANGELOG.md section moves Directory.Build.props' VersionPrefix with it - for a prerelease, to its version without the suffix");
    }

    [Theory]
    [InlineData("# 0.6.0", "0.6.0")]
    [InlineData("# 0.7.0-rc.1", "0.7.0")]
    [InlineData("# Changelog", null)]
    [InlineData("## 0.6.0", null)]
    public void The_header_is_read_as_the_build_reads_it(string line, string? core)
    {
        Match heading = ChangelogHeading().Match(line);

        (heading.Success ? heading.Groups["core"].Value : null).Should().Be(core);
    }

    [GeneratedRegex(@"^# (?<core>\d+\.\d+\.\d+)(?:-[0-9A-Za-z.-]+)?\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ChangelogHeading();

    [GeneratedRegex(@"<VersionPrefix>(?<version>[^<]*)</VersionPrefix>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionPrefix();
}
