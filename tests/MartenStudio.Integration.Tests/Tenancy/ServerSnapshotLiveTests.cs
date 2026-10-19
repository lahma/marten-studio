using Marten;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Tenancy;

/// <summary>
/// F8-D3-fix: <see cref="ServerSnapshot" />'s anti-vacuity. A snapshot that two reads always agreed on would make
/// every "first navigation changes nothing" fact pass for the wrong reason, so each part it reads is seen to move.
/// </summary>
/// <remarks>
/// <para>
/// The two writes that matter most are the ones D27 names: a cold master-table store's first enumeration
/// applies the lookup table, and a second store instance's first enumeration rewrites its seed rows. The
/// theory then makes one change of each kind the snapshot claims to read, in a database of its own each time,
/// and requires the comparison to fail.
/// </para>
/// <para>
/// <see cref="ServerSnapshot.ShouldBeUnchangedIn" /> compares the databases, then the objects, then the
/// lookup rows, so the message says which part moved; each fact checks that it is the part it should be.
/// </para>
/// </remarks>
public class ServerSnapshotLiveTests(PostgresFixture postgres)
{
    private const string LookupSchema = "tenants";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string LookupTable => SqlIdentifier.Qualify(LookupSchema, "mt_tenant_databases");

    [PostgresFact]
    public async Task A_cold_master_table_enumeration_is_seen_as_a_change()
    {
        await using OwnedDatabases owned = await OwnAsync("cold");
        string database = owned[0];

        ServerSnapshot before = await SnapshotAsync(owned, lookupTable: null);

        await using (DocumentStore store = DocumentStore.For(options => MasterTable(options, database, seed: false)))
        {
            _ = await store.Storage.AllDatabases();
        }

        ServerSnapshot after = await SnapshotAsync(owned, lookupTable: null);

        after.Objects.Should().Contain(
            static x => x.Contains("relation tenants.mt_tenant_databases ", StringComparison.Ordinal),
            "the premise: the enumeration applied the lookup table");

        FluentActions.Invoking(() => before.ShouldBeUnchangedIn(after))
            .Should().Throw<Exception>("a snapshot that cannot see a table appear proves nothing")
            .WithMessage("*schema object*");
    }

    [PostgresFact]
    public async Task A_second_store_instances_seed_upsert_is_seen_as_a_change()
    {
        await using OwnedDatabases owned = await OwnAsync("seeded");
        string database = owned[0];
        void Configure(StoreOptions options) => MasterTable(options, database, seed: true);

        await using (DocumentStore first = DocumentStore.For(Configure))
        {
            _ = await first.Storage.AllDatabases();
        }

        ServerSnapshot before = await SnapshotAsync(owned, LookupTable);
        before.Rows.Should().ContainSingle("the premise: the seed row the first instance wrote");

        await using (DocumentStore second = DocumentStore.For(Configure))
        {
            _ = await second.Storage.AllDatabases();
        }

        ServerSnapshot after = await SnapshotAsync(owned, LookupTable);

        after.Objects.Should().Equal(before.Objects, "the table was already there, so the second apply changed no object");

        FluentActions.Invoking(() => before.ShouldBeUnchangedIn(after))
            .Should().Throw<Exception>("the second instance rewrote its seed row, and an equal value is still a write")
            .WithMessage("*row of the lookup table*");
    }

    [PostgresTheory]
    [InlineData(1, "alter table app.item alter column name set default 'x'", "a column default")]
    [InlineData(2, "alter table app.item alter column name set not null", "a column's nullability")]
    [InlineData(3, "alter table app.item add constraint item_name_present check (name <> '')", "a constraint")]
    [InlineData(4, "create trigger item_touched before update on app.item for each row execute function app.touch()", "a trigger")]
    [InlineData(5, "alter table app.item disable trigger item_kept", "a trigger switched off")]
    [InlineData(6, "comment on table app.item is 'items'", "a comment")]
    [InlineData(7, "create schema elsewhere", "a schema nobody named")]
    [InlineData(8, "create index item_name on app.item (name)", "an index")]
    [InlineData(9, "drop table app.item; create table app.item (id int primary key, name text)", "a table recreated under its own name")]
    [InlineData(10, "grant select on app.item to public", "a grant")]
    [InlineData(11, "create or replace function app.touch() returns trigger language plpgsql as $$ begin new.name := new.name; return new; end $$", "a routine's body")]
    [InlineData(12, "create type app.mood as enum ('calm', 'busy')", "an enum")]
    public async Task Every_part_the_snapshot_reads_is_seen_to_change(int number, string ddl, string what)
    {
        await using OwnedDatabases owned = await OwnAsync("part_" + number);
        string database = owned[0];

        await TenancyDatabases.ExecuteAsync(
            database,
            "create schema app; "
            + "create table app.item (id int primary key, name text); "
            + "create function app.touch() returns trigger language plpgsql as $$ begin return new; end $$; "
            + "create trigger item_kept before insert on app.item for each row execute function app.touch();",
            Token);

        ServerSnapshot before = await SnapshotAsync(owned, lookupTable: null);

        await TenancyDatabases.ExecuteAsync(database, ddl, Token);

        ServerSnapshot after = await SnapshotAsync(owned, lookupTable: null);

        FluentActions.Invoking(() => before.ShouldBeUnchangedIn(after))
            .Should().Throw<Exception>("{0} must read as a change", what)
            .WithMessage("*schema object*");
    }

    /// <summary>
    /// A master-table tenancy whose lookup table lives in this test's own database; no tenant database is
    /// needed, because what is measured is the lookup table.
    /// </summary>
    private static void MasterTable(StoreOptions options, string database, bool seed)
    {
        options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
        {
            tenancy.ConnectionString = database;
            tenancy.SchemaName = LookupSchema;

            if (seed)
            {
                tenancy.RegisterDatabase("acme", database);
            }
        });

        options.DatabaseSchemaName = "app";
    }

    /// <summary>Databases of this test's own, named under <see cref="ServerSnapshot.SnapshotPrefix" />.</summary>
    private Task<OwnedDatabases> OwnAsync(string name) =>
        OwnedDatabases.CreateAsync(postgres, [ServerSnapshot.SnapshotPrefix + name], Token);

    private static Task<ServerSnapshot> SnapshotAsync(OwnedDatabases owned, string? lookupTable) =>
        ServerSnapshot.ReadAsync(owned.ConnectionStrings, ServerSnapshot.SnapshotPrefix, lookupTable, Token);
}
