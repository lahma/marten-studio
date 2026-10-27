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

    [Fact]
    public void A_not_null_duplicated_field_is_declared_not_null_again()
    {
        // DocumentMapping.DuplicateField sets NotNull from its argument (V9.39.0:889-895, V9.54.1:935-941): without
        // notNull: true the remedy's line would declare the column nullable (W5-B1 review F9).
        MartenTable readings = Document<HealthReadingDoc>(static o => o.Schema.For<HealthReadingDoc>().Duplicate(x => x.CustomerId, notNull: true));
        string column = MartenDuplicatedFields.Physical(readings.DocumentType!).Single().ColumnName;

        Marten.IndexTheForeignKey(readings, [column], partitioned: false)!.Code
            .Should().EndWith("opts.Schema.For<HealthReadingDoc>().Duplicate(x => x.CustomerId, notNull: true);");
    }

    [Fact]
    public void A_conjoined_key_to_a_conjoined_type_trails_tenant_id_and_is_given_the_per_tenant_index()
    {
        // ForeignKey<TRef> between two conjoined types keys (member, tenant_id) and indexes the member alone (W5-B1 review F1).
        MartenTable readings = Document<HealthReadingDoc>(static o =>
        {
            o.Schema.For<HealthOrderDoc>().MultiTenanted();
            o.Schema.For<HealthReadingDoc>().MultiTenanted().ForeignKey<HealthOrderDoc>(x => x.CustomerId);
        });

        readings.TrailingKeyColumns.Should().Equal("tenant_id");
        global::Marten.Schema.DocumentForeignKey key = readings.Mapping!.ForeignKeys.OfType<global::Marten.Schema.DocumentForeignKey>().Single();
        key.ColumnNames.Should().HaveCount(2).And.EndWith("tenant_id");

        HealthRemedy remedy = Marten.IndexTheForeignKey(readings, key.ColumnNames, partitioned: false)!;
        remedy.Code.Should().EndWith(
            "opts.Schema.For<HealthReadingDoc>().ForeignKey<HealthOrderDoc>(x => x.CustomerId, indexConfiguration: i => i.TenancyScope = TenancyScope.PerTenant);");
        remedy.Explanation.Should().Contain("TenancyScope.PerTenant indexes (" + key.ColumnNames[0] + ", tenant_id)");

        Document<HealthOrderDoc>().TrailingKeyColumns.Should().BeEmpty("a single-tenanted table's keys trail nothing");
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
        configured.Kind.Should().Be(HealthRemedyKind.Configuration);
        configured.Code.Should().Be(
            "// opts.Schema.For<HealthReadingDoc>() declares this table's ranges with ByRange() in its PartitionOn(...): declare the\n"
            + "// next one beside them, then apply the configuration.\n"
            + ".AddRange(\"p20261101\", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero))",
            "X4: the StoreOptions line itself, with the bounds as the values Weasel's AddRange<T> formats");
        configured.Code.Should().NotContain("CREATE TABLE", "an apply would drop a partition the configuration does not declare");
        configured.Explanation.Should().Contain("creates " + fixedRanges.Table + "_p20261101").And.Contain("always has a DEFAULT partition")
            .And.Contain("ByRollingRange(...)");

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

    [Fact]
    public void A_static_ranges_next_bound_is_the_clr_value_AddRange_takes_for_the_key_type()
    {
        // Each the value RangeBoundLiteral writes, as Weasel's FormatSqlValue<T> writes it back (Weasel 9.33.0 and 9.47.0):
        // an integer as itself, a DateTime as '2026-12-01 00:00:00', a DateTimeOffset with its offset.
        double december = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        MartenHealthRemedyProvider.ClrBound("integer", 3_000).Should().Be("3000");
        MartenHealthRemedyProvider.ClrBound("smallint", -5).Should().Be("-5");
        MartenHealthRemedyProvider.ClrBound("bigint", 3_000).Should().Be("3000L");
        MartenHealthRemedyProvider.ClrBound("date", december).Should().Be("new DateTime(2026, 12, 1, 0, 0, 0)");
        MartenHealthRemedyProvider.ClrBound("timestamp without time zone", december + 3_600).Should().Be("new DateTime(2026, 12, 1, 1, 0, 0)");
        MartenHealthRemedyProvider.ClrBound("timestamp with time zone", december).Should().Be("new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)");
        MartenHealthRemedyProvider.ClrBound("text", 1).Should().BeNull("only time and integer bounds are written");
        MartenHealthRemedyProvider.ClrBound("integer", 1.5).Should().BeNull();

        MartenTable numbered = Document<HealthReadingDoc>(static o => o.Schema.For<HealthReadingDoc>()
            .PartitionOn(x => x.Id, x => x.ByRange()));
        Marten.ExtendRangePartitions(numbered, new RangePartitionNext("bigint", 2_000, 3_000, "p2000", HasDefault: true))!.Code
            .Should().EndWith(".AddRange(\"p2000\", 2000L, 3000L)");
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
