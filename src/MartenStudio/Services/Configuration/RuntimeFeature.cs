using System.Reflection;

namespace MartenStudio.Services.Configuration;

/// <summary>
/// One thing a Marten (or JasperFx) newer than the studio's floor can do, how to tell whether this host can do it, and
/// the first release that could: the F1 probe table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The table is empty at 1.1.0, and the mechanism stays.</b> The floors moved to the newest releases (Marten 9.57.0,
/// 2026-11-03; <c>docs/maintainers/package-budget-changes.md</c>), so every feature the table held, from Marten 9.31 to
/// 9.53, became unconditional and its probe, its fallback and the tests of the fallback were deleted. The next time the
/// latest-tested Marten is bumped past the floor, a member the studio wants from the newer one is added here as a
/// <c>public static readonly RuntimeFeature</c> field in a <c>partial class RuntimeFeature</c> file of its own, and the
/// Configuration page's "Newer than the studio's floor" table lights up again.
/// </para>
/// <para>
/// <b>The table is the static fields of this class, and nothing else.</b> The constructor is private, so the only code
/// that can make a feature is code inside <c>partial class RuntimeFeature</c> - which is what lets a health remedy say
/// "Marten 9.47+" only about a feature this table probes, with no convention test needed to hold the two together: a
/// remedy takes a <see cref="RuntimeFeature" />, and there is no other way to get one. <see cref="All" /> finds the
/// fields by reflection.
/// </para>
/// <para>
/// <b>Every probe is pinned at both ends.</b> <c>MartenRuntimeProbeTests</c> asserts, for each feature, "present iff the
/// resolved package is at or above <see cref="IntroducedIn" />" - so the same test is right at the floor, where each
/// probe must find nothing, and at the latest Marten, where a probe that silently finds nothing (a renamed member, a
/// moved type) is exactly the failure light-up would otherwise hide. Each version here was read from the release's own
/// source, not from release notes.
/// </para>
/// <para>
/// <b>Cached, and never thrown out of.</b> A probe is reflection over assemblies that do not change while the process
/// runs, so it runs once; a probe that throws answers "absent", because the only thing on the other side of a probe is a
/// feature the studio then does without.
/// </para>
/// </remarks>
internal sealed partial class RuntimeFeature
{
    private static readonly Lazy<IReadOnlyList<RuntimeFeature>> Table = new(Discover, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Lazy<bool>? probe;

    private RuntimeFeature(
        string name,
        RuntimePackage package,
        string introducedIn,
        string? firstMarten,
        string summary,
        Func<bool>? probe)
    {
        Name = name;
        Package = package;
        IntroducedIn = Version.Parse(introducedIn);
        FirstMarten = firstMarten is null ? null : Version.Parse(firstMarten);
        Summary = summary;
        this.probe = probe is null ? null : new Lazy<bool>(() => Run(probe), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The feature's name, as the table spells its field.</summary>
    public string Name { get; }

    /// <summary>The package the member first appeared in.</summary>
    public RuntimePackage Package { get; }

    /// <summary>The version of <see cref="Package" /> the member first appeared in.</summary>
    public Version IntroducedIn { get; }

    /// <summary>
    /// The first Marten release that brings <see cref="IntroducedIn" />, or <see langword="null" /> when no
    /// Marten release does yet.
    /// </summary>
    public Version? FirstMarten { get; }

    /// <summary>What it lets the studio, or a remedy, do.</summary>
    public string Summary { get; }

    /// <summary>Whether a member is probed for, rather than the version alone compared.</summary>
    public bool HasProbe => probe is not null;

    /// <summary>
    /// What a remedy prints beside a line that needs this: "Marten 9.47+", "Marten 9.50.3+", or for a feature no
    /// Marten release brings yet, the package that does.
    /// </summary>
    /// <remarks>
    /// The patch is printed whenever it is not zero (<see cref="Short" />): a feature first in 9.50.3 is absent from
    /// 9.50.0 to 9.50.2, so "9.50+" would tell a host on 9.50.2 that it has something it does not.
    /// </remarks>
    public string Requirement => FirstMarten is { } marten
        ? "Marten " + Short(marten) + "+"
        : MartenRuntime.PackageName(Package) + " " + Short(IntroducedIn) + "+ (in no Marten release yet)";

    /// <summary>
    /// A version as short as it can be without saying something false: "9.47" for 9.47.0, "9.50.3" for 9.50.3.
    /// </summary>
    internal static string Short(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return version.Build > 0 ? version.ToString(3) : version.ToString(2);
    }

    /// <summary>Every feature in the table, in package and version order.</summary>
    public static IReadOnlyList<RuntimeFeature> All => Table.Value;

    /// <summary>The feature named <paramref name="name" />, or <see langword="null" />.</summary>
    public static RuntimeFeature? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="runtime" /> has this: the probe's answer for this process's own runtime, the
    /// version comparison for a runtime a test made up (or for a feature with no member to probe).
    /// </summary>
    public bool IsAvailable(MartenRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (runtime.IsProcessRuntime && probe is not null)
        {
            return probe.Value;
        }

        return runtime.VersionOf(Package) is { } version && version >= IntroducedIn;
    }

    /// <summary>What the probe finds in this process, or <see langword="null" /> for a version-only feature.</summary>
    internal bool? Probe() => probe?.Value;

    /// <inheritdoc />
    public override string ToString() => Name + " (" + Requirement + ")";

    private static bool Run(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static IReadOnlyList<RuntimeFeature> Discover() =>
    [
        .. typeof(RuntimeFeature)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static x => x.IsInitOnly && x.FieldType == typeof(RuntimeFeature))
            .Select(static x => (RuntimeFeature) x.GetValue(null)!)
            .OrderBy(static x => x.Package)
            .ThenBy(static x => x.IntroducedIn)
            .ThenBy(static x => x.Name, StringComparer.Ordinal),
    ];
}
