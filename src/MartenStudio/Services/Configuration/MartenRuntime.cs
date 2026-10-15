using System.Reflection;

using Marten;

using Npgsql;

namespace MartenStudio.Services.Configuration;

/// <summary>The packages whose resolved versions the studio reports, and that a light-up probe can name.</summary>
internal enum RuntimePackage
{
    /// <summary><c>Marten</c>.</summary>
    Marten,

    /// <summary><c>JasperFx</c>, which Marten brings.</summary>
    JasperFx,

    /// <summary><c>JasperFx.Events</c>, which Marten brings - the async daemon lives here.</summary>
    JasperFxEvents,

    /// <summary><c>Weasel.Core</c>, which Marten brings - the migration model.</summary>
    WeaselCore,

    /// <summary><c>Weasel.Postgresql</c>, which Marten brings - the Postgres schema objects.</summary>
    WeaselPostgresql,

    /// <summary><c>Npgsql</c>, which Weasel brings.</summary>
    Npgsql,
}

/// <summary>One package as this process resolved it.</summary>
/// <param name="Package">Which package.</param>
/// <param name="Name">The package's own name, as NuGet spells it.</param>
/// <param name="Version">
/// The version it was built as, read from the assembly's informational version and cut at its first
/// <c>+</c> or <c>-</c>; <see langword="null" /> when the assembly carries none the studio can read.
/// </param>
/// <param name="InformationalVersion">The informational version exactly as the assembly carries it.</param>
internal sealed record RuntimeComponent(RuntimePackage Package, string Name, Version? Version, string? InformationalVersion)
{
    /// <summary>The version for a person: three parts, or "unknown".</summary>
    public string DisplayVersion => Version is null ? "unknown" : MartenRuntime.Format(Version);
}

