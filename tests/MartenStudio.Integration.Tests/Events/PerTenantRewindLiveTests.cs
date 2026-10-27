using System.Globalization;

using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Integration.Tests.Documents;
using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// L2, live: "Rewind subscription to this event" on a store that numbers its events per tenant
/// (<c>UseTenantPartitionedEvents</c>), against a real Solo daemon running one agent per tenant.
/// </summary>
/// <remarks>
/// <para>
/// <b>From Marten 9.51 with JasperFx 2.91</b> (<c>RuntimeFeature.TenantSubscriptionRewind</c>) the studio rewinds one
/// tenant's agents: the daemon's per-tenant <c>RewindSubscriptionAsync</c> stops acme's agent and no other, Marten
/// rewrites acme's shard row through the tenant agent's identity and deletes acme's dead letters of the projection at or
/// above the floor, and the agent restarts against acme's own high-water mark. What is asserted is what that promises
/// and the store-global rewind broke: acme's poison event is re-applied (a new dead letter at the same sequence), and
/// globex's dead letter and progression row are exactly as they were. The authorization is the tenant's - the write
/// policy asked about (store, database, acme) - because nothing else is reached.
/// </para>
/// <para>
/// <b>Below either</b> the refusal SEC1-fix4 put in stands, unchanged, and nothing moves. CI runs both: the floor proves
/// the refusal, the latest-tested leg the light-up.
/// </para>
/// <para>
/// Every wait is on a condition with a deadline, never a fixed delay.
/// </para>
/// </remarks>
public class PerTenantRewindLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Acme = "acme";

    private const string Globex = "globex";

    /// <summary>The projection's name as Marten registers it - an <c>EventProjection</c>'s full type name.</summary>
    private static readonly string Projection = typeof(PoisonProjection).FullName!;

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly FakeStoreAuthorizationService policy = new();
    private readonly LogCapture logs = new();

    private IHost? host;
    private IDocumentStore? store;
    private string databaseId = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Schema { get; } = "events_rewind_tenant";

    private static bool TenantRewind => RuntimeFeature.TenantSubscriptionRewind.Probe() == true;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(Schema, Token);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMarten(options =>
            {
                options.Connection(postgres.ConnectionString);
                options.DatabaseSchemaName = Schema;
                options.Events.DatabaseSchemaName = Schema;
                options.AutoCreateSchemaObjects = AutoCreate.All;

                // What UseTenantPartitionedEvents requires: conjoined events, a quick append mode, conjoined documents.
                options.Events.TenancyStyle = TenancyStyle.Conjoined;
                options.Events.UseTenantPartitionedEvents = true;
                options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
                options.Policies.AllDocumentsAreMultiTenanted();

                options.Events.AddEventType<OrderPlaced>();
                options.Events.AddEventType<PoisonPill>();

                // Async, so the daemon runs it per tenant and its failures become dead letters.
                options.Projections.Add(new PoisonProjection(), ProjectionLifecycle.Async);
            })
            .UseLightweightSessions()
            .AddAsyncDaemon(DaemonMode.Solo);

        builder.Logging.AddProvider(logs);

        // Both policies set and asked of a fake that allows everything until a test narrows it - the reach tests' visitor.
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        builder.Services.AddSingleton<IAuthorizationService>(policy);
        builder.Services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = "studio-store";
            options.WriteAuthorizationPolicy = "studio-write";
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(Globex);
        });

        host = builder.Build();
        store = host.Services.GetRequiredService<IDocumentStore>();

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        // Registered before the daemon starts, so it starts one agent per tenant rather than the store-global shard.
        await store.Advanced.AddMartenManagedTenantsAsync(Token, Acme, Globex);

        foreach (string tenant in (string[]) [Acme, Globex])
        {
            await using IDocumentSession session = store.LightweightSession(tenant);
            session.Events.StartStream(Guid.CreateVersion7(), new OrderPlaced(tenant + "-customer"), new PoisonPill("a deliberately unapplicable event"));
            await session.SaveChangesAsync(Token);
        }

        databaseId = (await store.Storage.AllDatabases())[0].Id.Identity;

        await host.StartAsync(Token);

        // One agent per tenant, each recording its tenant's poison event as a dead letter - at both ends, so that the
        // floor's "nothing moves" is about dead letters that are there.
        await WaitForAsync(
            async () => (await LettersAsync(Acme)).Count > 0 && (await LettersAsync(Globex)).Count > 0,
            "the daemon to record a dead letter in each tenant",
            DescribeDaemonAsync);

        // And settled: the dead letter is recorded first, and the agent's progression row follows when it has finished the
        // batch - so a test that read "globex's progression" the moment the letter appeared read nothing, and the row
        // arrived at the tenant's last event while it ran (issue #27: "Expected value to be <null>, but found 2L"). The
        // state every test here starts from is each agent at its tenant's last event, so nothing is still moving.
        await WaitForAsync(
            async () => await ProgressAsync(ShardName(Acme)) >= await LastEventAsync(Acme)
                && await ProgressAsync(ShardName(Globex)) >= await LastEventAsync(Globex),
            "each tenant's agent to reach its tenant's last event",
            DescribeDaemonAsync);
    }

    /// <summary>The name of a tenant's progression row: the projection's shard for that tenant.</summary>
    private static string ShardName(string tenant) => $"{Projection}:All:{tenant}";

    /// <summary>The sequence of a tenant's newest event, or <see langword="null" /> when it has none.</summary>
    private async Task<long?> LastEventAsync(string tenant)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            "select max(seq_id) from " + SqlIdentifier.Qualify(Schema, "mt_events") + " where tenant_id = @tenant",
            connection);
        command.Parameters.AddWithValue("tenant", tenant);

        return await command.ExecuteScalarAsync(Token) is long value ? value : null;
    }

    /// <summary>What the daemon and the dead-letter table hold, for a wait that timed out.</summary>
    private async Task<string> DescribeDaemonAsync()
    {
        var coordinator = host!.Services.GetRequiredService<Marten.Events.Daemon.Coordination.IProjectionCoordinator>();
        IProjectionDaemon daemon = coordinator.DaemonForMainDatabase();

        await using IQuerySession session = store!.QuerySession();
        IReadOnlyList<DeadLetterEvent> all = await session.Query<DeadLetterEvent>().ToListAsync(Token);

        return $"daemon running: {daemon.IsRunning}; agents: [{string.Join(", ", daemon.CurrentAgents().Select(static x => x.Name.Identity + "=" + x.Status))}]; "
            + $"dead letters: [{string.Join(", ", all.Select(static x => x.ProjectionName + "/" + x.ShardName + "/" + x.TenantId + "#" + x.EventSequence))}]";
    }

    /// <summary>The sequence of a tenant's poison event: its newest dead letter's, or #2 - the second event it appended.</summary>
    private static long PoisonSequence(IReadOnlyList<DeadLetterEvent> letters) =>
        letters.Count > 0 ? letters.Max(static x => x.EventSequence) : 2;

    /// <summary>
    /// From 9.51: acme's rewind re-applies acme's poison event and leaves globex's dead letter and progression alone.
    /// Below it: refused, audited, and nothing moves.
    /// </summary>
    [PostgresFact]
    public async Task A_tenants_rewind_re_applies_that_tenants_event_and_leaves_the_other_tenant_alone()
    {
        using IServiceScope scope = host!.Services.CreateScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();

        IReadOnlyList<DeadLetterEvent> acmeBefore = await LettersAsync(Acme);
        IReadOnlyList<DeadLetterEvent> globexBefore = await LettersAsync(Globex);
        long? globexProgress = await ProgressAsync($"{Projection}:All:{Globex}");

        long sequence = PoisonSequence(acmeBefore);
        StudioScope acmeScope = new(MartenStoreRegistry.DefaultStoreKey, databaseId, Acme);

        Exception? outcome = await Record.ExceptionAsync(
            () => events.RewindSubscriptionAsync(acmeScope, Projection, sequence, Token));

        StudioActionLogEntry entry = host.Services.GetRequiredService<StudioActionLogService>().GetLatest()
            .First(static x => x.Action == EventDataService.RewindSubscriptionAction);

        if (!TenantRewind)
        {
            outcome.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(StreamCompactionRules.RewindNeedsOneSequence);
            entry.Succeeded.Should().BeFalse();
            entry.Message.Should().Be(StreamCompactionRules.RewindNeedsOneSequence);

            (await LettersAsync(Acme)).Select(static x => x.Id).Should().BeEquivalentTo(acmeBefore.Select(static x => x.Id), "nothing ran");
            (await LettersAsync(Globex)).Select(static x => x.Id).Should().BeEquivalentTo(globexBefore.Select(static x => x.Id));
            (await ProgressAsync($"{Projection}:All:{Globex}")).Should().Be(globexProgress);
            return;
        }

        DeadLetterEvent letter = acmeBefore.Single(x => x.EventSequence == sequence);

        outcome.Should().BeNull();
        entry.Succeeded.Should().BeTrue();
        entry.Message.Should().Contain("in tenant " + Acme);
        entry.Target.Should().Be($"{Projection} to #{letter.EventSequence.ToString(CultureInfo.InvariantCulture)}");

        // Marten deleted acme's dead letter at the floor, and the restarted agent re-applied the poison event: a new
        // record at the same sequence, which only a rewind that really moved acme's shard back can produce.
        await WaitForAsync(
            async () => (await LettersAsync(Acme)).Any(x => x.EventSequence == letter.EventSequence && x.Id != letter.Id),
            "acme's agent to re-apply its poison event and record a new dead letter");

        (await LettersAsync(Acme)).Should().NotContain(x => x.Id == letter.Id, "Marten deletes the tenant's dead letters at or above the floor");

        // globex: the same dead letter, untouched - the store-global rewind would have deleted it, every tenant's being
        // at or above a floor of one - and the same progression.
        (await LettersAsync(Globex)).Select(static x => x.Id).Should().BeEquivalentTo(globexBefore.Select(static x => x.Id),
            "a per-tenant rewind deletes only that tenant's dead letters");
        (await ProgressAsync($"{Projection}:All:{Globex}")).Should().Be(globexProgress, "globex's agent and row were not rewound");
    }

    /// <summary>
    /// The store-global form - no tenant in the scope - is refused at every version: below 9.51 for the numbering, from
    /// 9.51 because the floor is a position in one tenant's events. Nothing moves.
    /// </summary>
    [PostgresFact]
    public async Task A_rewind_with_no_tenant_is_refused_at_every_version()
    {
        using IServiceScope scope = host!.Services.CreateScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();

        IReadOnlyList<DeadLetterEvent> acmeBefore = await LettersAsync(Acme);
        IReadOnlyList<DeadLetterEvent> globexBefore = await LettersAsync(Globex);
        long sequence = PoisonSequence(globexBefore);

        StudioScope everyone = new(MartenStoreRegistry.DefaultStoreKey, databaseId, null);

        Func<Task> rewinding = () => events.RewindSubscriptionAsync(everyone, Projection, sequence, Token);

        (await rewinding.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(
            TenantRewind ? StreamCompactionRules.RewindNeedsTenant : StreamCompactionRules.RewindNeedsOneSequence);

        // The fixture waited for a dead letter in each tenant at both ends, and the refusal came before anything was asked
        // of the daemon: both tenants' dead letters are exactly the ones there were.
        (await LettersAsync(Acme)).Select(static x => x.Id).Should().BeEquivalentTo(acmeBefore.Select(static x => x.Id));
        (await LettersAsync(Globex)).Select(static x => x.Id).Should().BeEquivalentTo(globexBefore.Select(static x => x.Id));
    }

    /// <summary>
    /// Reach, with policies: a visitor the store and write policies allow only <c>(db, acme)</c>. From 9.51 the rewind of
    /// acme goes through - the whole-database question is never asked, because nothing beyond acme is reached - and
    /// globex's dead letter and progression row do not move. Below 9.51 the numbering's refusal stands.
    /// </summary>
    [PostgresFact]
    public async Task A_visitor_allowed_only_acme_rewinds_acme_and_globex_does_not_move()
    {
        policy.Allow(static resource => resource.TenantId == Acme);

        using IServiceScope scope = host!.Services.CreateScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();

        IReadOnlyList<DeadLetterEvent> acmeBefore = await LettersAsync(Acme);
        IReadOnlyList<DeadLetterEvent> globexBefore = await LettersAsync(Globex);
        long? globexProgress = await ProgressAsync($"{Projection}:All:{Globex}");
        long sequence = PoisonSequence(acmeBefore);

        StudioScope acmeScope = new(MartenStoreRegistry.DefaultStoreKey, databaseId, Acme);

        Exception? outcome = await Record.ExceptionAsync(
            () => events.RewindSubscriptionAsync(acmeScope, Projection, sequence, Token));

        policy.Calls.Should().NotContain(
            static x => x.Resource.TenantId == null && x.Resource.Capability == nameof(StudioCapability.ManageDeadLetters),
            "the per-tenant rewind reaches acme alone, so the database as a whole is never asked about");

        if (!TenantRewind)
        {
            outcome.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(StreamCompactionRules.RewindNeedsOneSequence);
        }
        else
        {
            outcome.Should().BeNull("the policy allows acme, and acme is all the rewind reaches");

            DeadLetterEvent letter = acmeBefore.Single(x => x.EventSequence == sequence);
            await WaitForAsync(
                async () => (await LettersAsync(Acme)).Any(x => x.EventSequence == letter.EventSequence && x.Id != letter.Id),
                "acme's agent to re-apply its poison event and record a new dead letter");
        }

        (await LettersAsync(Globex)).Select(static x => x.Id).Should().BeEquivalentTo(globexBefore.Select(static x => x.Id));
        (await ProgressAsync($"{Projection}:All:{Globex}")).Should().Be(globexProgress);
    }

    /// <summary>
    /// The same visitor's globex-scoped rewind is refused as a scope denial at every version - the resolver refuses
    /// (db, globex) before the operation runs - audited as one (event 9203), and globex's dead letter and progression row
    /// do not move.
    /// </summary>
    [PostgresFact]
    public async Task The_same_visitors_globex_rewind_is_refused_as_a_scope_denial_and_globex_does_not_move()
    {
        policy.Allow(static resource => resource.TenantId == Acme);

        using IServiceScope scope = host!.Services.CreateScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();

        IReadOnlyList<DeadLetterEvent> globexBefore = await LettersAsync(Globex);
        long? globexProgress = await ProgressAsync($"{Projection}:All:{Globex}");
        long sequence = PoisonSequence(globexBefore);

        StudioScope globexScope = new(MartenStoreRegistry.DefaultStoreKey, databaseId, Globex);

        Func<Task> rewinding = () => events.RewindSubscriptionAsync(globexScope, Projection, sequence, Token);

        (await rewinding.Should().ThrowAsync<StudioNotAuthorizedException>()).Which.Scope.TenantId.Should().Be(Globex);

        StudioActionLogEntry entry = host.Services.GetRequiredService<StudioActionLogService>().GetLatest()
            .First(static x => x.Action == EventDataService.RewindSubscriptionAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be("Not authorized for this store, database or tenant.");
        entry.TenantId.Should().Be(Globex);

        logs.Lines.Should().Contain(static x => x.EventId.Id == 9203 && x.Message.Contains(Globex, StringComparison.Ordinal),
            "a scope denial is logged as one");

        (await LettersAsync(Globex)).Select(static x => x.Id).Should().BeEquivalentTo(globexBefore.Select(static x => x.Id));
        (await ProgressAsync($"{Projection}:All:{Globex}")).Should().Be(globexProgress);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (host is null)
        {
            return;
        }

        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A daemon that will not stop must not fail a test that already passed.
        }

        host.Dispose();
    }

    /// <summary>One tenant's dead letters of the projection, read straight from Marten's document.</summary>
    private async Task<IReadOnlyList<DeadLetterEvent>> LettersAsync(string tenant)
    {
        await using IQuerySession session = store!.QuerySession();

        return await session.Query<DeadLetterEvent>()
            .Where(x => x.TenantId == tenant && x.ProjectionName == Projection)
            .ToListAsync(Token);
    }

    /// <summary>A progression row's position, or <see langword="null" /> when there is none.</summary>
    private async Task<long?> ProgressAsync(string name)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            "select last_seq_id from " + SqlIdentifier.Qualify(Schema, "mt_event_progression") + " where name = @name",
            connection);
        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteScalarAsync(Token) is long value ? value : null;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what, Func<Task<string>>? describe = null)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                string state = describe is null ? string.Empty : " " + await describe();
                throw new TimeoutException($"Waited {Deadline.TotalSeconds:0} s for {what} and it did not happen.{state}");
            }

            await timer.WaitForNextTickAsync(Token);
        }
    }
}
