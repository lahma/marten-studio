using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Xml.Linq;

using Fallout.Common;
using Fallout.Common.CI.GitHubActions;
using Fallout.Common.IO;
using Fallout.Common.Tooling;
using Fallout.Common.Tools.DotNet;
using Fallout.Common.Utilities.Collections;
using Fallout.Common.Utilities.Net;

using Serilog;

using static Fallout.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// The NuGet distribution channel (D17): <c>Pack</c> builds the two Razor Class Library packages, PostgresStudio
/// and MartenStudio - which depends on exactly the PostgresStudio packed beside it - and <c>Publish</c> pushes both
/// to nuget.org with one short-lived API key minted from the workflow's GitHub OIDC token (trusted publishing - no
/// stored NuGet API key anywhere).
/// </summary>
/// <remarks>
/// nuget.org is the <em>only</em> distribution channel. Each package is a Razor Class Library consumed
/// through a <c>PackageReference</c>; there are no binaries to run, nothing to download and unpack, and
/// therefore no assets on the GitHub Release either - <c>ICreateGitHubRelease.AssetFiles</c> in
/// <c>Build.cs</c> is deliberately empty and the release carries only the changelog notes. There is no
/// preview feed: every tagged version is published from <c>publish.yml</c> by trusted publishing, which
/// is why <c>Publish</c> is a single-source target gated on <see cref="Build.IsTaggedBuild"/> rather than
/// a two-way switch between a release feed and a preview one.
/// </remarks>
partial class Build
{
    const string NuGetSource = "https://api.nuget.org/v3/index.json";

    /// <summary>The OIDC audience nuget.org expects. Not <c>nuget</c>, whatever the design doc says.</summary>
    const string NuGetAudience = "https://www.nuget.org";

    const string NuGetTokenServiceUrl = "https://www.nuget.org/api/v2/token";

    /// <summary>
    /// Any non-empty User-Agent will do, but there must be one - see
    /// <see cref="GetTrustedPublishingApiKey"/>.
    /// </summary>
    const string NuGetUserAgent = "marten-studio-build/1.0";

    /// <summary>The workflow file the nuget.org trusted publishing policy is scoped to.</summary>
    /// <remarks>
    /// Only used in the error message, but it is the field people get wrong, so it is worth naming
    /// the actual value rather than describing it.
    /// </remarks>
    const string PublishWorkflowFile = "publish.yml";

    [Parameter("nuget.org profile name of the account that owns the trusted publishing policy " +
               "(env: NUGET_USER). Public information, not a secret.")]
    readonly string NuGetUser;

    AbsolutePath PackagesDirectory => ArtifactsDirectory / "packages";

    const string PostgresStudioPackageId = "PostgresStudio";
    const string MartenStudioPackageId = "MartenStudio";

    /// <summary>
    /// The two packages a release consists of, in the order they are packed and pushed: PostgresStudio first, because
    /// MartenStudio depends on exactly it. Pushing in this order means a failed PostgresStudio push stops the run before
    /// any MartenStudio is uploaded. It does not make the two appear together: nuget.org validates and indexes each
    /// package on its own, so for a few minutes MartenStudio can be listed while its PostgresStudio is not yet
    /// restorable, and a host restoring in that window gets NU1102 until it is.
    /// </summary>
    AbsolutePath[] ReleasePackages =>
    [
        PackagesDirectory / $"{PostgresStudioPackageId}.{Version}.nupkg",
        PackagesDirectory / $"{MartenStudioPackageId}.{Version}.nupkg",
    ];

