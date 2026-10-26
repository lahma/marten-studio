using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// PostgresStudio's Npgsql floor and latest-tested Npgsql are defined once, in <c>Directory.Packages.props</c>, are
/// exact, reach only the projects that ask for them, and never raise the Npgsql a MartenStudio host already resolves.
/// </summary>
/// <remarks>
/// <para>
/// The row is conditional on <c>PostgresStudioPinsNpgsql</c> because, with transitive pinning on, an unconditional
/// <c>Npgsql</c> row would pin Npgsql in MartenStudio too, below Weasel's own requirement (NU1109) - and would break
/// D3's "no direct Npgsql for MartenStudio" (design doc 6.2).
/// </para>
/// <para>
/// The floor invariant is read from MartenStudio's restore rather than stated: PostgresStudio's <c>Npgsql &gt;= floor</c>
/// reaches every MartenStudio host through the exact <c>PostgresStudio</c> dependency, so a floor above what Marten's
/// own packages already require would move every one of those hosts' Npgsql. Compared against what those packages
/// <em>declare</em>, not against what MartenStudio resolved - that is the larger of the two and would always pass.
/// </para>
/// </remarks>
public class NpgsqlVersionTests
{
    private static readonly Regex ExactVersion = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);

    private static readonly Regex FirstVersion = new(@"\d+(\.\d+){1,3}", RegexOptions.CultureInvariant);

    private static readonly Lazy<XDocument> PackagesProps =
        new(() => XDocument.Load(RepositoryRoot.Combine("Directory.Packages.props")));

    private static string MartenStudioAssetsFile => RepositoryRoot.Combine("src", StudioProjects.Marten, "obj", "project.assets.json");

    private static string Floor => SingleProperty("PostgresStudioNpgsqlFloor");

    private static string Latest => SingleProperty("PostgresStudioNpgsqlLatest");

    [Fact]
    public void The_floor_and_the_latest_tested_version_are_exact_and_in_order()
    {
        Floor.Should().MatchRegex(ExactVersion.ToString());
        Latest.Should().MatchRegex(ExactVersion.ToString());

        Version.Parse(Latest).Should().BeGreaterThanOrEqualTo(Version.Parse(Floor),
            "the latest-tested pass exists to test something newer than the floor, never older");
    }

    [Fact]
    public void Npgsql_is_restored_at_the_version_property_and_only_where_a_project_asks_for_it()
    {
        XElement npgsql = PackagesProps.Value.Descendants()
            .Where(static x => x.Name.LocalName == "PackageVersion" && (string?) x.Attribute("Include") == "Npgsql")
            .Should().ContainSingle().Subject;

        ((string?) npgsql.Attribute("Version")).Should().Be("$(PostgresStudioNpgsqlVersion)",
            "the version must come from the property the build and `-p:` set, or the latest-tested pass tests the floor twice");
        ((string?) npgsql.Attribute("Condition")).Should().Be("'$(PostgresStudioPinsNpgsql)' == 'true'",
            "an unconditional row would pin Npgsql in MartenStudio too, through transitive pinning");

        XElement defaulted = PackagesProps.Value.Descendants()
            .Where(static x => x.Name.LocalName == "PostgresStudioNpgsqlVersion")
            .Should().ContainSingle().Subject;

        ((string?) defaulted.Attribute("Condition")).Should().Be("'$(PostgresStudioNpgsqlVersion)' == ''");
        defaulted.Value.Trim().Should().Be("$(PostgresStudioNpgsqlFloor)");
    }

    [Fact]
    public void The_floor_never_raises_the_Npgsql_a_MartenStudio_host_already_resolves()
    {
        Assert.SkipUnless(File.Exists(MartenStudioAssetsFile), "src/MartenStudio has not been restored, so there is no project.assets.json to read");

        IReadOnlyDictionary<string, Version> declared = NpgsqlMinimumsDeclaredByPackages(File.ReadAllText(MartenStudioAssetsFile));

        // Not vacuous: Marten's own graph does depend on Npgsql (through Weasel.Postgresql).
        declared.Should().NotBeEmpty("MartenStudio reaches Npgsql through Marten's packages");

        Version.Parse(Floor).Should().BeLessThanOrEqualTo(declared.Values.Max()!,
            "PostgresStudioNpgsqlFloor reaches every MartenStudio host through its exact PostgresStudio dependency, and " +
            $"Marten's packages ask for no more than {declared.Values.Max()} ({string.Join(", ", declared.Select(static x => $"{x.Key} >= {x.Value}"))})");
    }

    [Theory]
    [InlineData("""{ "targets": { "net10.0": { "Weasel.Postgresql/9.33.0": { "type": "package", "dependencies": { "Npgsql": "9.0.4" } } } } }""", "Weasel.Postgresql/9.33.0=9.0.4")]
    [InlineData("""{ "targets": { "net10.0": { "A/1.0.0": { "type": "package", "dependencies": { "Npgsql": "[8.0.3, )" } }, "B/1.0.0": { "type": "package", "dependencies": { "Other": "1.0.0" } } } } }""", "A/1.0.0=8.0.3")]
    [InlineData("""{ "targets": { "net10.0": { "PostgresStudio/0.5.0": { "type": "project", "dependencies": { "Npgsql": "10.0.3" } } } } }""", "")]
    public void The_declared_minimums_are_read_from_packages_and_never_from_the_PostgresStudio_project(string assets, string expected)
    {
        string.Join(";", NpgsqlMinimumsDeclaredByPackages(assets).Select(static x => $"{x.Key}={x.Value}")).Should().Be(expected);
    }

    /// <summary>
    /// Every <em>package</em> in a restore's graph that depends on Npgsql, with the lowest version its range admits.
    /// The PostgresStudio project is left out: it is the thing whose floor is being judged.
    /// </summary>
    private static Dictionary<string, Version> NpgsqlMinimumsDeclaredByPackages(string assetsJson)
    {
        using JsonDocument assets = JsonDocument.Parse(assetsJson);
        Dictionary<string, Version> found = new(StringComparer.Ordinal);

        foreach (JsonProperty framework in assets.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (JsonProperty library in framework.Value.EnumerateObject())
            {
                if (library.Value.TryGetProperty("type", out JsonElement type) && type.GetString() == "package"
                    && library.Value.TryGetProperty("dependencies", out JsonElement dependencies)
                    && dependencies.TryGetProperty("Npgsql", out JsonElement range))
                {
                    found[library.Name] = Version.Parse(FirstVersion.Match(range.GetString()!).Value);
                }
            }
        }

        return found;
    }

    private static string SingleProperty(string name) =>
        PackagesProps.Value.Descendants()
            .Where(x => x.Name.LocalName == name)
            .Select(static x => x.Value.Trim())
            .Should().ContainSingle($"Directory.Packages.props defines {name} exactly once").Subject;
}
