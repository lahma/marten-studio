using JasperFx.Events;

using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Integration.Tests.Sql;

/// <summary>
/// The event-store reads against real <c>mt_events</c>/<c>mt_streams</c>-shaped tables, in both column
/// variants: one with every optional column the store can add, one with none of them (U8).
/// </summary>
public class EventQueryBuilderLiveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly Guid StreamOne = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid StreamTwo = new("aaaaaaaa-0000-0000-0000-000000000002");

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, $$"""
            create table "{{Schema}}".mt_events (
                seq_id bigint not null primary key,
                id uuid not null,
                stream_id uuid not null,
                version bigint not null,
                data jsonb not null,
                type varchar not null,
                timestamp timestamptz not null default transaction_timestamp(),
                tenant_id varchar not null default '*DEFAULT*',
                mt_dotnet_type varchar,
                is_archived boolean not null default false,
                is_skipped boolean not null default false,
                bdata bytea,
                correlation_id varchar,
                causation_id varchar,
                headers jsonb,
                user_name varchar
            );

            insert into "{{Schema}}".mt_events
                (seq_id, id, stream_id, version, data, type, timestamp, tenant_id, is_archived, bdata)
            values
                (1, gen_random_uuid(), '{{StreamOne}}', 1, '{"a":1}', 'OrderPlaced',  '2026-01-01T10:00:00Z', 'acme', false, null),
                (2, gen_random_uuid(), '{{StreamOne}}', 2, '{"a":2}', 'OrderShipped', '2026-01-02T10:00:00Z', 'acme', false, '\xdeadbeef'),
                (3, gen_random_uuid(), '{{StreamTwo}}', 1, '{"a":3}', 'OrderPlaced',  '2026-01-03T10:00:00Z', 'other', false, null),
                (4, gen_random_uuid(), '{{StreamTwo}}', 2, '{"a":4}', 'OrderShipped', '2026-01-04T10:00:00Z', 'other', true,  null);

            create table "{{Schema}}".mt_streams (
                id uuid not null primary key,
                type varchar,
                version bigint not null,
                timestamp timestamptz not null default transaction_timestamp(),
                created timestamptz not null default transaction_timestamp(),
                is_archived boolean not null default false,
                tenant_id varchar not null default '*DEFAULT*'
            );

            insert into "{{Schema}}".mt_streams (id, type, version, timestamp, tenant_id, is_archived) values
                ('{{StreamOne}}', 'Order', 2, '2026-01-02T10:00:00Z', 'acme', false),
                ('{{StreamTwo}}', 'OrderDraft', 2, '2026-01-04T10:00:00Z', 'other', true);

            create table "{{Schema}}".mt_doc_deadletterevent (id uuid not null primary key, data jsonb not null);
            insert into "{{Schema}}".mt_doc_deadletterevent values (gen_random_uuid(), '{}');

            drop schema if exists "{{Schema}}_minimal" cascade;
            create schema "{{Schema}}_minimal";
            create table "{{Schema}}_minimal".mt_events (
                seq_id bigint not null primary key,
                id uuid not null,
                stream_id varchar not null,
                version bigint not null,
                data jsonb not null,
                type varchar not null,
                timestamp timestamptz not null default transaction_timestamp()
            );

            insert into "{{Schema}}_minimal".mt_events (seq_id, id, stream_id, version, data, type) values
                (1, gen_random_uuid(), 'order-17', 1, '{"a":1}', 'OrderPlaced');

            create table "{{Schema}}_minimal".mt_streams (
                id varchar not null primary key,
                type varchar,
                version bigint not null,
                timestamp timestamptz not null default transaction_timestamp(),
                created timestamptz not null default transaction_timestamp()
            );

            insert into "{{Schema}}_minimal".mt_streams (id, type, version) values ('order-17', 'Order', 1);
            """);
    }

    [PostgresFact]
    public async Task The_feed_runs_against_a_table_with_every_optional_column()
    {
        var table = await DescribeAsync(Schema, StreamIdentity.AsGuid);

        var rows = await ReadAsync(EventQueryBuilder.BuildFeed(table, new EventFeedQuery { IncludeArchived = true }));

        rows.Should().HaveCount(4);
        rows[0]["seq_id"].Should().Be(4L, "the feed is newest first");
        rows.Should().AllSatisfy(row =>
        {
            row.Should().ContainKey("has_binary");
            row.Should().ContainKey("binary_length");
            row.Should().ContainKey("correlation_id");
            row.Should().NotContainKey("bdata");
        });
    }

    [PostgresFact]
    public async Task The_feed_runs_against_a_table_with_none_of_them()
    {
        var table = await DescribeAsync(Schema + "_minimal", StreamIdentity.AsString);

        var rows = await ReadAsync(EventQueryBuilder.BuildFeed(table, new EventFeedQuery()));

        rows.Should().ContainSingle();
        rows[0].Keys.Should().BeEquivalentTo(["seq_id", "id", "stream_id", "version", "type", "timestamp", "data"]);
    }

    [PostgresFact]
    public async Task The_binary_payload_is_reported_as_a_flag_and_a_length()
    {
        var table = await DescribeAsync(Schema, StreamIdentity.AsGuid);

        var rows = await ReadAsync(EventQueryBuilder.BuildFeed(table, new EventFeedQuery { IncludeArchived = true }));

        var withBinary = rows.Single(x => Equals(x["seq_id"], 2L));

        withBinary["has_binary"].Should().Be(true);
        withBinary["binary_length"].Should().Be(4);
    }

    [PostgresFact]
    public async Task The_feed_pages_on_the_sequence_and_filters_every_way_it_offers()
    {
        var table = await DescribeAsync(Schema, StreamIdentity.AsGuid);

        var firstPage = await ReadAsync(EventQueryBuilder.BuildFeed(
            table, new EventFeedQuery { PageSize = 2, IncludeArchived = true }));

        firstPage.Select(x => x["seq_id"]).Should().Equal(4L, 3L);

        var nextPage = await ReadAsync(EventQueryBuilder.BuildFeed(
            table, new EventFeedQuery { PageSize = 2, IncludeArchived = true, AfterSequence = 3 }));

        nextPage.Select(x => x["seq_id"]).Should().Equal(2L, 1L);

        var archivedHidden = await ReadAsync(EventQueryBuilder.BuildFeed(table, new EventFeedQuery()));

        archivedHidden.Select(x => x["seq_id"]).Should().NotContain(4L);

        var byTenant = await ReadAsync(EventQueryBuilder.BuildFeed(
            table, new EventFeedQuery { TenantId = "acme", IncludeArchived = true }));

        byTenant.Should().HaveCount(2);

        var byType = await ReadAsync(EventQueryBuilder.BuildFeed(
            table, new EventFeedQuery { EventType = "OrderPlaced", IncludeArchived = true }));

        byType.Should().HaveCount(2);

        var byStream = await ReadAsync(EventQueryBuilder.BuildFeed(
            table, new EventFeedQuery { StreamId = StreamOne.ToString("D"), IncludeArchived = true }));

        byStream.Should().HaveCount(2);
    }

    [PostgresFact]
    public async Task One_stream_is_read_in_version_order_from_a_version_the_caller_has()
    {
        var table = await DescribeAsync(Schema, StreamIdentity.AsGuid);

        EventQueryBuilder.TryBuildStreamEvents(table, StreamOne.ToString("D"), 0, 10, "acme", out var command, out _)
            .Should().BeTrue();

        var rows = await ReadAsync(command!);

        rows.Select(x => x["version"]).Should().Equal(1L, 2L);

        EventQueryBuilder.TryBuildStreamEvents(table, StreamOne.ToString("D"), 1, 10, null, out var after, out _)
            .Should().BeTrue();

        (await ReadAsync(after!)).Select(x => x["version"]).Should().Equal(2L);
    }

    [PostgresFact]
    public async Task A_string_identified_store_reads_one_of_its_streams()
    {
        var table = await DescribeAsync(Schema + "_minimal", StreamIdentity.AsString);

        EventQueryBuilder.TryBuildStreamEvents(table, "order-17", 0, 10, null, out var command, out _)
            .Should().BeTrue();

        (await ReadAsync(command!)).Should().ContainSingle();
    }

    [PostgresFact]
    public async Task The_stream_list_runs_in_both_column_variants()
    {
        var full = await DescribeAsync(Schema, StreamIdentity.AsGuid);

        var streams = await ReadAsync(EventQueryBuilder.BuildStreams(full, new StreamListQuery { IncludeArchived = true }));

        streams.Should().HaveCount(2);
        streams[0].Should().ContainKey("tenant_id");
        streams[0]["id"].Should().Be(StreamTwo, "newest activity first");

        var live = await ReadAsync(EventQueryBuilder.BuildStreams(full, new StreamListQuery()));

        live.Should().ContainSingle();

        var byType = await ReadAsync(EventQueryBuilder.BuildStreams(
            full, new StreamListQuery { TypePrefix = "OrderD", IncludeArchived = true }));

        byType.Should().ContainSingle();

        var minimal = await DescribeAsync(Schema + "_minimal", StreamIdentity.AsString);

        var minimalStreams = await ReadAsync(EventQueryBuilder.BuildStreams(minimal, new StreamListQuery()));

        minimalStreams.Should().ContainSingle();
        minimalStreams[0].Keys.Should().BeEquivalentTo(["id", "type", "version", "timestamp", "created"]);
    }

    [PostgresFact]
    public async Task A_keyset_page_of_streams_starts_after_the_cursor()
    {
        var table = await DescribeAsync(Schema, StreamIdentity.AsGuid);

        var cursor = new StreamKeysetCursor(
            new DateTimeOffset(2026, 1, 4, 10, 0, 0, TimeSpan.Zero), StreamTwo.ToString("D"));

        var rows = await ReadAsync(EventQueryBuilder.BuildStreams(
            table, new StreamListQuery { Cursor = cursor, IncludeArchived = true }));

        rows.Should().ContainSingle();
        rows[0]["id"].Should().Be(StreamOne);
    }

    [PostgresFact]
    public async Task The_overview_queries_run()
    {
        var table = await DescribeAsync(Schema, StreamIdentity.AsGuid);

        var active = await ReadAsync(EventQueryBuilder.BuildRecentlyActiveStreams(table, 10));

        active.Should().HaveCount(2);
        active[0]["id"].Should().Be(StreamTwo, "newest activity first");
        active[1]["id"].Should().Be(StreamOne);
        active[0].Keys.Should().Contain("timestamp").And.NotContain("last_seq",
            "the panel reads mt_streams now, not an aggregate of mt_events");

        var counts = await ReadAsync(EventQueryBuilder.BuildEventTypeCounts(table));

        counts.Should().HaveCount(2);
        counts.Should().AllSatisfy(row => row["event_count"].Should().Be(2L));

        await using var connection = await OpenAsync();

        using var highest = EventQueryBuilder.BuildHighestSequence(table);

        highest.Connection = connection;
        (await highest.ExecuteScalarAsync()).Should().Be(4L);

        using var deadLetters = EventQueryBuilder.BuildDeadLetterCount(Schema);

        deadLetters.Connection = connection;
        (await deadLetters.ExecuteScalarAsync()).Should().Be(1L);
    }

    /// <summary>
    /// The overview panel's three aggregates, each scoped to one tenant and each under a budget. Before
    /// this they were the only reads in the studio with neither.
    /// </summary>
    [PostgresFact]
    public async Task The_overview_aggregates_can_be_scoped_to_a_tenant_and_bounded()
    {
        var table = await DescribeAsync(Schema, StreamIdentity.AsGuid);
        var budget = TimeSpan.FromSeconds(10);

        var active = await ReadAsync(EventQueryBuilder.BuildRecentlyActiveStreams(table, 10, "acme", budget));

        active.Should().ContainSingle();
        active[0]["id"].Should().Be(StreamOne);

        var counts = await ReadAsync(EventQueryBuilder.BuildEventTypeCounts(table, "acme", budget));

        counts.Should().HaveCount(2, "acme has one OrderPlaced and one OrderShipped");
        counts.Should().AllSatisfy(row => row["event_count"].Should().Be(1L));

        await using var connection = await OpenAsync();

        using var deadLetters = EventQueryBuilder.BuildDeadLetterCount(Schema, "acme", budget);

        deadLetters.Connection = connection;

        // The dead-letter table in this schema has no tenant_id, which is exactly the shape the predicate
        // is conditional for: asking for a tenant on a store that is not conjoined is the caller's mistake
        // and Postgres names it, rather than the count quietly ignoring the scope.
        var act = async () => await deadLetters.ExecuteScalarAsync();

        await act.Should().ThrowAsync<PostgresException>().Where(x => x.SqlState == "42703");
    }

    /// <summary>
    /// A stream id this store could never hold matches nothing. It used to map onto a null parameter, which
    /// the guarded predicate read as "no filter", so a typo showed every event in the store.
    /// </summary>
    [PostgresFact]
    public async Task A_stream_filter_that_cannot_be_a_guid_returns_no_rows_at_all()
    {
        var table = await DescribeAsync(Schema, StreamIdentity.AsGuid);

        var unfiltered = await ReadAsync(EventQueryBuilder.BuildFeed(
            table, new EventFeedQuery { IncludeArchived = true }));

        unfiltered.Should().HaveCount(4, "the same query with no stream filter sees everything");

        var impossible = await ReadAsync(EventQueryBuilder.BuildFeed(
            table, new EventFeedQuery { StreamId = "not-a-guid", IncludeArchived = true }));

        impossible.Should().BeEmpty();

        var streams = await ReadAsync(EventQueryBuilder.BuildStreams(
            table, new StreamListQuery { StreamId = "not-a-guid", IncludeArchived = true }));

        streams.Should().BeEmpty();

        var realStreams = await ReadAsync(EventQueryBuilder.BuildStreams(
            table, new StreamListQuery { IncludeArchived = true }));

        realStreams.Should().HaveCount(2);
    }

    private async Task<EventTableInfo> DescribeAsync(string schema, StreamIdentity identity)
    {
        await using var connection = await OpenAsync();

        var catalog = new ColumnCatalog();

        return EventTableInfo.FromColumns(
            schema,
            identity,
            await catalog.GetAsync(connection, schema, EventTableInfo.EventsTable),
            await catalog.GetAsync(connection, schema, EventTableInfo.StreamsTable));
    }

    private async Task<List<Dictionary<string, object?>>> ReadAsync(NpgsqlCommand command)
    {
        await using var connection = await OpenAsync();

        using (command)
        {
            command.Connection = connection;

            await using var reader = await command.ExecuteReaderAsync();

            List<Dictionary<string, object?>> rows = [];

            while (await reader.ReadAsync())
            {
                Dictionary<string, object?> row = new(StringComparer.Ordinal);

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
        }
    }
}
