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
    }
}
