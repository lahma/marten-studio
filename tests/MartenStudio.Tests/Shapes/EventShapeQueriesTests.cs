using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Tests.Shapes;

/// <summary>The events' shape-check statements (D-SHAPE S2, S3): values are parameters, identifiers are quoted, and every ordering is the sequence's.</summary>
public class EventShapeQueriesTests
{
    private const string Nasty = "order_placed'; drop table x; --";

    private static readonly string[] MartenFields =
        ["data", "type", "mt_dotnet_type", "bdata", "seq_id", "id", "stream_id", "version", "timestamp", "tenant_id", "is_archived"];

    private static EventTableInfo Table(bool tenant = false, bool archived = true, bool skipped = false, bool binary = true)
    {
        List<string> columns = ["seq_id", "id", "stream_id", "version", "data", "type", "timestamp", "mt_dotnet_type"];

        if (binary)
        {
            columns.Add("bdata");
        }

        if (tenant)
        {
            columns.Add("tenant_id");
        }

        if (archived)
        {
            columns.Add("is_archived");
        }

        if (skipped)
        {
            columns.Add("is_skipped");
        }

        return new EventTableInfo { Schema = "My Schema", EventColumns = columns, StreamColumns = ["id", "type", "version"] };
    }

    private static string[] All(EventTableInfo table, EventShapeQueries.EventScope scope)
    {
        using NpgsqlCommand probe = EventShapeQueries.BuildSizeProbe(table, "order_placed", scope, 201, 10);
        using NpgsqlCommand complete = EventShapeQueries.BuildCompleteKeys(table, "order_placed", scope, 200, 10);
        using NpgsqlCommand edges = EventShapeQueries.BuildEdgeKeys(table, "order_placed", scope, 25, 10);
        using NpgsqlCommand spread = EventShapeQueries.BuildSpreadKeys(table, "order_placed", scope, [10, 20], 10);
        using NpgsqlCommand rows = EventShapeQueries.BuildRows(table, "order_placed", scope, MartenFields, false, [1, 2], [null, null], 262144, 10);

        return [probe.CommandText, complete.CommandText, edges.CommandText, spread.CommandText, rows.CommandText];
    }

    [Fact]
    public void No_statement_is_random_or_a_block_sample_and_every_identifier_is_quoted()
    {
        foreach (string sql in All(Table(tenant: true, skipped: true), new EventShapeQueries.EventScope("acme", true)))
        {
            sql.Should().NotContainEquivalentOf("random(").And.NotContainEquivalentOf("tablesample");
            sql.Should().Contain("\"My Schema\".\"mt_events\"");
        }
    }

    [Fact]
    public void Every_value_is_a_parameter_and_none_is_in_the_text()
    {
        using NpgsqlCommand edges = EventShapeQueries.BuildEdgeKeys(
            Table(tenant: true), Nasty, new EventShapeQueries.EventScope(Nasty, false), 25, 10);

        edges.CommandText.Should().NotContain("drop table").And.Contain("@type").And.Contain("@tenant");
        edges.Parameters["type"].Value.Should().Be(Nasty);
        edges.Parameters["tenant"].Value.Should().Be(Nasty);
    }

    [Fact]
    public void Each_scope_predicate_is_present_if_and_only_if_its_fact_is()
    {
        string plain = All(Table(archived: false), new EventShapeQueries.EventScope("acme", true))[0];
        plain.Should().NotContain("tenant_id").And.NotContain("is_archived").And.NotContain("is_skipped", "no such column");

        string tenantless = All(Table(tenant: true), new EventShapeQueries.EventScope(null, false))[0];
        tenantless.Should().NotContain("@tenant", "no tenant in scope reads every tenant of the database");

        string full = All(Table(tenant: true, skipped: true), new EventShapeQueries.EventScope("acme", true))[0];
        full.Should().Contain("e.\"type\" = @type").And.Contain("e.\"tenant_id\" = @tenant")
            .And.Contain("e.\"is_archived\" = false").And.Contain("e.\"is_skipped\" = false");

        string notSkipping = All(Table(tenant: true, skipped: true), new EventShapeQueries.EventScope("acme", false))[0];
        notSkipping.Should().NotContain("is_skipped", "the store does not skip events");
    }

    [Fact]
    public void Every_ordering_is_the_sequence_and_the_edges_are_bounded_both_ways()
    {
        EventTableInfo table = Table(tenant: true);
        EventShapeQueries.EventScope scope = new(null, false);

        using NpgsqlCommand edges = EventShapeQueries.BuildEdgeKeys(table, "t", scope, 25, 10);
        using NpgsqlCommand complete = EventShapeQueries.BuildCompleteKeys(table, "t", scope, 200, 10);
        using NpgsqlCommand spread = EventShapeQueries.BuildSpreadKeys(table, "t", scope, [5], 10);

        edges.CommandText.Should().Contain("order by e.\"seq_id\" asc").And.Contain("order by e.\"seq_id\" desc");
        edges.CommandText.Split("limit @edge").Length.Should().Be(3);
        edges.Parameters["edge"].Value.Should().Be(25);
        complete.CommandText.Should().Contain("order by e.\"seq_id\"").And.Contain("limit @sample");
        spread.CommandText.Should().Contain("unnest(@probes)").And.Contain("e.\"seq_id\" >= p.k").And.Contain("order by e.\"seq_id\"").And.Contain("limit 1");
        spread.Parameters["probes"].Value.Should().BeOfType<long[]>();

        foreach (string sql in All(table, scope))
        {
            sql.Should().NotContain("order by e.\"stream_id\"").And.NotContain("order by e.\"timestamp\"").And.NotContain("order by e.\"id\"");
        }
    }

