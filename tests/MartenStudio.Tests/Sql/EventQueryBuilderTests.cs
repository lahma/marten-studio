using JasperFx.Events;

using MartenStudio.Internal.Sql;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The event-store reads. The column set of <c>mt_events</c> is conditional (plan §4.4, U8), so every
/// query is asserted twice: against a table that has every optional column, and against one that has none.
/// </summary>
public class EventQueryBuilderTests
{
    private static EventTableInfo Full => new()
    {
        Schema = "studio_sql",
        StreamIdentity = StreamIdentity.AsGuid,
        EventColumns =
        [
            "seq_id", "id", "stream_id", "version", "data", "type", "timestamp", "tenant_id", "mt_dotnet_type",
            "is_archived", "is_skipped", "bdata", "correlation_id", "causation_id", "headers", "user_name", "tags",
        ],
        StreamColumns = ["id", "type", "version", "timestamp", "created", "is_archived", "tenant_id"],
    };

    private static EventTableInfo Minimal => new()
    {
        Schema = "studio_sql",
        StreamIdentity = StreamIdentity.AsString,
        EventColumns = ["seq_id", "id", "stream_id", "version", "data", "type", "timestamp"],
        StreamColumns = ["id", "type", "version", "timestamp", "created"],
    };

    [Fact]
    public void The_feed_selects_the_core_columns_the_optional_ones_and_the_json()
    {
        using var command = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery());

