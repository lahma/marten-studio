using System.Reflection;
using System.Runtime.CompilerServices;

namespace DataStudio.Kafka.Core.Clusters;

/// <summary>
/// Refuses to start the Kafka module on Confluent.Kafka 3 or later, and says what to do instead (#103).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The engine reads Kafka with Confluent.Kafka 2 and its package accepts no version from 3.0.0 on (the nuspec says so), but NU1608 is
/// only a warning and a host can take a newer one through its own reference. Confluent.Kafka 4 is a different client under the same package id (a Rust core and a
/// Java-style API), so the engine would start and fail at an arbitrary call with a <see cref="MissingMethodException" />.
/// </para>
/// <para>
/// <b>How it is checked.</b> The assembly is loaded by name and its version read, and the method names no Confluent type: a 4.x assembly may not have
/// the ones this engine uses. A Confluent.Kafka that is not there at all is not this guard's business: the existing behaviour stands.
/// </para>
/// </remarks>
internal static class ConfluentClientGuard
{
    /// <summary>The Confluent.Kafka assembly's name: a string, never a type, for the reason in the remarks.</summary>
    internal const string ConfluentAssemblyName = "Confluent.Kafka";

    /// <summary>The first Confluent.Kafka major this build does not support.</summary>
    internal const int FirstUnsupportedMajor = 3;

    /// <summary>Throws when the Confluent.Kafka this process loads is 3 or later.</summary>
    /// <exception cref="InvalidOperationException">Confluent.Kafka is 3 or later.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure()
    {
        Version? loaded;

        try
        {
            loaded = LoadConfluent().GetName().Version;
        }
        catch (FileNotFoundException)
        {
            return;
        }

        Ensure(loaded, ReleaseVersion(typeof(ConfluentClientGuard).Assembly));
    }

    /// <summary>The check with the loaded assembly's version and the engine's own supplied - the seam the tests use.</summary>
    /// <param name="confluent">The loaded Confluent.Kafka assembly's version; <see langword="null" /> passes.</param>
    /// <param name="ownVersion">The engine's own release version, as the message names it.</param>
    /// <exception cref="InvalidOperationException">Confluent.Kafka is 3 or later.</exception>
    internal static void Ensure(Version? confluent, string ownVersion)
    {
        if (confluent is null || confluent.Major < FirstUnsupportedMajor)
        {
            return;
        }

        throw new InvalidOperationException(Refusal(confluent, ownVersion));
    }

    /// <summary>The sentence the refusal says.</summary>
    /// <param name="confluent">The loaded Confluent.Kafka version.</param>
    /// <param name="ownVersion">The engine's own release version.</param>
    internal static string Refusal(Version confluent, string ownVersion) =>
        $"Data Studio {ownVersion} reads Kafka with Confluent.Kafka 2, and this application uses Confluent.Kafka " +
        $"{confluent.Major}.{confluent.Minor}.{Math.Max(confluent.Build, 0)}, a different client under the same package id. " +
        "Keep Confluent.Kafka 2 for Data Studio 2.";

    /// <summary>The part of an assembly's informational version before the <c>+</c> build metadata, or its assembly version without one.</summary>
    private static string ReleaseVersion(Assembly assembly)
    {
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();

        if (string.IsNullOrWhiteSpace(version))
        {
            return "?";
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);

        return plus < 0 ? version : version[..plus];
    }

    /// <summary>Loads Confluent.Kafka by name - the one place its name meets the loader.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly LoadConfluent() => Assembly.Load(new AssemblyName(ConfluentAssemblyName));
}
