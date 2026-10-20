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
/// <b>What it compares.</b> The informational versions, up to the <c>+</c> that SourceLink appends the commit
/// to: the two packages are packed from one commit at one CHANGELOG version, and that version is the contract.
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
        string required = ReleaseVersion(martenStudio);
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

        string found = ReleaseVersion(postgresStudio);

        if (!string.Equals(required, found, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"MartenStudio {required} needs PostgresStudio {required} exactly, and found PostgresStudio {found}. " +
                "The two packages are released together and must be upgraded together: a central PackageVersion or a " +
                $"PackageReference for PostgresStudio must say {required}, or be removed and let MartenStudio bring it.");
        }
    }

    /// <summary>An assembly's informational version up to the <c>+</c>, or its assembly version without one.</summary>
    /// <param name="assembly">The assembly.</param>
    internal static string ReleaseVersion(Assembly assembly) =>
        ReleaseVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString());

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