    Target Pack => _ => _
        .Description("Packs the PostgresStudio and MartenStudio NuGet packages into artifacts/packages")
        .Produces(PackagesDirectory / "*.nupkg")
        .Executes(() =>
        {
            // At both floors or not at all (Build.Marten.cs, Build.Npgsql.cs): a package packed against a newer
            // Marten or Npgsql would depend on it and raise every host's floor.
            AssertPackingAtTheMartenFloor();
            AssertPackingAtTheNpgsqlFloor();

            // Independent of Compile for the same reason PublishAot is: this is a Release build of
            // two projects, and `dotnet pack` runs its own build anyway.
            PackagesDirectory.CreateOrCleanDirectory();

            // One project at a time, PostgresStudio first, in a plain loop rather than the combinatorial overload
            // (PLINQ, no order promised): MartenStudio's pack builds PostgresStudio again through its
            // ProjectReference, and two packs writing one obj/ folder at once is a race.
            foreach (var project in new[] { PostgresStudioProject, StudioProject })
            {
                DotNetPack(_ => _
                    .SetProject(project)
                    .SetConfiguration(Configuration.Release)
                    .SetOutputDirectory(PackagesDirectory)
                    .SetProperty("Version", Version)
                    // Both said explicitly, so that MSBuild's environment-as-properties rule cannot pack at
                    // anything else either.
                    .SetProperty(MartenVersionProperty, MartenFloor)
                    .SetProperty(NpgsqlVersionProperty, NpgsqlFloor));
            }

            AssertTheReleasePackagesAndNothingElse();

            foreach (var package in ReleasePackages)
            {
                Log.Information("Packed {Package}", package.Name);
            }

            // MartenStudio depends on exactly its own PostgresStudio and on the Marten floor. The exact range comes
            // from MartenStudioPinsPostgresStudioExactly in MartenStudio.csproj, which works through two NuGet-private
            // names; this is what turns a NuGet that renamed them into a failed build rather than a shipped `>=`.
            // The assets pack is there too without MartenStudio referencing it: under transitive pinning NuGet writes
            // a centrally pinned transitive package (project.assets.json's centralTransitiveDependencyGroups) into the
            // nuspec - the same line MartenStudio 0.4.0 carried when it referenced the pack directly. Npgsql is not,
            // because its central row applies only to projects that set PostgresStudioPinsNpgsql.
            var assetsPack = CentralPackageVersion("Microsoft.AspNetCore.App.Internal.Assets");

            AssertNuspecDependencies(PackagesDirectory / $"{MartenStudioPackageId}.{Version}.nupkg",
                new Dictionary<string, string>
                {
                    ["Marten"] = MartenFloor,
                    [PostgresStudioPackageId] = $"[{Version}]",
                    ["Microsoft.AspNetCore.App.Internal.Assets"] = assetsPack,
                });

            // PostgresStudio depends on Npgsql at its floor and on the assets pack that carries blazor.web.js (D22),
            // and on no part of the Marten family: a host without Marten must not get it through this package.
            AssertNuspecDependencies(PackagesDirectory / $"{PostgresStudioPackageId}.{Version}.nupkg",
                new Dictionary<string, string>
                {
                    ["Npgsql"] = NpgsqlFloor,
                    ["Microsoft.AspNetCore.App.Internal.Assets"] = assetsPack,
                });

            ReportSummary(_ => _
                .AddPair("Version", Version)
                .AddPair("Marten floor", MartenFloor)
                .AddPair("Npgsql floor", NpgsqlFloor)
                .AddPair("Packages", ReleasePackages.Length.ToString()));
        });

    Target Publish => _ => _
        .Description("Pushes artifacts/packages to nuget.org using a trusted-publishing (OIDC) key")
        .DependsOn(Pack)
        .OnlyWhenDynamic(() => ShouldPublish())
        .Executes(() =>
        {
            AssertPublishContext();

            // Both packages or neither: a MartenStudio on nuget.org whose exact PostgresStudio is not there cannot
            // be restored by anybody. Checked before the key is minted, so a wrong set wastes nothing that expires.
            AssertTheReleasePackagesAndNothingElse();
            var packages = ReleasePackages;

            // Minted here rather than in an earlier target or job: the key lives 15-60 minutes and
            // one OIDC token mints exactly one key - which pushes both packages, in ReleasePackages' order.
            var apiKey = GetTrustedPublishingApiKey();

            // One push per package, in order, and the first failure ends the run: a single DotNetNuGetPush throws
            // on a non-zero exit, so a PostgresStudio push that fails never reaches MartenStudio's - which would
            // put on nuget.org a MartenStudio nobody can restore, because it depends on exactly that PostgresStudio.
            // Deliberately not the combinatorial overload: it runs its items through PLINQ, which promises no order
            // even at a degree of parallelism of 1, and with completeOnFailure it pushes the rest after a failure.
            // --skip-duplicate keeps a re-run after a partial failure safe: the package that did land is skipped,
            // the one that did not is pushed. ReleasePushOrderTests pins all of this.
            var pushed = 0;

            foreach (var package in packages)
            {
                DotNetNuGetPush(_ => _
                    .SetSource(NuGetSource)
                    .SetApiKey(apiKey)
                    .EnableSkipDuplicate()
                    .SetTargetPath(package));

                pushed++;
                Log.Information("Pushed {Package} ({Pushed} of {Count})", package.Name, pushed, packages.Length);
            }

            ReportSummary(_ => _
                .AddPair("Pushed", pushed.ToString())
                .AddPair("Version", Version));
        });

