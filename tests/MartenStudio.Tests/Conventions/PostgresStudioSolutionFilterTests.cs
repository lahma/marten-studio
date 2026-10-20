using System.Text.Json;
using System.Xml.Linq;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// <c>postgres-studio.slnf</c> - the solution filter the latest-Npgsql pass restores, builds and tests - holds exactly
/// the projects that set <c>PostgresStudioPinsNpgsql</c>, and is closed: nothing it builds reaches a project outside it.
/// </summary>
/// <remarks>
/// <para>
/// The pass (<c>build/Build.Npgsql.cs</c>) hands <c>-p:PostgresStudioNpgsqlVersion=</c> to the filter alone, so that
/// MartenStudio never meets that Npgsql. Both directions of the set matter. A PostgresStudio-only project missing from
/// the filter - <c>tests/PostgresStudio.Tests</c> the day it lands - is never tested at the latest Npgsql, and nothing
/// would say so. A project in the filter that does not set the property ignores the version it was handed, and so does
/// one it references: a <c>ProjectReference</c> to MartenStudio from inside the filter would restore MartenStudio at
/// Npgsql 10 in exactly the way the second pass exists to avoid.
/// </para>
/// </remarks>
public class PostgresStudioSolutionFilterTests
{
    private const string PinProperty = "PostgresStudioPinsNpgsql";

    private static string FilterFile => RepositoryRoot.Combine("postgres-studio.slnf");

    [Fact]
    public void The_filter_holds_exactly_the_projects_that_pin_npgsql()
    {
        string[] pinning = ProjectsThatPinNpgsql();

        // Not vacuous: the shared studio sets the property, and the scan found it.
        pinning.Should().Contain("src/PostgresStudio/PostgresStudio.csproj");

        FilterProjects().Should().BeEquivalentTo(pinning,
            "postgres-studio.slnf is what the latest-Npgsql pass builds and tests: a project that sets " +
            $"{PinProperty} and is missing from it is never tested at that Npgsql, and one listed without setting it " +
            "ignores the version the pass hands it");
    }

    [Fact]
    public void The_filter_names_this_solution_and_only_projects_it_has()
    {
        using JsonDocument filter = JsonDocument.Parse(File.ReadAllText(FilterFile));

        filter.RootElement.GetProperty("solution").GetProperty("path").GetString().Should().Be("marten-studio.slnx");

        string[] solution = [.. XDocument.Load(RepositoryRoot.Combine("marten-studio.slnx")).Descendants()
            .Where(static x => x.Name.LocalName == "Project")
            .Select(static x => ((string) x.Attribute("Path")!).Replace('\\', '/'))];

        FilterProjects().Should().OnlyContain(x => solution.Contains(x), "a filter entry the solution does not have fails the pass");
        FilterProjects().Should().OnlyContain(static x => File.Exists(RepositoryRoot.Combine(x.Split('/'))));
    }

    [Fact]
    public void Nothing_in_the_filter_reaches_a_project_outside_it_or_the_Marten_family()
    {
        string[] filtered = FilterProjects();
        List<string> problems = [];

        foreach (string project in filtered)
        {
            XDocument csproj = XDocument.Load(RepositoryRoot.Combine(project.Split('/')));
            string directory = Path.GetDirectoryName(RepositoryRoot.Combine(project.Split('/')))!;

            foreach (string reference in csproj.Descendants().Where(static x => x.Name.LocalName == "ProjectReference")
                .Select(static x => (string) x.Attribute("Include")!))
            {
                string target = Path.GetRelativePath(RepositoryRoot.FullPath, Path.GetFullPath(Path.Combine(directory, reference)))
                    .Replace('\\', '/');

                if (!filtered.Contains(target, StringComparer.Ordinal))
                {
                    problems.Add($"{project} references {target}, which the filter does not hold");
                }
            }

            foreach (string package in csproj.Descendants().Where(static x => x.Name.LocalName == "PackageReference")
                .Select(static x => (string) x.Attribute("Include")!)
                .Where(static x => x.StartsWith("Marten", StringComparison.Ordinal)
                    || x.StartsWith("JasperFx", StringComparison.Ordinal)
                    || x.StartsWith("Weasel", StringComparison.Ordinal)))
            {
                problems.Add($"{project} references {package}");
            }
        }

        problems.Should().BeEmpty(
            "the latest-Npgsql pass restores what the filter builds at that Npgsql, and MartenStudio - or Marten, whose Weasel " +
            "chooses its own Npgsql - must never be part of it");
    }

    /// <summary>The filter's projects, repository-relative with forward slashes.</summary>
    private static string[] FilterProjects()
    {
        using JsonDocument filter = JsonDocument.Parse(File.ReadAllText(FilterFile));

        return [.. filter.RootElement.GetProperty("solution").GetProperty("projects").EnumerateArray()
            .Select(static x => x.GetString()!.Replace('\\', '/'))];
    }

    /// <summary>The folders a project of this repository can live in.</summary>
    private static readonly string[] ProjectRoots = ["src", "tests", "samples"];

    /// <summary>Every project file under <c>src/</c>, <c>tests/</c> and <c>samples/</c> that sets the pin property to true.</summary>
    private static string[] ProjectsThatPinNpgsql() =>
        [.. ProjectRoots
            .Select(static x => RepositoryRoot.Combine(x))
            .Where(Directory.Exists)
            .SelectMany(static x => Directory.EnumerateFiles(x, "*.csproj", SearchOption.AllDirectories))
            .Select(static x => Path.GetRelativePath(RepositoryRoot.FullPath, x).Replace('\\', '/'))
            .Where(static x => !x.Split('/').Any(static segment => segment is "bin" or "obj"))
            .Where(static x => XDocument.Load(RepositoryRoot.Combine(x.Split('/'))).Descendants()
                .Any(static e => e.Name.LocalName == PinProperty && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)];
}
