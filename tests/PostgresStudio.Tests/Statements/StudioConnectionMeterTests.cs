using System.Data;
using System.Diagnostics;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;

using static PostgresStudio.Tests.Statements.StatementsTestFacts;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The cost meter that counts every statement (W5-Q1, backlog §C), with no Postgres: a connection is timed from its open to
/// its close - once, however often it is closed, and again when it is opened again - and Postgres Studio's databases hand out
/// metered connections only.
/// </summary>
public class StudioConnectionMeterTests
{
    private const string ConnectionString = "Host=db.example;Port=5433;Database=orders;Username=app;Password=secret";

    private static readonly StateChangeEventArgs Opened = new(ConnectionState.Closed, ConnectionState.Open);
    private static readonly StateChangeEventArgs Closed = new(ConnectionState.Open, ConnectionState.Closed);
    private static readonly StateChangeEventArgs Broke = new(ConnectionState.Open, ConnectionState.Broken);

    [Fact]
    public void A_connection_is_timed_from_its_open_to_its_close_and_counted_once()
    {
        Support.ManualTimeProvider clock = new(Now);
        StudioDatabaseTime meter = new(clock);
        long ticks = 0;
        StudioConnectionMeter.Tracker tracker = new("db.example:5433/orders", meter, () => ticks);

        tracker.OnStateChange(null, Opened);
        ticks += Stopwatch.Frequency * 2;
        tracker.OnStateChange(null, Closed);
        tracker.OnStateChange(null, Closed);

        meter.Total("db.example:5433/orders").Should().Be(new StudioCost(TimeSpan.FromSeconds(2), 1, TimeSpan.FromMinutes(5)),
            "a close that follows a close adds nothing");
        meter.ConnectionsEver("db.example:5433/orders").Should().Be(1);
        meter.RollbacksEver("db.example:5433/orders").Should().Be(0, "a connection's open and close are no rollback, so C08's count is untouched");

        tracker.OnStateChange(null, Opened);
        ticks += Stopwatch.Frequency;
        tracker.OnStateChange(null, Broke);

        meter.Total("db.example:5433/orders").Should().Be(new StudioCost(TimeSpan.FromSeconds(3), 2, TimeSpan.FromMinutes(5)),
            "a connection opened again is counted again, and one that broke is counted as it broke");
    }

    [Fact]
    public void A_data_sources_database_hands_out_metered_connections_and_nothing_else_is()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(ConnectionString);
        DataSourceDatabase database = new("orders", dataSource);

        using NpgsqlConnection metered = database.CreateConnection(StudioConnectionUsage.Read);
        using NpgsqlConnection plain = new(ConnectionString);

        StudioConnectionMeter.IsMetered(metered).Should().BeTrue();
        StudioConnectionMeter.IsMetered(plain).Should().BeFalse();
        StudioDatabaseTime.KeyOf(metered).Should().Be("db.example:5433/orders");
    }

    [Fact]
    public void Attaching_twice_meters_once()
    {
        using NpgsqlConnection connection = new(ConnectionString);

        StudioConnectionMeter.Attach(connection).Should().BeSameAs(connection);
        StudioConnectionMeter.Attach(connection).Should().BeSameAs(connection);

        StudioConnectionMeter.IsMetered(connection).Should().BeTrue();
    }
}
