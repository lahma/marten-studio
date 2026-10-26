using MartenStudio.Internal;
using MartenStudio.Services;
using MartenStudio.Services.Health.Checks;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

using Weasel.Core.Partitioning;

using static MartenStudio.Tests.Health.TableCheckBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>A document partitioned on a point in time, with a duplicated key a foreign key could read.</summary>
public sealed class HealthReadingDoc
{
    public Guid Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public Guid CustomerId { get; set; }
}

/// <summary>
/// W5-B1's Marten wording: what T15, T20, T25 and T26 offer for a table a Marten store declares - a line of configuration,
/// a call, a note, or nothing - and T17's count that never names a table.
/// </summary>
public class MartenLintRemediesTests
{
    private static MartenHealthRemedyProvider Marten => MartenHealthRemedyProvider.Instance;

    private static RangePartitionNext NextMonth(bool hasDefault) =>
        new("timestamp with time zone", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), "p20261101", hasDefault);

    // ---- T15 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_DCB_tag_table_gets_no_index_and_the_marten_5694_sentence()
    {
        MartenTable tags = EventTable("mt_event_tag_customer");

        Marten.IndexTheForeignKey(tags, ["seq_id"], partitioned: false).Should().BeNull("its next apply drops an index nobody declares");
        Marten.ForeignKeyIndexNote(tags, ["seq_id"]).Should().Be(MartenHealthRemedyProvider.TagTableNote)
            .And.Contain("marten#5694").And.Contain("idx_mt_event_tag_<suffix>_seq_id").And.Contain("Marten 9.51.0")
            .And.Contain("is not additive");

        Marten.ForeignKeyIndexNote(EventTable("mt_streams"), ["stream_id"]).Should().Be(MartenHealthRemedyProvider.MartenTableNote);
    }

    [Fact]
    public void A_document_tables_key_on_a_duplicated_field_is_indexed_by_declaring_the_field()
    {
        MartenTable readings = Document<HealthReadingDoc>(static o => o.Schema.For<HealthReadingDoc>().Duplicate(x => x.CustomerId));
        string column = MartenDuplicatedFields.Physical(readings.DocumentType!).Single().ColumnName;

        HealthRemedy remedy = Marten.IndexTheForeignKey(readings, [column], partitioned: false)!;

        remedy.Code.Should().Be(
            "// The foreign key reads the duplicated column " + column + "; declaring the field indexes it:\n"
            + "opts.Schema.For<HealthReadingDoc>().Duplicate(x => x.CustomerId);");
        remedy.Explanation.Should().Contain("ForeignKey<TReference>(x => x.CustomerId)").And.Contain("blocks writes");
        Marten.ForeignKeyIndexNote(readings, [column]).Should().BeNull("a document table's remedy is its own line");

        Marten.IndexTheForeignKey(readings, ["customer_id", "id"], partitioned: false).Should().BeNull("a two-column key is no one field's");
        Marten.IndexTheForeignKey(readings, ["not_a_duplicated_column"], partitioned: false).Should().BeNull();
    }

    // ---- T20 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_rolling_range_is_rolled_forward_by_Marten_and_judged_on_one_width()
    {
        MartenTable rolling = Document<HealthReadingDoc>(static o => o.Schema.For<HealthReadingDoc>()
            .PartitionOn(x => x.Timestamp, x => x.ByRollingRange(PartitionPeriod.Month, periodsAhead: 1, periodsBehind: 3)));

        rolling.RangePartitionsManaged.Should().BeTrue("ByRollingRange provisions its periods and always has a DEFAULT partition");

        HealthRemedy remedy = Marten.ExtendRangePartitions(rolling, NextMonth(hasDefault: true))!;
        remedy.Code.Should().Contain("await store.Advanced.RollPartitionsForwardAsync(CancellationToken.None);")
            .And.Contain("on a cadence shorter than one period");
        remedy.Explanation.Should().Contain("drops nothing").And.Contain("DEFAULT");
    }

    [Fact]
    public void A_static_range_is_a_line_of_configuration_and_an_externally_managed_one_the_hosts_SQL()
    {
        MartenTable fixedRanges = Document<HealthReadingDoc>(static o => o.Schema.For<HealthReadingDoc>()
            .PartitionOn(x => x.Timestamp, x => x.ByRange()));

        fixedRanges.RangePartitionsManaged.Should().BeFalse("Marten creates exactly the ranges the configuration declares");
        HealthRemedy configured = Marten.ExtendRangePartitions(fixedRanges, NextMonth(hasDefault: false))!;
        configured.Code.Should().StartWith("// opts.Schema.For<HealthReadingDoc>() partitions this table by fixed ranges (ByRange())")
            .And.Contain(".AddRange(...)").And.Contain("ByRollingRange(...)");
        configured.Code.Should().NotContain("CREATE TABLE", "an apply would drop a partition the configuration does not declare");

        MartenTable external = Document<HealthReadingDoc>(static o => o.Schema.For<HealthReadingDoc>()
            .PartitionOn(x => x.Timestamp, x => x.ByExternallyManagedRangePartitions()));

        external.RangePartitionsManaged.Should().BeFalse("the application manages them");
        HealthRemedy sql = Marten.ExtendRangePartitions(external, NextMonth(hasDefault: false))!;
        sql.Kind.Should().Be(HealthRemedyKind.Migration);
        sql.Code.Should().Be(
            "CREATE TABLE \"" + external.Schema + "\".\"" + external.Table + "_p20261101\" PARTITION OF \"" + external.Schema + "\".\"" + external.Table
            + "\" FOR VALUES FROM ('2026-11-01 00:00:00+00') TO ('2026-12-01 00:00:00+00');");
        sql.Explanation.Should().Contain("ByExternallyManagedRangePartitions()").And.Contain("neither creates nor drops");

        Marten.ExtendRangePartitions(Document<HealthOrderDoc>(), NextMonth(hasDefault: false)).Should().BeNull("an unpartitioned document table has no range");
        Marten.ExtendRangePartitions(EventTable(), NextMonth(hasDefault: false)).Should().BeNull();
    }

    // ---- T25 and T26 ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_declared_tables_bloated_index_or_narrow_key_gets_no_statement()
    {
        MartenTable orders = Document<HealthOrderDoc>();

        Marten.RebuildBloatedIndex(orders, IndexRow(orders, "mt_doc_healthorderdoc_idx_status", "status"), IndexOrigin.Declared)
            .Should().BeNull("REINDEX is not a line of configuration, and an apply keeps a valid index as it is");
        Marten.WidenKey(orders, new HealthIntegerKeyRow
        {
            Schema = orders.Schema,
            Table = orders.Table,
            Column = "id",
            ColumnType = "integer",
            SequenceSchema = orders.Schema,
            Sequence = "orders_id_seq",
            SequenceType = "integer",
            FedBy = "identity",
        }).Should().BeNull("no Marten table qualifies: mt_events.seq_id is bigint, and HiLo is T11's");
        Marten.WidenKey(EventTable(), new HealthIntegerKeyRow
        {
            Schema = "studio_sql_events",
            Table = "mt_events",
            Column = "seq_id",
            ColumnType = "bigint",
            SequenceSchema = "studio_sql_events",
            Sequence = "mt_events_sequence",
            SequenceType = "bigint",
            FedBy = "default",
        }).Should().BeNull();

        MartenTable hosts = new(SqlTestStoreSchema, "orders", PostgresStudio.Services.Sources.StudioTableKind.Relational, PostgresStudio.Services.Sources.StudioTableOwnership.Owned);
        Marten.RebuildBloatedIndex(hosts, IndexRow(hosts, "orders_by_note", "note"), IndexOrigin.Host)!.Code
            .Should().Be("REINDEX INDEX CONCURRENTLY \"studio_sql\".\"orders_by_note\";", "the host's own table beside the store gets Postgres Studio's statement");
    }

    private const string SqlTestStoreSchema = "studio_sql";

    // ---- T17 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Orphaned_document_tables_are_counted_and_sized_and_never_named()
    {
        HealthFinding finding = new OrphanedDocumentTablesCheck()
            .Evaluate(new OrphanedDocumentTablesFacts(2, 20L * 1024 * 1024, 1), Evaluation())
            .Findings.Single();

        finding.Code.Should().Be("T17");
        finding.Severity.Should().Be(HealthSeverity.Info, "an orphaned table costs disk and backups, nothing else");
        finding.Subject.Should().BeNull("a table that may be a type this process has not loaded yet is never named");
        finding.Title.Should().Be("2 tables in the store's schemas are named like document tables no store here declares, " + TableText.Bytes(20L * 1024 * 1024) + " together");
        finding.WhyItMatters.Should().Contain("never drops it").And.Contain("a document type this process has not loaded yet")
            .And.Contain("Schema.For<T>()").And.Contain("Marten will not");
        finding.Evidence.Should().Contain(new HealthEvidence("Tables", "2"));
        finding.Caveat.Should().Contain("which the host may hide");
        finding.Remedy.Should().BeNull("dropping one is a decision only somebody who knows the types can make");

        new OrphanedDocumentTablesCheck().Evaluate(new OrphanedDocumentTablesFacts(1, 8192, 2), Evaluation()).Findings.Single().Title
            .Should().StartWith("1 table in the store's schemas is named like document tables");

        HealthVerdict quiet = new OrphanedDocumentTablesCheck().Evaluate(new OrphanedDocumentTablesFacts(0, 0, 2), Evaluation());
        quiet.Findings.Should().BeEmpty();
        quiet.Note.Should().BeNull();
        new OrphanedDocumentTablesCheck().Evaluate(new OrphanedDocumentTablesFacts(0, 0, 0), Evaluation()).Note
            .Should().Be("The store declares no schema to read.");
    }
}
