using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Integration.Tests.Documents;
using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// DB-0-fix-3, R1 (release blocker, security): "Rewind subscription" from the dead-letter page reaches
/// every tenant of a shared database, so it is authorized for that database as a whole.
/// </summary>
/// <remarks>
/// <para>
/// The DB-0-fix-2 review's probe, as a test. A conjoined store, a real Solo daemon, one poison event per
/// tenant, and a policy that allows <c>(db, acme)</c> and nothing wider. Marten 9.31's
/// <c>RewindSubscriptionProgressAsync</c> opens its session with <c>AllowAnyTenant = true</c>, rewrites
/// every shard's progression row of the projection and deletes the projection's dead letters at or above
/// the floor with no tenant predicate, and the daemon restarts every agent of it from there. Before the
/// fix the rewind went through: <c>(db, null)</c> was never asked, <c>globex</c>'s dead letter was
/// deleted and <c>globex</c>'s poison event re-applied - by a visitor allowed only <c>acme</c>.
/// </para>
/// <para>
/// The second test is the other half of the claim the dialog now makes: with the database as a whole
/// allowed, the rewind goes ahead and does reach <c>globex</c>. That is Marten's behaviour, not a bug, and
/// it is exactly why the question has to be the wider one.
/// </para>
/// <para>
/// Every wait is on a condition with a deadline, never a fixed delay.
/// </para>
/// </remarks>
public class RewindReachLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private const string Schema = "events_rewind_reach";

    private const string Acme = "acme";

    private const string Globex = "globex";

    private readonly FakeStoreAuthorizationService policy = new();

    private IHost? host;
    private IDocumentStore? store;
    private string databaseId = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        CancellationToken token = TestContext.Current.CancellationToken;
        await postgres.CreateSchemaAsync(Schema, token);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMarten(options =>
            {
                options.Connection(postgres.ConnectionString);
                options.DatabaseSchemaName = Schema;
                options.Events.DatabaseSchemaName = Schema;
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Events.TenancyStyle = TenancyStyle.Conjoined;
                options.Policies.AllDocumentsAreMultiTenanted();
                options.Events.AddEventType<OrderPlaced>();
                options.Events.AddEventType<PoisonPill>();
                options.Projections.Add(new PoisonProjection(), ProjectionLifecycle.Async);
            })
            .UseLightweightSessions()
            .AddAsyncDaemon(DaemonMode.Solo);

        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        builder.Services.AddSingleton<IAuthorizationService>(policy);
        builder.Services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = "studio-store";
            options.WriteAuthorizationPolicy = "studio-write";
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(Globex);
        });

        host = builder.Build();
        store = host.Services.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        // acme's poison first, so its sequence is the lower one and a rewind to it is a floor below
        // globex's too.
        foreach (string tenant in new[] { Acme, Globex })
        {
            await using IDocumentSession session = store.LightweightSession(tenant);
            session.Events.StartStream(Guid.CreateVersion7(), new OrderPlaced(tenant), new PoisonPill(tenant));
            await session.SaveChangesAsync(token);
        }

        await host.StartAsync(token);
        databaseId = (await store.Storage.AllDatabases())[0].Id.Identity;

        await WaitForAsync(async () => (await LettersAsync(token)).Count >= 2, "a dead letter for each tenant", token);
    }

    /// <summary>
    /// The reviewer's probe, inverted into the assertion it should have been: a visitor allowed only
    /// <c>acme</c> is refused, the refusal names the database as a whole, and nothing of <c>globex</c>'s -
    /// nor of <c>acme</c>'s - was deleted or replayed.
    /// </summary>
    [PostgresFact]
    public async Task An_acme_only_visitor_is_refused_and_globex_is_untouched()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        policy.Allow(static resource => resource.TenantId == Acme);

        IReadOnlyList<DeadLetterEvent> before = await LettersAsync(token);
        DeadLetterEvent acmeLetter = before.Single(static x => x.TenantId == Acme);
        DeadLetterEvent globexLetter = before.Single(static x => x.TenantId == Globex);
        globexLetter.EventSequence.Should().BeGreaterThan(acmeLetter.EventSequence);

        using IServiceScope scope = host!.Services.CreateScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();
        var acme = new StudioScope(MartenStoreRegistry.DefaultStoreKey, databaseId, Acme);

        Func<Task> rewinding = () => events.RewindSubscriptionAsync(acme, acmeLetter.ProjectionName, acmeLetter.EventSequence, token);

        StudioNotAuthorizedException refused = (await rewinding.Should().ThrowAsync<StudioNotAuthorizedException>(
            "the rewind would delete globex's dead letter and re-apply globex's poison event")).Which;

        refused.Scope.TenantId.Should().BeNull();
        refused.Scope.DatabaseId.Should().Be(databaseId);

        policy.Calls.Should().Contain(
            x => x.Resource.DatabaseIdentifier == databaseId && x.Resource.TenantId == null,
            "the whole-database question is asked, which the probe proved it never was");

        // Nothing moved: the same two records, by id. A rewind that had gone through would have deleted
        // both (they are at or above the floor) before the daemon wrote new ones.
        IReadOnlyList<DeadLetterEvent> after = await LettersAsync(token);
        after.Select(static x => x.Id).Should().BeEquivalentTo(before.Select(static x => x.Id));

        StudioActionLogEntry entry = host.Services.GetRequiredService<StudioActionLogService>().GetLatest()
            .First(static x => x.Action == EventDataService.RewindSubscriptionAction);

        entry.Succeeded.Should().BeFalse();
        entry.TenantId.Should().BeNull("the audit names the tenant-less scope that was refused");
    }

    /// <summary>
    /// With the database as a whole allowed, the same visitor's rewind goes through - and does exactly what
    /// the dialog says: <c>globex</c>'s dead letter is deleted and <c>globex</c>'s poison event re-applied,
    /// recorded again under a new id.
    /// </summary>
    [PostgresFact]
    public async Task With_the_database_allowed_the_rewind_reaches_every_tenant_as_the_dialog_says()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        policy.Allow(static resource => resource.TenantId is null or Acme);

        IReadOnlyList<DeadLetterEvent> before = await LettersAsync(token);
        DeadLetterEvent acmeLetter = before.Single(static x => x.TenantId == Acme);
        DeadLetterEvent globexLetter = before.Single(static x => x.TenantId == Globex);

        using IServiceScope scope = host!.Services.CreateScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();
        var acme = new StudioScope(MartenStoreRegistry.DefaultStoreKey, databaseId, Acme);

        await events.RewindSubscriptionAsync(acme, acmeLetter.ProjectionName, acmeLetter.EventSequence, token);

        await WaitForAsync(
            async () => (await LettersAsync(token)).Any(x => x.TenantId == Globex && x.Id != globexLetter.Id),
            "globex's dead letter to be replaced by the replay the rewind started",
            token);
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

    private async Task<IReadOnlyList<DeadLetterEvent>> LettersAsync(CancellationToken token)
    {
        // DeadLetterEvent is SingleTenanted whatever the store's policy says, so a plain session reads
        // every tenant's records - which is what these assertions need.
        await using IQuerySession session = store!.QuerySession();
        return await session.Query<DeadLetterEvent>().ToListAsync(token);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what, CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Waited " + Deadline + " for " + what + ".");
            }

            await timer.WaitForNextTickAsync(token);
        }
    }
}
