using Marten;
using Marten.Storage;

using MartenStudio.Services;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Statements;

/// <summary>
/// MartenStudio's statement groups (W5-Q1), with no Postgres: Marten's own SQL - as <c>pg_stat_statements</c> recorded it on
/// a live server at the floor, and as Marten's sources write it at <c>V9.39.0</c> and <c>v9.51.0</c> (each shape at the
/// same line at both tags; <see cref="MartenStatementClassifier" /> cites them) - grouped by operation and document type,
/// plain SQL grouped as Postgres Studio groups it, and a hidden type never named.
/// </summary>
public class MartenStatementClassifierTests
{
    private static readonly StudioTable Customer = Document("mt_doc_customer", "Customer");
    private static readonly StudioTable OrderSummary = Document("mt_doc_ordersummary", "OrderSummary");
    private static readonly StudioTable Order = Document("mt_doc_order", "Order");
    private static readonly StudioTable Events = EventTable("mt_events");
    private static readonly StudioTable Streams = EventTable("mt_streams");
    private static readonly StudioTable Progression = EventTable("mt_event_progression");
    private static readonly StudioTable Tags = EventTable("mt_event_tag_region");
    private static readonly StudioTable Audit = new("public", "audit_log", StudioTableKind.Relational, StudioTableOwnership.Owned);

    /// <summary>The judged set: the store's declared tables and one of the host's own - never the hidden type's.</summary>
    private static readonly Set Tables = new([Customer, OrderSummary, Order, Events, Streams, Progression, Tags, Audit]);

    private static StudioTable Document(string table, string type) =>
        new("studio_sample", table, StudioTableKind.DocumentTable, StudioTableOwnership.Owned) { TypeName = type, Alias = type.ToLowerInvariant() };

    private static StudioTable EventTable(string table) => new("studio_sample_events", table, StudioTableKind.EventTable, StudioTableOwnership.Owned);

    private static string Classify(string text) => MartenStatementClassifier.Instance.Classify(text, Tables)!.Label;

    /// <summary>As <c>pg_stat_statements</c> recorded them on PostgreSQL 17 at Marten 9.39.0 (<c>StatementsPageLiveTests</c>, 2026-10-26).</summary>
    [Theory]
    [InlineData("select studio_sample_events.mt_quick_append_events($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)", "event append")]
    [InlineData("insert into studio_sample.mt_doc_customer (id, data, \"mt_version\", \"mt_dotnet_type\", \"mt_last_modified\", \"email\") values ($1, $2, $3, $4, transaction_timestamp(), $5) on conflict (id) do update set data = excluded.data, \"mt_version\" = excluded.\"mt_version\", \"mt_dotnet_type\" = excluded.\"mt_dotnet_type\", \"mt_last_modified\" = excluded.\"mt_last_modified\", \"email\" = excluded.\"email\" returning id", "upsert · Customer")]
    [InlineData("insert into studio_sample.mt_doc_ordersummary (id, data, \"mt_version\", \"mt_dotnet_type\", \"mt_last_modified\") values ($1, $2, CASE WHEN $3 = $11 THEN COALESCE((select version from studio_sample_events.mt_streams where studio_sample_events.mt_streams.id = $4), $12) ELSE $5 END, $6, transaction_timestamp()) on conflict (id) do update set data = excluded.data, mt_version = CASE WHEN $7 = $13 THEN COALESCE((select version from studio_sample_events.mt_streams where studio_sample_events.mt_streams.id = studio_sample.mt_doc_ordersummary.id), studio_sample.mt_doc_ordersummary.mt_version + $14) ELSE $8 END, \"mt_dotnet_type\" = excluded.\"mt_dotnet_type\", \"mt_last_modified\" = excluded.\"mt_last_modified\" where $9 = $15 or COALESCE((select version from studio_sample_events.mt_streams where studio_sample_events.mt_streams.id = studio_sample.mt_doc_ordersummary.id), $16) <= $10 returning mt_version", "upsert · OrderSummary")]
    [InlineData("select data, type, mt_dotnet_type, bdata, seq_id, id, stream_id, version, timestamp, tenant_id, correlation_id, causation_id, headers, is_archived from studio_sample_events.mt_events as d WHERE (d.is_archived = $2 and  d.stream_id = $1) ORDER BY d.seq_id", "event read")]
    [InlineData("select d.id, d.data, d.mt_version from studio_sample.mt_doc_ordersummary as d where id = ANY($1)", "load/query · OrderSummary")]
    [InlineData("select d.data from studio_sample.mt_doc_customer as d where id = $1", "load/query · Customer")]
    [InlineData("select d.data from studio_sample.mt_doc_customer as d where d.data ->> $2 = $1", "load/query · Customer")]
    public void Martens_recorded_statements_are_grouped_by_operation_and_document_type(string text, string group)
    {
        Classify(text).Should().Be(group);
    }

