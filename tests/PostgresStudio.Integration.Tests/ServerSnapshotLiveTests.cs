namespace PostgresStudio.Integration.Tests;

/// <summary>
/// <see cref="ServerSnapshot" />'s anti-vacuity: a snapshot two reads always agreed on would make every "a read path writes
/// nothing" fact pass for the wrong reason, so each kind of change it claims to read is made once and seen.
/// </summary>
public class ServerSnapshotLiveTests(PostgresFixture postgres)
{
    private const string Prefix = "pss_snap_";

    private const string Seed =
        "create schema app; "
        + "create table app.item (id int primary key, name text); "
        + "insert into app.item values (1, 'one'), (2, 'two'); "
        + "create sequence app.item_ids; "
        + "create materialized view app.item_names as select name from app.item;";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresTheory]
    [InlineData(1, "insert into app.item values (3, 'three')", "Rows", "an insert")]
    [InlineData(2, "update app.item set name = name", "Rows", "an update that leaves every value as it was")]
    [InlineData(3, "delete from app.item where id = 2", "Rows", "a delete")]
    [InlineData(4, "select nextval('app.item_ids')", "Rows", "a sequence's nextval")]
    [InlineData(5, "refresh materialized view app.item_names", "Rows", "a refreshed materialized view")]
    [InlineData(6, "create index item_name on app.item (name)", "Objects", "an index")]
    [InlineData(7, "comment on table app.item is 'items'", "Objects", "a comment")]
    [InlineData(8, "create schema elsewhere", "Objects", "a schema nobody named")]
    [InlineData(9, "alter table app.item alter column name set default 'x'", "Objects", "a column default")]
    public async Task Every_part_the_snapshot_reads_is_seen_to_change(int number, string change, string part, string what)
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + number], Token);
        await OwnedDatabases.ExecuteAsync(owned[0], Seed, Token);

        ServerSnapshot before = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix + number, Token);
        before.Rows.Should().HaveCount(3, "the premise: one table, one materialized view and one sequence are read");

        await OwnedDatabases.ExecuteAsync(owned[0], change, Token);

        ServerSnapshot after = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix + number, Token);

        FluentActions.Invoking(() => before.ShouldBeUnchangedIn(after))
            .Should().Throw<Exception>("{0} must read as a change", what)
            .WithMessage(part == "Rows" ? "*writes no row*" : "*schema object*");
    }

    [PostgresFact]
    public async Task A_created_database_is_seen_and_an_unchanged_one_is_not()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "db"], Token);
        await OwnedDatabases.ExecuteAsync(owned[0], Seed, Token);

        ServerSnapshot before = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix + "db", Token);
        before.ShouldBeUnchangedIn(await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix + "db", Token));

        await using OwnedDatabases another = await OwnedDatabases.CreateAsync(postgres, [Prefix + "db_another"], Token);
        ServerSnapshot after = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix + "db", Token);

        FluentActions.Invoking(() => before.ShouldBeUnchangedIn(after))
            .Should().Throw<Exception>("a database created under the prefix is a change").WithMessage("*creates no database*");
    }
}
