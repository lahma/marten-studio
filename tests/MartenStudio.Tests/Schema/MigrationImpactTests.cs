using MartenStudio.Internal.Sql;
using MartenStudio.Services.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// 1.2-APPLY: the apply section says what a migration will lock and rewrite - the classification of Weasel's statement shapes,
/// the join to what the catalog says about the tables, and the sentences about sessions. Notes, never a refusal.
/// </summary>
/// <remarks>
/// The scripts are the shapes Weasel 9.48's <c>TableDelta.WriteUpdate</c> writes (<c>StringWriterExtensions.WriteDropIndex</c>,
/// <c>TableColumn.AlterColumnTypeSql</c> and <c>DropColumnSql</c>, <c>Table.PrimaryKeyDeclaration</c>, the partition rebuild's
/// <c>create table … as select</c>), copied from its source.
/// </remarks>
public class MigrationImpactTests
{
    [Fact]
    public void A_plain_index_build_on_an_existing_table_takes_a_share_lock_and_rewrites_nothing()
    {
        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(
            "CREATE INDEX mt_doc_customer_idx_name ON studio.mt_doc_customer USING btree ((data ->> 'Name'));");

        TableLockStatement statement = found.Should().ContainSingle().Subject;
        statement.Lock.Should().Be(TableLockMode.Share);
        statement.LockName.Should().Be("SHARE");
        statement.Rewrite.Should().Be(TableRewrite.No);
        statement.IsBlockingIndexBuild.Should().BeTrue();
        statement.BlocksReaders.Should().BeFalse("a SHARE lock lets reads go on");
        (statement.Schema, statement.Name, statement.IsIndex).Should().Be(("studio", "mt_doc_customer", false));
    }

    [Fact]
    public void An_index_on_a_table_the_script_creates_a_concurrent_build_and_an_only_parent_lock_nothing_that_exists()
    {
        const string sql =
            """
            CREATE TABLE studio.mt_doc_new (id uuid not null, data jsonb not null);
            CREATE INDEX mt_doc_new_idx_name ON studio.mt_doc_new USING btree ((data ->> 'Name'));
            CREATE INDEX CONCURRENTLY mt_doc_customer_idx_name ON studio.mt_doc_customer USING btree ((data ->> 'Name'));
            CREATE INDEX idx_mt_events_tenant ON ONLY studio.mt_events USING btree (tenant_id);
            """;

        MigrationRisk.LockStatements(sql).Should().BeEmpty();
    }

    [Fact]
    public void A_tag_table_index_is_told_apart_by_what_waits()
    {
        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(
            "CREATE INDEX idx_mt_event_tag_account_seq_id ON studio.mt_event_tag_account USING btree (seq_id);");

        found.Should().ContainSingle().Which.Effect.Should().Contain("append of an event carrying the tag");
    }

    [Fact]
    public void A_changed_column_type_takes_the_strongest_lock_and_may_rewrite_the_table()
    {
        TableLockStatement statement = MigrationRisk.LockStatements("alter table studio.mt_doc_order alter column total   type numeric;")
            .Should().ContainSingle().Subject;

        statement.Kind.Should().Be(MigrationRisk.ChangesColumnTypeKind);
        statement.Lock.Should().Be(TableLockMode.AccessExclusive);
        statement.BlocksReaders.Should().BeTrue();
        statement.Rewrite.Should().Be(TableRewrite.Maybe);
        statement.RewriteText.Should().Contain("binary-coercible");
    }

    [Fact]
    public void A_dropped_column_a_dropped_primary_key_and_an_added_one_are_all_access_exclusive()
    {
        const string sql =
            """
            alter table studio.mt_doc_customer drop column legacy_code;
            alter table studio.mt_doc_customer drop constraint "pkey_mt_doc_customer_id" CASCADE;
            alter table studio.mt_doc_customer add CONSTRAINT pkey_mt_doc_customer_id PRIMARY KEY (id);
            """;

        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(sql);

        found.Select(static x => x.Kind).Should().Equal(
            MigrationRisk.DropsColumnKind,
            MigrationRisk.DropsConstraintKind,
            MigrationRisk.AddsPrimaryKeyKind);
        found.Should().OnlyContain(static x => x.Lock == TableLockMode.AccessExclusive && x.Rewrite == TableRewrite.No);
        found[1].Effect.Should().Contain("CASCADE");
    }

