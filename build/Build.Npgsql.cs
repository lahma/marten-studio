using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;

using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities;
using Fallout.Common.Utilities.Collections;
using Fallout.Components;
using Fallout.Solutions;

using Serilog;

using static Fallout.Common.Tools.DotNet.DotNetTasks;

using Project = Fallout.Solutions.Project;

/// <summary>
/// Which Npgsql the PostgresStudio-only projects are tested against: the floor the PostgresStudio package depends
/// on, and - in a second pass of their own - the newest Npgsql CI tests (design doc 6.2 and 6.3, and its amendment 2
/// after PS-0).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two passes, never one.</b> Both versions live in <c>Directory.Packages.props</c>
/// (<c>PostgresStudioNpgsqlFloor</c>, <c>PostgresStudioNpgsqlLatest</c>), and its conditional <c>Npgsql</c> row
/// applies only to projects that set <c>PostgresStudioPinsNpgsql</c>. Handing <c>-p:PostgresStudioNpgsqlVersion=</c>
/// to the whole solution would still reach MartenStudio: it references PostgresStudio, so restore takes
/// PostgresStudio's <c>Npgsql &gt;= 10.0.3</c> into MartenStudio's graph and its tests, with no diagnostic (measured
/// by PS-0). MartenStudio on Npgsql 10 is not a tested combination. So the whole-solution pass - Restore, Compile and
/// Test, at whatever Marten <c>--marten-version</c> chose - always gets the Npgsql <em>floor</em>, said explicitly so
/// that an environment variable of that name cannot change it, and a requested other Npgsql gets a pass of its own,
/// <see cref="NpgsqlPass" />: restore, build and test over <c>postgres-studio.slnf</c> alone, which holds exactly the
/// projects that set the property (<c>PostgresStudioSolutionFilterTests</c> keeps it so). It runs after Test, in
/// separate <c>dotnet</c> invocations, so MartenStudio never meets that Npgsql.
/// </para>
/// <para>
/// <b>Pack</b> refuses any Npgsql but the floor (<see cref="AssertPackingAtTheNpgsqlFloor" />), and
/// <c>src/PostgresStudio/PostgresStudio.csproj</c> refuses the same for a bare <c>dotnet pack</c>, checking what
/// <c>project.assets.json</c> resolved as well as the property: after the second pass that file holds Npgsql 10.0.3,
/// and NuGet writes the dependency range from it.
/// </para>
/// </remarks>
partial class Build
{
    /// <summary>The MSBuild property <c>Directory.Packages.props</c> takes the PostgresStudio-only Npgsql version from.</summary>
    const string NpgsqlVersionProperty = "PostgresStudioNpgsqlVersion";

    [Parameter("The Npgsql version the PostgresStudio-only projects (postgres-studio.slnf) are tested against: " +
               "'floor' (the default - the version the PostgresStudio package depends on, the only one Pack packs at, " +
               "and the one the whole-solution pass always uses), 'latest' (the newest Npgsql CI tests, pinned in " +
               "Directory.Packages.props), or an exact version such as 10.0.3. Anything but the floor adds a second " +
               "restore, build and test pass over postgres-studio.slnf after Test. (env: NPGSQL_VERSION)")]
    readonly string NpgsqlVersion;

    /// <summary>The solution filter holding exactly the projects that set <c>PostgresStudioPinsNpgsql</c>.</summary>
    AbsolutePath PostgresStudioSolutionFilter => RootDirectory / "postgres-studio.slnf";

    /// <summary><c>PostgresStudioNpgsqlFloor</c> from <c>Directory.Packages.props</c>.</summary>
    string NpgsqlFloor { get; set; }

    /// <summary><c>PostgresStudioNpgsqlLatest</c> from <c>Directory.Packages.props</c>.</summary>
    string NpgsqlLatest { get; set; }

    /// <summary>The exact Npgsql the second pass uses; the floor when there is no second pass.</summary>
    string ResolvedNpgsqlVersion { get; set; }

    /// <summary>
    /// Reads the two versions out of <c>Directory.Packages.props</c> and settles which one this run's second pass uses.
    /// Called from <c>OnBuildInitialized</c>, so a misspelt <c>--npgsql-version</c> fails before anything is restored.
    /// </summary>
    void ResolveNpgsqlVersion()
    {
        var props = XDocument.Load(PackagesPropsFile);

        NpgsqlFloor = ReadPackagesProperty(props, "PostgresStudioNpgsqlFloor");
        NpgsqlLatest = ReadPackagesProperty(props, "PostgresStudioNpgsqlLatest");

        var requested = NpgsqlVersion?.Trim();

        ResolvedNpgsqlVersion = requested switch
        {
            null or "" => NpgsqlFloor,
            _ when requested.Equals("floor", StringComparison.OrdinalIgnoreCase) => NpgsqlFloor,
            _ when requested.Equals("latest", StringComparison.OrdinalIgnoreCase) => NpgsqlLatest,
            _ when ExactVersion.IsMatch(requested) => requested,
            _ => throw new ArgumentException(
                $"--npgsql-version '{requested}' is neither 'floor', 'latest' nor an exact version such as " +
                $"{NpgsqlLatest}. A range or a wildcard is not accepted: the point of the parameter is that a run " +
                "says exactly which Npgsql it tested."),
        };

        if (ResolvedNpgsqlVersion == NpgsqlFloor)
        {
            Log.Information("Npgsql {Version} for PostgresStudio (the floor; latest tested {Latest}): no second pass",
                NpgsqlFloor, NpgsqlLatest);
        }
        else
        {
            Log.Information(
                "Npgsql {Version} for PostgresStudio ({Which}; floor {Floor}): a second pass over {Filter} after Test",
                ResolvedNpgsqlVersion, ResolvedNpgsqlVersion == NpgsqlLatest ? "the latest tested" : "an explicit version",
                NpgsqlFloor, PostgresStudioSolutionFilter.Name);
        }
    }

