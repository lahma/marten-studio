using System.Reflection;
using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// D31's "the studio never serves it" (API design §5.2): the diagnostics API is for host code alone, so its service, its host
/// gate and the builder behind it appear only under <c>src/PostgresStudio/Services/Diagnostics/</c>, in the read model's own
/// folder and in the two <c>Add*Studio</c> registrations; the studio's snapshot download (<c>VisitorSnapshotBuilder</c>) never
/// names any of them; no component, endpoint mapper or the health-check export names a read-model type; and no log event takes
/// one as an argument. A text scan with comments and strings stripped, as <see cref="NoAsyncVoidTests" /> is, with an
/// anti-vacuity theory of its own.
/// </summary>
public partial class DiagnosticsApiIsNotServedTests
{
    /// <summary>The host path: the API's service, its gate, its section builder and its limiter.</summary>
    internal static readonly string[] HostPath =
        ["IPostgresDiagnostics", "PostgresDiagnosticsService", "HostGate", "DiagnosticsSnapshotBuilder", "FreshReadLimiter"];

    /// <summary>Where the host path may be named.</summary>
    private static readonly string[] HostPathHomes =
    [
        "src/PostgresStudio/Services/Diagnostics/",
        "src/PostgresStudio/Diagnostics/",
        "src/PostgresStudio/PostgresStudioServiceCollectionExtensions.cs",
        "src/MartenStudio/MartenStudioServiceCollectionExtensions.cs",
    ];

    /// <summary>The studio's own download, which is built from the pages and must never reach the host path.</summary>
    private const string VisitorBuilder = "src/PostgresStudio/Services/Diagnostics/VisitorSnapshotBuilder.cs";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The read model's twenty types and the two links into the studio, by the files that declare them.</summary>
    internal static string[] ReadModel() =>
    [
        .. Directory.EnumerateFiles(RepositoryRoot.Combine("src", "PostgresStudio", "Diagnostics"), "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Whether <paramref name="relative" /> serves something - a component, an endpoint mapper, the health-check export.</summary>
    internal static bool Serves(string relative) =>
        relative.Contains("/Components/", StringComparison.Ordinal)
        || relative.EndsWith(".razor", StringComparison.Ordinal)
        || relative.Contains("/Services/Health/Export/", StringComparison.Ordinal)
        || relative.EndsWith("EndpointRouteBuilderExtensions.cs", StringComparison.Ordinal)
        || (relative.Contains("Endpoint", StringComparison.Ordinal) && relative.Contains("/Internal/", StringComparison.Ordinal));

    /// <summary>Everything one file does wrong, one line each.</summary>
    internal static List<string> Offences(string relative, string source, IReadOnlyCollection<string> readModel)
    {
        List<string> offences = [];
        string code = SourceScanner.StripCommentsAndStrings(source);

        bool home = HostPathHomes.Any(x => relative.StartsWith(x, StringComparison.Ordinal));

        foreach (string token in HostPath)
        {
            if (!Mentions(code, token))
            {
                continue;
            }

            if (!home)
            {
                offences.Add(relative + " names " + token + ": the diagnostics API is host code's, never the studio's (D31).");
            }
            else if (relative == VisitorBuilder)
            {
                offences.Add(relative + " names " + token + ": the download is built from the pages, as the visitor, never through the API's host gate.");
            }
        }

        if (Serves(relative))
        {
            foreach (string type in readModel.Where(type => Mentions(code, type)))
            {
                offences.Add(relative + " names " + type + ": no component, endpoint or the health-check export puts a diagnostics result anywhere.");
            }
        }

        if (relative.StartsWith("src/PostgresStudio/Services/Diagnostics/", StringComparison.Ordinal) && LoudLog().IsMatch(code))
        {
            offences.Add(relative + " logs above Debug directly: a diagnostics line above Debug is one of the declared events, which carry codes and names, never a result.");
        }

        return offences;
    }

    private static bool Mentions(string code, string token) =>
        Regex.IsMatch(code, @"(?<!\w)" + Regex.Escape(token) + @"\b", RegexOptions.CultureInvariant, RegexTimeout);

    [Fact]
    public void The_diagnostics_api_is_named_only_where_host_code_reaches_it()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] readModel = ReadModel();
        readModel.Should().HaveCount(22, "the read model's twenty-two types are found, or the scan checks nothing")
            .And.Contain("PostgresDiagnosticsSnapshot");

        List<string> offences = [.. files.SelectMany(file => Offences(SourceScanner.Relative(file), File.ReadAllText(file), readModel))];

        offences.Should().BeEmpty(string.Join(" | ", offences));
    }