    [Fact]
    public void A_dropped_index_locks_its_table_unless_it_is_dropped_concurrently_and_names_an_index_not_a_table()
    {
        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(
            "drop index if exists studio.hand_rolled_idx;\ndrop index concurrently if exists studio.\"Other_idx\";");

        found.Should().HaveCount(2);
        found[0].Lock.Should().Be(TableLockMode.AccessExclusive);
        found[0].IsIndex.Should().BeTrue();
        (found[0].Schema, found[0].Name).Should().Be(("studio", "hand_rolled_idx"));
        found[1].Lock.Should().Be(TableLockMode.ShareUpdateExclusive, "a concurrent drop blocks neither reads nor writes");
        found[1].Name.Should().Be("Other_idx", "a quoted name keeps its case");
    }

    [Fact]
    public void The_partition_rebuild_is_about_the_table_before_the_script_recreates_it_and_about_nothing_after()
    {
        const string sql =
            """
            create table studio.mt_events_temp as select * from studio.mt_events;
            drop table studio.mt_events cascade;
            CREATE TABLE studio.mt_events (
              seq_id bigint not null
            );
            CREATE INDEX mt_events_idx ON studio.mt_events USING btree (seq_id);
            insert into studio.mt_events(seq_id) select seq_id from studio.mt_events_temp;
            drop table studio.mt_events_temp cascade;
            """;

        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(sql);

        found.Select(static x => x.Kind).Should().Equal(MigrationRisk.CopiesTableKind, MigrationRisk.DropsTableKind);
        found.Should().OnlyContain(static x => x.Name == "mt_events", "mt_events_temp is the script's own, and so is the recreated table");
        found[0].Lock.Should().Be(TableLockMode.AccessShare);
        found[0].Rewrite.Should().Be(TableRewrite.Yes);
        found[1].Lock.Should().Be(TableLockMode.AccessExclusive);
    }

    [Fact]
    public void Weasels_partition_index_resume_is_not_a_lock_statement()
    {
        const string sql =
            """
            CREATE INDEX idx_mt_events_tenant ON ONLY studio.mt_events USING btree (tenant_id);
            --WEASEL_INDEX_CREATION_BEGIN
            DROP INDEX CONCURRENTLY IF EXISTS studio.idx_mt_events_tenant_acme;
            --WEASEL_INDEX_CREATION_END
            --WEASEL_INDEX_CREATION_BEGIN
            CREATE INDEX CONCURRENTLY idx_mt_events_tenant_acme ON studio.mt_events_acme USING btree (tenant_id);
            --WEASEL_INDEX_CREATION_END
            ALTER INDEX studio.idx_mt_events_tenant ATTACH PARTITION studio.idx_mt_events_tenant_acme;
            """;

        MigrationRisk.LockStatements(sql).Should().BeEmpty();
    }

    [Fact]
    public void Every_destructive_statement_on_a_table_has_a_lock_statement_and_every_blocking_index_note_too()
    {
        const string sql =
            """
            drop index if exists studio.hand_rolled_idx;
            alter table studio.mt_doc_customer alter column total type numeric;
            CREATE INDEX mt_doc_customer_idx_name ON studio.mt_doc_customer USING btree ((data ->> 'Name'));
            alter table studio.mt_doc_customer drop column legacy_code;
            drop function if exists studio.mt_something() cascade;
            """;

        HashSet<string> locked = [.. MigrationRisk.LockStatements(sql).Select(static x => x.Statement)];

        MigrationRisk.Find(sql)
            .Where(static x => x.Kind != "drops a function or sequence")
            .Should().OnlyContain(x => locked.Contains(x.Statement));
        MigrationRisk.Notes(sql).Should().OnlyContain(x => locked.Contains(x.Statement));
    }

