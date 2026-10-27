using System.Security.Claims;

using JasperFx;
using JasperFx.Events;
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

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// M4, live: a projection that is renamed - or given a new version - leaves the row its old name wrote in
/// <c>mt_event_progression</c>, and the projections screen counts it, names it and hands it over, at the Marten floor and
/// at the latest-tested Marten alike.
/// </summary>
/// <remarks>
/// <para>
/// The row is real: the old deployment's daemon - one this test owns, the only one there is - runs the projection under
/// its old name until it has written its progress. Then the new deployment's studio, which registers the projection under
/// its new name and runs no daemon, reads the same database. Nothing here writes a progression row by hand except the
/// statement the screen hands over, which the test runs as the host's migration would.
/// </para>
/// <para>
/// The composite store and the per-tenant store showing none are <c>CompositeProjectionLiveTests</c> and
/// <c>TenantPartitionedEventsLiveTests</c>; D10's own fires and quiet tests are <c>OrphanedProgressionHealthLiveTests</c>.
/// </para>
/// </remarks>
public class OrphanedProgressionLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The projection's name in the old deployment.</summary>
    private const string BeforeRename = "DailySalesBeforeRename";

    /// <summary>The row the old deployment's daemon wrote.</summary>
    private const string OrphanRow = BeforeRename + ":All";

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

        // The old deployment, and its daemon - this test's own, never the studio's (AGENTS.md hard rule 11 is about studio
        // code): run until the renamed projection's row is written, then stopped.
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

    /// <summary>
    /// The packet's first case: the renamed projection's row is counted and named, with why nothing advances it and the
    /// statement that removes it - and that statement, run as the host's migration would run it, removes exactly it.
    /// </summary>
    [PostgresFact]
    public async Task A_renamed_projection_leaves_an_orphan_that_is_counted_and_named()
    {
        await using Studio studio = Build(projectionName: "DailySales");

        ProjectionsView view = await studio.ViewAsync();

        view.Orphans.Count.Should().Be(1);
        view.Orphans.NamesShown.Should().BeTrue("no policy is configured, so the visitor may see the whole database");

        OrphanedProgressionRow orphan = view.Orphans.Rows.Should().ContainSingle().Subject;
        orphan.Name.Should().Be(OrphanRow);
        orphan.Reason.Should().Be(OrphanReason.NoSuchProjection);
        orphan.Explanation.Should().Be(
            "No projection or subscription named " + BeforeRename + " is registered: it was renamed or removed.");
        orphan.Sequence.Should().BePositive("the old deployment's daemon processed events under that name");
        orphan.LastUpdated.Should().NotBeNull();
        orphan.ReadByDaemon.Should().BeFalse();
        orphan.RemovalSql.Should().Be(
            "DELETE FROM " + SqlIdentifier.Qualify(EventSchema, "mt_event_progression") + " WHERE \"name\" = '" + OrphanRow + "';");
        orphan.RemovalCall.Should().Be(
            "await ((IEventDatabase) database).DeleteProjectionProgressByShardNameAsync(\"" + OrphanRow + "\");");

        // The new name's shard is the registered one, never started - and the orphan is not one of the shards.
        ShardProgress renamed = view.Progress.Should().ContainSingle().Subject;
        renamed.ShardName.Should().Be("DailySales:All");
        renamed.HasProgressRow.Should().BeFalse();

        ProjectionSummary summary = await studio.SummaryAsync();
        summary.WorstShardName.Should().NotBe(OrphanRow, "an orphan's lag only grows, and is never the worst lag on the Overview");
        summary.Progress.Should().NotContain(x => x.ShardName == OrphanRow);

        // The hand-over, run: the host's migration removes the row, and with it the orphan.
        await ExecuteAsync(orphan.RemovalSql!);
        (await PositionAsync(OrphanRow)).Should().BeNull();

        await ProjectionsFixture.WaitForAsync(
            async () => (await studio.ViewAsync()).Orphans.Count == 0,
            "the removed row to leave the orphans",
            cancellationToken: Token);
    }

    /// <summary>
    /// The premise the screen hands over on, live at both ends: Marten's name-keyed removal looks the name up among the
    /// registered projections and refuses any other - and its removal by the row's exact name (marten#4785) removes the
    /// renamed projection's row and nothing else. That is the call the screen and D10 hand the host.
    /// </summary>
    [PostgresFact]
    public async Task Martens_removal_by_name_refuses_a_projection_no_longer_registered_and_by_identity_removes_the_orphan()
    {
        await using DocumentStore after = DocumentStore.For(options => Configure(options, "DailySales"));
        var database = (IEventDatabase) after.Storage.Database;

        var events = (IEventStore<IDocumentOperations, IQuerySession>) after;
        Func<Task> removing = () => events.DeleteProjectionProgressAsync(database, BeforeRename, Token);

        await removing.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await PositionAsync(OrphanRow)).Should().BePositive("nothing was removed");

        long? mark = await PositionAsync("HighWaterMark");

        // The host's code, as the hand-over spells it - the test's own write.
        await database.DeleteProjectionProgressByShardNameAsync(OrphanRow, Token);

        (await PositionAsync(OrphanRow)).Should().BeNull("Marten removed the row by its exact name, registered or not");
        (await PositionAsync("HighWaterMark")).Should().Be(mark, "and nothing else");
    }

    /// <summary>
    /// A version bump leaves the prior version's row: an orphan, with the registered version named - and, behind
    /// <c>GateSideEffectsBehindPriorVersion</c>, one the daemon still reads, so no statement is offered for it.
    /// </summary>
    [PostgresFact]
    public async Task A_version_bump_leaves_the_prior_versions_row_and_the_side_effect_gate_keeps_it_read()
    {
        await using (Studio bumped = Build(BeforeRename, version: 2))
        {
            ProjectionsView view = await bumped.ViewAsync();

            OrphanedProgressionRow prior = view.Orphans.Rows.Should().ContainSingle().Subject;
            prior.Name.Should().Be(OrphanRow);
            prior.Reason.Should().Be(OrphanReason.OtherVersion);
            prior.Explanation.Should().Be(BeforeRename + " is registered at version 2, and this row is version 1's, which nothing advances any more.");
            prior.RemovalSql.Should().NotBeNull();
            view.Progress.Should().ContainSingle(x => x.ShardName == BeforeRename + ":V2:All");
        }

        await using (Studio gated = Build(BeforeRename, version: 2, gate: true))
        {
            OrphanedProgressionRow read = (await gated.ViewAsync()).Orphans.Rows.Should().ContainSingle().Subject;

            read.ReadByDaemon.Should().BeTrue();
            read.RemovalSql.Should().BeNull("the daemon reads it each time version 2 starts");
            read.Explanation.Should().Contain("GateSideEffectsBehindPriorVersion");
        }
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

    /// <summary>The new deployment's studio: the projection under its new name or version, and no daemon.</summary>
    private Studio Build(string projectionName, uint version = 1, bool gate = false)
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();
        services.AddMarten(options => Configure(options, projectionName, version, gate));
        services.AddMartenStudio(static options => options.AuthorizationPolicy = null);

        return new Studio(services.BuildServiceProvider());
    }

    private async Task<long?> PositionAsync(string name)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select last_seq_id from " + SqlIdentifier.Qualify(EventSchema, "mt_event_progression") + " where name = @name", connection);
        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteScalarAsync(Token) is long value ? value : null;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>One studio container.</summary>
    private sealed class Studio(ServiceProvider provider) : IAsyncDisposable
    {
        public async Task<ProjectionsView> ViewAsync()
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IProjectionDataService>().GetProjectionsAsync(Scope, Token);
        }

        public async Task<ProjectionSummary> SummaryAsync()
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IProjectionDataService>().GetSummaryAsync(Scope, Token);
        }

        private static StudioScope Scope => new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    /// <summary>Who a container-only studio's circuit belongs to: nobody.</summary>
    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
