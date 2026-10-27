using JasperFx.Events;

using Marten;
using Marten.Events;
using Marten.Internal;

using MartenStudio.Internal.Sql;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// DL: the two stream reads that replaced Marten's event-store calls - the replay's events, which
/// <c>AggregateStreamAsync</c> fetched through <c>FetchStreamAsync</c>, and a stream's existence, which
/// <c>FetchStreamStateAsync</c> answered. Both of those ensure the event store's storage first (AGENTS.md hard rule 14).
/// </summary>
public class StreamReadQueriesTests
{
    /// <summary>A select list shaped like Marten's: data, type, mt_dotnet_type and bdata first, then the rest.</summary>
    private static readonly string[] SelectFields =
        ["data", "type", "mt_dotnet_type", "bdata", "seq_id", "id", "stream_id", "version", "timestamp", "tenant_id", "is_archived"];

    private static EventTableInfo Table => new()
    {
        Schema = "studio_replay",
        StreamIdentity = StreamIdentity.AsGuid,
        EventColumns =
        [
            "seq_id", "id", "stream_id", "version", "data", "type", "timestamp", "tenant_id", "mt_dotnet_type",
            "is_archived", "is_skipped", "bdata",
        ],
        StreamColumns = ["id", "type", "version", "timestamp", "created", "is_archived", "tenant_id"],
    };

    private static ReplayEventsQuery Query(Guid stream) => new() { SelectFields = SelectFields, StreamId = stream };

    /// <summary>
    /// The select list is Marten's, in Marten's order, because Marten's selector reads the row by ordinal - including
    /// <c>bdata</c>, which every other studio event read refuses to select.
    /// </summary>
    [Fact]
    public void The_replay_selects_exactly_Martens_columns_in_Martens_order()
    {
        using NpgsqlCommand command = StreamReadQueries.BuildReplayEvents(Table, Query(Guid.NewGuid()));

        command.CommandText.Should().StartWith(
            "select e.\"data\", e.\"type\", e.\"mt_dotnet_type\", e.\"bdata\", e.\"seq_id\", e.\"id\", e.\"stream_id\", "
            + "e.\"version\", e.\"timestamp\", e.\"tenant_id\", e.\"is_archived\"\nfrom \"studio_replay\".\"mt_events\" as e\n");
    }

    /// <summary>
    /// <c>EventStatement</c>'s filters for what <c>FetchStreamAsync</c> sets: never archived, the stream, sequence order -
    /// and nothing else when nothing else was asked.
    /// </summary>
    [Fact]
    public void The_replay_filters_as_Martens_stream_fetch_does()
    {
        Guid stream = Guid.NewGuid();

        using NpgsqlCommand command = StreamReadQueries.BuildReplayEvents(Table, Query(stream));

        command.CommandText.Should().EndWith(
            "where e.\"stream_id\" = @stream\n  and e.\"is_archived\" = false\norder by e.\"seq_id\"");
        command.Parameters["stream"].Value.Should().Be(stream);
        command.Parameters["stream"].NpgsqlDbType.Should().Be(NpgsqlDbType.Uuid);
        command.Parameters.Should().HaveCount(1);
    }

    [Fact]
    public void A_version_a_tenant_and_skipping_each_add_Martens_own_predicate()
    {
        ReplayEventsQuery query = Query(Guid.NewGuid()) with { Version = 7, TenantId = "*DEFAULT*", IgnoreSkipped = true };

        using NpgsqlCommand command = StreamReadQueries.BuildReplayEvents(Table, query);

        command.CommandText.Should().Contain("  and e.\"is_skipped\" = false\n");
        command.CommandText.Should().Contain("  and e.\"version\" <= @version\n");
        command.CommandText.Should().Contain("  and e.\"tenant_id\" = @tenant\n");
        command.Parameters["version"].Value.Should().Be(7L);
        command.Parameters["tenant"].Value.Should().Be("*DEFAULT*", "the session's tenant, exactly as EventStatement binds it");
        command.CommandText.Should().NotContain("*DEFAULT*");
    }

