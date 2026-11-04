using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// 1.2, M6: Events › Event flow reads <c>StoreOptions</c> and the projection graph and nothing else, so no file of it calls a
/// member that describes, builds, validates, registers or migrates - nor opens a connection.
/// </summary>
/// <remarks>
/// <para>
/// Each member below does something the page must not (D-FLOW decision 4.3): <c>Describe</c> and <c>TryCreateUsage</c> enumerate
/// the tenancy and read the database's high water, and <c>ProjectionGraph.Describe</c> registers every declared type as an event
/// type as it goes; <c>AggregatorFor</c>, <c>AssembleAndAssertValidity</c> and <c>BuildExecution</c> build, cache or validate the
/// host's configuration; <c>CreateSchemaObjects</c> moves a flat table's schema and deletes data on teardown; <c>Shards()</c>
/// builds shards (the page reads <c>AllShards()</c> and <c>ShardNames()</c>, as the Projections page does); <c>EventMappingFor</c>
/// registers a mapping. The existing <see cref="StorageEnsuringCallTests" /> and <see cref="TenancyEnumerationCallTests" /> hold the
/// rest and pass with no new allowed site.
/// </para>
/// <para>
/// Read after <see cref="SourceScanner" /> blanks comments and strings, because the files explain every one of these at length.
/// </para>
/// </remarks>
public class EventFlowPurityTests
{
    /// <summary>A call to one of the members the flow never makes, as a member access.</summary>
    private static readonly Regex Forbidden = new(
        @"\.\s*(?<member>Describe|TryCreateUsage|TryDescribeDatabasesAsync|DescribeDatabasesAsync|AggregatorFor|AssembleAndAssertValidity"
        + @"|CreateSchemaObjects|Shards|BuildExecution|BuildForInline|TryBuildReplayExecutor|EventMappingFor|BuildProjectionDaemonAsync"
        + @"|AllDatabases|OpenAsync|CreateConnection|FetchMaxEventSequenceAsync)\s*[(<]",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>Types the flow never touches: a connection, a database, the event store seam, JasperFx's model source.</summary>
    private static readonly Regex ForbiddenTypes = new(
        @"\b(?:NpgsqlConnection|NpgsqlCommand|IMartenDatabase|IEventStore|ProjectionEventModelSource|ISubscriptionSource\.Describe)\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static string[] FlowFiles() =>
    [
        .. SourceScanner.ShippedSourceFiles().Where(static x =>
        {
            string name = Path.GetFileName(x);
            return name.StartsWith("EventFlow", StringComparison.Ordinal) || name == "EventDataService.EventFlow.cs";
        }),
    ];

    [Fact]
    public void The_flow_scans_the_files_it_is_made_of()
    {
        string[] names = [.. FlowFiles().Select(static x => Path.GetFileName(x)).Order(StringComparer.Ordinal)];

        string[] expected =
        [
            "EventFlow.cs", "EventFlow.razor", "EventFlowCache.cs", "EventFlowDiagram.cs", "EventFlowGraph.cs", "EventFlowLegend.razor", "EventDataService.EventFlow.cs",
        ];

        names.Should().Contain(expected);
    }

    [Fact]
    public void No_file_of_the_flow_calls_a_member_that_describes_builds_validates_registers_or_migrates()
    {
        List<string> found = [];

        foreach (string path in FlowFiles())
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(path));

            foreach (Match match in Forbidden.Matches(code))
            {
                found.Add($"{SourceScanner.Relative(path)}: .{match.Groups["member"].Value}(");
            }

            foreach (Match match in ForbiddenTypes.Matches(code))
            {
                found.Add($"{SourceScanner.Relative(path)}: {match.Value}");
            }
        }

        found.Should().BeEmpty("the flow is read from StoreOptions and the projection graph alone (D-FLOW decision 4.3)");
    }

    [Fact]
    public void The_scanner_finds_a_real_call_and_ignores_one_in_a_comment_or_a_string()
    {
        const string Source = """
            // store.Describe(usage) is never called: see the remarks.
            string text = "projection.CreateSchemaObjects(events)";
            var shards = options.Projections.Shards();
            var all = options.Projections.AllShards();
            """;

        string code = SourceScanner.StripCommentsAndStrings(Source);

        Forbidden.Matches(code).Select(static x => x.Groups["member"].Value).Should().Equal("Shards");
    }
}
