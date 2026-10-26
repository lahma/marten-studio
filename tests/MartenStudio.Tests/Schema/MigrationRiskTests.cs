using MartenStudio.Services.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// What the apply dialog has to put in front of somebody before they type a database name.
/// </summary>
/// <remarks>
/// The statements below are the real shapes Weasel's <c>TableDelta.WriteUpdate</c> writes, in the order
/// it writes them: extra indexes dropped, changed columns altered, the primary key dropped with
/// <c>CASCADE</c> and re-added, extra columns dropped, and a repartitioned table copied out and back.
/// Every one of them is reported as <c>Update</c> and every one of them is allowed under
/// <c>AutoCreate.CreateOrUpdate</c>.
/// </remarks>
public class MigrationRiskTests
{
    [Fact]
    public void A_migration_that_only_creates_things_is_not_destructive()
    {
        const string sql =
            """
            create table studio.mt_doc_customer (id uuid not null, data jsonb not null);
            CREATE INDEX mt_doc_customer_idx_email ON studio.mt_doc_customer USING btree (email);
            CREATE OR REPLACE FUNCTION studio.mt_jsonb_patch(jsonb, jsonb) RETURNS jsonb AS $function$ $function$;
            """;

        MigrationRisk.IsDestructive(sql).Should().BeFalse();
        MigrationRisk.Find(sql).Should().BeEmpty();
    }

    /// <summary>
    /// Marten 9.52's <c>mt_event_projection_floors</c> (#5740), written by Weasel from the declaration itself: what a store
    /// migrated below 9.52 previews as its one drift is a plain create, which removes nothing and earns no note. Below
    /// 9.52 nothing declares the table.
    /// </summary>
    [Fact]
    public void The_projection_floors_table_marten_9_52_adds_is_a_create_that_removes_nothing()
    {
        var options = new global::Marten.StoreOptions();
        options.Events.DatabaseSchemaName = "studio_events";

        Weasel.Postgresql.Tables.Table? floors = ((Weasel.Core.Migrations.IFeatureSchema) options.Events).Objects
            .OfType<Weasel.Postgresql.Tables.Table>()
            .FirstOrDefault(static x => x.Identifier.Name == "mt_event_projection_floors");

        bool declared = MartenStudio.Services.Configuration.MartenRuntime.Current
            .VersionOf(MartenStudio.Services.Configuration.RuntimePackage.Marten) >= new Version(9, 52, 0);

        if (!declared)
        {
            floors.Should().BeNull("Marten below 9.52 has no projection floors");
            return;
        }

        floors.Should().NotBeNull();

        using var writer = new StringWriter();
        floors!.WriteCreateStatement(new Weasel.Postgresql.PostgresqlMigrator(), writer);
        string sql = writer.ToString();

        sql.Should().ContainEquivalentOf("create table").And.Contain("mt_event_projection_floors");
        MigrationRisk.IsDestructive(sql).Should().BeFalse();
        MigrationRisk.Find(sql).Should().BeEmpty();
        MigrationRisk.Notes(sql).Should().BeEmpty("a new table blocks no writer: there is nothing on it yet to wait for");
    }

    [Fact]
    public void A_dropped_index_is_found_and_named()
    {
        IReadOnlyList<DestructiveStatement> found =
            MigrationRisk.Find("drop index if exists studio.hand_rolled_idx;");

        found.Should().ContainSingle();
        found[0].Kind.Should().Be("drops an index");
        found[0].Statement.Should().Contain("hand_rolled_idx");
    }

    [Fact]
    public void A_dropped_column_a_changed_type_and_a_dropped_primary_key_are_all_found()
    {
        const string sql =
            """
            alter table studio.mt_doc_customer drop column legacy_code;
            alter table studio.mt_doc_customer alter column total type numeric;
            alter table studio.mt_doc_customer drop constraint "pkey_mt_doc_customer_id" CASCADE;
            """;

        MigrationRisk.KindsIn(sql).Should().Equal(
            "drops a column",
            "changes a column type",
            "drops a constraint");
    }

