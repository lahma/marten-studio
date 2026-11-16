using System.Reflection;

using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// The four projects of the studio under <c>src/</c>: the shell, <c>DataStudio</c> (v2 step 3), the shared studio, <c>PostgresStudio</c>, the
/// diagnostics engine it is built on, <c>DataStudio.Postgres.Diagnostics</c> (no ASP.NET Core), and <c>MartenStudio</c>, which references
/// them. The Kafka module is a studio of its own and is read by its own suite. A convention that scanned one folder or one assembly before the move scans all of them through here, and asserts
/// what it found is not nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Before PS-2 every rule here read <c>src/DataStudio.Marten/</c> or <c>DataStudio.Marten.dll</c>, and that was
/// everything. After it, a scan of one folder or one assembly keeps passing while the code it was written about sits in
/// the other: a fence over <c>Services/Health/Checks/</c> that reads MartenStudio's alone finds four table checks where
/// there are nineteen, and an IL scan of the shared assembly alone never sees MartenStudio's checks calling into it. So
/// the folders and assemblies are named once, here, and every caller asserts that each one it reads holds something.
/// </para>
/// </remarks>
internal static class StudioProjects
{
    /// <summary>The shell's project folder name under <c>src/</c>, and its assembly name.</summary>
    public const string Shell = "DataStudio";

    /// <summary>The shared studio's project folder name under <c>src/</c>, and its assembly name.</summary>
    public const string Shared = "DataStudio.Postgres";

    /// <summary>The diagnostics engine's project folder name under <c>src/</c>, and its assembly name.</summary>
    public const string Diagnostics = "DataStudio.Postgres.Diagnostics";

    /// <summary>MartenStudio's project folder name under <c>src/</c>, and its assembly name.</summary>
    public const string Marten = "DataStudio.Marten";

    /// <summary>All four: the shared studio first, the engine it is built on second, MartenStudio third and the shell, which all three sit on, last.</summary>
    public static IReadOnlyList<string> Names { get; } = [Shared, Diagnostics, Marten, Shell];

    /// <summary>The shell's assembly.</summary>
    public static Assembly ShellAssembly => typeof(DataStudio.Components.StudioRoutes).Assembly;

    /// <summary>
    /// The projects that hold database code: every one but the shell. A convention about a database folder - a builder folder, a health check, a
    /// query page - asserts that each of these contributed to what it read, and the shell, which has none of those folders, is not asked to.
    /// </summary>
    public static IReadOnlyList<string> Databases { get; } = [Shared, Diagnostics, Marten];

    /// <summary>The shared studio's assembly.</summary>
    public static Assembly SharedAssembly => typeof(StudioCoreRegistration).Assembly;

    /// <summary>The diagnostics engine's assembly.</summary>
    public static Assembly DiagnosticsAssembly => typeof(IDiagnosticsSettings).Assembly;

    /// <summary>MartenStudio's assembly.</summary>
    public static Assembly MartenAssembly => typeof(MartenStudioOptions).Assembly;

    /// <summary>All four assemblies, the shared studio's first; four distinct ones, or the split did not happen.</summary>
    public static IReadOnlyList<Assembly> Assemblies => [SharedAssembly, DiagnosticsAssembly, MartenAssembly, ShellAssembly];

    /// <summary>
    /// <c>src/&lt;project&gt;/&lt;segments&gt;</c> for each database project (<see cref="Databases" />), as a repository-relative path with forward
    /// slashes and a trailing slash - the form <see cref="SourceScanner.Relative" /> reports files in, so a prefix test reads naturally.
    /// </summary>
    /// <param name="segments">The folder below each project's root.</param>
    public static IReadOnlyList<string> Prefixes(params string[] segments) =>
        [.. Databases.Select(name => "src/" + name + "/" + string.Concat(segments.Select(static x => x + "/")))];

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
