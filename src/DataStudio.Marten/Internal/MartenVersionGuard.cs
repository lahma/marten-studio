using System.Runtime.CompilerServices;

using global::Marten;

namespace DataStudio.Marten.Internal;

/// <summary>
/// Refuses to start the Marten module on Marten 10 or later, and says what to do instead (#103).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Data Studio 2 is built against Marten 9 and its package accepts no Marten from 10.0.0 on (the nuspec says so,
/// <c>Directory.Packages.props</c>), but NU1608 is only a warning and a host can take a newer Marten through its own reference, and the studio would
/// then start and fail at an arbitrary call with a <see cref="MissingMethodException" />. One build of the Marten module cannot serve both
/// majors: Data Studio 3.0 is the line for Marten 10.
/// </para>
/// <para>
/// <b>How it is checked.</b> The loaded Marten assembly's version, read through a type that exists in both majors
/// (<see cref="IDocumentStore" />) and compared by major. The method is <see cref="MethodImplOptions.NoInlining" />, as
/// <see cref="PostgresVersionGuard" /> is, and runs from the public <c>AddMarten</c> and <c>AddMartenDiagnostics</c> right after it.
/// </para>
/// </remarks>
internal static class MartenVersionGuard
{
    /// <summary>The first Marten major this build does not support.</summary>
    internal const int FirstUnsupportedMajor = 10;

    /// <summary>Throws when the Marten this process loaded is 10 or later.</summary>
    /// <exception cref="InvalidOperationException">Marten is 10 or later.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Ensure() =>
        Ensure(typeof(IDocumentStore).Assembly.GetName().Version, PostgresVersionGuard.ReleaseVersion(typeof(MartenVersionGuard).Assembly));

    /// <summary>The check with the loaded Marten version and the studio's own supplied - the seam the tests use.</summary>
    /// <param name="marten">The loaded Marten assembly's version; <see langword="null" /> passes.</param>
    /// <param name="ownVersion">The studio's own release version, as the message names it.</param>
    /// <exception cref="InvalidOperationException">Marten is 10 or later.</exception>
    internal static void Ensure(Version? marten, string ownVersion)
    {
        if (marten is null || marten.Major < FirstUnsupportedMajor)
        {
            return;
        }

        throw new InvalidOperationException(Refusal(marten, ownVersion));
    }

    /// <summary>The sentence the refusal says.</summary>
    /// <param name="marten">The loaded Marten version.</param>
    /// <param name="ownVersion">The studio's own release version.</param>
    internal static string Refusal(Version marten, string ownVersion) =>
        $"Data Studio {ownVersion} supports Marten 9, and this application uses Marten {marten.Major}.{marten.Minor}.{Math.Max(marten.Build, 0)}. " +
        "Marten 10 and later need Data Studio 3.0 or later.";
}