        command.CommandText.Should().Contain("select e.\"seq_id\",");
        command.CommandText.Should().Contain("e.\"stream_id\"");
        command.CommandText.Should().Contain("e.\"mt_dotnet_type\"");
        command.CommandText.Should().Contain("e.\"correlation_id\"");
        command.CommandText.Should().Contain("e.\"data\"::text as data");
        command.CommandText.Should().Contain("from \"studio_sql\".\"mt_events\" as e");
        command.CommandText.Should().NotContain("select *");
    }

    /// <summary>The one column that must never appear in a select list.</summary>
    [Fact]
    public void The_binary_payload_is_reported_but_never_selected()
    {
        using var command = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery());

        command.CommandText.Should().Contain("(e.\"bdata\" is not null) as has_binary");
        command.CommandText.Should().Contain("octet_length(e.\"bdata\") as binary_length");
        command.CommandText.Should().NotContain("       e.\"bdata\",", "the payload itself is never read");
    }

    [Fact]
    public void A_table_without_the_optional_columns_names_none_of_them()
    {
        using var command = EventQueryBuilder.BuildFeed(Minimal, new EventFeedQuery());

        command.CommandText.Should().NotContain("bdata");
        command.CommandText.Should().NotContain("tenant_id");
        command.CommandText.Should().NotContain("is_archived");
        command.CommandText.Should().NotContain("mt_dotnet_type");
        command.CommandText.Should().Contain("e.\"data\"::text as data");
    }

    [Fact]
    public void The_feed_pages_on_the_sequence_with_one_statement_for_every_page()
    {
        using var first = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery());
        using var next = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery { AfterSequence = 500 });

        first.CommandText.Should().Be(next.CommandText, "one statement means one plan, whatever page it is");
        first.CommandText.Should().Contain("where (@afterSeq is null or e.\"seq_id\" < @afterSeq)");
        first.CommandText.Should().Contain("order by e.\"seq_id\" desc");

        first.Parameters["afterSeq"].Value.Should().Be(DBNull.Value);
        next.Parameters["afterSeq"].Value.Should().Be(500L);
    }

    [Fact]
    public void Every_feed_filter_is_a_parameter_that_may_be_null()
    {
        using var command = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery
        {
            TenantId = "acme",
            EventType = "OrderPlaced",
            StreamId = "11111111-1111-1111-1111-111111111111",
            IncludeArchived = true,
        });

        command.CommandText.Should().Contain("and (@tenant is null or e.\"tenant_id\" = @tenant)");
        command.CommandText.Should().Contain("and (@streamId is null or e.\"stream_id\" = @streamId)");
        command.CommandText.Should().Contain("and (@type is null or e.\"type\" = @type)");
        command.CommandText.Should().Contain("and (@includeArchived or e.\"is_archived\" = false)");

        command.Parameters["streamId"].NpgsqlDbType.Should().Be(NpgsqlDbType.Uuid);
        command.Parameters["streamId"].Value.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        command.Parameters["includeArchived"].Value.Should().Be(true);
    }

    /// <summary>
    /// "Not asked" and "asked for something that cannot exist" are different questions. Binding both to a
    /// null parameter made the guarded predicate <c>(@streamId is null or …)</c> answer the first one, so a
    /// typo in the stream box showed the entire feed instead of an empty page.
    /// </summary>
    [Fact]
    public void A_stream_id_filter_that_cannot_be_a_guid_matches_nothing_rather_than_everything()
    {
        using var command = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery { StreamId = "not-a-guid" });

        command.CommandText.Should().Contain("and false");
        command.CommandText.Should().NotContain("@streamId");
        command.Parameters.Should().NotContain(x => x.ParameterName == "streamId");
    }

    [Fact]
    public void A_stream_list_filtered_by_an_impossible_id_matches_nothing_too()
    {
        using var command = EventQueryBuilder.BuildStreams(Full, new StreamListQuery { StreamId = "not-a-guid" });

        command.CommandText.Should().Contain("where false");
        command.Parameters.Should().NotContain(x => x.ParameterName == "id");
    }

    [Fact]
    public void A_string_identified_store_has_no_impossible_stream_ids()
    {
        using var command = EventQueryBuilder.BuildFeed(Minimal, new EventFeedQuery { StreamId = "not-a-guid" });

        command.CommandText.Should().NotContain("and false", "every string is a stream id this store could hold");
        command.Parameters["streamId"].Value.Should().Be("not-a-guid");
    }

    [Fact]
    public void No_stream_filter_at_all_is_still_the_guarded_predicate()
    {
        using var command = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery());

        command.CommandText.Should().Contain("and (@streamId is null or e.\"stream_id\" = @streamId)");
        command.Parameters["streamId"].Value.Should().Be(DBNull.Value);
    }

    [Fact]
    public void A_string_identified_store_binds_its_stream_ids_as_text()
    {
        using var command = EventQueryBuilder.BuildFeed(Minimal, new EventFeedQuery { StreamId = "order-17" });

        command.Parameters["streamId"].NpgsqlDbType.Should().Be(NpgsqlDbType.Varchar);
        command.Parameters["streamId"].Value.Should().Be("order-17");
    }

    [Fact]
    public void The_feed_page_size_is_clamped()
    {
        using var command = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery { PageSize = 100_000 });

        command.Parameters["limit"].Value.Should().Be(EventFeedQuery.MaxPageSize);
    }

    [Fact]
    public void One_stream_is_read_in_version_order_from_a_version_the_caller_already_has()
    {
        var streamId = Guid.NewGuid();

        EventQueryBuilder.TryBuildStreamEvents(Full, streamId.ToString("D"), 10, 50, "acme", out var command, out var error)
            .Should().BeTrue();

        using (command)
        {
            error.Should().BeNull();
            command!.CommandText.Should().Contain("where e.\"stream_id\" = @id");
            command.CommandText.Should().Contain("and e.\"version\" > @afterVersion");
            command.CommandText.Should().Contain("and e.\"tenant_id\" = @tenant");
            command.CommandText.Should().Contain("order by e.\"version\"");
            command.Parameters["id"].Value.Should().Be(streamId);
            command.Parameters["afterVersion"].Value.Should().Be(10L);
            command.Parameters["take"].Value.Should().Be(50);
        }
    }

    [Fact]
    public void A_stream_id_that_does_not_parse_comes_back_as_a_message()
    {
        EventQueryBuilder.TryBuildStreamEvents(Full, "nonsense", 0, 10, null, out var command, out var error)
            .Should().BeFalse();

        command.Should().BeNull();
        error.Should().Contain("not a GUID");
    }

    [Fact]
    public void The_stream_list_selects_only_the_columns_the_table_has()
    {
        using var full = EventQueryBuilder.BuildStreams(Full, new StreamListQuery());

        full.CommandText.Should().Contain("select s.\"id\"");
        full.CommandText.Should().Contain("s.\"is_archived\"");
        full.CommandText.Should().Contain("s.\"tenant_id\"");
        full.CommandText.Should().Contain("from \"studio_sql\".\"mt_streams\" as s");

        using var minimal = EventQueryBuilder.BuildStreams(Minimal, new StreamListQuery());

        minimal.CommandText.Should().NotContain("is_archived");
        minimal.CommandText.Should().NotContain("tenant_id");
    }

    [Fact]
    public void The_stream_list_orders_by_timestamp_with_the_id_as_the_tiebreaker()
    {
        using var command = EventQueryBuilder.BuildStreams(Full, new StreamListQuery());

        command.CommandText.Should().Contain("order by s.\"timestamp\" desc nulls last, s.\"id\"");
        command.CommandText.Should().Contain("limit @limit offset @offset");
    }

    [Fact]
    public void A_stream_type_filter_is_an_escaped_prefix_match()
    {
        using var command = EventQueryBuilder.BuildStreams(Full, new StreamListQuery { TypePrefix = "Order_" });

        command.CommandText.Should().Contain("and (@typePrefix is null or s.\"type\" like @typePrefix)");
        command.Parameters["typePrefix"].Value.Should().Be("Order\\_%");
    }

    [Fact]
    public void A_keyset_page_of_streams_starts_after_the_cursor_and_drops_the_offset()
    {
        var cursor = new StreamKeysetCursor(
            new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero),
            Guid.NewGuid().ToString("D"));

        using var command = EventQueryBuilder.BuildStreams(Full, new StreamListQuery { Cursor = cursor });

        command.CommandText.Should().Contain(
            "and (s.\"timestamp\" < @k or (s.\"timestamp\" = @k and s.\"id\" > @i))");
        command.CommandText.Should().NotContain("offset");
    }

    [Fact]
    public void An_offset_past_the_cap_is_refused()
    {
        var act = () => EventQueryBuilder.BuildStreams(
            Full, new StreamListQuery { Offset = DocumentQueryBuilder.MaxOffset + 1 });

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// The overview panel reads <c>mt_streams</c>, and must not aggregate <c>mt_events</c>: one row per
    /// stream with a <c>limit</c> on it, rather than a scan of every event the store has ever appended,
    /// repeated on the refresh timer.
    /// </summary>
    [Fact]
    public void The_recently_active_streams_query_is_a_top_n_of_the_streams_table()
    {
        using var command = EventQueryBuilder.BuildRecentlyActiveStreams(Full, 10);

        command.CommandText.Should().Be(
            """
            select s."id",
                   s."type",
                   s."version",
                   s."timestamp",
                   s."created",
                   s."is_archived",
                   s."tenant_id"
            from "studio_sql"."mt_streams" as s
            where (@tenant is null or s."tenant_id" = @tenant)
            order by s."timestamp" desc, s."id"
            limit @limit
            """.ReplaceLineEndings("\n"));

        command.CommandText.Should().NotContain("mt_events");
        command.CommandText.Should().NotContain("group by");
        command.Parameters["limit"].Value.Should().Be(10);
        command.Parameters["tenant"].Value.Should().Be(DBNull.Value);
    }

    [Fact]
    public void The_recently_active_streams_query_drops_the_tenant_clause_on_a_single_tenant_store()
    {
        using var command = EventQueryBuilder.BuildRecentlyActiveStreams(Minimal, 10);

        command.CommandText.Should().NotContain("tenant");
        command.CommandText.Should().Contain("order by s.\"timestamp\" desc, s.\"id\"");
    }

    /// <summary>
    /// The dead-letter count takes no tenant, and deliberately: <c>mt_doc_deadletterevent</c> has no
    /// <c>tenant_id</c> column on any store, because Marten registers <c>DeadLetterEvent</c>
    /// <c>SingleTenanted()</c> unconditionally. It keeps the budget, which it does need.
    /// </summary>
    [Fact]
    public void Every_unbounded_aggregate_can_be_scoped_to_a_tenant_and_given_a_budget()
    {
        using var active = EventQueryBuilder.BuildRecentlyActiveStreams(
            Full, 10, "acme", TimeSpan.FromSeconds(4));
        using var counts = EventQueryBuilder.BuildEventTypeCounts(Full, "acme", TimeSpan.FromSeconds(4));
        using var deadLetters = EventQueryBuilder.BuildDeadLetterCount("studio_sql", TimeSpan.FromSeconds(4));

        active.Parameters["tenant"].Value.Should().Be("acme");
        counts.Parameters["tenant"].Value.Should().Be("acme");
        deadLetters.Parameters.Should().BeEmpty("the dead-letter table has no column to scope by");

        foreach (var command in new[] { active, counts, deadLetters })
        {
            command.CommandTimeout.Should().Be(4);
        }
    }

    /// <summary>
    /// Sub-second budgets round up rather than down. <c>CommandTimeout = 0</c> is Npgsql for "wait
    /// forever", which is the opposite of what anybody passing a timeout meant.
    /// </summary>
    [Fact]
    public void A_budget_under_a_second_never_becomes_no_budget()
    {
        using var command = EventQueryBuilder.BuildEventTypeCounts(Full, null, TimeSpan.FromMilliseconds(50));

        command.CommandTimeout.Should().Be(1);
    }

    [Fact]
    public void An_aggregate_with_no_budget_keeps_Npgsqls_own_default()
    {
        using var command = EventQueryBuilder.BuildEventTypeCounts(Full);

        command.CommandTimeout.Should().Be(30, "that is NpgsqlCommand's own default, left alone");
    }

    [Fact]
    public void The_event_type_counts_query_reports_the_sequence_range_of_each_type()
    {
        using var command = EventQueryBuilder.BuildEventTypeCounts(Full);

        command.CommandText.Should().Contain("count(*) as event_count");
        command.CommandText.Should().Contain("min(e.\"seq_id\") as first_seq");
        command.CommandText.Should().Contain("max(e.\"seq_id\") as last_seq");
        command.CommandText.Should().Contain("where (@tenant is null or e.\"tenant_id\" = @tenant)");
        command.CommandText.Should().Contain("group by e.\"type\"");

        using var minimal = EventQueryBuilder.BuildEventTypeCounts(Minimal);

        minimal.CommandText.Should().NotContain("tenant", "the column is not there to filter on");
    }

    /// <summary>
    /// A plain count, with no tenant predicate available to it at all. The builder used to take a tenant
    /// and emit <c>where "tenant_id" = @tenant</c>, which could only ever raise <c>42703</c>: Marten
    /// registers <c>DeadLetterEvent</c> <c>SingleTenanted()</c> whatever the event store's tenancy is, so
    /// the column does not exist on any store. The tenant axis lives on the document's own
    /// <c>TenantId</c> property, which the list filters through LINQ.
    /// </summary>
    [Fact]
    public void The_dead_letter_count_is_a_plain_count_of_Martens_own_table()
    {
        using var command = EventQueryBuilder.BuildDeadLetterCount("studio_sql");

        command.CommandText.Should().Be(
            "select count(*) from \"studio_sql\".\"mt_doc_deadletterevent\"");
        command.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void The_high_water_fallback_is_a_max_of_the_sequence()
    {
        using var command = EventQueryBuilder.BuildHighestSequence(Full);

        command.CommandText.Should().Be(
            "select max(e.\"seq_id\") from \"studio_sql\".\"mt_events\" as e");
    }

    [Fact]
    public void The_table_description_answers_which_optional_columns_exist()
    {
        var info = Full;

        info.HasBinaryData.Should().BeTrue();
        info.HasTenantId.Should().BeTrue();
        info.HasIsSkipped.Should().BeTrue();
        info.HasTags.Should().BeTrue();
        info.QualifiedEvents.Should().Be("\"studio_sql\".\"mt_events\"");
        info.QualifiedStreams.Should().Be("\"studio_sql\".\"mt_streams\"");

        Minimal.HasBinaryData.Should().BeFalse();
        Minimal.HasCorrelationId.Should().BeFalse();
        Minimal.StreamIdDbType.Should().Be(NpgsqlDbType.Varchar);
    }

    [Fact]
    public void The_description_is_built_from_what_the_catalog_read()
    {
        var info = EventTableInfo.FromColumns(
            "events",
            StreamIdentity.AsString,
            new TableColumns("events", "mt_events", [new PostgresColumn("seq_id", "bigint", "int8", false)]),
            new TableColumns("events", "mt_streams", [new PostgresColumn("id", "character varying", "varchar", false)]));

        info.HasEventColumn("seq_id").Should().BeTrue();
        info.HasEventColumn("bdata").Should().BeFalse();
        info.HasStreamColumn("id").Should().BeTrue();
        info.StreamIdentity.Should().Be(StreamIdentity.AsString);
    }

    [Fact]
    public void A_stream_id_is_parsed_against_the_stores_stream_identity()
    {
        Full.ParseStreamId(Guid.Empty.ToString("D")).Value.Should().Be(Guid.Empty);
        Full.ParseStreamId("nope").Success.Should().BeFalse();
        Full.ParseStreamId("  ").Error.Should().Contain("required");
        Minimal.ParseStreamId("order-17").Value.Should().Be("order-17");
    }

    [Fact]
    public void Every_generated_event_query_quotes_its_identifiers()
    {
        List<NpgsqlCommand> commands =
        [
            EventQueryBuilder.BuildFeed(Full, new EventFeedQuery()),
            EventQueryBuilder.BuildStreams(Full, new StreamListQuery()),
            EventQueryBuilder.BuildRecentlyActiveStreams(Full, 5),
            EventQueryBuilder.BuildEventTypeCounts(Full),
            EventQueryBuilder.BuildHighestSequence(Full),
            EventQueryBuilder.BuildDeadLetterCount("studio_sql"),
        ];

        try
        {
            foreach (var command in commands)
            {
                command.CommandText.Should().Contain("\"studio_sql\".");
                command.CommandText.Should().NotContain("select *");
            }
        }
        finally
        {
            foreach (var command in commands)
            {
                command.Dispose();
            }
        }
    }
}