    /// <summary>
    /// M1: the stepper's window is bounded at both ends - versions above where it starts, and at most a limit of them -
    /// every bound a parameter, the order still Marten's.
    /// </summary>
    [Fact]
    public void The_stepper_window_reads_above_a_version_and_no_more_than_its_limit()
    {
        ReplayEventsQuery query = Query(Guid.NewGuid()) with { AfterVersion = 40, Limit = 21 };

        using NpgsqlCommand command = StreamReadQueries.BuildReplayEvents(Table, query);

        command.CommandText.Should().Contain("  and e.\"version\" > @after\n");
        command.CommandText.Should().EndWith("order by e.\"seq_id\"\nlimit @limit");
        command.Parameters["after"].Value.Should().Be(40L);
        command.Parameters["limit"].Value.Should().Be(21);
        command.CommandText.Should().NotContain("40").And.NotContain("21");

        EventTableInfo versionless = Table with { EventColumns = [.. Table.EventColumns.Where(static x => x != "version")] };
        var plain = new ReplayEventsQuery { SelectFields = ["data", "type"], StreamId = Guid.NewGuid() };

        StreamReadQueries.MissingColumns(versionless, plain).Should().BeEmpty("the premise: nothing else reads the version");
        StreamReadQueries.MissingColumns(versionless, plain with { AfterVersion = 40 })
            .Should().Equal(["version"], "the bound reads the version, so a table without it is refused before it is asked");
    }

    [Fact]
    public void Without_a_window_the_replay_has_no_limit()
    {
        using NpgsqlCommand command = StreamReadQueries.BuildReplayEvents(Table, Query(Guid.NewGuid()) with { Version = 9 });

        command.CommandText.Should().NotContain("limit").And.NotContain("@after");
    }

    /// <summary>
    /// <c>EventStatement</c> leaves the stream predicate out for an empty key, which reads the whole log. This never does.
    /// </summary>
    [Fact]
    public void The_stream_predicate_is_always_there()
    {
        using NpgsqlCommand command = StreamReadQueries.BuildReplayEvents(
            Table with { StreamIdentity = StreamIdentity.AsString }, new ReplayEventsQuery { SelectFields = SelectFields, StreamId = string.Empty });

        command.CommandText.Should().Contain("where e.\"stream_id\" = @stream");
        command.Parameters["stream"].NpgsqlDbType.Should().Be(NpgsqlDbType.Varchar);
    }

    [Theory]
    [InlineData("d.data")]
    [InlineData("data::text")]
    [InlineData("\"data\"")]
    [InlineData("data, type")]
    public void A_select_field_the_builder_cannot_quote_is_refused(string field)
    {
        Action build = () => StreamReadQueries.BuildReplayEvents(
            Table, new ReplayEventsQuery { SelectFields = ["data", field], StreamId = Guid.NewGuid() });

        build.Should().Throw<InvalidOperationException>().WithMessage("*not a plain column name*");
    }

    /// <summary>
    /// A column the configuration reads and the table lacks is a pending migration - Marten would have run it here - so the
    /// caller is told which, instead of sending a statement that can only fail with 42703.
    /// </summary>
    [Fact]
    public void A_missing_column_is_named_before_anything_is_sent()
    {
        EventTableInfo old = Table with { EventColumns = ["seq_id", "id", "stream_id", "version", "data", "type", "timestamp", "is_archived"] };

        StreamReadQueries.MissingColumns(old, Query(Guid.NewGuid()) with { IgnoreSkipped = true })
            .Should().Equal("mt_dotnet_type", "bdata", "tenant_id", "is_skipped");

        StreamReadQueries.MissingColumns(Table, Query(Guid.NewGuid()) with { IgnoreSkipped = true, Version = 3, TenantId = "a" })
            .Should().BeEmpty();
    }