/// <summary>
/// The Marten this host runs, and the packages it brought: the F1 half of the plan's "floor plus run-time
/// light-up".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the informational version, and never <see cref="AssemblyName.Version" />.</b> A strong-named
/// library is free to keep its assembly version fixed across releases so that binding redirects are never
/// needed, and a floor check against a fixed number would say every host runs the floor. The informational
/// version is what the package's build stamped - <c>9.31.0+&lt;commit&gt;</c> - and the part before the
/// <c>+</c> is the NuGet version. When an assembly carries none, the version is unknown rather than guessed.
/// </para>
/// <para>
/// <b>Read once.</b> The assemblies of a running process do not change, so <see cref="Current" /> is computed
/// on first use and kept. A test that needs an older or newer host builds one with
/// <see cref="ForVersions" />; such a runtime answers feature questions from its versions alone
/// (<see cref="RuntimeFeature.IsAvailable" />), because the probes can only ever see the assemblies this
/// process actually loaded.
/// </para>
/// </remarks>
internal sealed class MartenRuntime
{
    private static readonly Lazy<MartenRuntime> Detected = new(Detect, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Dictionary<RuntimePackage, RuntimeComponent> components;

    private MartenRuntime(IEnumerable<RuntimeComponent> components, bool isProcessRuntime)
    {
        this.components = components.ToDictionary(static x => x.Package);
        IsProcessRuntime = isProcessRuntime;
    }

    /// <summary>The runtime of this process: the assemblies it loaded, and the probes over them.</summary>
    public static MartenRuntime Current => Detected.Value;

    /// <summary>
    /// Whether this describes the assemblies the process loaded, so a probe's answer is about it - or a
    /// runtime a test made up, which only versions can answer for.
    /// </summary>
    public bool IsProcessRuntime { get; }

    /// <summary>Every package, in <see cref="RuntimePackage" /> order.</summary>
    public IReadOnlyList<RuntimeComponent> Components =>
        [.. components.Values.OrderBy(static x => x.Package)];

    /// <summary>The resolved version of <paramref name="package" />, or <see langword="null" /> when unknown.</summary>
    public Version? VersionOf(RuntimePackage package) =>
        components.TryGetValue(package, out RuntimeComponent? component) ? component.Version : null;

    /// <summary>"Marten 9.31.0", "JasperFx.Events 2.60.0" - or "Marten (version unknown)".</summary>
    public string Describe(RuntimePackage package) =>
        VersionOf(package) is { } version
            ? PackageName(package) + " " + Format(version)
            : PackageName(package) + " (version unknown)";

    /// <summary>
    /// What this host runs, as a person thinks of it - its Marten - with <paramref name="package" />'s own
    /// version in parentheses when the feature in question is that package's: "Marten 9.31.0 (JasperFx.Events
    /// 2.60.0)". A remedy says "Marten 9.50+", so the answer leads with the Marten it is compared to.
    /// </summary>
    public string DescribeHost(RuntimePackage package) =>
        package == RuntimePackage.Marten
            ? Describe(RuntimePackage.Marten)
            : Describe(RuntimePackage.Marten) + " (" + Describe(package) + ")";

    /// <summary>A runtime with exactly these versions, for a test about an older or newer host.</summary>
    /// <param name="versions">The versions, by package; a package not named is unknown.</param>
    public static MartenRuntime ForVersions(IReadOnlyDictionary<RuntimePackage, string> versions)
    {
        ArgumentNullException.ThrowIfNull(versions);

        return new MartenRuntime(
            Enum.GetValues<RuntimePackage>().Select(package => versions.TryGetValue(package, out string? text)
                ? new RuntimeComponent(package, PackageName(package), ParseVersion(text), text)
                : new RuntimeComponent(package, PackageName(package), null, null)),
            isProcessRuntime: false);
    }

    /// <summary>The package name as NuGet spells it.</summary>
    public static string PackageName(RuntimePackage package) => package switch
    {
        RuntimePackage.Marten => "Marten",
        RuntimePackage.JasperFx => "JasperFx",
        RuntimePackage.JasperFxEvents => "JasperFx.Events",
        RuntimePackage.WeaselCore => "Weasel.Core",
        RuntimePackage.WeaselPostgresql => "Weasel.Postgresql",
        RuntimePackage.Npgsql => "Npgsql",
        _ => throw new ArgumentOutOfRangeException(nameof(package), package, "No package name is mapped to this package."),
    };

    /// <summary>
    /// A type that lives in <paramref name="package" />'s assembly - what the version is read off.
    /// </summary>
    /// <remarks>
    /// <c>MartenRuntimeProbeTests</c> asserts every one of these lives in an assembly of the package's own
    /// name, so a type that moves to another assembly in a later release fails a test rather than quietly
    /// reporting the wrong package's version.
    /// </remarks>
    internal static Type AnchorType(RuntimePackage package) => package switch
    {
        RuntimePackage.Marten => typeof(DocumentStore),
        RuntimePackage.JasperFx => typeof(JasperFx.AutoCreate),
        RuntimePackage.JasperFxEvents => typeof(JasperFx.Events.IEvent),
        RuntimePackage.WeaselCore => typeof(Weasel.Core.SchemaMigrationException),
        RuntimePackage.WeaselPostgresql => typeof(Weasel.Postgresql.Tables.Table),
        RuntimePackage.Npgsql => typeof(NpgsqlConnection),
        _ => throw new ArgumentOutOfRangeException(nameof(package), package, "No anchor type is mapped to this package."),
    };

    /// <summary>
    /// The NuGet version an informational version names: the part before any <c>+</c> (build metadata) or
    /// <c>-</c> (a prerelease label), as a <see cref="Version" />; <see langword="null" /> when that is not one.
    /// </summary>
    internal static Version? ParseVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        string text = informational.Trim();
        int cut = text.IndexOfAny(['+', '-']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        return Version.TryParse(text, out Version? version) ? version : null;
    }

    /// <summary>A version in the three parts NuGet prints.</summary>
    internal static string Format(Version version) =>
        version.Build >= 0
            ? version.ToString(3)
            : version.ToString(2);

    private static MartenRuntime Detect() =>
        new(Enum.GetValues<RuntimePackage>().Select(Read), isProcessRuntime: true);

    private static RuntimeComponent Read(RuntimePackage package)
    {
        string? informational = null;

        try
        {
            informational = AnchorType(package).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A version is a fact for a person to read; one that cannot be read is "unknown", never a fault
            // on the Configuration page or in an advisor that only wanted to word a remedy.
        }

        return new RuntimeComponent(package, PackageName(package), ParseVersion(informational), informational);
    }
}
