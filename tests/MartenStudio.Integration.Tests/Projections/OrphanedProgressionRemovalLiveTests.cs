using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Live;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// 1.2-PROJ (a), live: the Remove button's service call removes exactly the orphaned progression row it was asked for -
/// Marten's own <c>DeleteProjectionProgressByShardNameAsync</c>, after the re-check - and refuses, audited, every name that is
/// not a removable orphan, at the Marten floor and at the latest-tested Marten alike.
/// </summary>
/// <remarks>
/// The row is real, written by the old deployment's own daemon exactly as <c>OrphanedProgressionLiveTests</c> does it. A second
/// and a tenant's row are inserted by the test's own SQL, because they are what "exactly that row" is measured against, and a
/// tenant-partitioned store with an orphaned tenant row would prove nothing the name's own <c>{Name}:{ShardKey}:{tenant}</c>
/// shape does not (Marten deletes by the identity given).
/// </remarks>
public class OrphanedProgressionRemovalLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string BeforeRename = "DailySalesBeforeRename";

    private const string OrphanRow = BeforeRename + ":All";

    private const string OtherOrphan = "OtherRetired:All";

    private const string TenantRow = "DailySales:All:acme";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Schema => GetType().Name.ToLowerInvariant();

    private string EventSchema => Schema + "_events";

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(Schema, Token);
        await postgres.CreateSchemaAsync(EventSchema, Token);

        await using DocumentStore before = DocumentStore.For(options => Configure(options, BeforeRename));
        await before.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await SampleDataSeeder.SeedEventsAsync(before, Token);

        using IProjectionDaemon daemon = await before.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        await ProjectionsFixture.WaitForAsync(
            async () => await PositionAsync(OrphanRow) > 0,
            "the old deployment's daemon to write " + OrphanRow,
            TimeSpan.FromSeconds(60),
            Token);

        await daemon.StopAllAsync();
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    [PostgresFact]
    public async Task Remove_deletes_exactly_the_named_row_and_audits_it()
    {
        await InsertRowAsync(OtherOrphan, 5);
        long? mark = await PositionAsync("HighWaterMark");
        long? position = await PositionAsync(OrphanRow);

        await using Studio studio = Build("DailySales");

        (await studio.ViewAsync()).Orphans.Rows.Select(static x => x.Name).Should().BeEquivalentTo([OrphanRow, OtherOrphan]);

        OrphanRemoval removal = await studio.RemoveAsync(OrphanRow);

        removal.Removed.Should().BeTrue();
        removal.Name.Should().Be(OrphanRow);

        (await PositionAsync(OrphanRow)).Should().BeNull("Marten removed the row by its exact name");
        (await PositionAsync(OtherOrphan)).Should().Be(5, "and only that row");
        (await PositionAsync("HighWaterMark")).Should().Be(mark, "the high-water row is not touched");
        position.Should().BePositive();

        studio.ActionLog.GetLatest().Should().ContainSingle(x =>
            x.Action == ProjectionDataService.RemoveOrphanAction && x.Succeeded && x.Target == OrphanRow && x.Capability == "CorrectProgression");

        (await studio.ViewAsync()).Orphans.Rows.Should().ContainSingle().Which.Name.Should().Be(OtherOrphan);
    }

    [PostgresFact]
    public async Task A_name_that_stopped_being_an_orphan_is_refused_audited_and_removes_nothing()
    {
        await InsertRowAsync(OtherOrphan, 5);
        await using Studio studio = Build("DailySales");

        (await studio.RemoveAsync(OrphanRow)).Removed.Should().BeTrue();

        // The same click again - the row is gone.
        OrphanRemoval again = await studio.RemoveAsync(OrphanRow);

        again.Removed.Should().BeFalse();
        again.Message.Should().Be(OrphanWording.RemovalNoLongerOrphan);
        (await PositionAsync(OtherOrphan)).Should().Be(5, "a refused removal removes nothing else");

        // A registered projection's shard is no orphan, and bookkeeping is not a shard.
        (await studio.RemoveAsync("DailySales:All")).Message.Should().Be(OrphanWording.RemovalNoLongerOrphan);
        (await studio.RemoveAsync("HighWaterMark")).Message.Should().Be(OrphanWording.RemovalNoLongerOrphan);
        (await PositionAsync("HighWaterMark")).Should().NotBeNull();

        studio.ActionLog.GetLatest().Where(static x => x.Action == ProjectionDataService.RemoveOrphanAction && !x.Succeeded)
            .Should().HaveCount(3).And.OnlyContain(static x => x.Message == OrphanWording.RemovalNoLongerOrphan);
    }

    [PostgresFact]
    public async Task A_row_the_daemon_may_still_read_is_not_removed()
    {
        await using Studio gated = Build(BeforeRename, version: 2, gate: true);

        OrphanRemoval refused = await gated.RemoveAsync(OrphanRow);

        refused.Removed.Should().BeFalse();
        refused.Message.Should().Be(OrphanWording.RemovalNotOffered);
        (await PositionAsync(OrphanRow)).Should().BePositive("the side-effect gate reads that row each time version 2 starts");
    }

    /// <summary>
    /// Marten's call can return and leave the row (here a trigger cancels the delete): that is a failure, audited, and - because
    /// Marten has ensured storage and run - the cached view is cleared as a success clears it, so the page does not redraw rows it
    /// cached before. The cache is made long enough that only the clear can explain the second view.
    /// </summary>
    [PostgresFact]
    public async Task A_call_that_leaves_the_row_fails_audited_and_clears_the_cached_view()
    {
        await using Studio studio = Build("DailySales", longCache: true);

        (await studio.ViewAsync()).Orphans.Rows.Should().ContainSingle().Which.Name.Should().Be(OrphanRow);

        string table = SqlIdentifier.Qualify(EventSchema, "mt_event_progression");
        string function = SqlIdentifier.Qualify(EventSchema, "ms_keep_progression_row");
        await ExecuteAsync(
            $"create function {function}() returns trigger language plpgsql as $$ begin return null; end $$;"
            + $" create trigger ms_keep_progression_row before delete on {table} for each row execute function {function}();");

        Func<Task> removing = () => studio.RemoveAsync(OrphanRow);

        await removing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is still in mt_event_progression*");
        (await PositionAsync(OrphanRow)).Should().BePositive("the trigger kept it");

        studio.ActionLog.GetLatest().Should().ContainSingle(x =>
            x.Action == ProjectionDataService.RemoveOrphanAction && !x.Succeeded && x.Target == OrphanRow);

        // Behind the cache: the row goes some other way. Only a cleared cache lets the next view see it.
        await ExecuteAsync($"drop trigger ms_keep_progression_row on {table}; delete from {table} where name = '{OrphanRow}';");

        (await studio.ViewAsync()).Orphans.Rows.Should().BeEmpty("the failed removal cleared the cache");
    }

    [PostgresFact]
    public async Task A_tenants_row_is_removed_alone_and_the_store_global_row_stays()
    {
        await InsertRowAsync(TenantRow, 7);
        await InsertRowAsync(BeforeRename + ":All:acme", 8);

        await using Studio studio = Build("DailySales");

        (await studio.ViewAsync()).Orphans.Rows.Should().Contain(x => x.Name == TenantRow && x.TenantId == "acme");

        (await studio.RemoveAsync(TenantRow)).Removed.Should().BeTrue();

        (await PositionAsync(TenantRow)).Should().BeNull();
        (await PositionAsync(BeforeRename + ":All:acme")).Should().Be(8, "another projection's tenant row is its own");
        (await PositionAsync(OrphanRow)).Should().BePositive("the store-global row of the old name is another row");
    }

    private void Configure(StoreOptions options, string projectionName, uint version = 1, bool gate = false)
    {
        options.Connection(postgres.ConnectionString);
        options.DatabaseSchemaName = Schema;
        options.Events.DatabaseSchemaName = EventSchema;
        options.AutoCreateSchemaObjects = AutoCreate.All;

        var projection = new DailySalesProjection { Name = projectionName, Version = version };
        projection.Options.GateSideEffectsBehindPriorVersion = gate;
        options.Projections.Add(projection, ProjectionLifecycle.Async);
    }

    private Studio Build(string projectionName, uint version = 1, bool gate = false, bool longCache = false)
    {
        var services = new ServiceCollection();

        if (longCache)
        {
            // Registered first, so it wins over the default's one second: a view cached before a failed removal stays cached for
            // the length of the test unless the removal clears it.
            services.AddSingleton(new StudioSnapshotCache(TimeProvider.System, TimeSpan.FromMinutes(10)));
        }

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();
        services.AddMarten(options => Configure(options, projectionName, version, gate));
        services.AddMartenStudio(static options =>
        {
            options.AuthorizationPolicy = null;
            options.Capabilities.CorrectProgression = true;
        });

        return new Studio(services.BuildServiceProvider());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<long?> PositionAsync(string name)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select last_seq_id from " + SqlIdentifier.Qualify(EventSchema, "mt_event_progression") + " where name = @name", connection);
        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteScalarAsync(Token) is long value ? value : null;
    }

    /// <summary>A progression row of the test's own, to measure "exactly that row" against.</summary>
    private async Task InsertRowAsync(string name, long sequence)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "insert into " + SqlIdentifier.Qualify(EventSchema, "mt_event_progression") + " (name, last_seq_id) values (@name, @seq)", connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("seq", sequence);
        await command.ExecuteNonQueryAsync(Token);
    }

    private sealed class Studio(ServiceProvider provider) : IAsyncDisposable
    {
        public StudioActionLogService ActionLog => provider.GetRequiredService<StudioActionLogService>();

        public async Task<ProjectionsView> ViewAsync()
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IProjectionDataService>().GetProjectionsAsync(Scope, Token);
        }

        public async Task<OrphanRemoval> RemoveAsync(string name)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IProjectionDataService>().RemoveOrphanedProgressionAsync(Scope, name, Token);
        }

        private static StudioScope Scope => new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