    [Fact]
    public void The_keys_carry_the_stored_size_with_the_binary_body_only_where_the_table_has_one()
    {
        using NpgsqlCommand withBinary = EventShapeQueries.BuildCompleteKeys(Table(), "t", new EventShapeQueries.EventScope(null, false), 200, 10);
        using NpgsqlCommand without = EventShapeQueries.BuildCompleteKeys(Table(binary: false), "t", new EventShapeQueries.EventScope(null, false), 200, 10);

        withBinary.CommandText.Should().Contain("pg_column_size(e.\"data\")").And.Contain("pg_column_size(e.\"bdata\")");
        without.CommandText.Should().Contain("pg_column_size(e.\"data\")").And.NotContain("bdata");
        withBinary.CommandText.Should().NotContain("\"data\"::text", "the keys phase never reads an event");
    }

    [Fact]
    public void The_rows_start_with_martens_select_list_unaltered_and_end_with_the_studios_one_column()
    {
        using NpgsqlCommand rows = EventShapeQueries.BuildRows(
            Table(tenant: true), "t", new EventShapeQueries.EventScope("acme", false), MartenFields, false, [1, 2], ["acme", "acme"], 262144, 10);

        string select = rows.CommandText[..rows.CommandText.IndexOf("\nfrom", StringComparison.Ordinal)];
        string expected = "select " + string.Join(", ", MartenFields.Select(static x => "e.\"" + x + "\"")) + ", t.j as shape_json";

        select.Should().Be(expected);
        rows.CommandText.Should().Contain("e.\"seq_id\" = any(@seqs)").And.Contain("order by e.\"seq_id\"");
        rows.Parameters["seqs"].Value.Should().BeOfType<long[]>();
    }

    [Fact]
    public void The_rows_have_a_text_size_guard_over_a_lateral_that_renders_the_json_once()
    {
        using NpgsqlCommand rows = EventShapeQueries.BuildRows(
            Table(), "t", new EventShapeQueries.EventScope(null, false), MartenFields, false, [1], [null], 4096, 10);

        rows.CommandText.Should().Contain("cross join lateral (select e.\"data\"::text as j offset 0) as t")
            .And.Contain("octet_length(t.j) <= @maxBytes");
        rows.Parameters["maxBytes"].Value.Should().Be(4096);
        rows.CommandText.Split("\"data\"::text").Length.Should().Be(2, "the jsonb is rendered as text in one place");
    }

    [Fact]
    public void A_select_field_that_is_not_a_plain_column_is_refused()
    {
        FluentActions.Invoking(() => EventShapeQueries.BuildRows(
                Table(), "t", new EventShapeQueries.EventScope(null, false), ["data", "id; drop table x"], false, [1], [null], 262144, 10))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Per_tenant_sequences_with_no_tenant_in_scope_key_a_row_by_tenant_and_sequence_and_otherwise_by_sequence_alone()
    {
        EventTableInfo table = Table(tenant: true);

        using NpgsqlCommand partitionedAll = EventShapeQueries.BuildRows(
            table, "t", new EventShapeQueries.EventScope(null, false), MartenFields, true, [1, 1], ["a", "b"], 262144, 10);
        using NpgsqlCommand partitionedOne = EventShapeQueries.BuildRows(
            table, "t", new EventShapeQueries.EventScope("a", false), MartenFields, true, [1, 2], ["a", "a"], 262144, 10);
        using NpgsqlCommand conjoinedAll = EventShapeQueries.BuildRows(
            table, "t", new EventShapeQueries.EventScope(null, false), MartenFields, false, [1, 2], ["a", "b"], 262144, 10);

        partitionedAll.CommandText.Should().Contain("(e.\"tenant_id\", e.\"seq_id\") in (select * from unnest(@tenants, @seqs))");
        partitionedAll.Parameters["tenants"].Value.Should().BeOfType<string[]>();
        partitionedOne.CommandText.Should().Contain("= any(@seqs)").And.NotContain("unnest").And.Contain("e.\"tenant_id\" = @tenant");
        conjoinedAll.CommandText.Should().Contain("= any(@seqs)").And.NotContain("unnest", "the sequence is global: one event per sequence");
    }

    [Fact]
    public void An_identifier_with_a_quote_in_it_is_refused_and_never_escaped()
    {
        EventTableInfo table = Table() with { Schema = "we\"ird" };

        FluentActions.Invoking(() => EventShapeQueries.BuildSizeProbe(table, "t", new EventShapeQueries.EventScope(null, false), 201, 10))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Parameter_names_are_listed_without_values()
    {
        using NpgsqlCommand probe = EventShapeQueries.BuildSizeProbe(Table(tenant: true), "t", new EventShapeQueries.EventScope("acme", false), 201, 10);

        EventShapeQueries.ParameterNames(probe).Should().BeEquivalentTo("@type", "@tenant", "@probe");
    }
}
