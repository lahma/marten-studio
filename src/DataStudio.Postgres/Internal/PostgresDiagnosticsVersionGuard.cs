using System.Reflection;
using System.Runtime.CompilerServices;

namespace DataStudio.Postgres.Internal;

/// <summary>
/// Refuses to start the Postgres module beside any DataStudio.Postgres.Diagnostics or DataStudio (the shell) but its own build - including none at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> DataStudio.Postgres and DataStudio.Postgres.Diagnostics are one program split in two files: the engine grants
/// DataStudio.Postgres its internals, so DataStudio.Postgres 2.0.0 beside DataStudio.Postgres.Diagnostics 2.0.1 is a
/// <see cref="MissingMethodException" /> on the first internal signature that moved. The nuspec pins
/// <c>DataStudio.Postgres.Diagnostics [&lt;version&gt;]</c> exactly (<c>DataStudio.Postgres.csproj</c>), and NuGet enforces that pin
/// everywhere but one place: with <c>CentralPackageTransitivePinningEnabled</c> and a central <c>PackageVersion</c> for
/// DataStudio.Postgres.Diagnostics at another version, the resolver drops it from the graph without a diagnostic - a green build
/// and a <see cref="FileNotFoundException" /> at startup (measured by PS-0 for the same pin one package up, 2026-10-20). A
/// host that silences NU1608 and references another DataStudio.Postgres.Diagnostics directly gets the other half: the wrong
/// assembly, loaded. The same guard, for the same reason, as <c>DataStudio.Marten.Internal.PostgresVersionGuard</c>.
/// </para>
/// <para>
/// <b>What the split and the scan are for.</b> This type names no DataStudio.Postgres.Diagnostics or DataStudio type, and neither does the
/// public <c>AddPostgres</c> that calls it first: everything that does is behind a
/// <see cref="MethodImplOptions.NoInlining" /> method, so the JIT resolves none of it until this guard has passed. The
/// assembly is loaded by name, from a <see cref="MethodImplOptions.NoInlining" /> method of its own.
/// <c>PostgresDiagnosticsVersionGuardTests</c> scans the IL of both to keep it that way, because in every test host
/// the engine is present and the same build, and a reference that crept in would break nothing there: it matters for a
/// newer engine whose members moved, which would otherwise fail on a bare <see cref="MissingMethodException" /> before the
/// guard could say why.
/// </para>
/// <para>
/// <b>What it cannot cover.</b> <c>AddPostgres</c>'s own parameter type, <c>Action&lt;PostgresOptions&gt;</c>,
/// names a type that derives from the engine's <c>PostgresDiagnosticsOptions</c>, so the runtime loads the engine to compile
/// <c>AddPostgres</c> itself, in every host, with a <c>configure</c> lambda or without, before the guard's first line.
/// A deployment with no engine, or an older one, therefore fails in the loader when <c>AddPostgres</c> is first called,
/// with a <see cref="FileNotFoundException" /> that names <c>DataStudio.Postgres.Diagnostics</c> and the version it wants (measured
/// on 2026-11-01 against throwaway hosts: the engine missing, and an older 0.9.0 under a 0.10.0 DataStudio.Postgres). This guard
/// is what answers a <em>newer</em> engine, or another commit of the same version, with the central-pin message. The shell is the
/// same case one package down: <c>AddPostgres</c> extends the shell's <c>DataStudioBuilder</c>, so the loader needs
/// <c>DataStudio</c> too before the guard's first line, and the guard answers a newer shell or another commit of the same version.
/// <c>DataStudio.Marten</c>'s <c>PostgresVersionGuard</c> names no options type before it runs, so for a Marten host it
/// answers a missing, older, newer or other-commit DataStudio.Postgres and engine itself; only the shell, which <c>AddMarten</c> extends as well,
/// is the loader's when it is missing or older.
/// </para>
/// <para>
/// <b>What it compares.</b> The informational versions. The packages are packed from one commit at one CHANGELOG version,
/// in Release, and SourceLink appends that commit after a <c>+</c>: when both assemblies are Release builds and both carry
/// one, the whole strings must match, so the same version packed from two commits is refused too. Otherwise - a Debug
/// build, where an IDE's incremental build after a commit legitimately leaves the two on different commits, or a build from
/// a tree with no git - only the part before the <c>+</c> is compared.
/// </para>
/// </remarks>
internal static class PostgresDiagnosticsVersionGuard
{
    /// <summary>The engine's assembly name: a string, never a type, for the reason in the remarks.</summary>
    internal const string DiagnosticsAssemblyName = "DataStudio.Postgres.Diagnostics";

    /// <summary>The shell's assembly name, checked the same way: a string, never a type.</summary>
    internal const string ShellAssemblyName = "DataStudio";

    /// <summary>
    /// Throws unless the DataStudio.Postgres.Diagnostics and the DataStudio this process loads are the builds this DataStudio.Postgres was packed with.
    /// </summary>
    /// <exception cref="InvalidOperationException">Either is missing, or is another version.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure() => Ensure(typeof(PostgresDiagnosticsVersionGuard).Assembly, LoadDiagnostics, LoadShell);

