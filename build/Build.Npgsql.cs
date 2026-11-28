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
/// Which Npgsql the Postgres-only projects are tested against: the floor the Postgres module package depends
/// on, and - in a second pass of their own - the newest Npgsql CI tests (design doc 6.2 and 6.3, and its amendment 2
/// after PS-0).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two passes, never one.</b> Both versions live in <c>Directory.Packages.props</c>
/// (<c>DataStudioNpgsqlFloor</c>, <c>DataStudioNpgsqlLatest</c>), and its conditional <c>Npgsql</c> row
/// applies only to projects that set <c>DataStudioPostgresPinsNpgsql</c>. Handing <c>-p:DataStudioNpgsqlVersion=</c>
/// to the whole solution would still reach the Marten module: it references the Postgres module, so restore takes
/// The Postgres module's <c>Npgsql &gt;= 10.0.3</c> into the Marten module's graph and its tests, with no diagnostic (measured
/// by PS-0). The Marten module on Npgsql 10 is not a tested combination. So the whole-solution pass - Restore, Compile and
/// Test, at whatever Marten <c>--marten-version</c> chose - always gets the Npgsql <em>floor</em>, said explicitly so
/// that an environment variable of that name cannot change it, and a requested other Npgsql gets a pass of its own,
/// <see cref="NpgsqlPass" />: restore, build and test over <c>data-studio-postgres.slnf</c> alone, which holds exactly the
/// projects that set the property (<c>PostgresSolutionFilterTests</c> keeps it so). It runs after Test, in
/// separate <c>dotnet</c> invocations, so the Marten module never meets that Npgsql.
/// </para>
/// <para>
/// <b>Pack</b> refuses any Npgsql but the floor (<see cref="AssertPackingAtTheNpgsqlFloor" />), and
/// <c>src/DataStudio.Postgres/DataStudio.Postgres.csproj</c> refuses the same for a bare <c>dotnet pack</c>, checking what
/// <c>project.assets.json</c> resolved as well as the property: after the second pass that file holds Npgsql 10.0.3,
/// and NuGet writes the dependency range from it.
/// </para>
/// </remarks>
partial class Build
{
    /// <summary>The MSBuild property <c>Directory.Packages.props</c> takes the Postgres-only Npgsql version from.</summary>
    const string NpgsqlVersionProperty = "DataStudioNpgsqlVersion";

    /// <summary>
    /// The MSBuild property the conditional <c>Microsoft.Extensions.*</c> rows take their version from. The second pass sets it to
    /// <c>DataStudioExtensionsLatest</c>: Npgsql 11's net10.0 asset asks for 11.0.0 of two of them, which a central pin at the
    /// floor either fails (NU1109) or silently beats.
    /// </summary>
    const string ExtensionsVersionProperty = "DataStudioExtensionsVersion";

    [Parameter("The Npgsql version the Postgres-only projects (data-studio-postgres.slnf) are tested against: " +
               "'floor' (the default - the version the DataStudio.Postgres package depends on, the only one Pack packs at, " +
               "and the one the whole-solution pass always uses), 'latest' (the newest Npgsql CI tests, pinned in " +
               "Directory.Packages.props), or an exact version such as 10.0.3. Anything but the floor adds a second " +
               "restore, build and test pass over data-studio-postgres.slnf after Test. (env: NPGSQL_VERSION)")]
    readonly string NpgsqlVersion;

    /// <summary>The solution filter holding exactly the projects that set <c>DataStudioPostgresPinsNpgsql</c>.</summary>
    AbsolutePath PostgresSolutionFilter => RootDirectory / "data-studio-postgres.slnf";

    /// <summary><c>DataStudioNpgsqlFloor</c> from <c>Directory.Packages.props</c>.</summary>
    string NpgsqlFloor { get; set; }

    /// <summary><c>DataStudioNpgsqlLatest</c> from <c>Directory.Packages.props</c>.</summary>
    string NpgsqlLatest { get; set; }

    /// <summary><c>DataStudioExtensionsFloor</c> from <c>Directory.Packages.props</c>: the Microsoft.Extensions.* every run but the second pass restores, and the only one Pack packs at.</summary>
    string ExtensionsFloor { get; set; }

    /// <summary><c>DataStudioExtensionsLatest</c> from <c>Directory.Packages.props</c>: the Microsoft.Extensions.* the second pass restores.</summary>
    string ExtensionsLatest { get; set; }

    /// <summary>The exact Npgsql the second pass uses; the floor when there is no second pass.</summary>
    string ResolvedNpgsqlVersion { get; set; }

