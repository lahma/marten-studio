using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The event checks' statements: what each reads, that every value is a parameter, that the traffic profile is one
/// bounded, ordered read the planner can see the bound of, and that nothing locks what it lists. No database - the live tests run them.
/// </summary>
public class HealthEventQueriesTests
{
    private const string Schema = "studio_events";

    private static readonly string[] ShapeTableNames =
        ["mt_events", "mt_streams", "mt_event_progression", "mt_high_water_skips", "mt_dcb_tag_version"];

    private static readonly string[] EventColumnNames = ["type", "mt_dotnet_type", "is_archived"];

    private static readonly string[] StreamColumnNames = ["version", "is_archived"];

    private static readonly string[] ProgressionNames = ["DailySales:All", "HighWaterMark"];

    private static readonly TrafficProfileShape Plain = new(Schema, "tombstone", HasBinaryData: false, HasIsArchived: true, ByTenant: false);

    public static TheoryData<string> Every()
    {
        TheoryData<string> data = new();

        foreach (string name in (string[]) ["shape", "statistics", "traffic", "traffic-tenants", "skips", "progression", "dcb"])
        {
            data.Add(name);
        }

        return data;
    }

    private static HealthQuery Build(string name) => name switch
    {
        "shape" => HealthEventQueries.EventStoreShape(Schema),
        "statistics" => HealthEventQueries.EventStatistics(Schema),
        "traffic" => HealthEventQueries.TrafficProfile(Plain),
        "traffic-tenants" => HealthEventQueries.TrafficProfile(Plain with { ByTenant = true, HasBinaryData = true }),
        "skips" => HealthEventQueries.HighWaterSkips(Schema),
        "progression" => HealthEventQueries.ProgressionModes(Schema, true, ["DailySales:All"]),
        "dcb" => HealthEventQueries.DcbTagVersion(Schema),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    /// <summary>D27: a list locks nothing it lists. None of these functions takes a lock per object, and none is here.</summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void No_statement_calls_a_function_that_locks_what_it_lists_or_selects_star(string name)
    {
        string sql = Build(name).Sql;

        sql.Should().NotContain("pg_partition_").And.NotContain("pg_sequence_last_value").And.NotContain("pg_relation_size")
            .And.NotContain("pg_total_relation_size").And.NotContain("pg_table_size").And.NotContain("select *");
    }

    /// <summary>A fresh statement every call: an NpgsqlParameter belongs to one command.</summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void Every_call_builds_fresh_parameters(string name)
    {
        HealthQuery first = Build(name);
        HealthQuery second = Build(name);

        first.Parameters.Should().NotIntersectWith(second.Parameters);
    }

    /// <summary>Every parameter the text names is bound, and every bound one is named.</summary>
    [Theory]
    [MemberData(nameof(Every))]
    public void The_parameters_the_text_names_are_the_ones_it_binds(string name)
    {
        HealthQuery query = Build(name);

        HashSet<string> named = [.. System.Text.RegularExpressions.Regex.Matches(query.Sql, @"@[a-z_]+").Select(static x => x.Value)];

        named.Should().BeEquivalentTo(query.ParameterNames);
    }

    [Fact]
    public void The_shape_is_a_catalog_read_with_every_name_a_value()
    {
        HealthQuery query = HealthEventQueries.EventStoreShape(Schema);

        query.Sql.Should().Contain("pg_catalog.pg_class").And.Contain("pg_catalog.pg_attribute").And.Contain("pg_catalog.pg_inherits");
        query.Sql.Should().NotContain(Schema).And.NotContain("mt_events", "names are parameters, never text");
        query.Parameters.Single(static x => x.ParameterName == "tables").Value.Should().BeEquivalentTo(ShapeTableNames);
    }

    [Fact]
    public void The_statistics_read_pg_stats_and_never_return_a_partitions_name()
    {
        HealthQuery query = HealthEventQueries.EventStatistics(Schema);

        query.Sql.Should().Contain("pg_catalog.pg_stats").And.Contain("pg_catalog.pg_inherits").And.Contain("pg_stat_get_last_autoanalyze_time");
        query.Sql.Should().NotContain("from \"" + Schema + "\"", "no event table is read, only the catalog's statistics about them");

        int select = query.Sql.LastIndexOf("select k.root", StringComparison.Ordinal);
        string output = query.Sql[select..query.Sql.IndexOf("from kept k", select, StringComparison.Ordinal)];
        output.Should().NotContain("relname", "a partition's name spells its tenant under per-tenant partitioning");

        query.Parameters.Single(static x => x.ParameterName == "event_columns").Value.Should().BeEquivalentTo(EventColumnNames);
        query.Parameters.Single(static x => x.ParameterName == "stream_columns").Value.Should().BeEquivalentTo(StreamColumnNames);
        query.Parameters.Single(static x => x.ParameterName == "max_relations").Value.Should().Be(HealthEventQueries.MaxStatisticsRelations);
    }

    [Fact]
    public void The_traffic_profile_is_the_newest_hundred_thousand_events_as_an_ordered_limit_the_planner_can_see()
    {
        HealthQuery query = HealthEventQueries.TrafficProfile(Plain);

        query.Sql.Should().Contain("from \"studio_events\".\"mt_events\" e\n   order by e.seq_id desc\n   limit @window\n",
            "an ordered limit is an Index Scan Backward of the primary key, stopped at the bound");
        query.Sql.Should().NotContain("newest").And.NotContain("- @window",
            "a bound computed in an InitPlan is invisible to the planner, which then chose a Seq Scan of the whole table");
        query.Sql.Should().Contain("win as materialized", "every branch reads one window, so the table is read once");
        query.Sql.Split("\"mt_events\"").Length.Should().Be(2, "mt_events is named once, by the window");
        query.Parameters.Single(static x => x.ParameterName == "window").Value.Should().Be(100_000L);
        query.Parameters.Single(static x => x.ParameterName == "tombstone").Value.Should().Be("tombstone");
    }

    [Fact]
    public void The_traffic_profile_groups_a_stream_by_its_id_alone_unless_the_store_is_conjoined()
    {
        string plain = HealthEventQueries.TrafficProfile(Plain).Sql;
        string conjoined = HealthEventQueries.TrafficProfile(Plain with { ByTenant = true }).Sql;

        plain.Should().Contain("(select 'stream', w.stream, null::text,")
            .And.Contain("group by w.stream\n  order by 4 desc, 2\n  limit @max_streams)",
                "rows of one stream that carry different tenant ids are still one stream, and one finding key");
        plain.Should().NotContain("group by w.tenant_id, w.stream");
        conjoined.Should().Contain("(select 'stream', w.stream, w.tenant_id,")
            .And.Contain("group by w.tenant_id, w.stream\n  order by 4 desc, 3, 2\n  limit @max_streams)",
                "on a conjoined store two tenants may each have a stream of the same id");
    }

    [Fact]
    public void The_traffic_profile_computes_its_percentiles_in_the_database_and_ships_no_payload()
    {
        string sql = HealthEventQueries.TrafficProfile(Plain).Sql;

        sql.Should().Contain("pg_catalog.pg_column_size(e.data)").And.Contain("percentile_disc(0.95::float8) within group (order by w.bytes)");
        sql.Should().NotContain("e.data,").And.NotContain("e.data\n", "the payload itself is never selected");
        sql.Should().Contain("where w.type <> @tombstone", "Marten's own tombstone stream is never the hottest one");
    }

    [Fact]
    public void The_traffic_profile_adds_binary_payloads_and_the_tenant_rows_only_when_asked()
    {
        string plain = HealthEventQueries.TrafficProfile(Plain).Sql;
        string full = HealthEventQueries.TrafficProfile(Plain with { HasBinaryData = true, ByTenant = true }).Sql;
        string noArchive = HealthEventQueries.TrafficProfile(Plain with { HasIsArchived = false }).Sql;

        plain.Should().NotContain("bdata").And.NotContain("'tenant'");
        full.Should().Contain("coalesce(pg_catalog.pg_column_size(e.bdata), 0)").And.Contain("(select 'tenant'");
        noArchive.Should().Contain("false as archived").And.NotContain("e.is_archived");
    }

    [Fact]
    public void The_skips_are_the_newest_by_primary_key_with_the_servers_clock()
    {
        HealthQuery query = HealthEventQueries.HighWaterSkips(Schema);

        query.Sql.Should().Contain("from \"studio_events\".\"mt_high_water_skips\" s");
        query.Sql.Should().Contain("order by s.ending_sequence desc").And.Contain("limit @limit");
        query.Sql.Should().Contain("pg_catalog.transaction_timestamp() as server_now");
        query.Sql.Should().Contain("left join lateral", "the clock comes back even when no gap was ever skipped");
        query.Parameters.Single().Value.Should().Be(HealthEventQueries.MaxSkipRows);
    }

    [Fact]
    public void The_progression_read_asks_for_mode_only_where_the_table_has_it_and_always_for_the_mark()
    {
        HealthQuery withMode = HealthEventQueries.ProgressionModes(Schema, true, ["DailySales:All"]);
        HealthQuery withoutMode = HealthEventQueries.ProgressionModes(Schema, false, ["DailySales:All"]);

        withMode.Sql.Should().Contain("p.mode::text as mode");
        withoutMode.Sql.Should().Contain("null::text as mode").And.NotContain("p.mode");
        withMode.Sql.Should().Contain("p.name = any(@names)").And.EndWith("limit 1001");
        withMode.Parameters.Single().Value.Should().BeEquivalentTo(ProgressionNames);
    }

    [Fact]
    public void The_dcb_read_is_the_collectors_counters_and_names_the_table_as_a_value()
    {
        HealthQuery query = HealthEventQueries.DcbTagVersion(Schema);

        query.Sql.Should().Contain("pg_stat_get_tuples_updated").And.Contain("pg_stat_get_tuples_hot_updated").And.Contain("reloptions");
        query.Sql.Should().NotContain("mt_dcb_tag_version");
        query.Parameters.Select(static x => x.Value).Should().BeEquivalentTo(new object[] { Schema, "mt_dcb_tag_version" });
    }

    [Theory]
    [InlineData("bad\"schema")]
    [InlineData("nul\0schema")]
    public void A_schema_that_cannot_be_quoted_is_refused(string schema)
    {
        Action build = () => HealthEventQueries.HighWaterSkips(schema);

        build.Should().Throw<ArgumentException>();
    }
}