    /// <summary>
    /// <c>artifacts/packages</c> holds exactly the two release packages at the CHANGELOG version - no third
    /// package, no stale version left behind, neither one missing.
    /// </summary>
    void AssertTheReleasePackagesAndNothingElse()
    {
        var found = PackagesDirectory.GlobFiles("*.nupkg").Select(x => x.Name).Order(StringComparer.Ordinal).ToList();
        var expected = ReleasePackages.Select(x => x.Name).Order(StringComparer.Ordinal).ToList();

        Assert.True(found.SequenceEqual(expected),
            $"'{PackagesDirectory}' must hold exactly {string.Join(" and ", expected)}; it holds " +
            $"[{string.Join(", ", found)}]. Run Pack, which packs both at the CHANGELOG version {Version}.");
    }

    /// <summary>
    /// The dependencies a packed nuspec declares are exactly <paramref name="expected" /> - each id at exactly that
    /// version string as NuGet wrote it (<c>9.39.0</c> means <c>&gt;= 9.39.0</c>, <c>[0.5.0]</c> means exactly 0.5.0),
    /// in every dependency group, and no other id.
    /// </summary>
    static void AssertNuspecDependencies(AbsolutePath package, IReadOnlyDictionary<string, string> expected)
    {
        Assert.True(package.FileExists(), $"'{package}' does not exist");

        using var archive = ZipFile.OpenRead(package);
        var entry = archive.Entries.SingleOrDefault(x => !x.FullName.Contains('/') && x.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            .NotNull($"{package.Name} has no .nuspec at its root");

        XDocument nuspec;
        using (var stream = entry.Open())
        {
            nuspec = XDocument.Load(stream);
        }

        var groups = nuspec.Descendants().Where(x => x.Name.LocalName == "group" && x.Parent?.Name.LocalName == "dependencies").ToList();
        Assert.NotEmpty(groups, $"{package.Name}'s nuspec declares no dependency group");

        foreach (var group in groups)
        {
            var declared = group.Elements().Where(x => x.Name.LocalName == "dependency")
                .Select(x => $"{(string) x.Attribute("id")} {(string) x.Attribute("version")}")
                .Order(StringComparer.Ordinal)
                .ToList();
            var wanted = expected.Select(x => $"{x.Key} {x.Value}").Order(StringComparer.Ordinal).ToList();
            var framework = (string) group.Attribute("targetFramework") ?? "(any)";

            Assert.True(declared.SequenceEqual(wanted),
                $"{package.Name} ({framework}) must depend on exactly [{string.Join(", ", wanted)}], and its nuspec says " +
                $"[{string.Join(", ", declared)}]");

            foreach (var line in declared)
            {
                Log.Information("{Package} ({Framework}) depends on {Dependency}", package.Name, framework, line);
            }
        }
    }

    /// <summary>A package's version from its <c>PackageVersion</c> row in <c>Directory.Packages.props</c>.</summary>
    string CentralPackageVersion(string id)
    {
        var rows = XDocument.Load(PackagesPropsFile).Descendants()
            .Where(x => x.Name.LocalName == "PackageVersion" && (string) x.Attribute("Include") == id)
            .Select(x => (string) x.Attribute("Version"))
            .ToList();

        Assert.True(rows.Count == 1, $"Directory.Packages.props must have exactly one PackageVersion row for {id}; found {rows.Count}");

        return rows[0];
    }

    /// <summary>
    /// The tag gate. There is exactly one thing a <c>Publish</c> run can legitimately be - a v* tag
    /// build pushing to nuget.org - so anything else is skipped rather than failed, which keeps
    /// <c>Compile Test Pack Publish</c> a safe line to run anywhere. Skipping is never silent.
    /// </summary>
    bool ShouldPublish()
    {
        if (IsTaggedBuild)
        {
            return true;
        }

        Log.Warning(
            "Skipping Publish: this build is not a v* tag build, and nothing is pushed to nuget.org from " +
            "anything else (there is no preview feed). Run `Pack` to build and inspect the package locally; " +
            "push tag v{Version} - the version CHANGELOG.md declares - to publish it.", Version);

        return false;
    }

    /// <summary>
    /// Refuses to run anywhere the OIDC exchange cannot possibly succeed, with the reason spelled
    /// out. Same tag-versus-CHANGELOG rule as <c>CreateGitHubRelease</c>: CHANGELOG.md is the
    /// version authority, so the tag has to agree with it or the package version and the release
    /// name would tell different stories.
    /// </summary>
    void AssertPublishContext()
    {
        Assert.True(GitHubActions.Instance != null,
            "Publish only runs on GitHub Actions: the nuget.org API key is minted from the workflow's " +
            "OIDC token, which exists nowhere else. Run `.\\build.ps1 Pack` locally to inspect the package, " +
            "and push a v* tag to publish.");

        Assert.NotNullOrWhiteSpace(NuGetUser,
            "The nuget.org profile name of the trusted publishing policy's creator is required. Set it as " +
            "NUGET_USER (env:) on the publish job, or pass --nuget-user. It is public information, " +
            "not a secret.");

        var expected = $"v{Version}";
        var actual = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");

        Assert.True(actual == expected,
            $"Refusing to publish to nuget.org: the workflow ran for ref '{actual}' but CHANGELOG.md says the " +
            $"version is {Version} (tag '{expected}'). Tag the commit that carries the matching CHANGELOG entry.");
    }

    /// <summary>
    /// Exchanges the job's GitHub OIDC token for a short-lived nuget.org API key - what
    /// <c>NuGet/login@v1</c> does, without taking a dependency on a marketplace action.
    /// </summary>
    string GetTrustedPublishingApiKey()
    {
        const string MissingOidc =
            "GitHub OIDC is unavailable - the publish job needs 'permissions: id-token: write'";

        var requestUrl = Assert.NotNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_URL"), MissingOidc);
        var requestToken = Assert.NotNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("ACTIONS_ID_TOKEN_REQUEST_TOKEN"), MissingOidc);

        using var client = new HttpClient();

        // REQUIRED. nuget.org's token endpoint sits behind Azure Front Door, which answers
        // HTTP 400 "A User-Agent header is required." to a request that carries none - and a bare
        // HttpClient sends none. The check runs before the token or the policy is looked at, so the
        // symptom of forgetting this is a 400 rather than the 401 a misconfiguration would give.
        // The JS http client behind NuGet/login always sends one, which is why only in-build .NET
        // callers ever hit it.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(NuGetUserAgent);

        var idToken = client
            .CreateRequest(HttpMethod.Get, $"{requestUrl}&audience={Uri.EscapeDataString(NuGetAudience)}")
            .WithBearerAuthentication(requestToken)
            .GetResponse()
            .AssertSuccessfulStatusCode()
            .GetBodyAsJsonObject().GetAwaiter().GetResult()["value"].GetValue<string>();

        var body = client
            .CreateRequest(HttpMethod.Post, NuGetTokenServiceUrl)
            .WithBearerAuthentication(idToken)
            .WithJsonContent(new { username = NuGetUser, tokenType = "ApiKey" })
            .GetResponse()
            // Surface the response body: nuget.org answers { "error": "..." } and it usually names
            // the cause outright.
            .AssertResponse(x => x.IsSuccessStatusCode
                ? null
                : $"nuget.org token exchange failed ({(int) x.StatusCode}): " +
                  $"{x.Content.ReadAsStringAsync().GetAwaiter().GetResult()}. A 400 means the request shape is " +
                  "wrong (checked before any policy lookup); a 401 means no trusted publishing policy matched " +
                  $"repository lahma/marten-studio, workflow file {PublishWorkflowFile}, environment nuget and " +
                  $"creator '{NuGetUser}'.")
            .GetBodyAsJsonObject().GetAwaiter().GetResult();

        // The action reads 'apiKey'; the original design document says 'api_key'. Accept either.
        var apiKey = (body["apiKey"] ?? body["api_key"])
            .NotNull($"nuget.org returned no API key: {body.ToJsonString()}")
            .GetValue<string>();

        GitHubActions.Instance?.WriteCommand("add-mask", apiKey);
        Log.Information("Minted a short-lived nuget.org API key, expires {Expires}",
            body["expires"]?.GetValue<string>() ?? "(not reported)");

        return apiKey;
    }
}
