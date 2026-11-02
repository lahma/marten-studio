using Npgsql;

using PostgresStudio.Components.Pages.Health;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
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

    /// <summary>Issue #49: the cap is the option, never above the data source's own Maximum Pool Size, and never below one.</summary>
    [Theory]
    [InlineData("Host=db;Maximum Pool Size=1", 2, 1, 1)]
    [InlineData("Host=db;Maximum Pool Size=5", 2, 2, null)]
    [InlineData("Host=db;Pooling=false;Maximum Pool Size=1", 2, 2, null)]
    [InlineData("Host=db", 2, 2, null)]
    [InlineData("Host=db", 100, 100, null)]
    [InlineData("Host=db;Maximum Pool Size=100", 100, 100, null)]
    [InlineData("Host=db;Maximum Pool Size=3", 3, 3, null)]
    public void A_registered_pools_cap_is_the_option_held_to_the_data_sources_pool_size(string connectionString, int option, int expectedMax, int? expectedPoolSize)
    {
        using NpgsqlDataSource source = NpgsqlDataSource.Create(connectionString);

        StudioConnectionPool pool = StudioConnectionPools.Register(source, option, null, "Postgres Studio", TimeSpan.FromSeconds(5));

        StudioConnectionPools.EffectiveMax(connectionString, option).Should().Be(expectedMax);
        pool.Max.Should().Be(expectedMax);
        pool.Configured.Should().Be(option);
        pool.PoolSize.Should().Be(expectedPoolSize);
    }

    [Fact]
    public void A_connection_string_Npgsql_cannot_read_is_held_to_the_option_alone()
    {
        StudioConnectionPools.EffectiveMax("Host=db;Not An Option=1", 7).Should().Be(7);
        StudioConnectionPools.EffectiveMax(null, 7).Should().Be(7, "an unset string is Npgsql's default of 100");
    }

    /// <summary>The <c>Configure</c> path - Marten's own data sources, which no provider registers - is held to the pool size too.</summary>
    [Fact]
    public void A_pool_reached_through_Of_after_Configure_is_held_to_the_data_sources_pool_size()
    {
        // No reset afterwards: every studio provider this suite builds calls Configure, and a reset would clear it under a parallel test.
        StudioConnectionPools.Configure(2, null, "Marten Studio", TimeSpan.FromSeconds(5));

        using NpgsqlDataSource small = NpgsqlDataSource.Create("Host=nowhere.invalid;Maximum Pool Size=1");
        using NpgsqlDataSource large = NpgsqlDataSource.Create("Host=nowhere.invalid;Maximum Pool Size=50");

        StudioConnectionPool? bounded = StudioConnectionPools.Of(small);
        bounded.Should().NotBeNull();
        bounded!.Max.Should().Be(1);
        bounded.Configured.Should().Be(2);
        bounded.PoolSize.Should().Be(1);

        StudioConnectionPool? free = StudioConnectionPools.Of(large);
        free!.Max.Should().Be(2);
        free.PoolSize.Should().BeNull();
    }

    [Fact]
    public async Task The_busy_sentence_says_the_pool_size_held_the_cap_only_when_it_did()
    {
        StudioConnectionPool bounded = new(1, null, TimeSpan.FromMilliseconds(100), configured: 2, poolSize: 1);
        StudioConnectionLease held = await bounded.AcquireAsync(new StudioConnectionLease(bounded, reentrant: false), Token);
        Func<Task> wait = () => bounded.AcquireAsync(new StudioConnectionLease(bounded, reentrant: false), Token);

        (await wait.Should().ThrowAsync<StudioConnectionsBusyException>()).Which.Message.Should().Be(
            "The studio's connections to this database are all in use (1 at most, MaxStudioConnections, held to the data source's Maximum Pool Size of 1), "
            + "and none came free within 0.1 s. Try again in a moment.");
        PostgresFailure.IsStudioBusy(new StudioConnectionsBusyException(1, TimeSpan.FromSeconds(30), poolSize: 1)).Should().BeTrue();
        held.Release();

        new StudioConnectionsBusyException(2, TimeSpan.FromSeconds(30)).Message.Should().Be(
            "The studio's connections to this database are all in use (2 at most, MaxStudioConnections), and none came free within 30 s. Try again in a moment.");
    }

    /// <summary>Issue #49: the pages' busy clause names the pool size only when it held the cap, whatever wraps the exception.</summary>
    [Fact]
    public void The_pages_busy_clause_names_the_pool_size_only_when_it_held_the_cap()
    {
        StudioConnectionsBusyException bounded = new(1, TimeSpan.FromSeconds(30), poolSize: 1);
        StudioConnectionsBusyException unbounded = new(2, TimeSpan.FromSeconds(30));

        bounded.PoolSize.Should().Be(1);
        unbounded.PoolSize.Should().BeNull();
        PostgresFailure.CouldNotConnectClause(new InvalidOperationException("wrapped", bounded)).Should().Be(
            "the studio's connections to this database were all in use (MaxStudioConnections, held to the data source's Maximum Pool Size of 1); try again in a moment");
        PostgresFailure.CouldNotConnectClause(unbounded).Should().Be(
            "the studio's connections to this database were all in use (MaxStudioConnections); try again in a moment");
        PostgresFailure.CouldNotConnect(bounded).Should().EndWith("Maximum Pool Size of 1); try again in a moment.");
        PostgresFailure.CouldNotConnectClause(new NpgsqlException("refused")).Should().Be("the studio could not connect to this database");
    }

    [Fact]
    public void The_health_pages_share_line_names_the_pool_size_only_when_it_held_the_cap()
    {
        IStudioSettings settings = PostgresStudioSettings.Of(new PostgresStudioOptions());

        HealthPresentation.Share(new StudioConnectionShare(2, 1, 2, null), settings).Should().Be(
            "It holds at most 2 of the pool's connections at a time (PostgresStudioOptions.MaxStudioConnections), 1 now.");
        HealthPresentation.Share(new StudioConnectionShare(1, 0, 2, 1), settings).Should().Be(
            "It holds at most 1 of the pool's connections at a time (PostgresStudioOptions.MaxStudioConnections is 2; the data source's Maximum Pool Size is 1), 0 now.");
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
