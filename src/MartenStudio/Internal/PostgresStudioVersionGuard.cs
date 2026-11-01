using System.Reflection;
using System.Runtime.CompilerServices;

namespace MartenStudio.Internal;

/// <summary>
/// Refuses to start Marten Studio beside any PostgresStudio, or any PostgresStudio.Diagnostics, but its own build - including none at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> MartenStudio and PostgresStudio are one program split in two files: PostgresStudio grants
/// MartenStudio its internals, so MartenStudio 0.5.0 beside PostgresStudio 0.5.1 is a
/// <see cref="MissingMethodException" /> on the first internal signature that moved. The nuspec pins
/// <c>PostgresStudio [&lt;version&gt;]</c> exactly (<c>MartenStudio.csproj</c>), and NuGet enforces that pin
/// everywhere but one place: with <c>CentralPackageTransitivePinningEnabled</c> and a central
/// <c>PackageVersion</c> for PostgresStudio at another version, SDK 10.0.402's resolver drops PostgresStudio
/// from the graph without a diagnostic - a green build and a <see cref="FileNotFoundException" /> at startup
/// (measured by PS-0, 2026-10-20; closest upstream report NuGet/Home#14698). A host that silences NU1608 and
/// references another PostgresStudio directly gets the other half: the wrong assembly, loaded.
/// </para>
/// <para>
/// <b>How it can run at all when PostgresStudio is missing.</b> This type names no PostgresStudio type, and
/// neither does the public <c>AddMartenStudio</c> that calls it first: everything that does is behind a
/// <see cref="MethodImplOptions.NoInlining" /> method, so the JIT resolves none of it until this guard has
/// passed. The assembly is loaded by name, from a <see cref="MethodImplOptions.NoInlining" /> method of its own.
/// <c>PostgresStudioVersionGuardTests</c> scans the IL of both to keep it that way, because in every test
/// host PostgresStudio is present and a reference that crept in would break nothing there.
/// </para>
/// <para>
/// <b>What it compares.</b> The informational versions. The two packages are packed from one commit at one CHANGELOG
/// version, in Release, and SourceLink appends that commit after a <c>+</c>: when both assemblies are Release builds and
/// both carry one, the whole strings must match, so the same version packed from two commits is refused too. Otherwise -
/// a Debug build, where an IDE's incremental build after a commit legitimately leaves the two on different commits, or a
/// build from a tree with no git - only the part before the <c>+</c> is compared.
/// </para>
/// </remarks>
internal static class PostgresStudioVersionGuard
{
    /// <summary>The shared studio's assembly name: a string, never a type, for the reason in the remarks.</summary>
    internal const string PostgresStudioAssemblyName = "PostgresStudio";

    /// <summary>The engine's assembly name, checked the same way: a string, never a type.</summary>
    internal const string DiagnosticsAssemblyName = "PostgresStudio.Diagnostics";

    /// <summary>
    /// Throws unless the PostgresStudio and the PostgresStudio.Diagnostics this process loads are the builds this
    /// MartenStudio was packed with.
    /// </summary>
    /// <exception cref="InvalidOperationException">Either is missing, or is another version.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure() =>
        Ensure(typeof(PostgresStudioVersionGuard).Assembly, LoadPostgresStudio, LoadDiagnostics);

    /// <summary>The check for PostgresStudio alone, with the two assemblies supplied - a seam the tests use.</summary>
    /// <param name="martenStudio">MartenStudio's own assembly, whose version is the one required.</param>
    /// <param name="loadPostgresStudio">Loads the shared studio's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <exception cref="InvalidOperationException">PostgresStudio is missing, or is another version.</exception>
    internal static void Ensure(Assembly martenStudio, Func<Assembly> loadPostgresStudio) =>
        EnsureAssembly(PostgresStudioAssemblyName, "MartenStudio", martenStudio, loadPostgresStudio);

    /// <summary>
    /// The whole check, with the three assemblies supplied - the seam the tests use: PostgresStudio first, then the
    /// PostgresStudio.Diagnostics it is built on, which MartenStudio reaches through PostgresStudio and names directly
    /// (the health export), and which a deployment can lack or skew in the same way.
    /// </summary>
    /// <param name="martenStudio">MartenStudio's own assembly, whose version is the one required.</param>
    /// <param name="loadPostgresStudio">Loads the shared studio's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <param name="loadDiagnostics">Loads the engine's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <exception cref="InvalidOperationException">Either is missing, or is another version.</exception>
    internal static void Ensure(Assembly martenStudio, Func<Assembly> loadPostgresStudio, Func<Assembly> loadDiagnostics)
    {
        EnsureAssembly(PostgresStudioAssemblyName, "MartenStudio", martenStudio, loadPostgresStudio);
        EnsureAssembly(DiagnosticsAssemblyName, "PostgresStudio", martenStudio, loadDiagnostics);
    }