    /// <summary>
    /// The PostgresStudio package is packed at the Npgsql floor or not at all. The csproj has the same guard for a bare
    /// <c>dotnet pack</c>; this one fails first and names the Fallout parameter.
    /// </summary>
    void AssertPackingAtTheNpgsqlFloor()
    {
        Assert.True(ResolvedNpgsqlVersion == NpgsqlFloor,
            $"Refusing to pack with --npgsql-version {ResolvedNpgsqlVersion}: PostgresStudio is packed only at the Npgsql " +
            $"floor ({NpgsqlFloor}, PostgresStudioNpgsqlFloor in Directory.Packages.props). The nuspec would say " +
            $"'Npgsql >= {ResolvedNpgsqlVersion}' and raise every host's Npgsql. Run Pack without --npgsql-version (and " +
            "without NPGSQL_VERSION in the environment), or raise the floor deliberately and record why in AGENTS.md, " +
            "Package budget changes.");
    }

    Target NpgsqlPass => _ => _
        .Description("After Test: restores, builds and tests postgres-studio.slnf at --npgsql-version, when that is not the floor")
        .TriggeredBy<ITest>(x => x.Test)
        .OnlyWhenDynamic(() => ResolvedNpgsqlVersion != NpgsqlFloor)
        .Executes(() =>
        {
            var projects = PostgresStudioFilterProjects();
            var configuration = ((IHasConfiguration)this).Configuration;

            Log.Information("Npgsql {Version} pass over {Filter}: {Projects}",
                ResolvedNpgsqlVersion, PostgresStudioSolutionFilter.Name, string.Join(", ", projects.Select(x => x.Name)));

            // The Marten version is passed for symmetry only: nothing in the filter references Marten.
            DotNetRestore(_ => _
                .SetProjectFile(PostgresStudioSolutionFilter)
                .SetProperty(MartenVersionProperty, ResolvedMartenVersion)
                .SetProperty(NpgsqlVersionProperty, ResolvedNpgsqlVersion));

            DotNetBuild(_ => _
                .SetProjectFile(PostgresStudioSolutionFilter)
                .SetConfiguration(configuration)
                .EnableNoRestore()
                .SetProperty("Version", Version)
                .SetProperty(MartenVersionProperty, ResolvedMartenVersion)
                .SetProperty(NpgsqlVersionProperty, ResolvedNpgsqlVersion));

            var testProjects = projects.Where(x => x.Name.EndsWith(".Tests", StringComparison.Ordinal)).ToList();

            if (testProjects.Count == 0)
            {
                // Until tests/PostgresStudio.Tests exists (PS-3a): the pass still proves src/PostgresStudio restores and
                // compiles at this Npgsql. PostgresStudioSolutionFilterTests fails the moment a project that sets
                // PostgresStudioPinsNpgsql is missing from the filter, so this branch cannot outlive that project.
                Log.Warning("{Filter} lists no test project yet: PostgresStudio was restored and built at Npgsql {Version}, " +
                            "and no test ran", PostgresStudioSolutionFilter.Name, ResolvedNpgsqlVersion);
            }
            else
            {
                var results = ((ITest)this).TestResultDirectory;

                // Each project's TRX is named after this pass, so the floor pass's file for the same project survives
                // beside it in artifacts/test-results and both are uploaded.
                DotNetTest(_ => _
                        .SetConfiguration(configuration)
                        .EnableNoBuild()
                        .SetResultsDirectory(results)
                        .SetProperty("Version", Version)
                        .SetProperty(MartenVersionProperty, ResolvedMartenVersion)
                        .SetProperty(NpgsqlVersionProperty, ResolvedNpgsqlVersion)
                        .CombineWith(testProjects, (_, project) => _
                            .SetProjectFile(project.Path)
                            .AddLoggers($"trx;LogFileName={project.Name}.npgsql-{ResolvedNpgsqlVersion}.trx")
                            .When(GitHubActions.Instance != null && project.HasPackageReference("GitHubActionsTestLogger"),
                                _ => _.AddLoggers("GitHubActions;report-warnings=false"))),
                    degreeOfParallelism: 1,
                    completeOnFailure: true);
            }

            ReportSummary(_ => _
                .AddPair("Npgsql pass", ResolvedNpgsqlVersion)
                .AddPair("Npgsql test projects", testProjects.Count.ToString()));
        });

    /// <summary>
    /// The projects <c>postgres-studio.slnf</c> lists, as the solution knows them. A path the solution does not have
    /// fails here, by name, rather than as an MSBuild error about a filter.
    /// </summary>
    IReadOnlyList<Project> PostgresStudioFilterProjects()
    {
        Assert.True(PostgresStudioSolutionFilter.FileExists(), $"'{PostgresStudioSolutionFilter}' does not exist");

        using var filter = JsonDocument.Parse(File.ReadAllText(PostgresStudioSolutionFilter));
        var listed = filter.RootElement.GetProperty("solution").GetProperty("projects").EnumerateArray()
            .Select(x => x.GetString()!.Replace('\\', '/'))
            .ToList();

        Assert.NotEmpty(listed, $"{PostgresStudioSolutionFilter.Name} lists no project");

        var known = Solution.GetAllProjects("*").ToDictionary(x => x.RelativePath.Replace('\\', '/'), StringComparer.Ordinal);

        return listed
            .Select(path => known.TryGetValue(path, out var project)
                ? project
                : throw new InvalidOperationException(
                    $"{PostgresStudioSolutionFilter.Name} lists '{path}', which marten-studio.slnx does not have"))
            .ToList();
    }
}
