using System.Text.Json;

using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The traffic profile's plan, as PostgreSQL makes it for the builder's own statement: the newest events read through
/// the primary key, newest first, and never a Seq Scan of <c>mt_events</c> - which is what the earlier
/// <c>seq_id &gt; (select max) - @window</c> form was planned as once archiving in place had lowered the table's
/// correlation (the H6 review, PostgreSQL 17, 3,000,000 rows).
/// </summary>
/// <remarks>
/// The window is the builder's statement with <c>@window</c> bound to <see cref="Window" /> rather than to the real
/// 100,000, over a table a tenth the size the review measured: the ratio the planner weighs is the same, and the
/// suite does not write three million rows to prove it.
/// </remarks>
public class EventTrafficPlanLiveTests(PostgresFixture postgres)
{
    private const int Events = 300_000;

    /// <summary>The window's share of the table the review measured: 100,000 of 3,000,000.</summary>
    private const long Window = 10_000;

    [PostgresFact]
    public async Task On_an_archive_partitioned_store_the_window_is_read_backwards_through_each_partitions_key()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            "health_plan_partitioned",
            store: static options => options.Events.UseArchivedStreamPartitioning = true);

        await EventHealthData.InsertSyntheticAsync(postgres, host.EventSchema, Events, archivedShare: 0.7);
        await EventHealthData.AnalyzeAsync(postgres, host.EventSchema);

        IReadOnlyList<PlanNode> plan = await ExplainAsync(host.EventSchema);

        plan.Should().NotContain(static x => x.Type == "Seq Scan", Describe(plan));
        plan.Where(static x => x.Relation is { } name && name.StartsWith(HealthEventQueries.EventsTable, StringComparison.Ordinal))
            .Should().NotBeEmpty(Describe(plan))
            .And.OnlyContain(static x => x.Type == "Index Scan" && x.Direction == "Backward", Describe(plan));
    }

    [PostgresFact]
    public async Task On_a_plain_table_archived_in_place_the_window_is_still_read_backwards_through_the_key()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_plan_plain");
        string events = SqlIdentifier.Qualify(host.EventSchema, HealthEventQueries.EventsTable);

        await EventHealthData.InsertSyntheticAsync(postgres, host.EventSchema, Events);

        // Archiving in place rewrites every archived event's row, and the new versions land wherever there is room:
        // the heap stops being in seq_id order, which is what made the planner give up on the key.
        await EventHealthData.ExecuteAsync(postgres, $"update {events} set is_archived = true where seq_id % 3 = 0");
        await EventHealthData.AnalyzeAsync(postgres, host.EventSchema);

        double correlation = await CorrelationAsync(host.EventSchema);
        correlation.Should().BeLessThan(0.99, "the precondition: archiving in place moved a third of the rows out of seq_id order");

        // The anti-vacuity half: on this table the form the review rejected - its bound an InitPlan value the planner
        // cannot see - is planned as a Seq Scan, so a plan without one below is the shape's doing, not the data's.
        IReadOnlyList<PlanNode> rejected = await ExplainAsync(
            $"with newest as (select e.seq_id from {events} e order by e.seq_id desc limit 1), "
            + $"win as materialized (select e.seq_id, e.type from {events} e where e.seq_id > (select n.seq_id from newest n) - @window) "
            + "select pg_catalog.count(*) from win",
            [new NpgsqlParameter("window", Window)]);
        rejected.Should().Contain(static x => x.Type == "Seq Scan" && x.Relation == HealthEventQueries.EventsTable, Describe(rejected));

        IReadOnlyList<PlanNode> plan = await ExplainAsync(host.EventSchema);

        plan.Should().NotContain(static x => x.Type == "Seq Scan", Describe(plan));
        plan.Should().ContainSingle(static x => x.Relation == HealthEventQueries.EventsTable, Describe(plan))
            .Which.Should().Match<PlanNode>(static x => x.Type == "Index Scan" && x.Direction == "Backward", Describe(plan));
    }

    /// <summary><c>EXPLAIN (FORMAT JSON)</c> of the builder's statement, its parameters bound as the runner binds them.</summary>
    private Task<IReadOnlyList<PlanNode>> ExplainAsync(string eventSchema)
    {
        HealthQuery query = HealthEventQueries.TrafficProfile(
            new TrafficProfileShape(eventSchema, "tombstone", HasBinaryData: false, HasIsArchived: true, ByTenant: false));

        List<NpgsqlParameter> parameters = [];
        foreach (NpgsqlParameter parameter in query.Parameters)
        {
            NpgsqlParameter bound = parameter.Clone();
            if (bound.ParameterName == "window")
            {
                bound.Value = Window;
            }

            parameters.Add(bound);
        }

        return ExplainAsync(query.Sql, parameters);
    }

    /// <summary><c>EXPLAIN (FORMAT JSON)</c> of a statement, flattened to its nodes.</summary>
    private async Task<IReadOnlyList<PlanNode>> ExplainAsync(string sql, IEnumerable<NpgsqlParameter> parameters)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new("explain (format json) " + sql, connection);
        foreach (NpgsqlParameter parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        string json = (string) (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        using JsonDocument document = JsonDocument.Parse(json);

        List<PlanNode> nodes = [];
        Walk(document.RootElement[0].GetProperty("Plan"), nodes);
        TestContext.Current.TestOutputHelper?.WriteLine(Describe(nodes));
        return nodes;
    }

    private async Task<double> CorrelationAsync(string eventSchema)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(
            "select correlation from pg_catalog.pg_stats where schemaname = @schema and tablename = @table and attname = 'seq_id'",
            connection);
        command.Parameters.AddWithValue("schema", eventSchema);
        command.Parameters.AddWithValue("table", HealthEventQueries.EventsTable);

        return Convert.ToDouble(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Walk(JsonElement node, List<PlanNode> nodes)
    {
        nodes.Add(new PlanNode(
            node.GetProperty("Node Type").GetString()!,
            node.TryGetProperty("Relation Name", out JsonElement relation) ? relation.GetString() : null,
            node.TryGetProperty("Scan Direction", out JsonElement direction) ? direction.GetString() : null));

        if (node.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                Walk(child, nodes);
            }
        }
    }

    private static string Describe(IReadOnlyList<PlanNode> plan) =>
        "the plan was: " + string.Join(" / ", plan.Select(static x => x.Type + (x.Relation is null ? string.Empty : " on " + x.Relation) + (x.Direction is null ? string.Empty : " " + x.Direction)));

    /// <summary>One node of a plan, flattened.</summary>
    private sealed record PlanNode(string Type, string? Relation, string? Direction);
}
