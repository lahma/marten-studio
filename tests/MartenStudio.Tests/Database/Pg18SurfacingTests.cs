using Bunit;

using MartenStudio.Tests.Support;

using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Relationships;
using PostgresStudio.Services.Statements;

namespace MartenStudio.Tests.Database;

/// <summary>
/// What PostgreSQL 18 adds, shown where it belongs (1.0-PG18), the fast half: virtual against stored generated columns,
/// temporal keys in Keys &amp; indexes and in a Relationships label, the 1.12 statement counters, and the Sessions page's
/// per-backend I/O - present from 18 and absent, not empty, below it. <c>Pg18SurfacingLiveTests</c> is the live half.
/// </summary>
public class Pg18SurfacingTests
{
    private static DatabaseObjectDetail Base() => FakeDatabaseObjects.Detail("quartz", "qrtz_triggers");

    private static DatabaseColumnInfo Column(string name, int position, bool generated, bool virtualColumn) =>
        new(name, position, "integer", true, "a * 2", DatabaseIdentityKind.None, generated, true, null, true)
        {
            GeneratedVirtual = virtualColumn,
        };

    [Fact]
    public void A_virtual_generated_column_is_badged_virtual_and_a_stored_one_stored()
    {
        using var context = DatabasePageData.Context();

        DatabaseObjectDetail detail = Base() with
        {
            Columns =
            [
                Column("plain", 1, generated: false, virtualColumn: false),
                Column("doubled", 2, generated: true, virtualColumn: false),
                Column("tripled", 3, generated: true, virtualColumn: true),
            ],
        };

        var tab = context.Render<ObjectColumnsTab>(p => p.Add(x => x.Detail, detail));
        var rows = tab.FindAll("tbody tr");

        rows[0].QuerySelectorAll(".ms-db-badge").Should().BeEmpty("a plain column carries neither badge");
        rows[1].QuerySelector(".ms-db-badge")!.TextContent.Trim().Should().Be("stored");
        rows[2].QuerySelector(".ms-db-badge")!.TextContent.Trim().Should().Be("virtual");
        rows[2].QuerySelector(".ms-db-badge")!.GetAttribute("title").Should().Contain("not stored");
    }

    [Fact]
    public void A_temporal_primary_key_and_a_period_foreign_key_say_so_in_keys_and_indexes()
    {
        using var context = DatabasePageData.Context();

        DatabaseObjectDetail detail = Base() with
        {
            Constraints =
            [
                new DatabaseConstraintInfo("booking_pkey", DatabaseConstraintKind.PrimaryKey, "PRIMARY KEY (room_id, valid_at WITHOUT OVERLAPS)", Temporal: true),
                new DatabaseConstraintInfo("booking_code_key", DatabaseConstraintKind.Unique, "UNIQUE (code)"),
            ],
            ForeignKeysOut =
            [
                new DatabaseForeignKeyInfo(
                    "booking_room_fkey", "app", "booking", ["room_id", "valid_at"], "app", "room", ["id", "valid_at"],
                    true, null, true, "no action", "no action")
                {
                    Period = true,
                },
                new DatabaseForeignKeyInfo(
                    "booking_guest_fkey", "app", "booking", ["guest_id"], "app", "guest", ["id"],
                    true, null, true, "no action", "no action"),
            ],
            ForeignKeysIn = [],
        };

        var tab = context.Render<ObjectKeysTab>(p => p.Add(x => x.Detail, detail));

        var constraints = tab.FindAll(".ms-db-constraints tbody tr");
        constraints[0].QuerySelector(".ms-db-badge-temporal")!.TextContent.Trim().Should().Be("WITHOUT OVERLAPS");
        constraints[1].QuerySelector(".ms-db-badge-temporal").Should().BeNull("an ordinary unique constraint is not temporal");

        var keys = tab.FindAll(".ms-db-fks-out tbody tr");
        keys[0].QuerySelector(".ms-db-badge-temporal")!.TextContent.Trim().Should().Be("PERIOD");
        keys[0].TextContent.Should().Contain("room_id, PERIOD valid_at").And.Contain("id, PERIOD valid_at");
        keys[1].QuerySelector(".ms-db-badge-temporal").Should().BeNull();
        keys[1].TextContent.Should().Contain("guest_id").And.NotContain("PERIOD");
    }

    [Fact]
    public void A_period_foreign_key_spells_its_last_column_as_declared()
    {
        DatabaseForeignKeyInfo key = new("k", "s", "t", ["a", "p"], "s", "u", ["a", "p"], true, null, true, "no action", "no action") { Period = true };

        key.ColumnText(key.Columns).Should().Be("a, PERIOD p");
        key.ColumnText([]).Should().BeEmpty();
        (key with { Period = false }).ColumnText(key.Columns).Should().Be("a, p", "below 18 nothing is a period");
    }

    [Fact]
    public void A_relationships_label_carries_the_word_period_and_changes_nothing_else()
    {
        RelationshipGraphBuilder.LabelFor(["room_id", "valid_at"], ["id", "valid_at"], null, null, period: true)
            .Should().Be("room_id, PERIOD valid_at");
        RelationshipGraphBuilder.LabelFor(["room_id", "valid_at"], ["id", "valid_at"], null, null)
            .Should().Be("room_id, valid_at");

        // The shared leading key column is stripped as for any composite key; the word stays on the column it belongs to.
        RelationshipGraphBuilder.LabelFor(["tenant", "valid_at"], ["tenant", "valid_at"], ["tenant", "id"], ["tenant", "id"], period: true)
            .Should().Be("PERIOD valid_at");

        // Both keys whole: one-to-one, and the period says it.
        RelationshipGraphBuilder.LabelFor(["id", "valid_at"], ["id", "valid_at"], ["id", "valid_at"], ["id", "valid_at"], period: true)
            .Should().Be(RelationshipGraphBuilder.OneToOneLabel + ", PERIOD valid_at");
        RelationshipGraphBuilder.PeriodColumns([]).Should().BeEmpty();
    }

    [Fact]
    public void A_statement_shows_the_wal_buffers_it_filled_and_the_workers_it_was_given_only_when_there_is_something_to_say()
    {
        new StatementServerCounters(null, null, null).Text.Should().BeNull("below extension 1.12 nothing is read");
        new StatementServerCounters(0, 0, 0).Text.Should().BeNull("a statement that ran no parallel plan and filled no buffer has nothing to add");

        new StatementServerCounters(8, 8, 3).Text.Should().Be(
            "Since its counters began (PostgreSQL 18): parallel workers: asked for 8, given 8; WAL buffers full 3 times.");
        new StatementServerCounters(0, 0, 1).Text.Should().Be("Since its counters began (PostgreSQL 18): WAL buffers full 1 time.");
    }
}
