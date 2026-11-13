using System.Reflection;
using System.Runtime.CompilerServices;

namespace KStudio.Internal;

/// <summary>
/// Refuses to start KStudio beside any PostgresStudio, PostgresStudio.Diagnostics or KStudio.Core but its own build - including none at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> KStudio, PostgresStudio, PostgresStudio.Diagnostics and KStudio.Core are one program split in four files: the first two grant
/// KStudio their internals, and KStudio.Core grants it its own, so one skewed assembly is a <see cref="MissingMethodException" /> on the first
/// internal signature that moved. The nuspec pins the others exactly, and NuGet enforces that pin everywhere but one place: with
/// <c>CentralPackageTransitivePinningEnabled</c> and a central <c>PackageVersion</c> for one of them at another version, the SDK's resolver
/// drops it from the graph without a diagnostic - a green build and a <see cref="FileNotFoundException" /> at startup (measured by PS-0,
/// 2026-10-20, for PostgresStudio; MartenStudio's <c>PostgresStudioVersionGuard</c> is this guard's model).
/// </para>
/// <para>
/// <b>How it can run at all when one is missing.</b> This type names no PostgresStudio or KStudio.Core type, and neither does the public
/// <c>AddKafkaStudio</c> that calls it first: everything that does is behind a <see cref="MethodImplOptions.NoInlining" /> method, so the JIT
/// resolves none of it until this guard has passed. Each assembly is loaded by name, from a <see cref="MethodImplOptions.NoInlining" /> method of
/// its own. <c>KStudioVersionGuardTests</c> scans the IL of both to keep it that way.
/// </para>
/// <para>
/// <b>The caveat.</b> <c>KafkaStudioOptions</c> derives from a KStudio.Core type, so the runtime loads KStudio.Core to compile
/// <c>AddKafkaStudio</c> itself: a <em>missing or older</em> KStudio.Core fails in the loader with a <see cref="FileNotFoundException" /> that
/// names it, before this guard runs. The guard explains a newer one and another build of the same version. A missing or mismatched PostgresStudio
/// is always the guard's message.
/// </para>
/// <para>
/// <b>What it compares.</b> The informational versions. The packages are packed from one commit at one CHANGELOG version, in Release, and
/// SourceLink appends that commit after a <c>+</c>: when both assemblies are Release builds and both carry one, the whole strings must match.
/// Otherwise - a Debug build, where an IDE's incremental build after a commit legitimately leaves the assemblies on different commits - only the
/// part before the <c>+</c> is compared.
/// </para>
/// <para>
/// <b>The preview label (K8).</b> KStudio, KStudio.Core and KStudio.Wolverine pack at <c>&lt;version&gt;-&lt;label&gt;</c> (<c>1.3.0-preview.1</c>), the
/// family beside them at <c>&lt;version&gt;</c>. So KStudio.Core is compared with KStudio's own informational version, label and all, and
/// PostgresStudio and PostgresStudio.Diagnostics with the <em>family version</em>: KStudio's with the label taken off, the label being the
/// <c>KStudioPrereleaseLabel</c> assembly metadata the three projects stamp. Without that metadata (a build that emptied the label) nothing is
/// taken off. A different label, or a family assembly of another version, still fails.
/// </para>
/// </remarks>
internal static class KStudioVersionGuard
{
    /// <summary>The shared studio's assembly name: a string, never a type, for the reason in the remarks.</summary>
    internal const string PostgresStudioAssemblyName = "PostgresStudio";

    /// <summary>The diagnostics engine's assembly name, checked the same way.</summary>
    internal const string DiagnosticsAssemblyName = "PostgresStudio.Diagnostics";

    /// <summary>KStudio's engine assembly name, checked the same way.</summary>
    internal const string EngineAssemblyName = "KStudio.Core";

    /// <summary>The key of the assembly metadata that names KStudio's preview label (Directory.Build.props' <c>KStudioPrereleaseLabel</c>).</summary>
    internal const string PrereleaseLabelMetadataKey = "KStudioPrereleaseLabel";

