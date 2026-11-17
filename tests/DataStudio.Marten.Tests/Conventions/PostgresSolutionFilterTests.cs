using System.Text.Json;
using System.Xml.Linq;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// <c>data-studio-postgres.slnf</c> - the solution filter the latest-Npgsql pass restores, builds and tests - holds exactly
/// the projects that set <c>DataStudioPostgresPinsNpgsql</c> and the shell they reference (which has no Npgsql to pin), and is
/// closed: nothing it builds reaches a project outside it.
/// </summary>
/// <remarks>
/// <para>
/// The pass (<c>build/Build.Npgsql.cs</c>) hands <c>-p:DataStudioNpgsqlVersion=</c> to the filter alone, so that
/// The Marten module never meets that Npgsql. Both directions of the set matter. A Postgres-only project missing from
/// the filter - <c>tests/DataStudio.Postgres.Tests</c> the day it lands - is never tested at the latest Npgsql, and nothing
/// would say so. A project in the filter that does not set the property ignores the version it was handed, and so does
/// one it references: a <c>ProjectReference</c> to the Marten module from inside the filter would restore the Marten module at
/// Npgsql 10 in exactly the way the second pass exists to avoid.
/// </para>
/// </remarks>
public class PostgresSolutionFilterTests
{
    private const string PinProperty = "DataStudioPostgresPinsNpgsql";

    /// <summary>
    /// The one project the filter holds without its setting the property (v2 step 3): the shell, which the database module references and which references no
    /// Npgsql at all (ShellFenceTests), so there is nothing for the pass to hand it. The filter is closed, and a project that is referenced has to be in it.
    /// </summary>
    private const string ShellProject = "src/DataStudio/DataStudio.csproj";

    private static string FilterFile => RepositoryRoot.Combine("data-studio-postgres.slnf");

    [Fact]
    public void The_filter_holds_exactly_the_projects_that_pin_npgsql()
    {
        string[] pinning = ProjectsThatPinNpgsql();

        // Not vacuous: the shared studio and the engine it is built on set the property, and the scan found both.
        pinning.Should().Contain("src/DataStudio.Postgres/DataStudio.Postgres.csproj").And.Contain("src/DataStudio.Postgres.Diagnostics/DataStudio.Postgres.Diagnostics.csproj");

        FilterProjects().Should().BeEquivalentTo([.. pinning, ShellProject],
            "data-studio-postgres.slnf is what the latest-Npgsql pass builds and tests: a project that sets " +
            $"{PinProperty} and is missing from it is never tested at that Npgsql, and one listed without setting it " +
            "ignores the version the pass hands it - the shell alone excepted, which has no Npgsql and is referenced from inside the filter");
        pinning.Should().NotContain(ShellProject, "the shell references no Npgsql, so it pins none");
    }

    [Fact]
    public void The_filter_names_this_solution_and_only_projects_it_has()
    {
        using JsonDocument filter = JsonDocument.Parse(File.ReadAllText(FilterFile));

        filter.RootElement.GetProperty("solution").GetProperty("path").GetString().Should().Be("data-studio.slnx");

        string[] solution = [.. XDocument.Load(RepositoryRoot.Combine("data-studio.slnx")).Descendants()
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
                // MSBuild accepts either separator in an Include, and '\\' is not one on Linux: normalize first, or
                // GetFullPath leaves a "..\\" segment unresolved and the reference looks like it leaves the filter.
                string target = Path.GetRelativePath(
                        RepositoryRoot.FullPath,
                        Path.GetFullPath(Path.Combine(directory, reference.Replace('\\', '/'))))
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
            "the latest-Npgsql pass restores what the filter builds at that Npgsql, and the Marten module - or Marten, whose Weasel " +
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