    [Fact]
    public void Quoted_and_unqualified_names_are_read_as_postgres_reads_them()
    {
        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(
            "alter table \"Studio\".\"Order\" drop column x;\nalter table loose drop column y;");

        (found[0].Schema, found[0].Name).Should().Be(("Studio", "Order"));
        found[1].Schema.Should().BeNull("an unqualified name would resolve through a search path this read does not know");
        MigrationImpact.RelationsOf(found).Should().Equal(("Studio", "Order"));
    }

    [Fact]
    public void Nothing_to_lock_is_nothing_to_say()
    {
        MigrationRisk.LockStatements(null).Should().BeEmpty();
        MigrationRisk.LockStatements("  ").Should().BeEmpty();
        MigrationRisk.LockStatements("-- drop index studio.x;").Should().BeEmpty();
        MigrationRisk.Advice([]).Should().BeEmpty();
        MigrationPreview.None.Impact.Should().BeSameAs(MigrationImpact.None);
        MigrationImpact.None.HasContent.Should().BeFalse();
    }

    // ------------------------------------------------------------------------------------------------
    // Advice
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_blocking_index_build_is_advised_the_migrators_concurrent_setting_by_its_exact_name()
    {
        IReadOnlyList<string> advice = MigrationRisk.Advice(MigrationRisk.LockStatements(
            "CREATE INDEX mt_doc_customer_idx_name ON studio.mt_doc_customer USING btree ((data ->> 'Name'));"));

        advice.Should().ContainSingle().Which.Should().Contain("opts.Advanced.Migrator.BuildIndexesConcurrentlyOnAlter = true");
    }

    [Fact]
    public void An_access_exclusive_statement_is_advised_a_quiet_time_or_a_lock_timeout_and_a_share_lock_is_not()
    {
        MigrationRisk.Advice(MigrationRisk.LockStatements("alter table studio.t drop column x;"))
            .Should().ContainSingle().Which.Should().Contain("queues every reader and writer").And.Contain("lock_timeout");

        MigrationRisk.Advice(MigrationRisk.LockStatements("alter table studio.t drop column x;\nCREATE INDEX i ON studio.t (a);"))
            .Should().HaveCount(2);
    }

    // ------------------------------------------------------------------------------------------------
    // The join to the catalog, and the sentences
    // ------------------------------------------------------------------------------------------------

    private const string Script =
        """
        drop index if exists studio.hand_rolled_idx;
        alter table studio.mt_doc_customer alter column total type numeric;
        CREATE INDEX mt_doc_order_idx_name ON studio.mt_doc_order USING btree ((data ->> 'Name'));
        alter table studio.gone drop column x;
        """;

