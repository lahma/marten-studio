using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The Marten floor and the latest-tested Marten are defined once, in <c>Directory.Packages.props</c>, and
/// every place that tells a person what they are says the same thing.
/// </summary>
/// <remarks>
/// <para>
/// The floor is what the package's nuspec says a host must have, so it is stated for people in three
/// more places: the README (which is also the nuget.org readme), AGENTS.md D3 and the AGENTS.md budget
/// table. When it moved from 9.31.0 to 9.39.0 all three had to be found by hand. The build reads the
/// two properties itself (<c>build/Build.Marten.cs</c>), so the props file is the authority and the prose
/// is checked against it rather than the other way round.
/// </para>
/// <para>
/// Both values must be exact versions. A floating <c>9.*</c> on the latest-tested leg would let an
/// upstream release turn <c>main</c> red with no pull request to blame, and a range on the floor would
/// let restore pick something nobody tested.
/// </para>
/// </remarks>
public class MartenVersionTests
{
    private static readonly Regex ExactVersion = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);

    private static readonly Lazy<XDocument> PackagesProps =
        new(() => XDocument.Load(RepositoryRoot.Combine("Directory.Packages.props")));

    private static string Floor => SingleProperty("MartenStudioMartenFloor");

    private static string Latest => SingleProperty("MartenStudioMartenLatest");

    [Fact]
    public void The_floor_and_the_latest_tested_version_are_exact_and_in_order()
    {
        Floor.Should().MatchRegex(ExactVersion.ToString());
        Latest.Should().MatchRegex(ExactVersion.ToString());

        Version.Parse(Latest).Should().BeGreaterThanOrEqualTo(Version.Parse(Floor),
            "the latest-tested leg exists to test something newer than the floor, never older");
    }

    [Fact]
    public void Marten_is_restored_at_the_version_property_which_defaults_to_the_floor()
    {
        var marten = PackagesProps.Value.Descendants()
            .Where(x => x.Name.LocalName == "PackageVersion" && (string?) x.Attribute("Include") == "Marten")
            .Should().ContainSingle().Subject;

        ((string?) marten.Attribute("Version")).Should().Be("$(MartenStudioMartenVersion)",
            "the version must come from the property the build and `-p:` set, or the latest-tested leg tests the floor twice");

        var defaulted = PackagesProps.Value.Descendants()
            .Where(x => x.Name.LocalName == "MartenStudioMartenVersion")
            .Should().ContainSingle().Subject;

        ((string?) defaulted.Attribute("Condition")).Should().Be("'$(MartenStudioMartenVersion)' == ''");
        defaulted.Value.Trim().Should().Be("$(MartenStudioMartenFloor)");
    }

    [Fact]
    public void The_readme_states_the_floor_the_package_depends_on()
    {
        File.ReadAllText(RepositoryRoot.Combine("README.md")).Should().Contain($"`Marten` (floor `{Floor}`",
            "README.md is the nuget.org readme, and it is where a host learns which Marten it needs");
    }

    [Fact]
    public void AGENTS_md_states_the_floor_and_the_latest_tested_version()
    {
        var agents = File.ReadAllText(RepositoryRoot.Combine("AGENTS.md"));

        agents.Should().Contain($"(`Marten`, floor `{Floor}`", "D3 names the floor");
        agents.Should().Contain($"| `Marten` | `{Floor}` (latest tested `{Latest}`) |",
            "the package budget row names both versions");
    }

    private static string SingleProperty(string name) =>
        PackagesProps.Value.Descendants()
            .Where(x => x.Name.LocalName == name)
            .Select(x => x.Value.Trim())
            .Should().ContainSingle($"Directory.Packages.props defines {name} exactly once").Subject;
}
