using System.Reflection;

using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The two shipped projects under <c>src/</c> since the 0.5 move: the shared studio, <c>PostgresStudio</c>, and
/// <c>MartenStudio</c>, which references it. A convention that scanned one folder or one assembly before the move scans
/// both through here, and asserts what it found is not nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Before PS-2 every rule here read <c>src/MartenStudio/</c> or <c>MartenStudio.dll</c>, and that was
/// everything. After it, a scan of one folder or one assembly keeps passing while the code it was written about sits in
/// the other: a fence over <c>Services/Health/Checks/</c> that reads MartenStudio's alone finds four table checks where
/// there are nineteen, and an IL scan of the shared assembly alone never sees MartenStudio's checks calling into it. So
/// the folders and assemblies are named once, here, and every caller asserts that each one it reads holds something.
/// </para>
/// </remarks>
internal static class StudioProjects
{
    /// <summary>The shared studio's project folder name under <c>src/</c>, and its assembly name.</summary>
    public const string Shared = "PostgresStudio";

    /// <summary>MartenStudio's project folder name under <c>src/</c>, and its assembly name.</summary>
    public const string Marten = "MartenStudio";

    /// <summary>Both, the shared studio first.</summary>
    public static IReadOnlyList<string> Names { get; } = [Shared, Marten];

    /// <summary>The shared studio's assembly.</summary>
    public static Assembly SharedAssembly => typeof(StudioCoreRegistration).Assembly;

    /// <summary>MartenStudio's assembly.</summary>
    public static Assembly MartenAssembly => typeof(MartenStudioOptions).Assembly;

    /// <summary>Both assemblies, the shared studio's first; two distinct ones, or the move did not happen.</summary>
    public static IReadOnlyList<Assembly> Assemblies => [SharedAssembly, MartenAssembly];

    /// <summary>
    /// <c>src/&lt;project&gt;/&lt;segments&gt;</c> for each project, as a repository-relative path with forward slashes and a
    /// trailing slash - the form <see cref="SourceScanner.Relative" /> reports files in, so a prefix test reads naturally.
    /// </summary>
    /// <param name="segments">The folder below each project's root.</param>
    public static IReadOnlyList<string> Prefixes(params string[] segments) =>
        [.. Names.Select(name => "src/" + name + "/" + string.Concat(segments.Select(static x => x + "/")))];

    /// <summary>Whether <paramref name="relativePath" /> is under <c>src/&lt;project&gt;/&lt;segments&gt;</c> of either project.</summary>
    public static bool IsUnder(string relativePath, params string[] segments) =>
        Prefixes(segments).Any(prefix => relativePath.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// The files matching <paramref name="pattern" /> in <c>src/&lt;project&gt;/&lt;segments&gt;</c> of each project that has the
    /// folder, build output excluded, in a stable order. Callers assert what they need of the result - that it is not
    /// empty, and usually that each project contributed - because a scan of nothing passes.
    /// </summary>
    /// <param name="pattern">A file pattern, <c>*.cs</c> or <c>*.razor</c>.</param>
    /// <param name="option">Whether to look below the folder too.</param>
    /// <param name="segments">The folder below each project's root.</param>
    public static string[] Files(string pattern, SearchOption option, params string[] segments) =>
        [.. Names
            .Select(name => RepositoryRoot.Combine(["src", name, .. segments]))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, pattern, option))
            .Where(static file => !SourceScanner.Relative(file).Split('/').Any(static x => x is "bin" or "obj"))
            .Order(StringComparer.Ordinal)];

    /// <summary>The project a repository-relative path under <c>src/</c> belongs to, or <see langword="null" />.</summary>
    public static string? ProjectOf(string relativePath) =>
        Names.FirstOrDefault(name => relativePath.StartsWith("src/" + name + "/", StringComparison.Ordinal));
}