    /// <summary>
    /// The select list really is Marten's: the fields the replay will ask Marten's own storage for, read off a store with
    /// no database, start with the four ordinals <c>EventDocumentStorage.Resolve</c> reads by position.
    /// </summary>
    [Fact]
    public void Martens_own_select_list_is_plain_column_names_led_by_the_four_positional_ones()
    {
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "studio_replay";
            options.Events.AddEventType<StreamReadHappened>();
        });

        using IQuerySession session = store.QuerySession();

        IEventStorage storage = ((IMartenSession) session).EventStorage();
        string[] fields = storage.SelectFields();

        fields.Take(4).Should().Equal("data", "type", "mt_dotnet_type", "bdata");
        fields.Should().Contain(["seq_id", "id", "stream_id", "version", "timestamp"]);

        Action build = () => StreamReadQueries.BuildReplayEvents(
            Table, new ReplayEventsQuery { SelectFields = fields, StreamId = Guid.NewGuid() }).Dispose();

        build.Should().NotThrow("every field Marten selects is a plain column name the builder can quote");
    }

    [Fact]
    public void Stream_existence_is_one_keyed_row_with_the_stream_lists_tenant_predicate()
    {
        Guid stream = Guid.NewGuid();

        using NpgsqlCommand scoped = StreamReadQueries.BuildStreamExists(Table, stream, "acme");
        using NpgsqlCommand everyone = StreamReadQueries.BuildStreamExists(Table, stream, null);

        scoped.CommandText.Should().Be(
            "select 1\nfrom \"studio_replay\".\"mt_streams\" as s\nwhere s.\"id\" = @id\n"
            + "  and (@tenant is null or s.\"tenant_id\" = @tenant)\nlimit 1");
        scoped.Parameters["tenant"].Value.Should().Be("acme");
        everyone.Parameters["tenant"].Value.Should().Be(DBNull.Value, "no tenant in scope is every tenant, as on the stream page");
        scoped.CommandText.Should().NotContain("is_archived", "an archived stream exists, as Marten's FetchStreamStateAsync said");
    }

    /// <summary>
    /// On Marten's real <c>mt_streams</c> - read off the event store's own feature schema, which builds its tables with
    /// no connection - <c>tenant_id</c> is there whatever the event store's tenancy: the key's lead when conjoined, a
    /// plain column otherwise (<c>StreamsTable</c>, the same at <c>V9.39.0</c> and <c>V9.50.2</c>). So a scope with a tenant
    /// selected filters on it on a single-tenant event store too, exactly as the stream page the link opens does.
    /// </summary>
    [Theory]
    [InlineData(JasperFx.MultiTenancy.TenancyStyle.Single, StreamIdentity.AsString, NpgsqlDbType.Varchar)]
    [InlineData(JasperFx.MultiTenancy.TenancyStyle.Conjoined, StreamIdentity.AsGuid, NpgsqlDbType.Uuid)]
    public void On_Martens_own_streams_table_a_selected_tenant_is_filtered_whatever_the_tenancy(
        JasperFx.MultiTenancy.TenancyStyle tenancy,
        StreamIdentity identity,
        NpgsqlDbType idType)
    {
        IReadOnlyList<string> columns = MartensStreamsColumns(tenancy, identity);

        columns.Should().Contain("tenant_id");

        object id = identity == StreamIdentity.AsString ? "order-1" : Guid.NewGuid();
        EventTableInfo table = Table with { StreamIdentity = identity, StreamColumns = [.. columns] };

        using NpgsqlCommand command = StreamReadQueries.BuildStreamExists(table, id, "acme");

        command.CommandText.Should().Be(
            "select 1\nfrom \"studio_replay\".\"mt_streams\" as s\nwhere s.\"id\" = @id\n"
            + "  and (@tenant is null or s.\"tenant_id\" = @tenant)\nlimit 1");
        command.Parameters["tenant"].Value.Should().Be("acme");
        command.Parameters["id"].NpgsqlDbType.Should().Be(idType);
        command.Parameters["id"].Value.Should().Be(id);
    }

    /// <summary>
    /// A streams table without <c>tenant_id</c> - which Marten never builds; one migrated by hand might be - gets no
    /// tenant predicate rather than a statement that could only fail with 42703.
    /// </summary>
    [Fact]
    public void A_streams_table_without_a_tenant_column_gets_no_tenant_predicate()
    {
        using NpgsqlCommand command = StreamReadQueries.BuildStreamExists(
            Table with { StreamIdentity = StreamIdentity.AsString, StreamColumns = ["id", "type", "version"] }, "order-1", "acme");

        command.CommandText.Should().NotContain("tenant");
        command.Parameters.Should().ContainSingle();
    }

    /// <summary>The columns of <c>mt_streams</c> as Marten's own <c>StreamsTable</c> declares them for this tenancy.</summary>
    private static IReadOnlyList<string> MartensStreamsColumns(JasperFx.MultiTenancy.TenancyStyle tenancy, StreamIdentity identity)
    {
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "studio_replay";
            options.Events.TenancyStyle = tenancy;
            options.Events.StreamIdentity = identity;
            options.Events.AddEventType<StreamReadHappened>();
        });

        var feature = (Weasel.Core.Migrations.IFeatureSchema) ((IDocumentStore) store).Options.Events;
        Weasel.Postgresql.Tables.Table streams = feature.Objects
            .OfType<Weasel.Postgresql.Tables.Table>()
            .Single(static x => x.Identifier.Name == EventTableInfo.StreamsTable);

        return [.. streams.Columns.Select(static x => x.Name)];
    }

    /// <summary>An event type, so the store has an event store.</summary>
    /// <param name="What">Something.</param>
    public sealed record StreamReadHappened(string What);
}