    /// <summary>One required assembly: loaded, and the same build as MartenStudio's own.</summary>
    /// <param name="name">The required package, <c>PostgresStudio</c> or <c>PostgresStudio.Diagnostics</c>.</param>
    /// <param name="bringer">The package whose reference brings it, which a host that removes its pin lets do so.</param>
    /// <param name="martenStudio">MartenStudio's own assembly, whose version is the one required.</param>
    /// <param name="load">Loads the required assembly; may throw <see cref="FileNotFoundException" />.</param>
    private static void EnsureAssembly(string name, string bringer, Assembly martenStudio, Func<Assembly> load)
    {
        string? martenStudioVersion = InformationalVersion(martenStudio);
        string required = ReleaseVersion(martenStudioVersion);
        Assembly loaded;

        try
        {
            loaded = load();
        }
        catch (FileNotFoundException e)
        {
            throw new InvalidOperationException(
                $"MartenStudio {required} needs {name} {required} exactly, and this application was deployed " +
                $"without {name}. The usual cause is a central PackageVersion for {name} at another " +
                $"version under CentralPackageTransitivePinningEnabled: NuGet then drops {name} from the graph " +
                $"without a warning. The packages are released together: pin {name} to {required}, or remove " +
                $"its PackageVersion row and let {bringer} bring it.",
                e);
        }

        string? loadedVersion = InformationalVersion(loaded);
        bool bothRelease = IsReleaseBuild(martenStudio) && IsReleaseBuild(loaded);

        if (SameBuild(martenStudioVersion, loadedVersion, bothRelease))
        {
            return;
        }

        // The commits were compared: name them, since the release version may well be the same on both sides.
        bool commits = bothRelease && HasBuildMetadata(martenStudioVersion) && HasBuildMetadata(loadedVersion);
        string shownRequired = commits ? martenStudioVersion! : required;
        string shownFound = commits ? loadedVersion! : ReleaseVersion(loadedVersion);

        throw new InvalidOperationException(
            $"MartenStudio {shownRequired} needs {name} {shownRequired} exactly, and found {name} " +
            $"{shownFound}. The packages are released together and must be upgraded together: a central " +
            $"PackageVersion or a PackageReference for {name} must say {required}, or be removed and let " +
            $"{bringer} bring it.");
    }

    /// <summary>
    /// Whether two informational versions name the same build. When both assemblies are Release builds and both carry
    /// build metadata - the <c>+&lt;commit&gt;</c> SourceLink appends - the whole strings must match, so two packed builds of
    /// one version from different commits are told apart. Otherwise the release versions (the part before the <c>+</c>)
    /// must match: a Debug build is a development tree, where an IDE's incremental build after a commit recompiles
    /// MartenStudio with the new commit and leaves an unchanged PostgresStudio with the old one, and that is not a skew.
    /// </summary>
    /// <param name="martenStudio">MartenStudio's informational version.</param>
    /// <param name="postgresStudio">PostgresStudio's informational version.</param>
    /// <param name="bothRelease">Whether both assemblies say <c>[AssemblyConfiguration("Release")]</c>, as every pack does.</param>
    internal static bool SameBuild(string? martenStudio, string? postgresStudio, bool bothRelease) =>
        bothRelease && HasBuildMetadata(martenStudio) && HasBuildMetadata(postgresStudio)
            ? string.Equals(martenStudio, postgresStudio, StringComparison.Ordinal)
            : string.Equals(ReleaseVersion(martenStudio), ReleaseVersion(postgresStudio), StringComparison.Ordinal);

    /// <summary>Whether an assembly was built in the Release configuration, as every pack of the packages is.</summary>
    /// <param name="assembly">The assembly.</param>
    internal static bool IsReleaseBuild(Assembly assembly) =>
        string.Equals(
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            "Release",
            StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Loads the engine by name, for the same reason and from a method of its own.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadDiagnostics() => Assembly.Load(new AssemblyName(DiagnosticsAssemblyName));
}
