using System.Reflection;
using System.Runtime.CompilerServices;

namespace DataStudio.Postgres.Internal;

/// <summary>
/// Refuses to start Postgres Studio beside any DataStudio.Postgres.Diagnostics or DataStudio (the shell) but its own build - including none at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> PostgresStudio and DataStudio.Postgres.Diagnostics are one program split in two files: the engine grants
/// PostgresStudio its internals, so PostgresStudio 0.11.0 beside DataStudio.Postgres.Diagnostics 0.11.1 is a
/// <see cref="MissingMethodException" /> on the first internal signature that moved. The nuspec pins
/// <c>DataStudio.Postgres.Diagnostics [&lt;version&gt;]</c> exactly (<c>DataStudio.Postgres.csproj</c>), and NuGet enforces that pin
/// everywhere but one place: with <c>CentralPackageTransitivePinningEnabled</c> and a central <c>PackageVersion</c> for
/// DataStudio.Postgres.Diagnostics at another version, the resolver drops it from the graph without a diagnostic - a green build
/// and a <see cref="FileNotFoundException" /> at startup (measured by PS-0 for the same pin one package up, 2026-10-20). A
/// host that silences NU1608 and references another DataStudio.Postgres.Diagnostics directly gets the other half: the wrong
/// assembly, loaded. The same guard, for the same reason, as <c>DataStudio.Marten.Internal.PostgresStudioVersionGuard</c>.
/// </para>
/// <para>
/// <b>What the split and the scan are for.</b> This type names no DataStudio.Postgres.Diagnostics or DataStudio type, and neither does the
/// public <c>AddPostgresStudio</c> that calls it first: everything that does is behind a
/// <see cref="MethodImplOptions.NoInlining" /> method, so the JIT resolves none of it until this guard has passed. The
/// assembly is loaded by name, from a <see cref="MethodImplOptions.NoInlining" /> method of its own.
/// <c>PostgresStudioDiagnosticsVersionGuardTests</c> scans the IL of both to keep it that way, because in every test host
/// the engine is present and the same build, and a reference that crept in would break nothing there: it matters for a
/// newer engine whose members moved, which would otherwise fail on a bare <see cref="MissingMethodException" /> before the
/// guard could say why.
/// </para>
/// <para>
/// <b>What it cannot cover.</b> <c>AddPostgresStudio</c>'s own parameter type, <c>Action&lt;PostgresStudioOptions&gt;</c>,
/// names a type that derives from the engine's <c>PostgresDiagnosticsOptions</c>, so the runtime loads the engine to compile
/// <c>AddPostgresStudio</c> itself, in every host, with a <c>configure</c> lambda or without, before the guard's first line.
/// A deployment with no engine, or an older one, therefore fails in the loader when <c>AddPostgresStudio</c> is first called,
/// with a <see cref="FileNotFoundException" /> that names <c>DataStudio.Postgres.Diagnostics</c> and the version it wants (measured
/// on 2026-11-01 against throwaway hosts: the engine missing, and an older 0.9.0 under a 0.10.0 PostgresStudio). This guard
/// is what answers a <em>newer</em> engine, or another commit of the same version, with the central-pin message. The shell is the
/// same case one package down: <c>PostgresStudioOptions</c> implements the shell's <c>IShellSettings</c>, so the loader needs
/// <c>DataStudio</c> too before the guard's first line, and the guard answers a newer shell or another commit of the same version.
/// <c>MartenStudio</c>'s <c>PostgresStudioVersionGuard</c> names no options type before it runs and covers all three cases
/// for a Marten host.
/// </para>
/// <para>
/// <b>What it compares.</b> The informational versions. The packages are packed from one commit at one CHANGELOG version,
/// in Release, and SourceLink appends that commit after a <c>+</c>: when both assemblies are Release builds and both carry
/// one, the whole strings must match, so the same version packed from two commits is refused too. Otherwise - a Debug
/// build, where an IDE's incremental build after a commit legitimately leaves the two on different commits, or a build from
/// a tree with no git - only the part before the <c>+</c> is compared.
/// </para>
/// </remarks>
internal static class PostgresStudioDiagnosticsVersionGuard
{
    /// <summary>The engine's assembly name: a string, never a type, for the reason in the remarks.</summary>
    internal const string DiagnosticsAssemblyName = "DataStudio.Postgres.Diagnostics";

    /// <summary>The shell's assembly name, checked the same way: a string, never a type.</summary>
    internal const string ShellAssemblyName = "DataStudio";

    /// <summary>
    /// Throws unless the DataStudio.Postgres.Diagnostics and the DataStudio this process loads are the builds this PostgresStudio was packed with.
    /// </summary>
    /// <exception cref="InvalidOperationException">Either is missing, or is another version.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure() => Ensure(typeof(PostgresStudioDiagnosticsVersionGuard).Assembly, LoadDiagnostics, LoadShell);

