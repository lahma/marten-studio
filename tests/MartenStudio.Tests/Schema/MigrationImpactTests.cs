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
    // 1.2-APPLY-FIX: several statements on one line, and the shapes beyond the destructive ones
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Marten V9.57.0's <c>DuplicatedFieldColumn.AddColumnSql</c>: Weasel's <c>alter table … add column …;</c> with the backfilling
    /// <c>update</c> appended on the same line, which Weasel sends as one command.
    /// </summary>
    private const string AddedDuplicatedField =
        "alter table studio.mt_doc_customer add column email varchar NULL;update studio.mt_doc_customer set \"email\" = CAST(d.data ->> 'Email' as varchar);";

    /// <summary>Marten V9.57.0's <c>DuplicatedFieldColumn.AlterColumnTypeSql</c>: drop, add, fill, on one line.</summary>
    private const string ChangedDuplicatedField =
        "alter table studio.mt_doc_customer drop column email;alter table studio.mt_doc_customer add column email varchar NULL;update studio.mt_doc_customer set \"email\" = CAST(d.data ->> 'Email' as varchar);";

    [Fact]
    public void An_added_duplicated_field_is_listed_and_holds_the_lock_through_the_backfill()
    {
        TableLockStatement statement = MigrationRisk.LockStatements(AddedDuplicatedField).Should().ContainSingle().Subject;

        statement.Kind.Should().Be(MigrationRisk.AddsDuplicatedColumnKind);
        statement.Lock.Should().Be(TableLockMode.AccessExclusive);
        statement.Rewrite.Should().Be(TableRewrite.Yes);
        statement.Effect.Should().Be("updates every row to fill the duplicated column while it holds the lock");
        (statement.Schema, statement.Name).Should().Be(("studio", "mt_doc_customer"));
    }

    [Fact]
    public void A_changed_duplicated_field_is_a_type_change_that_rewrites_every_row_and_is_one_statement()
    {
        TableLockStatement statement = MigrationRisk.LockStatements(ChangedDuplicatedField).Should().ContainSingle().Subject;

        statement.Kind.Should().Be(MigrationRisk.ChangesColumnTypeKind);
        statement.Lock.Should().Be(TableLockMode.AccessExclusive);
        statement.Rewrite.Should().Be(TableRewrite.Yes, "the same command updates every row while it holds the lock");
        statement.Statement.Should().Contain("update studio.mt_doc_customer");
        MigrationRisk.Find(ChangedDuplicatedField).Should().ContainSingle("Find still reports the drop on this line");
    }

    [Fact]
    public void A_standalone_update_is_never_a_lock_statement_and_a_dropped_column_is_not_a_changed_one()
    {
        MigrationRisk.LockStatements("update studio.mt_doc_customer set \"email\" = CAST(d.data ->> 'Email' as varchar);").Should().BeEmpty();
        MigrationRisk.LockStatements("    UPDATE studio.mt_doc_customer SET x = 1;").Should().BeEmpty();

        MigrationRisk.LockStatements("alter table studio.mt_doc_customer drop column email;").Should().ContainSingle()
            .Which.Kind.Should().Be(MigrationRisk.DropsColumnKind);

        // An update of another table does not backfill this one.
        MigrationRisk.LockStatements("alter table studio.a add column c int NULL;update studio.b set c = 1;").Should().ContainSingle()
            .Which.Kind.Should().Be(MigrationRisk.AltersTableKind);
    }

    [Fact]
    public void Semicolons_inside_quotes_and_dollar_quotes_do_not_split_a_line()
    {
        MigrationRisk.SplitStatements("a;b;;  c ;").Should().Equal("a", "b", "c");
        MigrationRisk.SplitStatements("update t set x = 'a;b';select \"c;d\";select $$e;f$$;select $tag$g;h$tag$")
            .Should().Equal("update t set x = 'a;b'", "select \"c;d\"", "select $$e;f$$", "select $tag$g;h$tag$");
        MigrationRisk.SplitStatements("select $1;select 'it''s;ok'").Should().Equal("select $1", "select 'it''s;ok'");
    }

    [Fact]
    public void A_column_added_with_a_volatile_default_rewrites_the_table_and_a_plain_add_is_the_catch_all()
    {
        // Marten's VersionColumn: DefaultExpression = "(md5(random()::text || clock_timestamp()::text)::uuid)".
        TableLockStatement version = MigrationRisk.LockStatements(
                "alter table studio.mt_doc_customer add column mt_version uuid NOT NULL default (md5(random()::text || clock_timestamp()::text)::uuid);")
            .Should().ContainSingle().Subject;

        version.Kind.Should().Be(MigrationRisk.AddsVolatileColumnKind);
        version.Lock.Should().Be(TableLockMode.AccessExclusive);
        version.Rewrite.Should().Be(TableRewrite.Yes);

        TableLockStatement plain = MigrationRisk.LockStatements("alter table studio.mt_doc_customer add column note text NULL;")
            .Should().ContainSingle().Subject;

        plain.Kind.Should().Be(MigrationRisk.AltersTableKind);
        plain.Rewrite.Should().Be(TableRewrite.Unknown);
        plain.Lock.Should().Be(TableLockMode.AccessExclusive);
        plain.RewriteText.Should().Contain("does not judge");
    }

    [Fact]
    public void Set_not_null_is_a_full_scan_under_access_exclusive_and_set_default_is_the_catch_all()
    {
        // Weasel's TableColumn.WriteDriftCorrections.
        TableLockStatement notNull = MigrationRisk.LockStatements("alter table studio.mt_doc_customer alter column email set not null;")
            .Should().ContainSingle().Subject;

        notNull.Kind.Should().Be(MigrationRisk.SetsNotNullKind);
        notNull.Lock.Should().Be(TableLockMode.AccessExclusive);
        notNull.Effect.Should().Contain("scans the whole table");

        MigrationRisk.LockStatements("alter table studio.mt_doc_customer alter column email set default 'x';").Should().ContainSingle()
            .Which.Kind.Should().Be(MigrationRisk.AltersTableKind);
        MigrationRisk.LockStatements("alter table studio.mt_doc_customer alter column email drop not null;").Should().ContainSingle()
            .Which.Kind.Should().Be(MigrationRisk.AltersTableKind);
    }

    [Fact]
    public void An_added_foreign_key_over_three_lines_takes_share_row_exclusive_on_both_tables()
    {
        // Weasel 9.48.0 ForeignKey.WriteAddStatement: three WriteLine/Write calls, then ";" and a newline.
        const string sql =
            """
            ALTER TABLE studio.mt_event_tag_account
            ADD CONSTRAINT fk_tag_stream FOREIGN KEY(stream_id)
            REFERENCES studio.mt_streams(id) ON DELETE CASCADE;
            """;

        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(sql);

        found.Select(static x => x.Kind).Should().Equal(MigrationRisk.AddsForeignKeyKind, MigrationRisk.ReferencedByForeignKeyKind);
        found.Should().OnlyContain(static x => x.Lock == TableLockMode.ShareRowExclusive && x.Rewrite == TableRewrite.No);
        found.Select(static x => x.Name).Should().Equal("mt_event_tag_account", "mt_streams");
        found[0].LockName.Should().Be("SHARE ROW EXCLUSIVE");
        MigrationRisk.Advice(found).Should().BeEmpty("a SHARE ROW EXCLUSIVE lock lets reads go on");
    }

    [Fact]
    public void A_partition_created_on_an_existing_parent_takes_access_exclusive_on_the_parent_and_on_a_new_one_nothing()
    {
        // Weasel 9.48.0 ListPartition, RangePartition and HashPartition .WriteCreateStatement.
        const string sql =
            """
            CREATE TABLE IF NOT EXISTS studio.mt_doc_invoice_acme partition of studio.mt_doc_invoice for values in ('acme');
            CREATE TABLE IF NOT EXISTS studio.mt_events_2026 PARTITION OF studio.mt_events FOR VALUES FROM ('2026-01-01') TO ('2027-01-01');
            create table if not exists studio.mt_streams_0 partition of studio.mt_streams for values with (modulus 3, remainder 0);
            CREATE TABLE studio.mt_new (id uuid not null) partition by list (tenant_id);
            create table if not exists studio.mt_new_a partition of studio.mt_new for values in ('a');
            """;

        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(sql);

        found.Select(static x => x.Name).Should().Equal("mt_doc_invoice", "mt_events", "mt_streams");
        found.Should().OnlyContain(static x => x.Kind == MigrationRisk.AddsPartitionKind && x.Lock == TableLockMode.AccessExclusive);
    }

    [Fact]
    public void A_renamed_primary_key_constraint_is_access_exclusive_without_a_rewrite()
    {
        TableLockStatement statement = MigrationRisk.LockStatements(
                "alter table studio.mt_doc_customer rename constraint pk_old to pkey_mt_doc_customer_id;")
            .Should().ContainSingle().Subject;

        statement.Kind.Should().Be(MigrationRisk.RenamesConstraintKind);
        statement.Lock.Should().Be(TableLockMode.AccessExclusive);
        statement.Rewrite.Should().Be(TableRewrite.No);
    }

    [Fact]
    public void Other_statements_that_lock_nothing_holding_data_are_not_listed()
    {
        const string sql =
            """
            CREATE OR REPLACE FUNCTION studio.mt_upsert_customer(doc jsonb) RETURNS void AS $function$
            BEGIN
              update studio.mt_doc_customer set data = doc;
            END;
            $function$ LANGUAGE plpgsql;
            CREATE SEQUENCE studio.mt_hilo_seq;
            CREATE TABLE studio.mt_doc_new (id uuid not null);
            """;

        MigrationRisk.LockStatements(sql).Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // 1.2-APPLY-FU
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_function_dropped_with_cascade_is_listed_as_taking_its_indexes_with_it_and_a_plain_drop_is_not()
    {
        // Weasel 9.48.0 StringWriterExtensions.WriteDropFunction appends " cascade;".
        const string sql = "DROP FUNCTION IF EXISTS studio.mt_immutable_timestamp(timestamp without time zone) cascade;";

        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(sql);

        TableLockStatement statement = found.Should().ContainSingle().Subject;
        statement.Kind.Should().Be(MigrationRisk.DropsFunctionCascadeKind);
        statement.IsFunction.Should().BeTrue();
        statement.Lock.Should().Be(TableLockMode.AccessExclusive);
        (statement.Schema, statement.Name).Should().Be(("studio", "mt_immutable_timestamp"));
        MigrationImpact.RelationsOf(found).Should().BeEmpty("a function is not a relation to look up");
        MigrationRisk.Advice(found).Should().ContainSingle().Which.Should().Contain("ACCESS EXCLUSIVE");

        MigrationRisk.LockStatements("drop function if exists studio.mt_something();").Should().BeEmpty();
    }

    [Fact]
    public void A_dropped_function_names_the_tables_whose_declared_indexes_use_it_from_the_configuration_alone()
    {
        TableLockStatement[] statements = [.. MigrationRisk.LockStatements("drop function if exists studio.mt_immutable_timestamp(timestamp) cascade;")];
        PostgresStudio.Services.Schema.DeclaredIndex[] declared =
        [
            new("order", "Order", "studio", "mt_doc_order", "mt_doc_order_idx_placed",
                "CREATE INDEX mt_doc_order_idx_placed ON studio.mt_doc_order USING btree (studio.mt_immutable_timestamp((data ->> 'Placed')::timestamp));"),
            new("customer", "Customer", "studio", "mt_doc_customer", "mt_doc_customer_idx_name",
                "CREATE INDEX mt_doc_customer_idx_name ON studio.mt_doc_customer USING btree ((data ->> 'Name'));"),
        ];

        TableImpact named = MigrationImpact.Assemble(statements, [], sessions: null, declared).Tables.Should().ContainSingle().Subject;
        named.IsFunction.Should().BeTrue();
        named.Display.Should().Be("function studio.mt_immutable_timestamp");
        named.Note.Should().Contain("studio.mt_doc_order (mt_doc_order_idx_placed)").And.NotContain("mt_doc_customer")
            .And.Contain("Any other table whose indexes or defaults use it");

        MigrationImpact.Assemble(statements, [], sessions: null, declaredIndexes: null).Tables.Single().Note
            .Should().Be("Tables whose indexes or defaults use it.");
        MigrationImpact.Assemble(statements, [], sessions: null, declaredIndexes: []).Tables.Single().Note
            .Should().Be("Tables whose indexes or defaults use it.");
    }

    [Fact]
    public void A_new_tables_foreign_key_to_an_existing_table_locks_the_existing_one_and_a_key_between_new_tables_nothing()
    {
        const string sql =
            """
            CREATE TABLE studio.mt_doc_new (id uuid not null, customer_id uuid);
            CREATE TABLE studio.mt_doc_newer (id uuid not null);
            ALTER TABLE studio.mt_doc_new
            ADD CONSTRAINT fk_new_customer FOREIGN KEY(customer_id)
            REFERENCES studio.mt_doc_customer(id);
            ALTER TABLE studio.mt_doc_new
            ADD CONSTRAINT fk_new_newer FOREIGN KEY(id)
            REFERENCES studio.mt_doc_newer(id);
            """;

        TableLockStatement statement = MigrationRisk.LockStatements(sql).Should().ContainSingle().Subject;

        statement.Kind.Should().Be(MigrationRisk.ReferencedByForeignKeyKind);
        statement.Lock.Should().Be(TableLockMode.ShareRowExclusive);
        statement.Name.Should().Be("mt_doc_customer");
    }

    [Fact]
    public void A_backfilling_update_in_the_same_command_makes_any_alter_a_rewrite()
    {
        TableLockStatement dropped = MigrationRisk.LockStatements("alter table studio.t drop column x;update studio.t set y = 1;")
            .Should().ContainSingle().Subject;

        dropped.Kind.Should().Be(MigrationRisk.DropsColumnKind);
        dropped.Rewrite.Should().Be(TableRewrite.Yes);
        dropped.Effect.Should().Contain("updates every row");

        MigrationRisk.LockStatements("alter table studio.t alter column x set default 1;update studio.t set y = 1;")
            .Should().ContainSingle().Which.Rewrite.Should().Be(TableRewrite.Yes);
    }

    [Theory]
    [InlineData("random()", true)]
    [InlineData("clock_timestamp()", true)]
    [InlineData("gen_random_uuid()", true)]
    [InlineData("uuid_generate_v1()", true)]
    [InlineData("uuid_generate_v1mc()", true)]
    [InlineData("uuid_generate_v4()", true)]
    [InlineData("uuidv4()", true)]
    [InlineData("uuidv7()", true)]
    [InlineData("nextval('studio.s')", true)]
    [InlineData("statement_timestamp()", false)]
    [InlineData("now()", false)]
    [InlineData("uuid_generate_v3(uuid_ns_dns(), 'x')", false)]
    [InlineData("uuid_generate_v5(uuid_ns_dns(), 'x')", false)]
    public void A_default_is_volatile_only_when_its_function_is(string expression, bool volatileDefault)
    {
        TableLockStatement statement = MigrationRisk.LockStatements($"alter table studio.t add column c uuid default ({expression});")
            .Should().ContainSingle().Subject;

        statement.Kind.Should().Be(volatileDefault ? MigrationRisk.AddsVolatileColumnKind : MigrationRisk.AltersTableKind);
    }

    [Fact]
    public void The_lines_inside_a_dollar_quoted_body_are_not_statements_but_the_lines_around_it_are()
    {
        const string sql =
            """
            CREATE OR REPLACE FUNCTION studio.host_fn() RETURNS void AS $function$
            BEGIN
              CREATE TABLE studio.in_body (id int);
              ALTER TABLE studio.in_body ADD COLUMN y int;
              drop index studio.in_body_idx;
              ALTER TABLE studio.mt_doc_customer DROP COLUMN z;
            END;
            $function$ LANGUAGE plpgsql;
            alter table studio.in_body drop column q;
            alter table studio.mt_doc_customer drop column real_one;
            """;

        IReadOnlyList<TableLockStatement> found = MigrationRisk.LockStatements(sql);

        found.Select(static x => x.Name).Should().Equal("in_body", "mt_doc_customer");
        found.Select(static x => x.Statement).Should().Equal(
            "alter table studio.in_body drop column q;",
            "alter table studio.mt_doc_customer drop column real_one;");
        MigrationRisk.Find(sql).Select(static x => x.Statement).Should().Equal(
            "alter table studio.in_body drop column q;",
            "alter table studio.mt_doc_customer drop column real_one;");
        MigrationRisk.Notes(sql).Should().BeEmpty();
    }

    [Fact]
    public void A_body_that_opens_and_closes_on_one_line_a_string_holding_dollars_and_a_transaction_wrapper_hide_nothing()
    {
        MigrationRisk.LockStatements(
                "CREATE FUNCTION studio.f() RETURNS int AS $$ select 1 $$ LANGUAGE sql;\nalter table studio.t drop column a;")
            .Should().ContainSingle();

        MigrationRisk.LockStatements("update studio.t set note = 'costs $$ a lot';\nalter table studio.t drop column b;")
            .Should().ContainSingle().Which.Statement.Should().Contain("column b");

        MigrationRisk.LockStatements("DO LANGUAGE plpgsql $tran$\nBEGIN\nalter table studio.t drop column c;\nEND;\n$tran$;")
            .Should().ContainSingle().Which.Statement.Should().Contain("column c");
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
