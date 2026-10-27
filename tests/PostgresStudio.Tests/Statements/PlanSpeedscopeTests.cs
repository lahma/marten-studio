using System.Text.Json;

using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The speedscope export over <c>analyzed-join.json</c> (see <see cref="PlanAnalysisTests" /> for its numbers): the exact shape
/// of the file, a frame for each node, a sample that ends at its own node, weights that are the tree's own values - and the
/// profiles a plan has no data for left out.
/// </summary>
public class PlanSpeedscopeTests
{
    private const string Exporter = "Postgres Studio 0.9.0";

    private static JsonElement Build(string plan, string name = "select 1")
    {
        QueryPlan read = plan.StartsWith('[')
            ? QueryPlanReader.Read(plan).Plan!
            : PlanFixtures.Plan(plan);

        return JsonDocument.Parse(PlanSpeedscope.Build(read, name, Exporter)).RootElement;
    }

    private static JsonElement Profile(JsonElement file, string name) =>
        file.GetProperty("profiles").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);

    private static double[] Weights(JsonElement profile) => [.. profile.GetProperty("weights").EnumerateArray().Select(static x => x.GetDouble())];

    private static int[][] Samples(JsonElement profile) =>
        [.. profile.GetProperty("samples").EnumerateArray().Select(static x => x.EnumerateArray().Select(static f => f.GetInt32()).ToArray())];

    [Fact]
    public void The_file_has_speedscopes_top_level_shape()
    {
        JsonElement file = Build("analyzed-join", "select o.id from orders o");

        file.EnumerateObject().Select(static x => x.Name).Should().Equal("$schema", "name", "exporter", "activeProfileIndex", "shared", "profiles");
        file.GetProperty("$schema").GetString().Should().Be("https://www.speedscope.app/file-format-schema.json");
        file.GetProperty("name").GetString().Should().Be("select o.id from orders o");
        file.GetProperty("exporter").GetString().Should().Be(Exporter);
        file.GetProperty("activeProfileIndex").GetInt32().Should().Be(0);
        file.GetProperty("shared").EnumerateObject().Select(static x => x.Name).Should().Equal("frames");
    }

    [Fact]
    public void There_is_one_frame_for_each_node_named_for_the_node_as_the_tree_labels_it_and_its_place_in_the_plan()
    {
        QueryPlan plan = PlanFixtures.Plan("analyzed-join");
        JsonElement file = Build("analyzed-join");

        string[] frames = [.. file.GetProperty("shared").GetProperty("frames").EnumerateArray().Select(static x => x.GetProperty("name").GetString()!)];

        frames.Should().HaveCount(plan.NodeCount);
        frames.Should().Equal(
            "Sort [#0]",
            "Hash Join [#1]",
            "Seq Scan on orders o [#2]",
            "Hash [#3]",
            "Seq Scan on customers c [#4]");
        frames.Should().Equal(plan.Nodes.Select(static x => x.Description + " [#" + x.Id + "]"), "the tree's own label, and the node's index in pre-order");
    }

    [Fact]
    public void Two_identical_scans_stay_two_frames()
    {
        JsonElement file = Build("""
            [{"Plan": {"Node Type": "Append", "Total Cost": 3, "Plans": [
              {"Node Type": "Seq Scan", "Relation Name": "t", "Total Cost": 1},
              {"Node Type": "Seq Scan", "Relation Name": "t", "Total Cost": 1}]}}]
            """);

        string[] frames = [.. file.GetProperty("shared").GetProperty("frames").EnumerateArray().Select(static x => x.GetProperty("name").GetString()!)];

        frames.Should().Equal("Append [#0]", "Seq Scan on t [#1]", "Seq Scan on t [#2]");
    }

    [Fact]
    public void The_profiles_are_those_the_plan_has_data_for_in_the_order_they_are_listed()
    {
        // Timed, with buffers that read something and spilled nothing: self time, self cost, shared blocks read.
        Build("analyzed-join").GetProperty("profiles").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).Should()
            .Equal("Self time", "Self cost", "Shared blocks read");

        // Timed, buffers that read nothing from outside the cache, and a spill: no "Shared blocks read" - all zero - but the temp blocks.
        Build(ExplainFixtures.Get("18-sort-spill-analyze")).GetProperty("profiles").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).Should()
            .Equal("Self time", "Self cost", "Temp blocks written");

        // A plain plan: its cost, and nothing it did not do.
        Build(ExplainFixtures.Get("17-nestloop-plain")).GetProperty("profiles").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).Should()
            .Equal("Self cost");
    }

    [Fact]
    public void A_profile_whose_weights_are_all_zero_is_omitted()
    {
        // Buffers were printed, and nothing was read from outside the cache: no "Shared blocks read" to draw.
        JsonElement file = Build("p09-quiet");

        file.GetProperty("profiles").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).Should().Equal("Self time", "Self cost");

        // Not timed (TIMING OFF) and no buffers: just the cost.
        Build("""
            [{"Plan": {"Node Type": "Seq Scan", "Total Cost": 10, "Actual Loops": 1, "Actual Rows": 1}, "Execution Time": 1}]
            """).GetProperty("profiles").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).Should().Equal("Self cost");

        // A plan with no cost at all has no profile to draw.
        Build("""[{"Plan": {"Node Type": "Result"}}]""").GetProperty("profiles").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Each_profile_is_a_sampled_one_that_starts_at_zero_and_ends_at_the_sum_of_its_weights()
    {
        JsonElement file = Build("analyzed-join");

        foreach (JsonElement profile in file.GetProperty("profiles").EnumerateArray())
        {
            profile.GetProperty("type").GetString().Should().Be("sampled");
            profile.GetProperty("startValue").GetInt32().Should().Be(0);
            profile.GetProperty("endValue").GetDouble().Should().BeApproximately(Weights(profile).Sum(), 1e-9);
            profile.GetProperty("samples").GetArrayLength().Should().Be(5, "one sample for each node");
            profile.GetProperty("weights").GetArrayLength().Should().Be(5);
        }

        Profile(file, "Self time").GetProperty("unit").GetString().Should().Be("milliseconds");
        Profile(file, "Self cost").GetProperty("unit").GetString().Should().Be("none");
        Profile(file, "Shared blocks read").GetProperty("unit").GetString().Should().Be("none");
    }

    [Fact]
    public void A_sample_is_the_path_of_frame_indexes_from_the_root_and_ends_at_its_own_node()
    {
        JsonElement file = Build("analyzed-join");
        int[][] samples = Samples(Profile(file, "Self cost"));

        samples.Select(static x => string.Join(',', x)).Should().Equal("0", "0,1", "0,1,2", "0,1,3", "0,1,3,4");

        for (int node = 0; node < samples.Length; node++)
        {
            samples[node][^1].Should().Be(node, "the last frame of a node's sample is the node");
        }
    }

    [Fact]
    public void The_weights_are_the_values_the_tree_shows_for_each_node()
    {
        QueryPlan plan = PlanFixtures.Plan("analyzed-join");
        JsonElement file = Build("analyzed-join");

        // The tree's own time, computed once.
        Weights(Profile(file, "Self time")).Should().Equal(plan.Nodes.Select(static x => x.ExclusiveMs!.Value));
        Weights(Profile(file, "Self time")).Should().Equal(8d, 2d, 30d, 8d, 10d);
        Weights(Profile(file, "Self cost")).Should().Equal(plan.Nodes.Select(static x => x.ExclusiveCost!.Value));
        Weights(Profile(file, "Self cost")).Should().Equal(110d, 400d, 1500d, 0d, 100d);
        // Each node's own reads: its total less its children's.
        Weights(Profile(file, "Shared blocks read")).Should().Equal(0d, 0d, 350d, 0d, 50d);
        Profile(file, "Self time").GetProperty("endValue").GetDouble().Should().BeApproximately(58, 1e-9);
    }

    [Fact]
    public void Temp_blocks_written_are_each_nodes_own_and_a_weight_is_never_negative()
    {
        QueryPlan plan = QueryPlanReader.Read(ExplainFixtures.Get("18-sort-spill-analyze")).Plan!;
        JsonElement file = Build(ExplainFixtures.Get("18-sort-spill-analyze"));

        Weights(Profile(file, "Temp blocks written")).Should().Equal(plan.Nodes.Select(static x => (double) PlanNodeOwn.Of(x)!.TempWritten));
        Weights(Profile(file, "Temp blocks written")).Sum().Should().BeGreaterThan(0);

        foreach (JsonElement profile in file.GetProperty("profiles").EnumerateArray())
        {
            Weights(profile).Should().OnlyContain(static x => x >= 0);
        }

        // A node whose total is below its children's - a nested loop's repeated inner side - is clamped, never negative.
        JsonElement odd = Build("""
            [{"Plan": {"Node Type": "Limit", "Total Cost": 1, "Plans": [{"Node Type": "Seq Scan", "Total Cost": 9}]}}]
            """);
        Weights(Profile(odd, "Self cost")).Should().Equal(0d, 9d);
    }

    [Theory]
    [InlineData("select 1", "select 1")]
    [InlineData("\n\n  select 2  \nfrom t", "select 2")]
    [InlineData("", "Pasted plan")]
    [InlineData("   \n  ", "Pasted plan")]
    [InlineData(null, "Pasted plan")]
    public void A_profile_is_named_for_the_first_line_of_the_statement_or_a_pasted_plan(string? statement, string name) =>
        PlanSpeedscope.NameOf(statement).Should().Be(name);

    [Fact]
    public void A_name_is_cut_at_eighty_characters()
    {
        PlanSpeedscope.NameOf(new string('x', 200)).Should().HaveLength(80);
        PlanSpeedscope.NameOf(new string('x', 80)).Should().HaveLength(80);
    }

    [Fact]
    public void The_exporter_names_the_studio_and_its_version()
    {
        string exporter = PlanSpeedscope.Exporter("Postgres Studio");

        exporter.Should().StartWith("Postgres Studio ").And.MatchRegex(@"^Postgres Studio [0-9]+\.[0-9]+\.[0-9]+", "the version is the package's, cut at the +");
        exporter.Should().NotContain("+");
        PlanSpeedscope.FileName.Should().Be("plan.speedscope.json");
    }
}