    [Fact]
    public void An_index_is_grouped_under_the_table_the_catalog_says_it_is_on_and_a_missing_relation_has_no_figures()
    {
        IReadOnlyList<TableLockStatement> statements = MigrationRisk.LockStatements(Script);
        IReadOnlyList<(string Schema, string Name)> asked = MigrationImpact.RelationsOf(statements);

        asked.Should().Equal(
            ("studio", "hand_rolled_idx"),
            ("studio", "mt_doc_customer"),
            ("studio", "mt_doc_order"),
            ("studio", "gone"));

        RelationFootprintRow[] rows =
        [
            new(0, 11, "studio", "mt_doc_customer", false, 1_200_000, 300_000_000, 380_000_000),
            new(1, 11, "studio", "mt_doc_customer", false, 1_200_000, 300_000_000, 380_000_000),
            new(2, 12, "studio", "mt_doc_order", true, -1, 8192, 16384),
        ];

        MigrationImpact impact = MigrationImpact.Assemble(statements, rows, sessions: null);

        impact.Tables.Select(static x => x.Display).Should().Equal("studio.mt_doc_customer", "studio.mt_doc_order", "studio.gone");

        TableImpact customer = impact.Tables[0];
        customer.Statements.Should().HaveCount(2, "the dropped index and the type change are both about the customer table");
        customer.StrongestLock.Should().Be(TableLockMode.AccessExclusive);
        customer.Rewrite.Should().Be(TableRewrite.Maybe);
        customer.Footprint!.RowsText.Should().Be("~1,200,000 rows");
        customer.Footprint.SizeText.Should().Contain("~").And.Contain("MiB").And.Contain("of heap");
        customer.Sessions.Should().BeNull("sessions were not read");

        impact.Tables[1].Footprint!.RowsText.Should().Contain("not analyzed");
        impact.Tables[2].Footprint.Should().BeNull();
        impact.Advice.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(-1L, 0L, false)]
    [InlineData(0L, 8192L, false)]
    [InlineData(0L, 0L, true)]
    [InlineData(5L, 8192L, true)]
    public void Rows_are_known_when_the_statistics_say_something_about_them(long rows, long heap, bool known)
    {
        new TableFootprint(rows, heap, heap, IsPartitioned: false).RowsKnown.Should().Be(known);
    }

    [Fact]
    public void Sessions_are_counts_and_states_in_a_sentence_and_are_attached_by_table()
    {
        IReadOnlyList<TableLockStatement> statements = MigrationRisk.LockStatements("alter table studio.t drop column x;");
        RelationFootprintRow[] footprints = [new(0, 7, "studio", "t", false, 10, 8192, 16384)];
        TableSessionRow[] sessions =
        [
            new(7, "idle in transaction", Granted: true, 1),
            new(8, "active", Granted: true, 4),
        ];

        MigrationImpact impact = MigrationImpact.Assemble(statements, footprints, sessions);

        TableSessions onTable = impact.Tables.Single().Sessions!;
        onTable.Holding.Should().Be(1);
        onTable.Waiting.Should().Be(0);
        onTable.Summary.Should().Be("1 session holds a lock (1 idle in transaction)");
    }

    [Fact]
    public void Holders_and_waiters_are_told_apart_and_nobody_is_none()
    {
        new TableSessions([new SessionStateCount("active", true, 2), new SessionStateCount("active", false, 1)]).Summary
            .Should().Be("2 sessions hold a lock (2 active); 1 session waits for one (1 active)");

        new TableSessions([]).Summary.Should().Contain("no sessions");
    }

    // ------------------------------------------------------------------------------------------------
    // The statements: nothing that locks, nothing that says who
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_footprint_read_calls_no_size_function_and_locks_nothing_it_reports_on()
    {
        string sql = MigrationImpactQueries.FootprintSql;

        sql.Should().NotContainEquivalentOf("pg_relation_size").And.NotContainEquivalentOf("pg_total_relation_size")
            .And.NotContainEquivalentOf("pg_table_size").And.NotContainEquivalentOf("pg_indexes_size")
            .And.NotContainEquivalentOf("pg_partition_tree").And.NotContainEquivalentOf("pg_get_indexdef")
            .And.NotContainEquivalentOf("regclass", "a cast through regclass opens the relation's name lookup with a lock");
        sql.Should().Contain("relpages").And.Contain("reltuples").And.Contain("pg_inherits");
        sql.Should().Contain("@schemas").And.Contain("@names");
    }

    [Fact]
    public void The_sessions_read_counts_and_selects_no_text_role_application_or_address()
    {
        string sql = MigrationImpactQueries.SessionsSql;

        sql.Should().NotContainEquivalentOf("query").And.NotContainEquivalentOf("usename").And.NotContainEquivalentOf("application_name")
            .And.NotContainEquivalentOf("client_addr").And.NotContainEquivalentOf("client_hostname").And.NotContainEquivalentOf("backend_type");
        sql.Should().Contain("count(distinct").And.Contain("pg_backend_pid()").And.Contain("@tables");
    }
}
