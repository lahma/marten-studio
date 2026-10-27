using System.Reflection;
using System.Text.Json;

using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// What the analyzer added to the plan reader: the keys 0.9's insights and views read (heap fetches, memoize, WAL, settings,
/// planning memory, serialization), every key a node printed kept as text for the detail panel - and never for a masked read -
/// and the dictionary of what each key means, held to every key the reader knows.
/// </summary>
public class QueryPlanReaderAnalyzerTests
{
    [Fact]
    public void The_new_node_keys_are_read_and_a_missing_one_is_blank()
    {
        PlanNode scan = QueryPlanReader.Read("""
            [{"Plan": {"Node Type": "Index Only Scan", "Heap Fetches": 4500, "WAL Records": 12, "WAL FPI": 3, "WAL Bytes": 9000,
              "Cache Hits": 1, "Cache Misses": 2, "Cache Evictions": 3, "Cache Overflows": 4}}]
            """).Plan!.Root;

        scan.HeapFetches.Should().Be(4500);
        scan.WalRecords.Should().Be(12);
        scan.WalFpi.Should().Be(3);
        scan.WalBytes.Should().Be(9000);
        (scan.CacheHits, scan.CacheMisses, scan.CacheEvictions, scan.CacheOverflows).Should().Be((1d, 2d, 3d, 4d));

        PlanNode bare = QueryPlanReader.Read("""[{"Plan": {"Node Type": "Seq Scan"}}]""").Plan!.Root;
        bare.HeapFetches.Should().BeNull();
        bare.WalBytes.Should().BeNull();
        bare.CacheHits.Should().BeNull();
        bare.Properties.Should().ContainSingle().Which.Should().Be(new PlanProperty("Node Type", "Seq Scan"));
    }

    [Fact]
    public void A_key_of_the_wrong_type_reads_as_absent_never_as_an_exception()
    {
        PlanNode node = QueryPlanReader.Read("""[{"Plan": {"Node Type": "Memoize", "Cache Hits": [1], "Heap Fetches": {"a": 1}, "WAL Bytes": null}}]""").Plan!.Root;

        node.CacheHits.Should().BeNull();
        node.HeapFetches.Should().BeNull();
        node.WalBytes.Should().BeNull();
    }

    [Fact]
    public void The_statements_settings_jit_planning_memory_and_serialization_are_read()
    {
        QueryPlan plan = PlanFixtures.Plan("analyzed-join");

        plan.Settings.Should().Equal(new PlanProperty("work_mem", "64MB"), new PlanProperty("random_page_cost", "1.1"));
        plan.Triggers.Should().ContainSingle().Which.Should().Be(new PlanTrigger("orders_audit", "orders", 1.25, 3));
        plan.TriggersMs.Should().Be(1.25);
        plan.RowsReturned.Should().Be(1000);

        QueryPlan jit = PlanFixtures.Plan("p12-fires");
        jit.JitProperties.Select(static x => x.Key).Should().Contain(["Functions", "Options › Inlining", "Timing › Total"]);
        jit.JitProperties.Single(static x => x.Key == "Timing › Total").Value.Should().Be("45.2");

        QueryPlan seventeen = QueryPlanReader.Read("""
            [{"Plan": {"Node Type": "Result"}, "Planning": {"Memory Used": 17840, "Memory Allocated": 24576},
              "Serialization": {"Time": 0.065, "Output Volume": 2, "Format": "text"}}]
            """).Plan!;

        seventeen.PlanningMemoryBytes.Should().Be(17840);
        seventeen.SerializationMs.Should().Be(0.065);
        seventeen.SerializationKb.Should().Be(2);
    }

    [Fact]
    public void Every_scalar_key_a_node_printed_is_kept_as_text_in_the_order_it_printed_them_but_Plans()
    {
        PlanNode root = PlanFixtures.Plan("p14-fires").Root;

        root.Properties.Select(static x => x.Key).Take(3).Should().Equal("Node Type", "Parallel Aware", "Async Capable");
        root.Properties.Select(static x => x.Key).Should().NotContain("Plans");
        root.Properties.Single(static x => x.Key == "Actual Rows").Value.Should().Be("1000.00", "a number as Postgres wrote it");
        root.Properties.Single(static x => x.Key == "Parallel Aware").Value.Should().Be("false");

        PlanNode memoize = PlanFixtures.Plan("p14-fires").Nodes.Single(static x => x.NodeType == "Memoize");
        memoize.Properties.Single(static x => x.Key == "Cache Key").Value.Should().Be("o.customer_id");
        memoize.Properties.Single(static x => x.Key == "Cache Evictions").Value.Should().Be("450");
    }