    [Fact]
    public void The_scan_sees_the_host_paths_homes_and_the_studios_download()
    {
        string[] relative = [.. SourceScanner.ShippedSourceFiles().Select(SourceScanner.Relative)];

        relative.Should().Contain(VisitorBuilder);
        relative.Should().Contain("src/PostgresStudio/Components/Pages/Health/SnapshotMenu.razor");
        relative.Count(static x => HostPathHomes.Any(home => x.StartsWith(home, StringComparison.Ordinal)))
            .Should().BeGreaterThan(20, "the read model's folder and the API's are both found");
    }

    [Fact]
    public void No_log_event_takes_a_diagnostics_result()
    {
        Assembly postgres = typeof(PostgresStudio.PostgresStudioOptions).Assembly;
        Assembly marten = typeof(MartenStudio.MartenStudioOptions).Assembly;
        HashSet<string> readModel = [.. ReadModel(), "HostGate"];

        Type[] logs =
        [
            .. postgres.GetTypes().Concat(marten.GetTypes())
                .Where(static x => x.IsAbstract && x.IsSealed && x.Name is "StudioLog" or "PostgresStudioLog" or "MartenLog"),
        ];
        logs.Should().HaveCountGreaterThanOrEqualTo(3);

        foreach (MethodInfo method in logs.SelectMany(static x => x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)))
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Type type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                IEnumerable<Type> named = type.IsGenericType ? [type, .. type.GetGenericArguments()] : [type];

                named.Select(static x => x.Name).Should().NotContain(
                    readModel,
                    method.DeclaringType!.Name + "." + method.Name + "(" + parameter.Name + ") takes no diagnostics result: a log line carries codes and names");
            }
        }
    }

    // -----------------------------------------------------------------------------------------------
    // Anti-vacuity: the scan catches each offender and ignores prose and strings
    // -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("src/PostgresStudio/Components/Pages/Health/Bad.razor", "@inject IPostgresDiagnostics Diagnostics", "names IPostgresDiagnostics")]
    [InlineData("src/PostgresStudio/Services/Health/HealthService.cs", "var gate = new HostGate(true, screen);", "names HostGate")]
    [InlineData(VisitorBuilder, "var snapshot = await diagnostics.GetSnapshotAsync(x); IPostgresDiagnostics d;", "never through the API's host gate")]
    [InlineData("src/PostgresStudio/Services/Health/Export/HealthExportRunner.cs", "PostgresDiagnosticsSnapshot snapshot = null!;", "names PostgresDiagnosticsSnapshot")]
    [InlineData("src/PostgresStudio/Components/Pages/Health/Bad.razor", "@code { PostgresFinding? finding; }", "names PostgresFinding")]
    [InlineData("src/PostgresStudio/Services/Diagnostics/Loud.cs", "logger.LogWarning(\"x\");", "logs above Debug directly")]
    public void The_scan_finds_each_offender(string relative, string source, string offence)
    {
        Offences(relative, source, ["PostgresDiagnosticsSnapshot", "PostgresFinding"])
            .Should().Contain(x => x.Contains(offence, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("src/PostgresStudio/Components/Pages/Health/Fine.razor", "@* Never IPostgresDiagnostics: see HostGate. *@")]
    [InlineData("src/PostgresStudio/Services/Health/HealthService.cs", "/// <see cref=\"HostGate\" /> is the host's.\nvar text = \"IPostgresDiagnostics\";")]
    [InlineData("src/PostgresStudio/Services/Diagnostics/PostgresDiagnosticsService.cs", "internal sealed class PostgresDiagnosticsService : IPostgresDiagnostics { HostGate gate; }")]
    [InlineData("src/MartenStudio/MartenStudioServiceCollectionExtensions.cs", "services.TryAddSingleton<IPostgresDiagnostics, PostgresDiagnosticsService>();")]
    [InlineData("src/PostgresStudio/Services/Health/HealthService.cs", "var x = MyHostGateway.Open(); var y = a.IPostgresDiagnosticsLike; var z = HostGates;")]
    [InlineData("src/PostgresStudio/Services/Diagnostics/Quiet.cs", "logger.LogDebug(\"x\"); logger.DiagnosticsSectionFailed(level, e, s, k, d, q);")]
    public void The_scan_ignores_prose_strings_homes_and_lookalikes(string relative, string source)
    {
        Offences(relative, source, ["PostgresDiagnosticsSnapshot", "PostgresFinding"]).Should().BeEmpty();
    }

    [GeneratedRegex(@"\.Log(Information|Warning|Error|Critical)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex LoudLog();
}
