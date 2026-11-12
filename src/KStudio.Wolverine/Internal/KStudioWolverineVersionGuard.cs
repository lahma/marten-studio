using System.Reflection;
using System.Runtime.CompilerServices;

namespace KStudio.Internal;

/// <summary>
/// Refuses to wire KStudio.Wolverine to any KStudio.Core but its own build (D43, D30's discipline one assembly over). KStudio.Core grants this
/// assembly its internals (<c>InternalsVisibleTo</c>), so one skewed assembly is a <see cref="MissingMethodException" /> on the first internal
/// member that moved; the nuspec pins KStudio.Core exactly, and this is the check for the place NuGet's pin does not reach.
/// </summary>
/// <remarks>
/// <para>
/// <b>How it runs before anything of KStudio.Core's internals is touched.</b> The public <c>UseWolverineKafka</c> names only KStudio.Core's public
/// <c>KafkaEngineOptions</c> and calls <see cref="Ensure()" /> first; the body that adds the source - which names <c>IKafkaClusterSource</c> and
/// the rest - is a <see cref="MethodImplOptions.NoInlining" /> method of its own, so the JIT resolves none of it until the guard has passed.
/// <c>KStudioWolverineVersionGuardTests</c> scans the IL to keep it that way.
/// </para>
/// <para>
/// <b>What it compares.</b> The informational versions: when both are Release builds that carry a commit (SourceLink's <c>+&lt;commit&gt;</c>) the
/// whole strings; otherwise the part before the <c>+</c>.
/// </para>
/// </remarks>
internal static class KStudioWolverineVersionGuard
{
    /// <summary>KStudio's engine assembly name: a string, never a type, for the reason in the remarks.</summary>
    internal const string EngineAssemblyName = "KStudio.Core";

    /// <summary>Throws unless the KStudio.Core this process loads is the build this KStudio.Wolverine was packed with.</summary>
    /// <exception cref="InvalidOperationException">KStudio.Core is missing, or is another version.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure() => Ensure(typeof(KStudioWolverineVersionGuard).Assembly, LoadEngine);

    /// <summary>The whole check, with the loader supplied - the seam the tests use.</summary>
    /// <param name="self">This assembly, whose version is the one required.</param>
    /// <param name="loadEngine">Loads KStudio.Core; may throw <see cref="FileNotFoundException" />.</param>
    /// <exception cref="InvalidOperationException">KStudio.Core is missing, or is another version.</exception>
    internal static void Ensure(Assembly self, Func<Assembly> loadEngine)
    {
        string? selfVersion = InformationalVersion(self);
        string required = ReleaseVersion(selfVersion);
        Assembly loaded;

        try
        {
            loaded = loadEngine();
        }
        catch (FileNotFoundException e)
        {
            throw new InvalidOperationException(
                $"KStudio.Wolverine {required} needs {EngineAssemblyName} {required} exactly, and this application was deployed " +
                $"without {EngineAssemblyName}. The packages are released together: pin {EngineAssemblyName} to {required}, or remove its " +
                $"PackageVersion row and let KStudio.Wolverine bring it.",
                e);
        }

        string? loadedVersion = InformationalVersion(loaded);
        bool bothRelease = IsReleaseBuild(self) && IsReleaseBuild(loaded);

        if (SameBuild(selfVersion, loadedVersion, bothRelease))
        {
            return;
        }

        bool commits = bothRelease && HasBuildMetadata(selfVersion) && HasBuildMetadata(loadedVersion);
        string shownRequired = commits ? selfVersion! : required;
        string shownFound = commits ? loadedVersion! : ReleaseVersion(loadedVersion);

        throw new InvalidOperationException(
            $"KStudio.Wolverine {shownRequired} needs {EngineAssemblyName} {shownRequired} exactly, and found {EngineAssemblyName} " +
            $"{shownFound}. The packages are released together and must be upgraded together: a central PackageVersion or a " +
            $"PackageReference for {EngineAssemblyName} must say {required}, or be removed and let KStudio.Wolverine bring it.");
    }

    /// <summary>Whether two informational versions name the same build (the rule in the remarks).</summary>
    /// <param name="self">This assembly's informational version.</param>
    /// <param name="other">The other assembly's informational version.</param>
    /// <param name="bothRelease">Whether both assemblies say <c>[AssemblyConfiguration("Release")]</c>.</param>
    internal static bool SameBuild(string? self, string? other, bool bothRelease) =>
        bothRelease && HasBuildMetadata(self) && HasBuildMetadata(other)
            ? string.Equals(self, other, StringComparison.Ordinal)
            : string.Equals(ReleaseVersion(self), ReleaseVersion(other), StringComparison.Ordinal);

    /// <summary>Whether an assembly was built in the Release configuration, as every pack is.</summary>
    /// <param name="assembly">The assembly.</param>
    internal static bool IsReleaseBuild(Assembly assembly) =>
        string.Equals(
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            "Release",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>An assembly's informational version as it carries it, or its assembly version when it has none.</summary>
    /// <param name="assembly">The assembly.</param>
    internal static string? InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString();

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

    private static bool HasBuildMetadata(string? informationalVersion) =>
        informationalVersion?.Contains('+', StringComparison.Ordinal) == true;

    /// <summary>Loads KStudio.Core by name - the one place its name meets the loader.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadEngine() => Assembly.Load(new AssemblyName(EngineAssemblyName));
}
