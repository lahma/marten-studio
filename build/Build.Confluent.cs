using System;
using System.Xml.Linq;

using Fallout.Common;

using Serilog;

/// <summary>
/// Which Confluent.Kafka the KStudio projects are restored, compiled and tested against - the floor KStudio.Core
/// depends on, or the newest release CI also tests (docs/maintainers/package-budget-changes.md, 2026-11-09).
/// </summary>
/// <remarks>
/// <para>
/// Both versions live in <c>Directory.Packages.props</c> and nowhere else: <c>KStudioConfluentFloor</c> and
/// <c>KStudioConfluentLatest</c>. This file only reads them (with <see cref="ReadPackagesProperty"/> and
/// <see cref="ExactVersion"/>, Build.Marten.cs's), so bumping either one is an edit to that file and to nothing in
/// <c>build/</c> or <c>.github/</c>. The <c>marten-latest</c> workflow sets <c>CONFLUENT_VERSION: latest</c>, which
/// Fallout binds to <see cref="ConfluentVersion"/>; every other workflow, and every local run that does not ask, gets
/// the floor.
/// </para>
/// <para>
/// Unlike Npgsql's, the property may go to the whole solution and there is no second pass: the central
/// <c>Confluent.Kafka</c> row is unconditional, and only the KStudio projects reference the package, so a MartenStudio
/// or PostgresStudio project never reads it. It is passed to Restore, Compile and Test <em>always</em>, the floor
/// included, so that an environment variable of that name (MSBuild reads every environment variable as a property)
/// cannot quietly change what a Fallout run tests; Restore needs it as much as Compile does, because Compile runs
/// <c>--no-restore</c>.
/// </para>
/// <para>
/// <c>Pack</c> refuses any version but the floor (<see cref="AssertPackingAtTheConfluentFloor"/>). Nothing KStudio
/// ships is packed before plan K8, but the guard exists now so that the first KStudio package cannot raise a
/// Wolverine host's Confluent.Kafka by being packed on the latest leg.
/// </para>
/// </remarks>
partial class Build
{
    /// <summary>The MSBuild property <c>Directory.Packages.props</c> takes the Confluent.Kafka version from.</summary>
    const string ConfluentVersionProperty = "KStudioConfluentVersion";

    [Parameter("The Confluent.Kafka version the KStudio projects are restored, compiled and tested against: 'floor' " +
               "(the default - the version KStudio.Core depends on, and the only one Pack packs at), 'latest' (the " +
               "newest release CI tests, pinned in Directory.Packages.props), or an exact version such as 2.16.0. " +
               "(env: CONFLUENT_VERSION)")]
    readonly string ConfluentVersion;

    /// <summary><c>KStudioConfluentFloor</c> from <c>Directory.Packages.props</c>.</summary>
    string ConfluentFloor { get; set; }

    /// <summary><c>KStudioConfluentLatest</c> from <c>Directory.Packages.props</c>.</summary>
    string ConfluentLatest { get; set; }

    /// <summary>The exact Confluent.Kafka version this run restores, compiles and tests against.</summary>
    string ResolvedConfluentVersion { get; set; }

    /// <summary>
    /// Reads the two versions out of <c>Directory.Packages.props</c> and settles which one this run uses. Called from
    /// <c>OnBuildInitialized</c>, so a misspelt <c>--confluent-version</c> fails before anything is restored.
    /// </summary>
    void ResolveConfluentVersion()
    {
        var props = XDocument.Load(PackagesPropsFile);

        ConfluentFloor = ReadPackagesProperty(props, "KStudioConfluentFloor");
        ConfluentLatest = ReadPackagesProperty(props, "KStudioConfluentLatest");

        var requested = ConfluentVersion?.Trim();

        ResolvedConfluentVersion = requested switch
        {
            null or "" => ConfluentFloor,
            _ when requested.Equals("floor", StringComparison.OrdinalIgnoreCase) => ConfluentFloor,
            _ when requested.Equals("latest", StringComparison.OrdinalIgnoreCase) => ConfluentLatest,
            _ when ExactVersion.IsMatch(requested) => requested,
            _ => throw new ArgumentException(
                $"--confluent-version '{requested}' is neither 'floor', 'latest' nor an exact version such as " +
                $"{ConfluentLatest}. A range or a wildcard is not accepted: the point of the parameter is that a run " +
                "says exactly which Confluent.Kafka it tested."),
        };

        var which = ResolvedConfluentVersion == ConfluentFloor ? "the floor"
            : ResolvedConfluentVersion == ConfluentLatest ? "the latest tested"
            : "an explicit version";

        Log.Information("Confluent.Kafka {Version} ({Which}; floor {Floor}, latest tested {Latest})",
            ResolvedConfluentVersion, which, ConfluentFloor, ConfluentLatest);
    }

    /// <summary>
    /// KStudio is packed at the Confluent.Kafka floor or not at all: a nuspec written at a newer version would say
    /// <c>Confluent.Kafka &gt;= {that version}</c> and raise every host's Confluent.Kafka.
    /// </summary>
    void AssertPackingAtTheConfluentFloor()
    {
        Assert.True(ResolvedConfluentVersion == ConfluentFloor,
            $"Refusing to pack with --confluent-version {ResolvedConfluentVersion}: KStudio is packed only at the " +
            $"Confluent.Kafka floor ({ConfluentFloor}, KStudioConfluentFloor in Directory.Packages.props). The nuspec " +
            $"would say 'Confluent.Kafka >= {ResolvedConfluentVersion}' and raise every host's Confluent.Kafka (a " +
            "Wolverine host pins the floor). Run Pack without --confluent-version (and without CONFLUENT_VERSION in " +
            "the environment), or raise the floor deliberately and record why in " +
            "docs/maintainers/package-budget-changes.md.");
    }
}