    [Fact]
    public void A_partition_rebuild_is_found_because_it_copies_the_whole_table_out_and_back()
    {
        const string sql =
            """
            create table studio.mt_events_temp as select * from studio.mt_events;
            drop table studio.mt_events cascade;
            insert into studio.mt_events(seq_id) select seq_id from studio.mt_events_temp;
            drop table studio.mt_events_temp cascade;
            """;

        MigrationRisk.KindsIn(sql).Should().Contain("copies a table out and back (partition rebuild)");
        MigrationRisk.KindsIn(sql).Should().Contain("drops a table");
    }

    [Fact]
    public void A_comment_that_mentions_dropping_is_not_a_dropped_object()
    {
        MigrationRisk.Find("-- drop index studio.something_idx; -- do not do this").Should().BeEmpty();
    }

    [Fact]
    public void A_preview_reports_its_own_destructive_statements()
    {
        var preview = new MigrationPreview(
            "drop index studio.hand_rolled_idx;\nCREATE INDEX mt_doc_customer_idx_email ON studio.mt_doc_customer (email);",
            1,
            [],
            "Update",
            null);

        preview.IsDestructive.Should().BeTrue();
        preview.DestructiveStatements.Should().ContainSingle();
        preview.DestructiveStatements[0].Kind.Should().Be("drops an index");
    }