    [Fact]
    public void Nested_objects_and_arrays_are_flattened_to_parent_and_child_and_a_list_of_scalars_is_one_value()
    {
        PlanNode node = QueryPlanReader.Read("""
            [{"Plan": {"Node Type": "Sort", "Sort Key": ["a", "b DESC"],
              "Full-sort Groups": {"Group Count": 3, "Sort Methods Used": ["quicksort", "top-N heapsort"], "Sort Space Memory": {"Average Sort Space Used": 26, "Peak Sort Space Used": 26}},
              "Workers": [{"Worker Number": 0, "Actual Rows": 5}, {"Worker Number": 1, "Actual Rows": 7}]}}]
            """).Plan!.Root;

        node.Properties.Single(static x => x.Key == "Sort Key").Value.Should().Be("a, b DESC");
        node.Properties.Single(static x => x.Key == "Full-sort Groups › Group Count").Value.Should().Be("3");
        node.Properties.Single(static x => x.Key == "Full-sort Groups › Sort Methods Used").Value.Should().Be("quicksort, top-N heapsort");
        node.Properties.Single(static x => x.Key == "Full-sort Groups › Sort Space Memory › Peak Sort Space Used").Value.Should().Be("26");
        node.Properties.Single(static x => x.Key == "Workers[1] › Actual Rows").Value.Should().Be("7");
    }

    [Fact]
    public void A_nodes_keys_are_bounded_and_so_is_each_value()
    {
        string many = string.Join(",", Enumerable.Range(0, QueryPlanReader.MaxProperties + 50).Select(static i => "\"K" + i + "\": " + i));
        string long_ = new('x', QueryPlanReader.MaxPropertyCharacters + 500);

        PlanNode node = QueryPlanReader.Read("[{\"Plan\": {\"Node Type\": \"Result\", " + many + ", \"Filter\": \"" + long_ + "\"}}]").Plan!.Root;

        node.Properties.Should().HaveCount(QueryPlanReader.MaxProperties);

        PlanNode wide = QueryPlanReader.Read("[{\"Plan\": {\"Node Type\": \"Result\", \"Filter\": \"" + long_ + "\"}}]").Plan!.Root;
        wide.Properties.Single(static x => x.Key == "Filter").Value.Should().HaveLength(QueryPlanReader.MaxPropertyCharacters + 1).And.EndWith("…");
    }

    [Fact]
    public void A_masked_read_keeps_no_raw_keys_and_no_settings_because_a_key_the_reader_does_not_model_can_name_what_the_mask_hides()
    {
        string json = """
            [{"Plan": {"Node Type": "Seq Scan", "Schema": "hr", "Relation Name": "vip", "Output": ["hr.vip.secret_column"]},
              "Settings": {"search_path": "hr, public"}, "JIT": {"Functions": 1}}]
            """;

        QueryPlan masked = QueryPlanReader.Read(json, static x => x.Replace("hr", "‹withheld›", StringComparison.Ordinal)).Plan!;

        masked.Root.Properties.Should().BeEmpty();
        masked.Settings.Should().BeEmpty();
        masked.JitProperties.Should().BeEmpty();
        masked.Root.Schema.Should().Be("‹withheld›", "what the reader models is masked, as it always was");

        QueryPlan open = QueryPlanReader.Read(json).Plan!;
        open.Root.Properties.Select(static x => x.Key).Should().Contain("Output");
        open.Settings.Should().ContainSingle();
    }

    [Fact]
    public void The_misestimate_is_the_larger_over_the_smaller_per_loop_and_blank_for_a_node_that_did_not_run()
    {
        PlanNode Node(string json) => QueryPlanReader.Read("[{\"Plan\": " + json + "}]").Plan!.Root;

        Node("""{"Node Type": "Seq Scan", "Plan Rows": 100, "Actual Rows": 1000, "Actual Loops": 1}""").MisestimateRatio.Should().Be(10);
        Node("""{"Node Type": "Seq Scan", "Plan Rows": 1000, "Actual Rows": 10, "Actual Loops": 1}""").MisestimateRatio.Should().Be(100);
        Node("""{"Node Type": "Seq Scan", "Plan Rows": 1, "Actual Rows": 90, "Actual Loops": 1}""").MisestimateRatio.Should().Be(90);
        Node("""{"Node Type": "Seq Scan", "Plan Rows": 5, "Actual Rows": 0, "Actual Loops": 1}""").MisestimateRatio.Should().Be(5, "a zero is never a division by zero");
        Node("""{"Node Type": "Seq Scan", "Plan Rows": 5, "Actual Rows": 0, "Actual Loops": 0}""").MisestimateRatio.Should().BeNull();
        Node("""{"Node Type": "Seq Scan", "Plan Rows": 5}""").MisestimateRatio.Should().BeNull();
        Node("""{"Node Type": "Seq Scan", "Plan Rows": 100, "Actual Rows": 1000, "Actual Loops": 1}""").Underestimated.Should().BeTrue();
        Node("""{"Node Type": "Seq Scan", "Plan Rows": 1000, "Actual Rows": 10, "Actual Loops": 1}""").Underestimated.Should().BeFalse();
    }

