using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Fallout.Common;
using Fallout.Common.CI;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities.Collections;
using Fallout.Components;
using Fallout.Solutions;

using Serilog;

using Project = Fallout.Solutions.Project;

/// <summary>
/// The Fallout orchestrator for marten-studio.
/// </summary>
/// <remarks>
/// Restore / Compile / Test come from the Fallout.Components interfaces. Only six things are
/// hand-written: the CHANGELOG-driven version (<see cref="OnBuildInitialized"/>), the NuGet leg in
/// <c>Build.Publish.cs</c>, the Playwright browser install in <c>Build.Browsers.cs</c>, the choice of
/// Marten - the floor, or the latest-tested 9.x - in <c>Build.Marten.cs</c>, and the choice of Npgsql for
/// the PostgresStudio-only projects, with its second pass over <c>postgres-studio.slnf</c>, in
/// <c>Build.Npgsql.cs</c>, and the choice of Confluent.Kafka for the KStudio projects in <c>Build.Confluent.cs</c>.
/// There is no publish or archive target: the studio ships as three NuGet packages,
/// PostgresStudio.Diagnostics, PostgresStudio and MartenStudio, released together, and nothing else.
/// </remarks>
[ShutdownDotNetAfterServerBuild]
partial class Build : FalloutBuild,
    IHasSolution,
    IHasConfiguration,
    IHasArtifacts,
    IHasChangelog,
    IHasGitRepository,
    IRestore,
    ICompile,
    ITest,
    ICreateGitHubRelease
{
    public static int Main() => Execute<Build>(x => ((ITest)x).Test);

    [Solution] readonly Solution Solution;
    Solution IHasSolution.Solution => Solution;

    public AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath SamplesDirectory => RootDirectory / "samples";
    AbsolutePath TestsDirectory => RootDirectory / "tests";

    // Spelled out rather than looked up through the solution: on Linux the path is case-sensitive, and
    // a typo here should fail as a missing file rather than as a null reference three targets later.
    AbsolutePath StudioProject => SourceDirectory / "MartenStudio" / "MartenStudio.csproj";
    AbsolutePath DiagnosticsProject => SourceDirectory / "PostgresStudio.Diagnostics" / "PostgresStudio.Diagnostics.csproj";
    AbsolutePath PostgresStudioProject => SourceDirectory / "PostgresStudio" / "PostgresStudio.csproj";
    AbsolutePath SampleProject => SamplesDirectory / "MartenStudio.Sample" / "MartenStudio.Sample.csproj";

    AbsolutePath ChangelogPath => RootDirectory / "CHANGELOG.md";
    AbsolutePath ReleaseNotesFile => ArtifactsDirectory / "release-notes.md";

    /// <summary>The version parsed out of CHANGELOG.md - the single version authority.</summary>
    string Version { get; set; }

    ReleaseNotes LatestReleaseNotes { get; set; }

    /// <summary>True when this build is running for a <c>v*</c> tag - i.e. it is a release build.</summary>
    /// <remarks>
    /// Fallout's own repositories derive this from <c>GitRepository.Tags</c> because there the tag
    /// <em>is</em> the version. Here CHANGELOG.md is the version authority and the tag only has to
    /// agree with it, so on GitHub Actions the ref the run was triggered for is the authoritative
    /// answer: <c>GITHUB_REF_NAME</c> is the tag name on a tag push, and it is what
    /// <see cref="AssertReleaseTagMatchesChangelogVersion"/> compares against anyway. Off CI it
    /// falls back to a v* tag pointing at HEAD, so the gate can be exercised locally.
    /// </remarks>
    bool IsTaggedBuild => VersionTag != null;

    /// <summary>The <c>v*</c> tag this build is running for, or <c>null</c> if it is not a tag build.</summary>
    string VersionTag
    {
        get
        {
            if (GitHubActions.Instance == null)
            {
                return ((IHasGitRepository)this).GitRepository?.Tags.FirstOrDefault(IsVersionTag);
            }

            // GITHUB_REF_TYPE distinguishes a tag push from a branch push, but it is only consulted
            // when it is actually set, so the gate stays exercisable with GITHUB_REF_NAME alone.
            var refType = Environment.GetEnvironmentVariable("GITHUB_REF_TYPE");
            var refName = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

            return refType is null or "tag" && IsVersionTag(refName) ? refName : null;
        }
    }

    /// <summary>A version tag is <c>v</c> followed by a digit - so a <c>vnext</c> branch is not one.</summary>
    static bool IsVersionTag(string value) =>
        value?.StartsWith('v') == true && value.Length > 1 && char.IsAsciiDigit(value[1]);

    protected override void OnBuildInitialized()
    {
        base.OnBuildInitialized();

        // CHANGELOG.md is the version authority (never mutated by the build). Its first line must
        // parse as a version header - a "# Changelog" title would abort here.
        var changelog = new ReleaseNotesParser().Parse(File.ReadAllText(ChangelogPath));
        LatestReleaseNotes = changelog.FirstOrDefault()
            .NotNull($"{ChangelogPath} contains no parsable release section");

        Version = LatestReleaseNotes.SemVersion.ToString();
        Log.Information("Version from {Changelog}: {Version}", ChangelogPath, Version);

        // Build.Marten.cs: the floor, or the latest-tested Marten when --marten-version / MARTEN_VERSION
        // asks for it. Settled here so a bad value fails before anything is restored.
        ResolveMartenVersion();

        // Build.Npgsql.cs: the same for the PostgresStudio-only projects' Npgsql, whose non-floor values get a
        // pass of their own after Test.
        ResolveNpgsqlVersion();

        // Build.Confluent.cs: the floor, or the latest-tested Confluent.Kafka for the KStudio projects.
        ResolveConfluentVersion();
    }

    Target Clean => _ => _
        .Description("Deletes all build output and the artifacts directory")
        .Before<IRestore>()
        .Executes(() =>
        {
            // samples/ is swept as well as src/ and tests/: the sample host is a real web project whose
            // obj/ holds a static web assets manifest, and a stale one is exactly the kind of thing that
            // makes an RCL's _content/ assets look fine locally and 404 on a clean machine.
            SourceDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            SamplesDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            TestsDirectory.GlobDirectories("**/bin", "**/obj").DeleteDirectories();
            ArtifactsDirectory.CreateOrCleanDirectory();
        });

    IEnumerable<Project> ITest.TestProjects => Solution.GetAllProjects("*.Tests");

    // The Marten version goes to all three, the floor included (Build.Marten.cs says why). The Npgsql version of
    // this whole-solution pass is always the floor - never the requested one, which would reach MartenStudio through
    // its PostgresStudio reference - and is said explicitly for the same reason (Build.Npgsql.cs). The Confluent.Kafka
    // version goes to all three too: only the KStudio projects reference the package (Build.Confluent.cs).
    Configure<DotNetRestoreSettings> IRestore.RestoreSettings => _ => _
        .SetProperty(MartenVersionProperty, ResolvedMartenVersion)
        .SetProperty(NpgsqlVersionProperty, NpgsqlFloor)
        .SetProperty(ConfluentVersionProperty, ResolvedConfluentVersion);

    Configure<DotNetBuildSettings> ICompile.CompileSettings => _ => _
        .SetProperty("Version", Version)
        .SetProperty(MartenVersionProperty, ResolvedMartenVersion)
        .SetProperty(NpgsqlVersionProperty, NpgsqlFloor)
        .SetProperty(ConfluentVersionProperty, ResolvedConfluentVersion);

    Configure<DotNetTestSettings> ITest.TestSettings => _ => _
        .SetProperty("Version", Version)
        .SetProperty(MartenVersionProperty, ResolvedMartenVersion)
        .SetProperty(NpgsqlVersionProperty, NpgsqlFloor)
        .SetProperty(ConfluentVersionProperty, ResolvedConfluentVersion);

    // 1.2-HANG-2: the three fast suites run with a ten-minute blame-hang timeout, so a test that waits for ever fails the run within ten
    // minutes with a dump and a sequence file naming it (in artifacts/test-results, which the workflows upload), instead of running to the
    // job's own timeout with nothing said. Not the three integration suites: their Playwright and live tests may legitimately run long.
    Configure<DotNetTestSettings, Project> ITest.TestProjectSettings => (_, project) => WithBlameHang(_, project);

    /// <summary>The test projects that run without Docker or a browser: nothing in them takes ten minutes, so a test that does is hung.</summary>
    static readonly string[] BlameHangTestProjects = ["MartenStudio.Tests", "PostgresStudio.Tests", "KStudio.Tests"];

    /// <summary>How long a single fast-suite test may run before the run is stopped and a mini dump taken (<c>--blame-hang-timeout</c>).</summary>
    const string BlameHangTimeout = "10m";

    /// <summary>Adds the blame-hang collector to <paramref name="settings" /> when <paramref name="project" /> is one of the fast suites.</summary>
    static DotNetTestSettings WithBlameHang(DotNetTestSettings settings, Project project) =>
        BlameHangTestProjects.Contains(project.Name, StringComparer.Ordinal)
            ? settings.EnableBlameHang().SetBlameHangTimeout(BlameHangTimeout).SetBlameHangDumpType("mini")
            : BlameCrashTestProjects.Contains(project.Name, StringComparer.Ordinal)
                ? settings.EnableBlameCrash()
                : settings;

    /// <summary>
    /// KS-CI-FIX-4: the suite that loads native librdkafka runs with the blame-crash collector, so a native crash (exit code 139, marten-latest run
    /// 43212361402) names the test that was running in the log and a sequence file, instead of only "Test process crashed".
    /// </summary>
    static readonly string[] BlameCrashTestProjects = ["KStudio.Integration.Tests"];

    string ICreateGitHubRelease.Name => $"v{Version}";

    /// <summary>A version with a pre-release suffix (1.0.0-rc.1, 0.0.1-test) never gets marked "Latest".</summary>
    bool ICreateGitHubRelease.Prerelease => Version.Contains('-');

    /// <summary>The GitHub Release carries the changelog notes and no files at all.</summary>
    /// <remarks>
    /// MartenStudio is consumed by <c>PackageReference</c>; the package on nuget.org is the artifact and
    /// there is nothing a user could usefully download from a release page. Attaching a second copy of
    /// the .nupkg would only invite someone to install the file rather than the published package - a
    /// build that never went through the trusted-publishing path and carries none of its provenance.
    /// </remarks>
    IEnumerable<AbsolutePath> ICreateGitHubRelease.AssetFiles => [];

    /// <summary>
    /// The release body is read from here rather than from CHANGELOG.md directly.
    /// </summary>
    /// <remarks>
    /// <c>ICreateGitHubRelease</c> builds the body with <c>ChangelogTasks.ExtractChangelogSectionNotes</c>,
    /// which only recognises <c>## </c> headings and stops a section at the first line that is not a
    /// bullet. Our changelog uses <c>#</c> headings (the format <c>ReleaseNotesParser</c> - the version
    /// authority - expects) and wraps its bullets over several lines, so pointed at CHANGELOG.md that
    /// helper finds nothing and the release ships with an empty body. <see cref="WriteReleaseNotes"/>
    /// rewrites the top section into the shape it does understand.
    /// </remarks>
    string IHasChangelog.ChangelogFile => ReleaseNotesFile;

    // Both actions run before the inherited release logic: actions are appended in call order.
    Target ICreateGitHubRelease.CreateGitHubRelease => _ => _
        .Executes(AssertReleaseTagMatchesChangelogVersion)
        .Executes(WriteReleaseNotes)
        .Inherit<ICreateGitHubRelease>();

    /// <summary>
    /// Rewrites the newest CHANGELOG.md section into <see cref="ReleaseNotesFile"/> as a
    /// <c>## version</c> heading followed by one single-line bullet per entry, which is the only
    /// shape <c>ExtractChangelogSectionNotes</c> reads back in full.
    /// </summary>
    void WriteReleaseNotes()
    {
        var bullets = new List<string>();

        foreach (var line in LatestReleaseNotes.Notes)
        {
            // A "- " line opens a new entry; every other line is the continuation of a wrapped one.
            // The leading prose paragraph has no bullet to continue, so it becomes an entry itself.
            if (line.StartsWith("- ", StringComparison.Ordinal) || bullets.Count == 0)
            {
                bullets.Add(line.StartsWith("- ", StringComparison.Ordinal) ? line[2..] : line);
            }
            else
            {
                bullets[^1] += " " + line;
            }
        }

        var lines = new List<string> { $"## {Version}" };
        lines.AddRange(bullets.Select(x => $"- {x}"));

        ReleaseNotesFile.Parent.CreateDirectory();
        ReleaseNotesFile.WriteAllLines(lines.ToArray());

        Log.Information("Wrote {Count} release note(s) to {File}", bullets.Count, ReleaseNotesFile);
    }

    /// <summary>
    /// In CI the git tag is what people see; CHANGELOG.md is what the build believes. If the two
    /// disagree the release would be named after one and contain the other, so fail loudly.
    /// </summary>
    void AssertReleaseTagMatchesChangelogVersion()
    {
        if (GitHubActions.Instance == null)
        {
            Log.Warning("Not running in GitHub Actions - skipping the release tag check");
            return;
        }

        var expected = $"v{Version}";
        var actual = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

        Assert.True(actual == expected,
            $"Refusing to publish: the workflow ran for ref '{actual}' but CHANGELOG.md says the version " +
            $"is {Version} (tag '{expected}'). Tag the commit that carries the matching CHANGELOG entry.");
    }
}
