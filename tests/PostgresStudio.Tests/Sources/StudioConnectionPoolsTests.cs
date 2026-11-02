using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// Issue #44: the studio's share of an application's pool - the name its sessions carry, and the slots it waits for - without a
/// database: what can be shown of it before anything connects. The live half (StudioConnectionsLiveTests) runs it on a server.
/// </summary>
public class StudioConnectionPoolsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Host=db;Application Name=orders-api", null, "Marten Studio", "orders-api (Marten Studio)")]
    [InlineData("Host=db", null, "Postgres Studio", "Postgres Studio")]
    [InlineData("Host=db;Application Name=orders-api", "ops-console", "Marten Studio", "ops-console")]
    [InlineData("Host=db;Application Name=orders-api", "", "Marten Studio", null)]
    [InlineData("Host=db;Application Name=Bestellungen·API", null, "Marten Studio", "Bestellungen?API (Marten Studio)")]
    public void The_session_name_is_the_hosts_or_the_applications_with_the_studios_after_it(string connectionString, string? configured, string product, string? expected) =>
        StudioConnectionPools.ApplicationNameFor(configured, connectionString, product).Should().Be(expected);

    [Fact]
    public void A_long_application_name_is_cut_and_the_studio_is_still_named()
    {
        string name = StudioConnectionPools.ApplicationNameFor(null, "Host=db;Application Name=" + new string('a', 120), "Marten Studio")!;

        name.Length.Should().Be(StudioConnectionPools.MaxApplicationNameBytes);
        name.Should().EndWith(" (Marten Studio)");
        StudioConnectionPools.IsValidApplicationName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("ops-console", true)]
    [InlineData("", true)]
    [InlineData("tab\there", false)]
    [InlineData("naïve", false)]
    public void A_configured_name_is_one_PostgreSQL_keeps_as_written(string name, bool valid)
    {
        StudioConnectionPools.IsValidApplicationName(name).Should().Be(valid);
        DiagnosticsOptionsValidation.IsStudioApplicationName(name).Should().Be(valid);
    }

    [Fact]
    public void The_options_bound_the_cap_and_the_name()
    {
        DiagnosticsOptionsValidation.IsStudioApplicationName(null).Should().BeTrue();
        DiagnosticsOptionsValidation.IsStudioApplicationName(new string('x', 64)).Should().BeFalse("PostgreSQL keeps 63 bytes");
        DiagnosticsOptionsValidation.IsMaxStudioConnections(0).Should().BeFalse();
        DiagnosticsOptionsValidation.IsMaxStudioConnections(1).Should().BeTrue();
        DiagnosticsOptionsValidation.IsMaxStudioConnections(100).Should().BeTrue();
        DiagnosticsOptionsValidation.IsMaxStudioConnections(101).Should().BeFalse();
        new PostgresDiagnosticsOptions().MaxStudioConnections.Should().Be(2);
        new PostgresStudioOptions().StudioApplicationName.Should().BeNull("the derived name is the default");
    }

    [Fact]
    public async Task A_full_pool_makes_the_next_open_wait_and_a_freed_slot_lets_it_in()
    {
        StudioConnectionPool pool = new(1, "app (Postgres Studio)", TimeSpan.FromSeconds(30));

        StudioConnectionLease first = await pool.AcquireAsync(new StudioConnectionLease(pool, reentrant: false), Token);
        pool.InUse.Should().Be(1);

        Task<StudioConnectionLease> second = pool.AcquireAsync(new StudioConnectionLease(pool, reentrant: false), Token);
        await WaitUntilAsync(() => pool.Waiting == 1);
        second.IsCompleted.Should().BeFalse("the one slot is held");

        first.Release();
        StudioConnectionLease admitted = await second.WaitAsync(TimeSpan.FromSeconds(30), Token);

        admitted.IsActive.Should().BeTrue();
        pool.InUse.Should().Be(1);
        pool.Waiting.Should().Be(0);

        admitted.Release();
        admitted.Release();
        pool.InUse.Should().Be(0, "a lease gives its slot back once");
    }

    [Fact]
    public async Task An_open_that_finds_no_slot_within_the_timeout_says_so_and_holds_nothing()
    {
        StudioConnectionPool pool = new(1, null, TimeSpan.FromMilliseconds(200));
        StudioConnectionLease held = await pool.AcquireAsync(new StudioConnectionLease(pool, reentrant: false), Token);

        StudioConnectionLease waiting = new(pool, reentrant: false);
        Func<Task> wait = () => pool.AcquireAsync(waiting, Token);

        (await wait.Should().ThrowAsync<StudioConnectionsBusyException>()).Which.Message
            .Should().Contain("MaxStudioConnections").And.Contain("1 at most");
        waiting.IsActive.Should().BeFalse();
        waiting.Release();
        pool.InUse.Should().Be(1, "the timed-out open took no slot, and giving back its lease returns none");

        held.Release();
        pool.InUse.Should().Be(0);
    }

    /// <remarks>
    /// The process-wide configuration is not touched here: the fast suite's other tests open connections in parallel, and a cap of
    /// one set from a test would hold them to it.
    /// </remarks>
    [Fact]
    public void A_registered_pool_keeps_its_own_settings_and_the_first_registration_wins()
    {
        using NpgsqlDataSource source = NpgsqlDataSource.Create("Host=nowhere.invalid;Application Name=orders-api");

        StudioConnectionPool registered = StudioConnectionPools.Register(source, 3, null, "Postgres Studio", TimeSpan.FromSeconds(5));

        StudioConnectionPools.Of(source).Should().BeSameAs(registered);
        registered.Max.Should().Be(3);
        registered.ApplicationName.Should().Be("orders-api (Postgres Studio)");
        StudioConnectionPools.Register(source, 9, "x", "y", TimeSpan.Zero).Should().BeSameAs(registered, "the first registration wins");
    }

    [Fact]
    public async Task A_connection_the_studio_did_not_meter_is_opened_as_it_always_was()
    {
        await using NpgsqlConnection connection = new("Host=nowhere.invalid;Timeout=1");

        StudioConnectionMeter.PoolOf(connection).Should().BeNull();
        Func<Task> open = () => connection.OpenStudioAsync(Token);

        await open.Should().ThrowAsync<Exception>("it is simply opened, and there is no server");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (!condition())
        {
            await Task.Yield();
            await Task.Delay(10, timeout.Token);
        }
    }
}
