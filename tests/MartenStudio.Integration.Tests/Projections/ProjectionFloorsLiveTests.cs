using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;

using Npgsql;

using PostgresStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// B, live: from Marten 9.52 a rebuild replays from the projection's start floor (JasperFx.Events 2.93's
/// <c>DetermineRebuildFloorAsync</c>), so the rebuild dialog reads the floor stored for it in
/// <c>mt_event_projection_floors</c> - with the studio's own SQL, never Marten's seam, which ensures the event storage
/// first - and takes it off the events to replay. Below 9.52 nothing is read and nothing changes.
/// </summary>
public class ProjectionFloorsLiveTests(PostgresFixture postgres)
{
    private const string DailySales = "DailySales";

    private const string FloorsTable = "mt_event_projection_floors";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool FloorsApply => RuntimeFeature.ProjectionStartFloors.IsAvailable(MartenRuntime.Current);

    [PostgresFact]
    public async Task A_stored_floor_is_where_the_rebuild_dialog_says_the_replay_starts_and_what_it_takes_off()
    {
        await using ProjectionsFixture fixture = await ProjectionsFixture.StartAsync(
            postgres, "projections_floors", withDaemon: false, cancellationToken: Token);

        RebuildScope before = await fixture.UseAsync(service => service.DescribeRebuildAsync(fixture.Scope, DailySales, Token));
        long highWater = before.EventsToReplay;
        highWater.Should().BeGreaterThan(10, "the premise: the seeded events are there to replay");

        if (!FloorsApply)
        {
            before.Floor.Should().BeNull("below Marten 9.52 every rebuild replays from the first event, and nothing is read");
            (await TableExistsAsync(fixture.EventSchema)).Should().BeFalse("and nothing declares the table");
            return;
        }

        before.Floor.Should().Be(RebuildFloor.None, "the table is there and holds no floor for the projection");

        // What SubscribeFromFirstRun or an operator's SetProjectionFloorAsync would store - written here by hand, so the
        // test stands on the table's shape rather than on a member the floor's Marten does not have.
        await StoreFloorAsync(fixture.EventSchema, DailySales, string.Empty, 10);
        await StoreFloorAsync(fixture.EventSchema, "SomeoneElse", string.Empty, 3);

        RebuildScope after = await fixture.UseAsync(service => service.DescribeRebuildAsync(fixture.Scope, DailySales, Token));

        after.Floor!.Stored.Should().Be(10);
        after.EventsToReplay.Should().Be(highWater - 10, "the replay starts after the floor");
        RebuildWording.ReplayPhrase(after.Floor).Should().Contain("after sequence 10");
    }

    /// <summary>
    /// A store migrated below 9.52 has no floors table: the dialog says none is stored, and reading it created nothing -
    /// where Marten's own floor read would have created the table to answer (hard rule 14).
    /// </summary>
    [PostgresFact]
    public async Task A_database_without_the_floors_table_stores_none_and_describing_a_rebuild_creates_nothing()
    {
        await using ProjectionsFixture fixture = await ProjectionsFixture.StartAsync(
            postgres, "projections_floors_missing", withDaemon: false, cancellationToken: Token);

        await DropTableAsync(fixture.EventSchema);

        RebuildScope scope = await fixture.UseAsync(service => service.DescribeRebuildAsync(fixture.Scope, DailySales, Token));

        if (FloorsApply)
        {
            scope.Floor.Should().NotBeNull();
            scope.Floor!.TableExists.Should().BeFalse();
            scope.Floor.Stored.Should().BeNull();
        }
        else
        {
            scope.Floor.Should().BeNull();
        }

        (await TableExistsAsync(fixture.EventSchema)).Should().BeFalse("a read path creates nothing");
    }

    private async Task StoreFloorAsync(string schema, string projection, string tenant, long floor)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            "insert into " + SqlIdentifier.Qualify(schema, FloorsTable) + " (name, tenant_id, floor) values (@name, @tenant, @floor)",
            connection);
        command.Parameters.AddWithValue("name", projection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("floor", floor);

        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<bool> TableExistsAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            "select exists (select 1 from pg_catalog.pg_tables where schemaname = @schema and tablename = @table)",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("table", FloorsTable);

        return (bool) (await command.ExecuteScalarAsync(Token))!;
    }

    private async Task DropTableAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand("drop table if exists " + SqlIdentifier.Qualify(schema, FloorsTable), connection);
        await command.ExecuteNonQueryAsync(Token);
    }
}