    /// <summary>
    /// Throws unless the PostgresStudio, PostgresStudio.Diagnostics and KStudio.Core this process loads are the builds this KStudio was packed
    /// with.
    /// </summary>
    /// <exception cref="InvalidOperationException">Any is missing, or is another version.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure() =>
        Ensure(typeof(KStudioVersionGuard).Assembly, LoadPostgresStudio, LoadDiagnostics, LoadEngine);

    /// <summary>
    /// The whole check, with the loaders supplied - the seam the tests use: PostgresStudio first, then the PostgresStudio.Diagnostics it is
    /// built on, then KStudio.Core.
    /// </summary>
    /// <param name="kStudio">KStudio's own assembly, whose version is the one required.</param>
    /// <param name="loadPostgresStudio">Loads the shared studio's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <param name="loadDiagnostics">Loads the diagnostics engine's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <param name="loadEngine">Loads KStudio.Core; may throw <see cref="FileNotFoundException" />.</param>
    /// <exception cref="InvalidOperationException">Any is missing, or is another version.</exception>
    internal static void Ensure(
        Assembly kStudio,
        Func<Assembly> loadPostgresStudio,
        Func<Assembly> loadDiagnostics,
        Func<Assembly> loadEngine)
    {
        EnsureAssembly(PostgresStudioAssemblyName, "KStudio", kStudio, loadPostgresStudio, family: true);
        EnsureAssembly(DiagnosticsAssemblyName, "PostgresStudio", kStudio, loadDiagnostics, family: true);
        EnsureAssembly(EngineAssemblyName, "KStudio", kStudio, loadEngine, family: false);
    }

    /// <summary>One required assembly: loaded, and the same build as KStudio's own.</summary>
    /// <param name="name">The required package.</param>
    /// <param name="bringer">The package whose reference brings it, which a host that removes its pin lets do so.</param>
    /// <param name="kStudio">KStudio's own assembly, whose version is the one required.</param>
    /// <param name="load">Loads the required assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <param name="family">Whether the required assembly is the family's, at the version without KStudio's preview label.</param>
    private static void EnsureAssembly(string name, string bringer, Assembly kStudio, Func<Assembly> load, bool family)
    {
        string? self = InformationalVersion(kStudio);
        string? kStudioVersion = family ? FamilyVersion(self, PrereleaseLabel(kStudio)) : self;
        string required = ReleaseVersion(kStudioVersion);
        Assembly loaded;

        try
        {
            loaded = load();
        }
        catch (FileNotFoundException e)
        {
            throw new InvalidOperationException(
                $"KStudio {ReleaseVersion(self)} needs {name} {required} exactly, and this application was deployed " +
                $"without {name}. The usual cause is a central PackageVersion for {name} at another " +
                $"version under CentralPackageTransitivePinningEnabled: NuGet then drops {name} from the graph " +
                $"without a warning. The packages are released together: pin {name} to {required}, or remove " +
                $"its PackageVersion row and let {bringer} bring it.",
                e);
        }

        string? loadedVersion = InformationalVersion(loaded);
        bool bothRelease = IsReleaseBuild(kStudio) && IsReleaseBuild(loaded);

        if (SameBuild(kStudioVersion, loadedVersion, bothRelease))
        {
            return;
        }

        // The commits were compared: name them, since the release version may well be the same on both sides.
        bool commits = bothRelease && HasBuildMetadata(kStudioVersion) && HasBuildMetadata(loadedVersion);
        string shownRequired = commits ? kStudioVersion! : required;
        string shownSelf = commits ? self! : ReleaseVersion(self);
        string shownFound = commits ? loadedVersion! : ReleaseVersion(loadedVersion);

        throw new InvalidOperationException(
            $"KStudio {shownSelf} needs {name} {shownRequired} exactly, and found {name} " +
            $"{shownFound}. The packages are released together and must be upgraded together: a central " +
            $"PackageVersion or a PackageReference for {name} must say {required}, or be removed and let " +
            $"{bringer} bring it.");
    }