    /// <summary>The check for the engine alone, with the two assemblies supplied - a seam the tests use.</summary>
    /// <param name="postgresAssembly">DataStudio.Postgres' own assembly, whose version is the one required.</param>
    /// <param name="loadDiagnostics">Loads the engine's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <exception cref="InvalidOperationException">DataStudio.Postgres.Diagnostics is missing, or is another version.</exception>
    internal static void Ensure(Assembly postgresAssembly, Func<Assembly> loadDiagnostics) =>
        EnsureAssembly(DiagnosticsAssemblyName, postgresAssembly, loadDiagnostics);

    /// <summary>
    /// The whole check, with the three assemblies supplied - a seam the tests use: the engine first, then the shell
    /// (<c>DataStudio</c>, v2 step 3), which this assembly names in every signature and a deployment can lack or skew in the same way.
    /// </summary>
    /// <param name="postgresAssembly">DataStudio.Postgres' own assembly, whose version is the one required.</param>
    /// <param name="loadDiagnostics">Loads the engine's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <param name="loadShell">Loads the shell's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <exception cref="InvalidOperationException">Either is missing, or is another version.</exception>
    internal static void Ensure(Assembly postgresAssembly, Func<Assembly> loadDiagnostics, Func<Assembly> loadShell)
    {
        EnsureAssembly(DiagnosticsAssemblyName, postgresAssembly, loadDiagnostics);
        EnsureAssembly(ShellAssemblyName, postgresAssembly, loadShell);
    }

    /// <summary>One required assembly: loaded, and the same build as DataStudio.Postgres' own.</summary>
    /// <param name="name">The required package, <c>DataStudio.Postgres.Diagnostics</c> or <c>DataStudio</c>.</param>
    /// <param name="postgresAssembly">DataStudio.Postgres' own assembly, whose version is the one required.</param>
    /// <param name="load">Loads the required assembly; may throw <see cref="FileNotFoundException" />.</param>
    private static void EnsureAssembly(string name, Assembly postgresAssembly, Func<Assembly> load)
    {
        string? postgresVersion = InformationalVersion(postgresAssembly);
        string required = ReleaseVersion(postgresVersion);
        Assembly loaded;

        try
        {
            loaded = load();
        }
        catch (FileNotFoundException e)
        {
            throw new InvalidOperationException(
                $"DataStudio.Postgres {required} needs {name} {required} exactly, and this application was " +
                $"deployed without {name}. The usual cause is a central PackageVersion for " +
                $"{name} at another version under CentralPackageTransitivePinningEnabled: NuGet then " +
                $"drops {name} from the graph without a warning. The packages are released together: pin " +
                $"{name} to {required}, or remove its PackageVersion row and let DataStudio.Postgres bring it.",
                e);
        }

        string? loadedVersion = InformationalVersion(loaded);
        bool bothRelease = IsReleaseBuild(postgresAssembly) && IsReleaseBuild(loaded);

        if (SameBuild(postgresVersion, loadedVersion, bothRelease))
        {
            return;
        }

        // The commits were compared: name them, since the release version may well be the same on both sides.
        bool commits = bothRelease && HasBuildMetadata(postgresVersion) && HasBuildMetadata(loadedVersion);
        string shownRequired = commits ? postgresVersion! : required;
        string shownFound = commits ? loadedVersion! : ReleaseVersion(loadedVersion);

        throw new InvalidOperationException(
            $"DataStudio.Postgres {shownRequired} needs {name} {shownRequired} exactly, and found " +
            $"{name} {shownFound}. The packages are released together and must be upgraded together: a " +
            $"central PackageVersion or a PackageReference for {name} must say {required}, or be removed " +
            "and let DataStudio.Postgres bring it.");
    }
    /// <summary>
    /// Whether two informational versions name the same build. When both assemblies are Release builds and both carry
    /// build metadata - the <c>+&lt;commit&gt;</c> SourceLink appends - the whole strings must match, so two packed builds of
    /// one version from different commits are told apart. Otherwise the release versions (the part before the <c>+</c>)
    /// must match: a Debug build is a development tree, where an IDE's incremental build after a commit recompiles
    /// DataStudio.Postgres with the new commit and leaves an unchanged engine with the old one, and that is not a skew.
    /// </summary>
    /// <param name="postgresVersion">DataStudio.Postgres' informational version.</param>
    /// <param name="loadedVersion">The loaded assembly's informational version: the engine's, or the shell's.</param>
    /// <param name="bothRelease">Whether both assemblies say <c>[AssemblyConfiguration("Release")]</c>, as every pack does.</param>
    internal static bool SameBuild(string? postgresVersion, string? loadedVersion, bool bothRelease) =>
        bothRelease && HasBuildMetadata(postgresVersion) && HasBuildMetadata(loadedVersion)
            ? string.Equals(postgresVersion, loadedVersion, StringComparison.Ordinal)
            : string.Equals(ReleaseVersion(postgresVersion), ReleaseVersion(loadedVersion), StringComparison.Ordinal);

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