    // ---- What each key means -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_key_the_reader_knows_has_a_one_line_explanation()
    {
        PlanKeys.Known.Should().NotBeEmpty().And.OnlyHaveUniqueItems();

        List<string> unexplained = [.. PlanKeys.Known.Where(static x => PlanKeyCatalog.Find(x) is null)];

        unexplained.Should().BeEmpty("a key the reader reads is a key the detail panel explains: {0}", string.Join(", ", unexplained));
    }

    [Fact]
    public void The_known_keys_are_the_constants_the_reader_uses_and_nothing_else()
    {
        string[] constants =
        [
            .. typeof(PlanKeys).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(static x => x.IsLiteral)
                .Select(static x => (string) x.GetRawConstantValue()!)
                .Distinct(StringComparer.Ordinal),
        ];

        PlanKeys.Known.Should().BeEquivalentTo(constants);

        // The reader spells no key itself: every property name it asks for is one of these constants.
        string source = File.ReadAllText(MartenStudio.Tests.Conventions.RepositoryRoot.Combine("src", "PostgresStudio", "Services", "Query", "QueryPlanReader.cs"));

        System.Text.RegularExpressions.Regex.Matches(source, "(?:Text|Number|Flag|Masked|Texts|Long|Sum|TryGetProperty|GetProperty)\\(\\w+, \"", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2))
            .Should().BeEmpty("a key spelled in the reader is a key PlanKeys does not hold, and the catalog test cannot see");
    }

    [Fact]
    public void The_explanations_are_one_sentence_and_in_a_group()
    {
        foreach (string key in PlanKeyCatalog.Keys)
        {
            PlanKeyMeaning meaning = PlanKeyCatalog.Find(key)!;

            meaning.Text.Should().NotBeNullOrWhiteSpace().And.EndWith(".", "{0} is a sentence", key);
            meaning.Text.Should().NotContain("\n");
            meaning.Group.Should().NotBe(PlanKeyGroup.Other, "{0} is explained, so it is somewhere", key);
        }

        PlanKeyCatalog.Title(PlanKeyGroup.SortHashMemoize).Should().Be("Sort, hash and memoize");
    }

    [Fact]
    public void A_nested_key_is_explained_by_its_leaf_and_an_unknown_key_by_nothing()
    {
        PlanKeyCatalog.Find("Workers[0] › Actual Rows").Should().Be(PlanKeyCatalog.Find("Actual Rows"));
        PlanKeyCatalog.Find("Full-sort Groups › Sort Space Memory › Peak Sort Space Used")!.Group.Should().Be(PlanKeyGroup.SortHashMemoize);
        PlanKeyCatalog.Find("Nobody Prints This").Should().BeNull();
    }

    [Fact]
    public void Every_key_in_the_plans_the_studio_is_tested_on_is_explained_or_listed_here()
    {
        // Keys a plan prints that the studio has no sentence for: shown under "Other keys" with none, which is the honest answer.
        HashSet<string> knownGaps = new(StringComparer.Ordinal);

        HashSet<string> printed = new(StringComparer.Ordinal);

        foreach (string json in ExplainFixtures.All.Values.Concat(PlanFixtures.Names.Select(PlanFixtures.Get)))
        {
            using JsonDocument document = JsonDocument.Parse(json);

            foreach (JsonElement statement in document.RootElement.EnumerateArray())
            {
                Collect(statement, printed);
            }
        }

        List<string> unexplained = [.. printed.Where(x => PlanKeyCatalog.Find(x) is null && !knownGaps.Contains(x)).Order(StringComparer.Ordinal)];

        unexplained.Should().BeEmpty("a key the plans print and the catalog does not explain: {0}", string.Join(", ", unexplained));

        static void Collect(JsonElement element, HashSet<string> into)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        into.Add(property.Name);

                        // The names under Settings are the server's setting names, not keys of a plan.
                        if (property.Name != "Settings")
                        {
                            Collect(property.Value, into);
                        }
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        Collect(item, into);
                    }

                    break;
            }
        }
    }
}