    /// <summary>The shapes Marten's sources write, at <c>V9.39.0</c> and <c>v9.51.0</c>, as <c>pg_stat_statements</c> normalises them.</summary>
    [Theory]
    // DocumentStorageDescriptorBuilder.cs 511-513: an insert.
    [InlineData("insert into studio_sample.mt_doc_customer (id, data, \"mt_version\", \"mt_dotnet_type\") values ($1, $2, $3, $4) on conflict (id) do nothing returning id", "insert · Customer")]
    // 491-494, guarded: T22's optimistic upsert.
    [InlineData("insert into studio_sample.mt_doc_order (id, data, \"mt_version\") values ($1, $2, $3) on conflict (id) do update set data = excluded.data, \"mt_version\" = excluded.\"mt_version\" where studio_sample.mt_doc_order.mt_version = $4 returning mt_version", "upsert · Order")]
    // 611: an update, unguarded and guarded.
    [InlineData("update studio_sample.mt_doc_customer set data = $1, \"mt_dotnet_type\" = $2 where id = $3 returning id", "update · Customer")]
    [InlineData("update studio_sample.mt_doc_order set data = $1, \"mt_version\" = $2 where id = $3 and mt_version = $4 returning mt_version", "update · Order")]
    // HardDelete.cs 14 and SoftDelete.cs 16.
    [InlineData("delete from studio_sample.mt_doc_customer as d where d.id = $1", "delete · Customer")]
    [InlineData("update studio_sample.mt_doc_order as d set mt_deleted = $1, mt_deleted_at = now() where d.id = $2", "delete · Order")]
    [InlineData("update studio_sample.mt_doc_order as d set mt_deleted = True, mt_deleted_at = now() where d.data ->> $1 = $2", "delete · Order")]
    // UnSoftDelete.cs 16: an undelete sets the same column and is no delete, whether its NULL is recorded or normalised.
    [InlineData("update studio_sample.mt_doc_order as d set mt_deleted = $1, mt_deleted_at = NULL where d.id = $2", "undelete · Order")]
    [InlineData("update studio_sample.mt_doc_order as d set mt_deleted = $1, mt_deleted_at = $2 where d.id = $3", "undelete · Order")]
    [InlineData("update studio_sample.mt_doc_order as d set mt_deleted = $1, mt_deleted_at = NULL where d.mt_deleted_at = now()", "undelete · Order")]
    // PostgresEventStoreDialect.cs 293, 325, 347 and 612: a rich append's stream, its strict identity, its version bump, its events.
    [InlineData("insert into studio_sample_events.mt_streams (id, type, version, timestamp, tenant_id) values ($1, $2, $3, now(), $4)", "event append")]
    [InlineData("with new_stream as (insert into studio_sample_events.mt_streams (id, type, version) values ($1, $2, $3) returning id) insert into studio_sample_events.mt_streams_identity (id) select id from new_stream", "event append")]
    [InlineData("update studio_sample_events.mt_streams set version = $1 where id = $2 and version = $3 returning version", "event append")]
    [InlineData("insert into studio_sample_events.mt_events (seq_id, id, stream_id, version, data, type, timestamp, tenant_id, mt_dotnet_type, is_archived) values (nextval($1), $2, $3, $4, $5, $6, $7, $8, $9, $10)", "event append")]
    [InlineData("insert into studio_sample_events.mt_event_tag_region (value, seq_id) values ($1, $2)", "event append")]
    // 771: a stream's version.
    [InlineData("select version from studio_sample_events.mt_streams where id = $1", "event read")]
    // InsertProjectionProgress.cs 38, UpdateProjectionProgress.cs 43 (V9.39.0) / 45 (v9.51.0), ProjectionProgressStatement.cs 48-60.
    [InlineData("insert into studio_sample_events.mt_event_progression (name, last_seq_id) values ($1, $2)", "projection progress")]
    [InlineData("update studio_sample_events.mt_event_progression set last_seq_id = $1, last_updated = transaction_timestamp() where name = $2 and last_seq_id = $3", "projection progress")]
    [InlineData("select name, last_seq_id, mode, rebuild_threshold, assigned_node from studio_sample_events.mt_event_progression", "projection progress")]
    [InlineData("delete from studio_sample_events.mt_event_progression where name = $1", "projection progress")]
    // HighWaterDetector.cs, GapDetector.cs and EventLoader.cs: the daemon's reads of the sequence.
    [InlineData("select coalesce(max(seq_id), $1) from studio_sample_events.mt_events", "daemon")]
    [InlineData("select d.seq_id, d.id, d.stream_id, d.version, d.data, d.type from studio_sample_events.mt_events as d where d.seq_id > $1 and d.seq_id <= $2 order by d.seq_id", "daemon")]
    [InlineData("select last_value from studio_sample_events.mt_events_sequence", "daemon")]
    [InlineData("select p.name, p.last_seq_id, (select max(e.seq_id) from studio_sample_events.mt_events e) from studio_sample_events.mt_event_progression p", "daemon")]
    public void Martens_own_shapes_are_grouped_as_its_operations(string text, string group)
    {
        Classify(text).Should().Be(group);
    }

