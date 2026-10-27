using System.Text.Json;

using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// P09 to P14 over the corpus (<see cref="PlanFixtures" />): each insight fires on the plan that has its shape and on no plan
/// that does not, in both studios' words - and the same shape without <c>ANALYZE</c> is not judged, never read as quiet.
/// </summary>
public class PlanCorpusTests
{
    /// <summary>Which fixtures each of the six fires on.</summary>
    private static readonly Dictionary<string, string[]> Fires = new(StringComparer.Ordinal)
    {
        ["P09"] = ["p09-fires"],
        ["P10"] = ["p10-fires"],
        ["P11"] = ["p11-fires"],
        ["P12"] = ["p12-fires"],
        ["P13"] = ["p13-fires"],
        ["P14"] = ["p14-fires"],
    };

    public static TheoryData<string, string> EveryInsightOnEveryFixture()
    {
        TheoryData<string, string> data = [];

        foreach (string code in Fires.Keys)
        {
            foreach (string name in PlanFixtures.Names)
            {
                data.Add(code, name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryInsightOnEveryFixture))]
    public void An_insight_fires_on_the_corpus_plan_with_its_shape_and_stays_quiet_on_every_other(string code, string name)
    {
        List<PlanInsightFinding> findings = [.. PlanInsights.Evaluate(PlanFixtures.Plan(name), StudioText.Marten).Where(x => x.Insight.Code == code)];

        if (Fires[code].Contains(name))
        {
            findings.Should().NotBeEmpty("{0} is the shape {1} is about", name, code);
        }
        else
        {
            findings.Should().BeEmpty("{0} is not {1}'s shape: {2}", name, code, string.Join(" | ", findings.Select(static x => x.Message)));
        }
    }

    [Fact]
    public void Every_new_insight_has_a_firing_and_a_quiet_fixture()
    {
        PlanFixtures.Names.Should().Contain(["p09-fires", "p09-quiet", "p10-fires", "p10-quiet", "p11-fires", "p11-quiet", "p12-fires", "p12-quiet", "p13-fires", "p13-quiet", "p14-fires", "p14-quiet"]);
        Fires.Keys.Should().BeEquivalentTo(PlanInsights.All.Skip(8).Select(static x => x.Code), "P09 onward are 0.9's");
    }

    [Theory]
    [InlineData("P09", "p09-fires")]
    [InlineData("P10", "p10-fires")]
    [InlineData("P11", "p11-fires")]
    [InlineData("P12", "p12-fires")]
    [InlineData("P13", "p13-fires")]
    [InlineData("P14", "p14-fires")]
    public void The_same_plan_without_ANALYZE_is_not_judged_and_the_insight_is_listed_as_such(string code, string name)
    {
        string plain = StripActuals(PlanFixtures.Get(name));
        QueryPlan plan = QueryPlanReader.Read(plain).Plan!;

        plan.Analyzed.Should().BeFalse();
        PlanInsights.Evaluate(plan, StudioText.Marten).Where(x => x.Insight.Code == code).Should().BeEmpty("a plain plan prints none of what {0} reads", code);
        PlanInsights.NotJudged(plan).Select(static x => x.Code).Should().Contain(code);
    }

    [Fact]
    public void The_findings_say_what_the_plan_says_with_its_numbers()
    {
        Message("P09", "p09-fires").Should().Be(
            "Index Only Scan using orders_status_idx on orders visited the table 4,500 times to return 5,000 rows (90% as many fetches as rows), "
            + "because the pages those rows are on are not marked all-visible - so it was no cheaper than an index scan.");

        Message("P10", "p10-fires").Should().Be(
            "Index Scan using events_account_idx on events returned 100 rows and threw away 49,900 more with its filter, "
            + "(kind = 'refund'::text): 499 times as many as it kept. The index found rows the filter then rejected, so the table was visited for rows nobody wanted.");

        Message("P11", "p11-fires").Should().Be(
            "Hash was built in 8 batches, where 2 were planned, 4.0 MiB in memory at its peak: the table did not fit in work_mem, "
            + "so every batch but the first went through temporary files.");

        Message("P12", "p12-fires").Should().Be("JIT compiled 6 functions and took 45.2 ms - 38% of the 118.9 ms the statement ran for, which is a short run.");

        Message("P13", "p13-fires").Should().Be("Postgres spent 24.1 ms planning the statement and 3.2 ms running it.");

        Message("P14", "p14-fires").Should().Be(
            "Memoize found 100 of its lookups in the cache and missed 900, and evicted 450 cached results to make room (4.0 MiB in the cache at its peak): "
            + "the cache was too small for the parameters it was asked about, so it saved little.");

        static string Message(string code, string name) =>
            PlanInsights.Evaluate(PlanFixtures.Plan(name), StudioText.Marten).Single(x => x.Insight.Code == code).Message;
    }

    [Fact]
    public void P11_says_how_much_work_mem_the_hash_would_have_needed()
    {
        PlanInsightFinding finding = PlanInsights.Evaluate(PlanFixtures.Plan("p11-fires"), StudioText.Marten).Single(static x => x.Insight.Code == "P11");

        // 4,096 kB at its peak, in 8 batches: 32,768 kB, which is 32 MB.
        finding.Remedy.Should().Contain("about 32 MB").And.Contain("SET work_mem = '32MB'").And.Contain("hash_mem_multiplier");
    }

    [Fact]
    public void P09_links_to_Maintenance_and_the_others_link_nowhere()
    {
        PlanInsights.Evaluate(PlanFixtures.Plan("p09-fires"), StudioText.Marten).Single(static x => x.Insight.Code == "P09")
            .RelatedPage.Should().Be(PlanInsightPage.Maintenance);

        foreach (string name in new[] { "p10-fires", "p11-fires", "p12-fires", "p13-fires", "p14-fires" })
        {
            PlanInsights.Evaluate(PlanFixtures.Plan(name), StudioText.Marten).Should().OnlyContain(static x => x.RelatedPage == PlanInsightPage.None);
        }
    }

    [Fact]
    public void P10s_remedy_is_each_studios_own_placement()
    {
        QueryPlan plan = PlanFixtures.Plan("p10-fires");

        PlanInsights.Evaluate(plan, StudioText.Marten).Single(static x => x.Insight.Code == "P10").Remedy.Should()
            .Contain(StudioText.Marten.PlanIndexRemedy).And.Contain("CreateOrUpdate");
        PlanInsights.Evaluate(plan, PostgresStudioText.Instance).Single(static x => x.Insight.Code == "P10").Remedy.Should()
            .Contain(PostgresStudioText.Instance.PlanIndexRemedy).And.NotContain("Marten");
    }

    /// <summary>The fixture's plan with everything only <c>ANALYZE</c> prints removed: what a plain <c>EXPLAIN</c> would have said.</summary>
    private static string StripActuals(string json)
    {
        string[] analyzeOnly =
        [
            "Actual Startup Time", "Actual Total Time", "Actual Rows", "Actual Loops", "Heap Fetches", "Rows Removed by Filter",
            "Rows Removed by Index Recheck", "Hash Batches", "Original Hash Batches", "Peak Memory Usage", "Cache Hits", "Cache Misses",
            "Cache Evictions", "Cache Overflows", "Execution Time", "JIT", "Shared Hit Blocks", "Shared Read Blocks", "Shared Dirtied Blocks",
            "Shared Written Blocks", "Local Hit Blocks", "Local Read Blocks", "Local Dirtied Blocks", "Local Written Blocks",
            "Temp Read Blocks", "Temp Written Blocks",
        ];

        using JsonDocument document = JsonDocument.Parse(json);
        using MemoryStream stream = new();

        using (Utf8JsonWriter writer = new(stream))
        {
            Write(document.RootElement, writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());

        void Write(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();

                    foreach (JsonProperty property in element.EnumerateObject().Where(x => !analyzeOnly.Contains(x.Name)))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(property.Value, writer);
                    }

                    writer.WriteEndObject();
                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();

                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        Write(item, writer);
                    }

                    writer.WriteEndArray();
                    break;

                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
