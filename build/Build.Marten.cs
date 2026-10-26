using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Fallout.Common;
using Fallout.Common.IO;

using Serilog;

/// <summary>
/// Which Marten the solution is restored, compiled and tested against - the floor the package ships
/// with, or the newest 9.x CI also tests (docs/maintainers/package-budget-changes.md, 2026-10-15).
/// </summary>
/// <remarks>
/// <para>
/// Both versions live in <c>Directory.Packages.props</c> and nowhere else: <c>MartenStudioMartenFloor</c>
/// and <c>MartenStudioMartenLatest</c>. This file only reads them, so bumping either one is an edit to
/// that file and to nothing in <c>build/</c> or <c>.github/</c>. The <c>marten-latest</c> workflow sets
/// <c>MARTEN_VERSION: latest</c>, which Fallout binds to <see cref="MartenVersion"/>; every other
/// workflow, and every local run that does not ask, gets the floor.
/// </para>
/// <para>
/// The resolved version is passed as <c>-p:MartenStudioMartenVersion=</c> to Restore, Compile and Test
/// <em>always</em>, the floor included, so an environment variable of that name on somebody's machine
/// (MSBuild reads every environment variable as a property) cannot quietly change what a Fallout run
/// tests. Restore needs it as much as Compile does: Compile runs <c>--no-restore</c> after Restore, so a
/// version passed only to Compile would build against the floor's assets file.
/// </para>
/// <para>
/// <c>Pack</c> refuses any version but the floor (<see cref="AssertPackingAtTheMartenFloor"/>), and
/// <c>src/MartenStudio/MartenStudio.csproj</c> refuses the same thing for a bare <c>dotnet pack</c> -
/// checking what <c>project.assets.json</c> resolved as well as the property, because that file is where
/// NuGet takes the range from: the nuspec says <c>Marten &gt;= {the version it was restored at}</c>, so a
/// package packed on the latest leg would raise every host's floor without anybody having decided to.
/// </para>
/// </remarks>
partial class Build
{
    /// <summary>The MSBuild property <c>Directory.Packages.props</c> takes the Marten version from.</summary>
    const string MartenVersionProperty = "MartenStudioMartenVersion";

    [Parameter("The Marten version to restore, compile and test against: 'floor' (the default - the version " +
               "the package depends on, and the only one Pack packs at), 'latest' (the newest 9.x CI tests, " +
               "pinned in Directory.Packages.props), or an exact version such as 9.54.1. (env: MARTEN_VERSION)")]
    readonly string MartenVersion;

    AbsolutePath PackagesPropsFile => RootDirectory / "Directory.Packages.props";

    /// <summary><c>MartenStudioMartenFloor</c> from <c>Directory.Packages.props</c>.</summary>
    string MartenFloor { get; set; }

    /// <summary><c>MartenStudioMartenLatest</c> from <c>Directory.Packages.props</c>.</summary>
    string MartenLatest { get; set; }

    /// <summary>The exact Marten version this run restores, compiles and tests against.</summary>
    string ResolvedMartenVersion { get; set; }

    /// <summary>A NuGet version without ranges or wildcards: <c>9.54.1</c>, <c>9.52.0-rc.1</c>.</summary>
    static readonly Regex ExactVersion = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the two versions out of <c>Directory.Packages.props</c> and settles which one this run uses.
    /// Called from <c>OnBuildInitialized</c>, so a misspelt <c>--marten-version</c> fails before anything
    /// is restored rather than as a confusing NU1102 three targets later.
    /// </summary>
    void ResolveMartenVersion()
    {
        var props = XDocument.Load(PackagesPropsFile);

        MartenFloor = ReadPackagesProperty(props, "MartenStudioMartenFloor");
        MartenLatest = ReadPackagesProperty(props, "MartenStudioMartenLatest");

        var requested = MartenVersion?.Trim();

        ResolvedMartenVersion = requested switch
        {
            null or "" => MartenFloor,
            _ when requested.Equals("floor", StringComparison.OrdinalIgnoreCase) => MartenFloor,
            _ when requested.Equals("latest", StringComparison.OrdinalIgnoreCase) => MartenLatest,
            _ when ExactVersion.IsMatch(requested) => requested,
            _ => throw new ArgumentException(
                $"--marten-version '{requested}' is neither 'floor', 'latest' nor an exact version such as " +
                $"{MartenLatest}. A range or a wildcard is not accepted: the point of the parameter is that a run " +
                "says exactly which Marten it tested."),
        };

        var which = ResolvedMartenVersion == MartenFloor ? "the floor"
            : ResolvedMartenVersion == MartenLatest ? "the latest tested"
            : "an explicit version";

        Log.Information("Marten {Version} ({Which}; floor {Floor}, latest tested {Latest})",
            ResolvedMartenVersion, which, MartenFloor, MartenLatest);
    }

    /// <summary>
    /// The package is packed at the floor or not at all. The csproj has the same guard for a bare
    /// <c>dotnet pack</c>; this one fails first and names the Fallout parameter.
    /// </summary>
    void AssertPackingAtTheMartenFloor()
    {
        Assert.True(ResolvedMartenVersion == MartenFloor,
            $"Refusing to pack against Marten {ResolvedMartenVersion}: MartenStudio is packed only at the Marten " +
            $"floor ({MartenFloor}, MartenStudioMartenFloor in Directory.Packages.props). The nuspec would say " +
            $"'Marten >= {ResolvedMartenVersion}' and raise every host's floor. Run Pack without --marten-version " +
            "(and without MARTEN_VERSION in the environment), or raise the floor deliberately and record why in " +
            "docs/maintainers/package-budget-changes.md.");
    }

    static string ReadPackagesProperty(XDocument props, string name)
    {
        // Directory.Packages.props declares no xmlns, but match on the local name so that adding the
        // legacy MSBuild namespace one day does not make every property "missing".
        var values = props.Descendants()
            .Where(x => x.Name.LocalName == name)
            .Select(x => x.Value.Trim())
            .ToList();

        Assert.True(values.Count == 1,
            $"Directory.Packages.props must define {name} exactly once; found {values.Count}");

        var value = values[0];

        Assert.True(ExactVersion.IsMatch(value),
            $"{name} in Directory.Packages.props is '{value}', which is not an exact version. The Marten and the " +
            "Npgsql versions are all pinned exactly, never floating: a red main caused by an upstream release would " +
            "be nobody's pull request's fault.");

        return value;
    }
}