    /// <summary>The check for the engine alone, with the two assemblies supplied - a seam the tests use.</summary>
    /// <param name="postgresStudio">PostgresStudio's own assembly, whose version is the one required.</param>
    /// <param name="loadDiagnostics">Loads the engine's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <exception cref="InvalidOperationException">DataStudio.Postgres.Diagnostics is missing, or is another version.</exception>
    internal static void Ensure(Assembly postgresStudio, Func<Assembly> loadDiagnostics) =>
        EnsureAssembly(DiagnosticsAssemblyName, postgresStudio, loadDiagnostics);

    /// <summary>
    /// The whole check, with the three assemblies supplied - a seam the tests use: the engine first, then the shell
    /// (<c>DataStudio</c>, v2 step 3), which this assembly names in every signature and a deployment can lack or skew in the same way.
    /// </summary>
    /// <param name="postgresStudio">PostgresStudio's own assembly, whose version is the one required.</param>
    /// <param name="loadDiagnostics">Loads the engine's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <param name="loadShell">Loads the shell's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <exception cref="InvalidOperationException">Either is missing, or is another version.</exception>
    internal static void Ensure(Assembly postgresStudio, Func<Assembly> loadDiagnostics, Func<Assembly> loadShell)
    {
        EnsureAssembly(DiagnosticsAssemblyName, postgresStudio, loadDiagnostics);
        EnsureAssembly(ShellAssemblyName, postgresStudio, loadShell);
    }

    /// <summary>One required assembly: loaded, and the same build as PostgresStudio's own.</summary>
    /// <param name="name">The required package, <c>DataStudio.Postgres.Diagnostics</c> or <c>DataStudio</c>.</param>
    /// <param name="postgresStudio">PostgresStudio's own assembly, whose version is the one required.</param>
    /// <param name="load">Loads the required assembly; may throw <see cref="FileNotFoundException" />.</param>
    private static void EnsureAssembly(string name, Assembly postgresStudio, Func<Assembly> load)
    {
        string? postgresStudioVersion = InformationalVersion(postgresStudio);
        string required = ReleaseVersion(postgresStudioVersion);
        Assembly loaded;

        try
        {
            loaded = load();
        }
        catch (FileNotFoundException e)
        {
            throw new InvalidOperationException(
                $"PostgresStudio {required} needs {name} {required} exactly, and this application was " +
                $"deployed without {name}. The usual cause is a central PackageVersion for " +
                $"{name} at another version under CentralPackageTransitivePinningEnabled: NuGet then " +
                $"drops {name} from the graph without a warning. The packages are released together: pin " +
                $"{name} to {required}, or remove its PackageVersion row and let PostgresStudio bring it.",
                e);
        }

        string? loadedVersion = InformationalVersion(loaded);
        bool bothRelease = IsReleaseBuild(postgresStudio) && IsReleaseBuild(loaded);

        if (SameBuild(postgresStudioVersion, loadedVersion, bothRelease))
        {
            return;
        }

        // The commits were compared: name them, since the release version may well be the same on both sides.
        bool commits = bothRelease && HasBuildMetadata(postgresStudioVersion) && HasBuildMetadata(loadedVersion);
        string shownRequired = commits ? postgresStudioVersion! : required;
        string shownFound = commits ? loadedVersion! : ReleaseVersion(loadedVersion);

        throw new InvalidOperationException(
            $"PostgresStudio {shownRequired} needs {name} {shownRequired} exactly, and found " +
            $"{name} {shownFound}. The packages are released together and must be upgraded together: a " +
            $"central PackageVersion or a PackageReference for {name} must say {required}, or be removed " +
            "and let PostgresStudio bring it.");
    }
    /// <summary>
    /// Whether two informational versions name the same build. When both assemblies are Release builds and both carry
    /// build metadata - the <c>+&lt;commit&gt;</c> SourceLink appends - the whole strings must match, so two packed builds of
    /// one version from different commits are told apart. Otherwise the release versions (the part before the <c>+</c>)
    /// must match: a Debug build is a development tree, where an IDE's incremental build after a commit recompiles
    /// PostgresStudio with the new commit and leaves an unchanged engine with the old one, and that is not a skew.
    /// </summary>
    /// <param name="postgresStudio">PostgresStudio's informational version.</param>
    /// <param name="diagnostics">DataStudio.Postgres.Diagnostics' informational version.</param>
    /// <param name="bothRelease">Whether both assemblies say <c>[AssemblyConfiguration("Release")]</c>, as every pack does.</param>
    internal static bool SameBuild(string? postgresStudio, string? diagnostics, bool bothRelease) =>
        bothRelease && HasBuildMetadata(postgresStudio) && HasBuildMetadata(diagnostics)
            ? string.Equals(postgresStudio, diagnostics, StringComparison.Ordinal)
            : string.Equals(ReleaseVersion(postgresStudio), ReleaseVersion(diagnostics), StringComparison.Ordinal);

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

    /// <summary>Loads the engine by name - the one place its name meets the loader.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadDiagnostics() => Assembly.Load(new AssemblyName(DiagnosticsAssemblyName));

    /// <summary>Loads the shell by name, for the same reason and from a method of its own.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadShell() => Assembly.Load(new AssemblyName(ShellAssemblyName));
}