    [Fact]
    public void An_empty_preview_is_never_destructive()
    {
        MigrationPreview.None.IsDestructive.Should().BeFalse();
        MigrationRisk.Find(null).Should().BeEmpty();
        MigrationRisk.Find("   ").Should().BeEmpty();
        MigrationRisk.Notes(null).Should().BeEmpty();
        MigrationPreview.None.Notes.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // L9: Weasel 9.46's partition-index resume (weasel#718, Marten 9.51) is not a drop
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// What Weasel 9.46's <c>IndexDefinition.toPartitionedConcurrentDDL</c> writes for one partition of a concurrent
    /// index, markers and all, copied from its source: the drop removes only what a stopped build of the same index left
    /// unattached.
    /// </summary>
    private const string PartitionResume =
        """
        CREATE INDEX idx_mt_events_tenant ON ONLY studio.mt_events USING btree (tenant_id);
        --WEASEL_INDEX_CREATION_BEGIN
        DROP INDEX CONCURRENTLY IF EXISTS studio.idx_mt_events_tenant_acme;
        --WEASEL_INDEX_CREATION_END
        --WEASEL_INDEX_CREATION_BEGIN
        CREATE INDEX CONCURRENTLY idx_mt_events_tenant_acme ON studio.mt_events_acme USING btree (tenant_id);
        --WEASEL_INDEX_CREATION_END
        ALTER INDEX studio.idx_mt_events_tenant ATTACH PARTITION studio.idx_mt_events_tenant_acme;
        --WEASEL_INDEX_CREATION_BEGIN
        DROP INDEX CONCURRENTLY IF EXISTS studio."idx_mt_events_tenant_Globex";
        --WEASEL_INDEX_CREATION_END
        --WEASEL_INDEX_CREATION_BEGIN
        CREATE UNIQUE INDEX CONCURRENTLY "idx_mt_events_tenant_Globex" ON studio."mt_events_Globex" USING btree (tenant_id);
        --WEASEL_INDEX_CREATION_END
        ALTER INDEX studio.idx_mt_events_tenant ATTACH PARTITION studio."idx_mt_events_tenant_Globex";
        """;

    [Fact]
    public void A_partition_index_resume_is_a_note_and_not_a_dropped_index()
    {
        MigrationRisk.Find(PartitionResume).Should().BeEmpty("each drop is followed by the build and the attach of the same index");
        MigrationRisk.IsDestructive(PartitionResume).Should().BeFalse();

        IReadOnlyList<MigrationNote> notes = MigrationRisk.Notes(PartitionResume);

        notes.Should().HaveCount(2);
        notes.Should().OnlyContain(static x => x.Kind == MigrationRisk.PartitionIndexResumeKind);
        notes[0].Statement.Should().Contain("idx_mt_events_tenant_acme");
        notes[1].Statement.Should().Contain("\"idx_mt_events_tenant_Globex\"");
    }

    /// <summary>The same drop that is not followed by the build of that index is a drop - including Weasel's spelling.</summary>
    [Theory]
    [InlineData("DROP INDEX CONCURRENTLY IF EXISTS studio.hand_rolled_idx;")]
    [InlineData(
        """
        DROP INDEX CONCURRENTLY IF EXISTS studio.idx_a;
        CREATE INDEX CONCURRENTLY idx_b ON studio.mt_events_acme USING btree (tenant_id);
        ALTER INDEX studio.idx ATTACH PARTITION studio.idx_b;
        """)]
    [InlineData(
        """
        DROP INDEX CONCURRENTLY IF EXISTS studio.idx_a;
        CREATE INDEX CONCURRENTLY idx_a ON studio.mt_events_acme USING btree (tenant_id);
        """)]
    [InlineData(
        """
        DROP INDEX CONCURRENTLY IF EXISTS studio.idx_a;
        CREATE INDEX CONCURRENTLY idx_a ON studio.mt_events_acme USING btree (tenant_id);
        ALTER INDEX studio.idx ATTACH PARTITION studio.idx_other;
        """)]
    [InlineData(
        """
        DROP INDEX CONCURRENTLY IF EXISTS studio.idx_a;
        CREATE INDEX idx_a ON studio.mt_events_acme USING btree (tenant_id);
        ALTER INDEX studio.idx ATTACH PARTITION studio.idx_a;
        """)]
    [InlineData("drop index studio.mt_doc_customer_idx_old;")]
    public void Any_other_drop_index_is_still_a_dropped_index(string sql)
    {
        IReadOnlyList<DestructiveStatement> found = MigrationRisk.Find(sql);

        found.Should().ContainSingle().Which.Kind.Should().Be("drops an index");
        MigrationRisk.Notes(sql).Should().NotContain(static x => x.Kind == MigrationRisk.PartitionIndexResumeKind);
    }

    // ------------------------------------------------------------------------------------------------
    // Item 12: an index built without CONCURRENTLY on a table that exists - the 9.51 tag-table index among them
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A store whose DCB tag tables were built below Marten 9.51 previews one plain <c>CREATE INDEX</c> per tag table
    /// (#5694's <c>idx_mt_event_tag_&lt;suffix&gt;_seq_id</c>). It removes nothing, so the apply does not wait for a tick
    /// against it - but it holds a SHARE lock on the tag table while it builds, so every tagged append waits, and the
    /// notes say so.
    /// </summary>
    [Fact]
    public void A_plain_index_build_on_an_existing_tag_table_is_a_note_that_tagged_appends_wait()
    {
        const string sql =
            """
            CREATE INDEX idx_mt_event_tag_customer_seq_id ON studio.mt_event_tag_customer USING btree (seq_id);
            CREATE INDEX mt_doc_customer_idx_email ON studio.mt_doc_customer USING btree ((data ->> 'Email'));
            """;

        MigrationRisk.IsDestructive(sql).Should().BeFalse("an index build removes nothing");

        IReadOnlyList<MigrationNote> notes = MigrationRisk.Notes(sql);

        notes.Select(static x => x.Kind).Should().Equal(MigrationRisk.BlockingTagIndexBuildKind, MigrationRisk.BlockingIndexBuildKind);
        notes[0].Statement.Should().Contain("mt_event_tag_customer");
        MigrationRisk.BlockingTagIndexBuildKind.Should().Contain("every append of an event carrying that tag waits");
    }

    [Fact]
    public void An_index_on_a_table_the_script_creates_or_built_concurrently_or_on_only_the_parent_is_no_note()
    {
        const string sql =
            """
            CREATE TABLE IF NOT EXISTS studio.mt_event_tag_customer (value varchar NOT NULL, seq_id bigint NOT NULL);
            CREATE INDEX idx_mt_event_tag_customer_seq_id ON studio.mt_event_tag_customer USING btree (seq_id);
            create table Studio.MT_DOC_ORDER (id uuid not null, data jsonb not null);
            CREATE UNIQUE INDEX mt_doc_order_idx_number ON studio.mt_doc_order USING btree ((data ->> 'Number'));
            CREATE TABLE "Legacy"."Order Lines" (id int);
            CREATE INDEX order_lines_idx ON "Legacy"."Order Lines" USING btree (id);
            --WEASEL_INDEX_CREATION_BEGIN
            CREATE INDEX CONCURRENTLY mt_doc_customer_idx_name ON studio.mt_doc_customer USING btree ((data ->> 'Name'));
            --WEASEL_INDEX_CREATION_END
            CREATE INDEX idx_mt_events_tenant ON ONLY studio.mt_events USING btree (tenant_id);
            """;

        MigrationRisk.Notes(sql).Should().BeEmpty(
            "nothing waits for an index on a table created in the same script - unquoted names fold to lower case, quoted ones "
            + "match exactly - a concurrent build, or ON ONLY's metadata");
    }

    /// <summary>
    /// A quoted identifier keeps its case and an unquoted one folds to lower case, as Postgres resolves them: a table
    /// created as <c>"mt_doc_Order"</c> is not the <c>mt_doc_order</c> an index names, so that index builds on a table the
    /// script did not create, and is a note.
    /// </summary>
    [Fact]
    public void A_quoted_name_matches_only_its_own_case()
    {
        const string sql =
            """
            create table studio."mt_doc_Order" (id uuid not null, data jsonb not null);
            CREATE INDEX quoted_idx ON studio."mt_doc_Order" USING btree (id);
            CREATE INDEX folded_idx ON studio.mt_doc_order USING btree (id);
            """;

        IReadOnlyList<MigrationNote> notes = MigrationRisk.Notes(sql);

        notes.Should().ContainSingle("the quoted index is on the created table; the unquoted one names another table")
            .Which.Statement.Should().Contain("folded_idx");

        // A quoted name with a space in it is one name, not a name cut at the space.
        MigrationRisk.Notes(
            """
            CREATE TABLE "Legacy"."Order Lines" (id int);
            CREATE INDEX lines_idx ON "Legacy"."Order Lines" USING btree (id);
            CREATE INDEX other_idx ON "Legacy"."Order Totals" USING btree (id);
            """).Should().ContainSingle().Which.Statement.Should().Contain("other_idx");

        MigrationRisk.Normalize("Studio.\"mt_doc_Order\"").Should().Be(MigrationRisk.Normalize("studio.\"mt_doc_Order\""));
        MigrationRisk.Normalize("studio.\"mt_doc_Order\"").Should().NotBe(MigrationRisk.Normalize("studio.mt_doc_order"));
        MigrationRisk.Normalize("\"a.b\"").Should().NotBe(MigrationRisk.Normalize("a.b"), "a dot inside quotes is part of the name");
        MigrationRisk.Normalize("\"say \"\"hi\"\"\"").Should().Be("say \"hi\"", "a doubled quote is one quote");
    }

    [Fact]
    public void A_preview_reports_its_own_notes()
    {
        var preview = new MigrationPreview(
            "CREATE INDEX idx_mt_event_tag_customer_seq_id ON studio.mt_event_tag_customer USING btree (seq_id);",
            1,
            [],
            "Update",
            null);

        preview.IsDestructive.Should().BeFalse();
        preview.Notes.Should().ContainSingle().Which.Kind.Should().Be(MigrationRisk.BlockingTagIndexBuildKind);
    }
}
