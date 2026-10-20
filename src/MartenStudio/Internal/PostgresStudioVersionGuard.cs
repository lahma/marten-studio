using System.Reflection;
using System.Runtime.CompilerServices;

namespace MartenStudio.Internal;

/// <summary>
/// Refuses to start Marten Studio beside any PostgresStudio but its own build - including none at all.
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
/// version, and SourceLink appends that commit after a <c>+</c>: when both assemblies carry one, the whole strings must
/// match, so the same version built from two commits is refused too; when either lacks it (a build from a tree with no
/// git), only the part before the <c>+</c> is compared.
/// </para>
/// </remarks>
internal static class PostgresStudioVersionGuard
{
    /// <summary>The shared studio's assembly name: a string, never a type, for the reason in the remarks.</summary>
    internal const string PostgresStudioAssemblyName = "PostgresStudio";

    /// <summary>Throws unless the PostgresStudio this process loads is the build this MartenStudio was packed with.</summary>
    /// <exception cref="InvalidOperationException">PostgresStudio is missing, or is another version.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure() => Ensure(typeof(PostgresStudioVersionGuard).Assembly, LoadPostgresStudio);

    /// <summary>The check itself, with the two assemblies supplied - the seam the tests use.</summary>
    /// <param name="martenStudio">MartenStudio's own assembly, whose version is the one required.</param>
    /// <param name="loadPostgresStudio">Loads the shared studio's assembly; may throw <see cref="FileNotFoundException" />.</param>
    /// <exception cref="InvalidOperationException">PostgresStudio is missing, or is another version.</exception>
    internal static void Ensure(Assembly martenStudio, Func<Assembly> loadPostgresStudio)
    {
        string? martenStudioVersion = InformationalVersion(martenStudio);
        string required = ReleaseVersion(martenStudioVersion);
        Assembly postgresStudio;

        try
        {
            postgresStudio = loadPostgresStudio();
        }
        catch (FileNotFoundException e)
        {
            throw new InvalidOperationException(
                $"MartenStudio {required} needs PostgresStudio {required} exactly, and this application was deployed " +
                "without PostgresStudio. The usual cause is a central PackageVersion for PostgresStudio at another " +
                "version under CentralPackageTransitivePinningEnabled: NuGet then drops PostgresStudio from the graph " +
                $"without a warning. The two packages are released together: pin PostgresStudio to {required}, or remove " +
                "its PackageVersion row and let MartenStudio bring it.",
                e);
        }

        string? postgresStudioVersion = InformationalVersion(postgresStudio);

        if (SameBuild(martenStudioVersion, postgresStudioVersion))
        {
            return;
        }

        // Both carry a commit: name it, since the release version may well be the same on both sides.
        bool commits = HasBuildMetadata(martenStudioVersion) && HasBuildMetadata(postgresStudioVersion);
        string shownRequired = commits ? martenStudioVersion! : required;
        string shownFound = commits ? postgresStudioVersion! : ReleaseVersion(postgresStudioVersion);

        throw new InvalidOperationException(
            $"MartenStudio {shownRequired} needs PostgresStudio {shownRequired} exactly, and found PostgresStudio " +
            $"{shownFound}. The two packages are released together and must be upgraded together: a central " +
            $"PackageVersion or a PackageReference for PostgresStudio must say {required}, or be removed and let " +
            "MartenStudio bring it.");
    }

    /// <summary>
    /// Whether two informational versions name the same build. When both carry build metadata - the <c>+&lt;commit&gt;</c>
    /// SourceLink appends - the whole strings must match, so two builds of one version from different commits are told
    /// apart. When only one side carries it, or neither does, there is no commit to compare, and the release versions
    /// (the part before the <c>+</c>) must match, as before.
    /// </summary>
    /// <param name="martenStudio">MartenStudio's informational version.</param>
    /// <param name="postgresStudio">PostgresStudio's informational version.</param>
    internal static bool SameBuild(string? martenStudio, string? postgresStudio) =>
        HasBuildMetadata(martenStudio) && HasBuildMetadata(postgresStudio)
            ? string.Equals(martenStudio, postgresStudio, StringComparison.Ordinal)
            : string.Equals(ReleaseVersion(martenStudio), ReleaseVersion(postgresStudio), StringComparison.Ordinal);

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
}