    [Theory]
    // Marten's migrations are grouped by their verb and table, never as the reads they name tables like.
    [InlineData("create table studio_sample.mt_doc_customer (id uuid not null, data jsonb not null)", "create · studio_sample.mt_doc_customer")]
    [InlineData("alter table studio_sample_events.mt_events add column x int", "alter · studio_sample_events.mt_events")]
    [InlineData("do $$ begin perform 1; end $$", "do · other")]
    // The host's own tables beside the store (issue #8 G1): as Postgres Studio groups them.
    [InlineData("select * from public.audit_log where id = $1", "select · public.audit_log")]
    [InlineData("insert into public.audit_log (id) values ($1)", "insert · public.audit_log")]
    [InlineData("select 1", "select · other")]
    [InlineData("begin", "begin · other")]
    public void What_is_none_of_Martens_shapes_is_grouped_as_Postgres_Studio_groups_it(string text, string group)
    {
        Classify(text).Should().Be(group);
    }

    [Theory]
    [InlineData("insert into studio_sample.mt_doc_statementhidden (id, data) values ($1, $2) on conflict (id) do update set data = excluded.data returning id")]
    [InlineData("select d.data from studio_sample.mt_doc_statementhidden as d where id = $1")]
    [InlineData("select d.data from mt_doc_statementhidden as d where id = $1")]
    [InlineData("select c.data, h.data from studio_sample.mt_doc_customer c join studio_sample.mt_doc_statementhidden h on h.id = c.id")]
    [InlineData("create table studio_sample.mt_doc_statementhidden (id uuid not null)")]
    [InlineData("select d.data from other_store.mt_doc_customer as d where id = $1")]
    public void A_document_table_the_set_does_not_hold_is_a_hidden_type_and_never_named(string text)
    {
        StatementGroup group = MartenStatementClassifier.Instance.Classify(text, Tables)!;

        group.Should().Be(MartenStatementClassifier.Hidden);
        group.Label.Should().Be("a hidden type").And.NotContain("statementhidden");
        group.Table.Should().BeNull();
    }

    [Fact]
    public void A_name_in_a_comment_is_no_table_of_the_statement()
    {
        Classify("/* studio_sample.mt_doc_statementhidden */ select 1").Should().Be("select · other",
            "a comment is free text: a sqlcommenter tag can neither hide a statement nor name its table");
        Classify("/* public.audit_log */ select d.data from studio_sample.mt_doc_customer as d where id = $1").Should().Be("load/query · Customer");
    }

    [Fact]
    public void A_document_group_carries_its_table_for_the_pages_gate()
    {
        StatementGroup group = MartenStatementClassifier.Instance.Classify("select d.data from studio_sample.mt_doc_customer as d where id = $1", Tables)!;

        (group.Key, group.Verb, group.Table).Should().Be(("doc|load/query|studio_sample.mt_doc_customer", "load/query", Customer));
    }

    /// <summary>MartenStudio's database hands out metered connections, and so does the one way its own reads take one.</summary>
    [Fact]
    public void A_Marten_database_hands_out_metered_connections()
    {
        using DocumentStore store = DocumentStore.For("Host=db.example;Port=5433;Database=orders;Username=app;Password=secret");
        MartenStudioDatabase database = MartenStudioDatabase.Of(store.Storage.Database);

        using NpgsqlConnection seam = database.CreateConnection(StudioConnectionUsage.Read);
        using NpgsqlConnection own = MartenStudioDatabase.CreateConnection(store.Storage.Database, ConnectionUsage.Read);
        using NpgsqlConnection marten = store.Storage.Database.CreateConnection();

        StudioConnectionMeter.IsMetered(seam).Should().BeTrue();
        StudioConnectionMeter.IsMetered(own).Should().BeTrue();
        StudioConnectionMeter.IsMetered(marten).Should().BeFalse("a connection Marten hands out for its own sessions is Marten's, and not counted");
    }

    /// <summary>A judged set of stated tables.</summary>
    private sealed record Set(IReadOnlyList<StudioTable> Tables) : IStudioTableSet
    {
        public string? Coverage => null;

        public StudioTable? Find(string schema, string table) =>
            Tables.FirstOrDefault(x => string.Equals(x.Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Table, table, StringComparison.OrdinalIgnoreCase));

        public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table) => IndexOrigin.Host;
    }
}
