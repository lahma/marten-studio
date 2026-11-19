using System.Reflection;
using System.Text.RegularExpressions;

using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Kafka.Tests.Conventions;

/// <summary>
/// D50: the Kafka diagnostics API is the host's own code's, and the studio never serves it. Nothing in the Kafka module (its pages,
/// its services, its export) names <see cref="IKafkaDiagnostics" /> or a type of its read model, the engine names them only where the API
/// lives and is registered, and no engine log event takes a result. The Postgres API is held the same way by
/// <c>DiagnosticsApiIsNotServedTests</c> in the Marten suite (D31).
/// </summary>
public class KafkaDiagnosticsApiIsNotServedTests
{
    /// <summary>Where the API and its read model may be named: where they are declared, and where the engine registers the service.</summary>
    private static readonly string[] Homes =
    [
        "src/DataStudio.Kafka.Core/Diagnostics/",
        "src/DataStudio.Kafka.Core/Clusters/KafkaEngineRegistration.cs",
    ];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The API and every type declared beside it, by name: the types of the files in the API's own folder.</summary>
    internal static string[] ApiTypes() =>
    [
        .. Directory.EnumerateFiles(RepositoryRoot.Combine("src", "DataStudio.Kafka.Core", "Diagnostics"), "*.cs")
            .SelectMany(static file => Regex.Matches(
                SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)),
                @"\b(?:class|record|enum|interface|struct)\s+([A-Za-z_][A-Za-z0-9_]*)",
                RegexOptions.CultureInvariant,
                RegexTimeout))
            .Select(static match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>Everything one file does wrong, one line each.</summary>
    internal static List<string> Offences(string relative, string source, IReadOnlyCollection<string> apiTypes)
    {
        if (Homes.Any(home => relative.StartsWith(home, StringComparison.Ordinal)))
        {
            return [];
        }

        string code = SourceScanner.StripCommentsAndStrings(source);

        return
        [
            .. apiTypes
                .Where(type => Regex.IsMatch(code, @"(?<!\w)" + Regex.Escape(type) + @"\b", RegexOptions.CultureInvariant, RegexTimeout))
                .Select(type => relative + " names " + type + ": the Kafka diagnostics API is host code's, never the studio's (D50)."),
        ];
    }

    [Fact]
    public void The_diagnostics_api_is_named_only_where_it_lives_and_is_registered()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] relative = [.. files.Select(SourceScanner.Relative)];
        relative.Should().Contain("src/DataStudio.Kafka.Core/Diagnostics/KafkaDiagnostics.cs", "the API's own folder is scanned");
        relative.Count(static x => x.StartsWith("src/DataStudio.Kafka/", StringComparison.Ordinal)).Should().BeGreaterThan(50, "the Kafka module's pages and services are scanned");

        string[] types = ApiTypes();
        types.Should().Contain(["IKafkaDiagnostics", "KafkaGroupStatus", "KafkaReadProblem", "KafkaMemberPlace"]);
        types.Length.Should().BeGreaterThan(10, "the read model is found, or the scan checks nothing");

        List<string> offences = [.. files.SelectMany(file => Offences(SourceScanner.Relative(file), File.ReadAllText(file), types))];

        offences.Should().BeEmpty(string.Join(" | ", offences));
    }

    [Fact]
    public void No_engine_log_event_takes_a_diagnostics_result()
    {
        HashSet<string> types = [.. ApiTypes()];
        Type[] logs =
        [
            .. typeof(IKafkaDiagnostics).Assembly.GetTypes().Concat(typeof(DataStudio.Kafka.KafkaOptions).Assembly.GetTypes())
                .Where(static x => x.IsAbstract && x.IsSealed && x.Name is "KafkaEngineLog" or "KafkaLog"),
        ];
        logs.Should().NotBeEmpty();

        foreach (MethodInfo method in logs.SelectMany(static x => x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)))
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Type type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                IEnumerable<Type> named = type.IsGenericType ? [type, .. type.GetGenericArguments()] : [type];

                named.Select(static x => x.Name).Should().NotContain(
                    types,
                    method.DeclaringType!.Name + "." + method.Name + "(" + parameter.Name + ") takes no diagnostics result: a log line carries codes and keys");
            }
        }
    }

    [Theory]
    [InlineData("src/DataStudio.Kafka/Components/Pages/Bad.razor", "@inject IKafkaDiagnostics Diagnostics", true)]
    [InlineData("src/DataStudio.Kafka/Services/KafkaCatalogService.cs", "KafkaGroupStatus status = await x.GetGroupAsync(a, b, c);", true)]
    [InlineData("src/DataStudio.Kafka.Core/Catalog/KafkaWireReads.cs", "var api = new KafkaDiagnostics(a, b, c, d);", true)]
    [InlineData("src/DataStudio.Kafka/Components/Pages/Fine.razor", "@* Never IKafkaDiagnostics: it is the host's. *@", false)]
    [InlineData("src/DataStudio.Kafka/Services/Fine.cs", "var text = \"KafkaGroupStatus\"; var x = MyKafkaGroupStatusLike;", false)]
    [InlineData("src/DataStudio.Kafka.Core/Diagnostics/KafkaDiagnostics.cs", "internal sealed class KafkaDiagnostics : IKafkaDiagnostics { }", false)]
    [InlineData("src/DataStudio.Kafka.Core/Clusters/KafkaEngineRegistration.cs", "services.TryAddSingleton<IKafkaDiagnostics, KafkaDiagnostics>();", false)]
    public void The_scan_finds_an_offender_and_ignores_prose_strings_homes_and_lookalikes(string relative, string source, bool offends)
    {
        Offences(relative, source, ["IKafkaDiagnostics", "KafkaDiagnostics", "KafkaGroupStatus"]).Should().HaveCountGreaterThanOrEqualTo(offends ? 1 : 0)
            .And.HaveCountLessThanOrEqualTo(offends ? 3 : 0);
    }
}