    /// <summary>
    /// Whether two informational versions name the same build. When both assemblies are Release builds and both carry build metadata - the
    /// <c>+&lt;commit&gt;</c> SourceLink appends - the whole strings must match; otherwise the release versions (the part before the <c>+</c>) must.
    /// </summary>
    /// <param name="kStudio">KStudio's informational version.</param>
    /// <param name="other">The other assembly's informational version.</param>
    /// <param name="bothRelease">Whether both assemblies say <c>[AssemblyConfiguration("Release")]</c>, as every pack does.</param>
    internal static bool SameBuild(string? kStudio, string? other, bool bothRelease) =>
        bothRelease && HasBuildMetadata(kStudio) && HasBuildMetadata(other)
            ? string.Equals(kStudio, other, StringComparison.Ordinal)
            : string.Equals(ReleaseVersion(kStudio), ReleaseVersion(other), StringComparison.Ordinal);

    /// <summary>Whether an assembly was built in the Release configuration, as every pack of the packages is.</summary>
    /// <param name="assembly">The assembly.</param>
    internal static bool IsReleaseBuild(Assembly assembly) =>
        string.Equals(
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            "Release",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An informational version without KStudio's preview label: <c>1.3.0-preview.1+5f1c2a9d</c> is <c>1.3.0+5f1c2a9d</c> for the label
    /// <c>preview.1</c>, which is the version the family packages beside KStudio carry. Unchanged when there is no label, or when the version
    /// does not end its release part with exactly <c>-&lt;label&gt;</c> (so another label is left to fail the comparison).
    /// </summary>
    /// <param name="informationalVersion">KStudio's informational version as the assembly carries it.</param>
    /// <param name="label">The preview label, or <see langword="null" /> or empty for none.</param>
    internal static string? FamilyVersion(string? informationalVersion, string? label)
    {
        if (string.IsNullOrEmpty(informationalVersion) || string.IsNullOrEmpty(label))
        {
            return informationalVersion;
        }

        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        string release = plus < 0 ? informationalVersion : informationalVersion[..plus];
        string suffix = "-" + label;

        return release.EndsWith(suffix, StringComparison.Ordinal)
            ? release[..^suffix.Length] + (plus < 0 ? string.Empty : informationalVersion[plus..])
            : informationalVersion;
    }

    /// <summary>The preview label an assembly was stamped with (<see cref="PrereleaseLabelMetadataKey" />), or <see langword="null" /> for none.</summary>
    /// <param name="assembly">The assembly.</param>
    internal static string? PrereleaseLabel(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static x => x.Key == PrereleaseLabelMetadataKey)?.Value;

    /// <summary>An assembly's informational version up to the <c>+</c>, or its assembly version without one.</summary>
    /// <param name="assembly">The assembly.</param>
    internal static string ReleaseVersion(Assembly assembly) => ReleaseVersion(InformationalVersion(assembly));

    /// <summary>An assembly's informational version as it carries it, or its assembly version when it has none.</summary>
    /// <param name="assembly">The assembly.</param>
    internal static string? InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString();

    private static bool HasBuildMetadata(string? informationalVersion) =>
        informationalVersion?.Contains('+', StringComparison.Ordinal) == true;

    /// <summary>The part of an informational version before the <c>+</c> build metadata, or <c>?</c> for none.</summary>
    /// <param name="informationalVersion">The version as the assembly carries it.</param>
    internal static string ReleaseVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "?";
        }

        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);

        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    /// <summary>Loads the shared studio by name - the one place its name meets the loader.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadPostgresStudio() => Assembly.Load(new AssemblyName(PostgresStudioAssemblyName));

    /// <summary>Loads the diagnostics engine by name, for the same reason and from a method of its own.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadDiagnostics() => Assembly.Load(new AssemblyName(DiagnosticsAssemblyName));

    /// <summary>Loads KStudio's engine by name, for the same reason and from a method of its own.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadEngine() => Assembly.Load(new AssemblyName(EngineAssemblyName));
}