    /// <summary>
    /// Reads the two versions out of <c>Directory.Packages.props</c> and settles which one this run's second pass uses.
    /// Called from <c>OnBuildInitialized</c>, so a misspelt <c>--npgsql-version</c> fails before anything is restored.
    /// </summary>
    void ResolveNpgsqlVersion()
    {
        var props = XDocument.Load(PackagesPropsFile);

        NpgsqlFloor = ReadPackagesProperty(props, "DataStudioNpgsqlFloor");
        NpgsqlLatest = ReadPackagesProperty(props, "DataStudioNpgsqlLatest");
        ExtensionsFloor = ReadPackagesProperty(props, "DataStudioExtensionsFloor");
        ExtensionsLatest = ReadPackagesProperty(props, "DataStudioExtensionsLatest");

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
            Log.Information("Npgsql {Version} for the Postgres module (the floor; latest tested {Latest}): no second pass",
                NpgsqlFloor, NpgsqlLatest);
        }
        else
        {
            Log.Information(
                "Npgsql {Version} for the Postgres module ({Which}; floor {Floor}): a second pass over {Filter} after Test",
                ResolvedNpgsqlVersion, ResolvedNpgsqlVersion == NpgsqlLatest ? "the latest tested" : "an explicit version",
                NpgsqlFloor, PostgresSolutionFilter.Name);
        }
    }

    /// <summary>
    /// The Postgres module package is packed at the Npgsql floor or not at all. The csproj has the same guard for a bare
    /// <c>dotnet pack</c>; this one fails first and names the Fallout parameter.
    /// </summary>
    void AssertPackingAtTheNpgsqlFloor()
    {
        Assert.True(ResolvedNpgsqlVersion == NpgsqlFloor,
            $"Refusing to pack with --npgsql-version {ResolvedNpgsqlVersion}: the Postgres module is packed only at the Npgsql " +
            $"floor ({NpgsqlFloor}, DataStudioNpgsqlFloor in Directory.Packages.props). The nuspec would say " +
            $"'Npgsql >= {ResolvedNpgsqlVersion}' and raise every host's Npgsql. Run Pack without --npgsql-version (and " +
            "without NPGSQL_VERSION in the environment), or raise the floor deliberately and record why in " +
            "docs/maintainers/package-budget-changes.md.");
    }

    Target NpgsqlPass => _ => _
        .Description("After Test: restores, builds and tests data-studio-postgres.slnf at --npgsql-version, when that is not the floor")
        .TriggeredBy<ITest>(x => x.Test)
        .OnlyWhenDynamic(() => ResolvedNpgsqlVersion != NpgsqlFloor)
        .Executes(() =>
        {
            var projects = PostgresFilterProjects();
            var configuration = ((IHasConfiguration)this).Configuration;

            Log.Information("Npgsql {Version} pass over {Filter}: {Projects}",
                ResolvedNpgsqlVersion, PostgresSolutionFilter.Name, string.Join(", ", projects.Select(x => x.Name)));

            // The Marten version is passed for symmetry only: nothing in the filter references Marten.
            DotNetRestore(_ => _
                .SetProjectFile(PostgresSolutionFilter)
                .SetProperty(MartenVersionProperty, ResolvedMartenVersion)
                .SetProperty(NpgsqlVersionProperty, ResolvedNpgsqlVersion)
                .SetProperty(ExtensionsVersionProperty, ExtensionsLatest));

            DotNetBuild(_ => _
                .SetProjectFile(PostgresSolutionFilter)
                .SetConfiguration(configuration)
                .EnableNoRestore()
                .SetProperty("Version", Version)
                .SetProperty(MartenVersionProperty, ResolvedMartenVersion)
                .SetProperty(NpgsqlVersionProperty, ResolvedNpgsqlVersion)
                .SetProperty(ExtensionsVersionProperty, ExtensionsLatest));

            var testProjects = projects.Where(x => x.Name.EndsWith(".Tests", StringComparison.Ordinal)).ToList();

            if (testProjects.Count == 0)
            {
                // Until tests/DataStudio.Postgres.Tests exists (PS-3a): the pass still proves src/DataStudio.Postgres restores and
                // compiles at this Npgsql. PostgresSolutionFilterTests fails the moment a project that sets
                // DataStudioPostgresPinsNpgsql is missing from the filter, so this branch cannot outlive that project.
                Log.Warning("{Filter} lists no test project yet: the Postgres module was restored and built at Npgsql {Version}, " +
                            "and no test ran", PostgresSolutionFilter.Name, ResolvedNpgsqlVersion);
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
                        .SetProperty(ExtensionsVersionProperty, ExtensionsLatest)
                        .CombineWith(testProjects, (_, project) => WithBlameHang(_, project)
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
    /// The projects <c>data-studio-postgres.slnf</c> lists, as the solution knows them. A path the solution does not have
    /// fails here, by name, rather than as an MSBuild error about a filter.
    /// </summary>
    IReadOnlyList<Project> PostgresFilterProjects()
    {
        Assert.True(PostgresSolutionFilter.FileExists(), $"'{PostgresSolutionFilter}' does not exist");

        using var filter = JsonDocument.Parse(File.ReadAllText(PostgresSolutionFilter));
        var listed = filter.RootElement.GetProperty("solution").GetProperty("projects").EnumerateArray()
            .Select(x => x.GetString()!.Replace('\\', '/'))
            .ToList();

        Assert.NotEmpty(listed, $"{PostgresSolutionFilter.Name} lists no project");

        var known = Solution.GetAllProjects("*").ToDictionary(x => x.RelativePath.Replace('\\', '/'), StringComparer.Ordinal);

        return listed
            .Select(path => known.TryGetValue(path, out var project)
                ? project
                : throw new InvalidOperationException(
                    $"{PostgresSolutionFilter.Name} lists '{path}', which data-studio.slnx does not have"))
            .ToList();
    }
}
