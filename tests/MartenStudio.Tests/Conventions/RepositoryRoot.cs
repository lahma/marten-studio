namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Finds the repository root from the test assembly's own location.
/// </summary>
/// <remarks>
/// The convention tests read the checked-in source tree, not the build output, so they need a path to it
/// that does not depend on the working directory - <c>dotnet test</c>, the IDE runner and
/// <c>dotnet fallout Test</c> all set a different one. Walking up from
/// <see cref="AppContext.BaseDirectory"/> until the solution file appears is the one answer that holds
/// for all three, and it fails loudly rather than silently scanning nothing if the layout ever moves.
/// </remarks>
internal static class RepositoryRoot
{
    /// <summary>The file that identifies the repository root when walking up from the test assembly.</summary>
    private const string RootMarker = "marten-studio.slnx";

    private static readonly Lazy<string> Path = new(Find);

    /// <summary>The absolute path of the directory containing <c>marten-studio.slnx</c>.</summary>
    public static string FullPath => Path.Value;

    public static string Combine(params string[] relativeSegments) =>
        System.IO.Path.Combine([FullPath, .. relativeSegments]);

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, RootMarker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not find {RootMarker} above {AppContext.BaseDirectory}.");
    }
}
