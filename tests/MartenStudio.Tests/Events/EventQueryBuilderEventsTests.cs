using JasperFx.Events;

using MartenStudio.Internal.Sql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Events;

/// <summary>
/// What P4 added to <see cref="EventQueryBuilder" />: the feed's multi-type and time-range filters, the
/// skipped switch, the "recently active" ordering and the single-event read.
/// </summary>
/// <remarks>
/// Kept beside the Events pages rather than in the SQL suite that W2 wrote, so the two packets do not
/// both own one file. The rules are the same ones: identifiers quoted, values parameters, never
/// <c>select *</c>, and never <c>bdata</c>.
/// </remarks>
public class EventQueryBuilderEventsTests
{
    private static readonly string[] ThreeTypes = ["A", "B", "C"];

    private static EventTableInfo Full => new()
    {
        Schema = "studio_sql",
        StreamIdentity = StreamIdentity.AsGuid,
        EventColumns =
        [
            "seq_id", "id", "stream_id", "version", "data", "type", "timestamp", "tenant_id", "mt_dotnet_type",
            "is_archived", "is_skipped", "bdata", "correlation_id", "causation_id", "headers", "user_name",
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

    /// <summary>
    /// A multi-select is one array parameter, not a generated <c>in (…)</c> list: one statement, one
    /// plan, however many types are ticked, and no value ever written into the SQL.
    /// </summary>
    [Fact]
    public void The_type_multi_select_is_one_array_parameter()
    {
        using var one = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery { EventTypes = ["A"] });
        using var three = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery { EventTypes = ["A", "B", "C"] });

        one.CommandText.Should().Be(three.CommandText);
        one.CommandText.Should().Contain("and (@types is null or e.\"type\" = any(@types))");
        three.Parameters["types"].Value.Should().BeEquivalentTo(ThreeTypes);
        three.Parameters["types"].NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Text);
    }

    [Fact]
    public void An_empty_type_selection_filters_nothing()
    {
        using var none = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery());
        using var blanks = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery { EventTypes = ["", "   "] });

        none.Parameters["types"].Value.Should().Be(DBNull.Value);
        blanks.Parameters["types"].Value.Should().Be(DBNull.Value);
    }

    [Fact]
    public void The_time_range_is_two_nullable_parameters_and_is_half_open()
    {
        var from = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

        using var command = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery { From = from });

        command.CommandText.Should().Contain("and (@from is null or e.\"timestamp\" >= @from)");
        command.CommandText.Should().Contain("and (@to is null or e.\"timestamp\" < @to)");
        command.Parameters["from"].Value.Should().Be(from);
        command.Parameters["to"].Value.Should().Be(DBNull.Value);
    }

    /// <summary>
    /// The skipped switch exists only where the column does. A store that never enabled event skipping
    /// has no <c>is_skipped</c>, and naming it would make every feed read fail with 42703.
    /// </summary>
    [Fact]
    public void The_skipped_filter_appears_only_when_the_column_does()
    {
        using var full = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery { IncludeSkipped = false });
        using var minimal = EventQueryBuilder.BuildFeed(Minimal, new EventFeedQuery { IncludeSkipped = false });

        full.CommandText.Should().Contain("and (@includeSkipped or e.\"is_skipped\" = false)");
        minimal.CommandText.Should().NotContain("is_skipped");
    }

    [Fact]
    public void Skipped_events_are_included_by_default()
    {
        using var command = EventQueryBuilder.BuildFeed(Full, new EventFeedQuery());

        command.Parameters["includeSkipped"].Value.Should().Be(true);
    }

    [Fact]
    public void The_recently_active_ordering_joins_the_aggregate_and_orders_by_the_last_sequence()
    {
        using var command = EventQueryBuilder.BuildStreamsByRecentActivity(Full, new StreamListQuery());

        command.CommandText.Should().Contain("max(e.\"seq_id\") as last_seq");
        command.CommandText.Should().Contain("group by e.\"stream_id\"");
        command.CommandText.Should().Contain("on a.stream_id = s.\"id\"");
        command.CommandText.Should().Contain("order by a.last_seq desc");
        command.CommandText.Should().NotContain("select *");
    }

    [Fact]
    public void The_recently_active_ordering_keeps_the_same_filters_as_the_keyset_one()
    {
        using var command = EventQueryBuilder.BuildStreamsByRecentActivity(Full, new StreamListQuery
        {
            TypePrefix = "Order",
            TenantId = "acme",
            IncludeArchived = false,
        });

        command.CommandText.Should().Contain("and (@typePrefix is null or s.\"type\" like @typePrefix)");
        command.CommandText.Should().Contain("and (@tenant is null or s.\"tenant_id\" = @tenant)");
        command.CommandText.Should().Contain("and (@includeArchived or s.\"is_archived\" = false)");
        command.Parameters["typePrefix"].Value.Should().Be("Order%");
    }

    [Fact]
    public void The_recently_active_ordering_refuses_an_offset_past_the_cap()
    {
        Action build = () => EventQueryBuilder.BuildStreamsByRecentActivity(
            Full, new StreamListQuery { Offset = DocumentQueryBuilder.MaxOffset + 1 });

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void One_event_by_sequence_selects_the_same_columns_as_the_feed_and_never_the_payload()
    {
        using var command = EventQueryBuilder.BuildEventBySequence(Full, 42);

        command.CommandText.Should().Contain("where e.\"seq_id\" = @seq");
        command.CommandText.Should().Contain("(e.\"bdata\" is not null) as has_binary");
        command.CommandText.Should().NotContain("       e.\"bdata\",");
        command.Parameters["seq"].Value.Should().Be(42L);
        command.Parameters["seq"].NpgsqlDbType.Should().Be(NpgsqlDbType.Bigint);
    }

    /// <summary>
    /// The single-event read is the one the dead-letter screen uses, and a dead letter names its event by
    /// the <em>global</em> sequence - <c>DeadLetterEvent</c> is registered <c>SingleTenanted()</c>, so the
    /// number can perfectly well belong to another tenant. Without the sibling predicate the expansion
    /// rendered that tenant's event body, and the Skip action went on to write to it.
    /// </summary>
    [Fact]
    public void One_event_by_sequence_is_scoped_to_the_tenant_when_the_store_has_one()
    {
        using var scoped = EventQueryBuilder.BuildEventBySequence(Full, 42, "acme");
        using var unscoped = EventQueryBuilder.BuildEventBySequence(Full, 42);

        scoped.CommandText.Should().Contain("and (@tenant is null or e.\"tenant_id\" = @tenant)");
        scoped.Parameters["tenant"].Value.Should().Be("acme");
        scoped.Parameters["tenant"].NpgsqlDbType.Should().Be(NpgsqlDbType.Varchar);

        // One statement, one plan, for both the scoped and the unscoped read - the same guarded-predicate
        // shape as the feed, rather than two command texts.
        unscoped.CommandText.Should().Be(scoped.CommandText);
        unscoped.Parameters["tenant"].Value.Should().Be(DBNull.Value);
    }

    /// <summary>A store that is not conjoined has no column to name, and naming it would be 42703.</summary>
    [Fact]
    public void One_event_by_sequence_names_no_tenant_column_where_there_is_none()
    {
        using var command = EventQueryBuilder.BuildEventBySequence(Minimal, 42, "acme");

        command.CommandText.Should().NotContain("tenant");
        command.Parameters.Should().ContainSingle();
    }

    /// <summary>The dead letter table is a document table Marten puts in the *event* schema.</summary>
    [Fact]
    public void The_dead_letter_count_is_scoped_by_the_schema_it_is_given()
    {
        using var command = EventQueryBuilder.BuildDeadLetterCount("studio_events");

        command.CommandText.Should().Be("select count(*) from \"studio_events\".\"mt_doc_deadletterevent\"");
        command.Parameters.Should().BeEmpty(
            "mt_doc_deadletterevent has no tenant_id column on any store - Marten registers DeadLetterEvent "
            + "SingleTenanted() whatever the event store's tenancy is");
    }
}
