using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Integration.Tests.Documents;
using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// A conjoined event store with <c>UseTenantPartitionedEvents</c> and event skipping: each tenant numbers its events from
/// a sequence of its own, so acme's event #1 and globex's event #1 are two events with one <c>seq_id</c>.
/// </summary>
public sealed class PerTenantSkipFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The tenant the skip is asked from.</summary>
    public const string Acme = "acme";

    /// <summary>The tenant whose same-numbered event must not move.</summary>
    public const string Globex = "globex";

    private ServiceProvider? provider;

    /// <summary>The schema.</summary>
    public string Schema { get; } = "sec1_fix4_per_tenant_skip";

    /// <summary>The store.</summary>
    public DocumentStore Store { get; private set; } = null!;

    /// <summary>The studio's event service, every capability on.</summary>
    internal IEventDataService Events { get; private set; } = null!;

    /// <summary>The audit ring.</summary>
    internal StudioActionLogService Audit { get; private set; } = null!;

    /// <summary>The store and write policies' answers - everything allowed unless a test says otherwise.</summary>
    internal FakeStoreAuthorizationService Policy { get; } = new();

    /// <summary>What the studio logged.</summary>
    internal LogCapture Logs { get; } = new();

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(Schema);

        Store = DocumentStore.For(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // What UseTenantPartitionedEvents requires: conjoined events and a quick append mode (the per-tenant sequence
            // pick lives in the quick-append function), and conjoined documents.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            options.Events.EnableEventSkippingInProjectionsOrSubscriptions = true;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Events.AddEventType<OrderPlaced>();
        });

        await Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await Store.Advanced.AddMartenManagedTenantsAsync(CancellationToken.None, Acme, Globex);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(Logs));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton<IDocumentStore>(Store);

        // Both policies set and asked of a fake that allows everything until a reach test narrows it, and resets it.
        services.AddSingleton<IAuthorizationService>(Policy);
        services.AddMartenStudio(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = "studio-store";
            options.WriteAuthorizationPolicy = "studio-write";
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(Globex);
        });

        provider = services.BuildServiceProvider();
        Events = provider.CreateScope().ServiceProvider.GetRequiredService<IEventDataService>();
        Audit = provider.GetRequiredService<StudioActionLogService>();
    }

    /// <summary>Appends one event to a new stream of <paramref name="tenant" />, and answers its <c>seq_id</c>.</summary>
    public async Task<long> AppendOneAsync(string tenant)
    {
        Guid id = Guid.CreateVersion7();

        await using (IDocumentSession session = Store.LightweightSession(tenant))
        {
            session.Events.StartStream(id, new OrderPlaced(tenant + "-customer"));
            await session.SaveChangesAsync();
        }

        return (long) (await ScalarAsync("select seq_id from " + SqlIdentifier.Qualify(Schema, "mt_events") + " where stream_id = @id", id))!;
    }

    /// <summary>
    /// One event for each tenant, appended until both have an event of the same number - whatever the tests before this
    /// one appended - and that number for each.
    /// </summary>
    public async Task<(long Acme, long Globex)> AppendOneEachWithOneNumberAsync()
    {
        long acme = await AppendOneAsync(Acme);
        long globex = await AppendOneAsync(Globex);

        while (acme < globex)
        {
            acme = await AppendOneAsync(Acme);
        }

        while (globex < acme)
        {
            globex = await AppendOneAsync(Globex);
        }

        return (acme, globex);
    }

    /// <summary>Whether a tenant's event of a number is skipped, read straight from the table.</summary>
    public async Task<bool> IsSkippedAsync(string tenant, long sequence)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select is_skipped from " + SqlIdentifier.Qualify(Schema, "mt_events") + " where tenant_id = @tenant and seq_id = @seq",
            connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("seq", sequence);

        return (bool) (await command.ExecuteScalarAsync())!;
    }

    private async Task<object?> ScalarAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteScalarAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (provider is not null)
        {
            await provider.DisposeAsync();
        }

        if (Store is not null)
        {
            await Store.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// SEC1-fix4 item 1, live: below Marten 9.51 <c>MarkEventsAsSkipped</c> is <c>where seq_id = ANY(:sequences)</c> with no
/// tenant, so on this store skipping acme's #N would skip globex's #N as well. The studio refuses the skip there, audited,
/// and neither tenant's event moves - which, without the refusal, globex's does. From 9.51 (#5698, L1) Marten's tenant
/// overload adds <c>and tenant_id = :tenant_id</c>, and the studio skips acme's #N and nothing else.
/// </summary>
/// <remarks>
/// Which half runs is the light-up probe's answer for the Marten this run resolved (<c>RuntimeFeature.TenantEventSkipping</c>):
/// the floor and the latest-tested CI legs each prove theirs, and the one assertion both share - globex's event of the same
/// number never moves - is the one the refusal and the light-up both exist for.
/// </remarks>
public class PerTenantSkipLiveTests(PerTenantSkipFixture fixture) : IClassFixture<PerTenantSkipFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool TenantSkipping => RuntimeFeature.TenantEventSkipping.Probe() == true;

    [PostgresFact]
    public async Task Skipping_a_tenants_event_never_skips_another_tenants_event_of_that_number()
    {
        (long acme, long globex) = await fixture.AppendOneEachWithOneNumberAsync();
        globex.Should().Be(acme, "the premise: each tenant numbers its own events, so the two share a seq_id");

        var acmeScope = new StudioScope(MartenStoreRegistry.DefaultStoreKey, string.Empty, PerTenantSkipFixture.Acme);

        // The rows first, then the outcome: what must never happen is globex's event moving, and that is the assertion a
        // service without the refusal - or a light-up that called the tenant-less overload - fails.
        Exception? refused = await Record.ExceptionAsync(() => fixture.Events.SkipEventAsync(acmeScope, acme, Token));

        (await fixture.IsSkippedAsync(PerTenantSkipFixture.Globex, globex)).Should().BeFalse("globex's event with the same number is not acme's to skip");

        StudioActionLogEntry entry = fixture.Audit.GetLatest()
            .First(x => x.Action == EventDataService.SkipEventAction && x.Target == acme.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (TenantSkipping)
        {
            refused.Should().BeNull("Marten 9.51 skips one tenant's event");
            (await fixture.IsSkippedAsync(PerTenantSkipFixture.Acme, acme)).Should().BeTrue("acme's event is the one that was asked for");

            entry.Succeeded.Should().BeTrue();
            entry.Message.Should().Be("marked as skipped in tenant " + PerTenantSkipFixture.Acme);
            entry.Capability.Should().Be(nameof(StudioCapability.ManageDeadLetters));
            return;
        }

        (await fixture.IsSkippedAsync(PerTenantSkipFixture.Acme, acme)).Should().BeFalse("and nothing was written at all");

        refused.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);

        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);
    }

    /// <summary>
    /// With no tenant in the scope the skip is refused at every version - below 9.51 for the numbering, from 9.51 because a
    /// sequence names an event in every tenant - and neither tenant's event moves.
    /// </summary>
    [PostgresFact]
    public async Task Skipping_with_no_tenant_is_refused_and_moves_no_event()
    {
        (long acme, long globex) = await fixture.AppendOneEachWithOneNumberAsync();
        globex.Should().Be(acme);

        var everyone = new StudioScope(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

        Exception? refused = await Record.ExceptionAsync(() => fixture.Events.SkipEventAsync(everyone, acme, Token));

        (await fixture.IsSkippedAsync(PerTenantSkipFixture.Acme, acme)).Should().BeFalse();
        (await fixture.IsSkippedAsync(PerTenantSkipFixture.Globex, globex)).Should().BeFalse();

        refused.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(
            TenantSkipping ? StreamCompactionRules.SkipNeedsTenant : StreamCompactionRules.SkipNeedsOneSequence);
    }

    /// <summary>
    /// Reach, with policies: a visitor the store and write policies allow only <c>(db, acme)</c> skips acme's #N - from
    /// 9.51 it goes through without the whole database ever being asked about, below it the numbering's refusal stands -
    /// and globex's #N never moves.
    /// </summary>
    [PostgresFact]
    public async Task A_visitor_allowed_only_acme_skips_acmes_event_and_never_globexs()
    {
        (long acme, long globex) = await fixture.AppendOneEachWithOneNumberAsync();
        fixture.Policy.Allow(static resource => resource.TenantId == PerTenantSkipFixture.Acme);

        try
        {
            var acmeScope = new StudioScope(MartenStoreRegistry.DefaultStoreKey, string.Empty, PerTenantSkipFixture.Acme);
            int asked = fixture.Policy.Calls.Count;

            Exception? outcome = await Record.ExceptionAsync(() => fixture.Events.SkipEventAsync(acmeScope, acme, Token));

            fixture.Policy.Calls.Skip(asked).Should().NotContain(
                static x => x.Resource.TenantId == null && x.Resource.Capability == nameof(StudioCapability.ManageDeadLetters),
                "a skip reaches one tenant's event, so the database as a whole is never asked about");

            (await fixture.IsSkippedAsync(PerTenantSkipFixture.Globex, globex)).Should().BeFalse();

            if (TenantSkipping)
            {
                outcome.Should().BeNull();
                (await fixture.IsSkippedAsync(PerTenantSkipFixture.Acme, acme)).Should().BeTrue();
            }
            else
            {
                outcome.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);
                (await fixture.IsSkippedAsync(PerTenantSkipFixture.Acme, acme)).Should().BeFalse();
            }
        }
        finally
        {
            fixture.Policy.AllowEverything();
        }
    }

    /// <summary>
    /// The same visitor's globex-scoped skip is refused as a scope denial at every version - the resolver refuses
    /// (db, globex) before the operation runs - audited as one (event 9203), and globex's #N does not move.
    /// </summary>
    [PostgresFact]
    public async Task The_same_visitors_globex_skip_is_refused_as_a_scope_denial_and_globex_does_not_move()
    {
        (_, long globex) = await fixture.AppendOneEachWithOneNumberAsync();
        fixture.Policy.Allow(static resource => resource.TenantId == PerTenantSkipFixture.Acme);

        try
        {
            var globexScope = new StudioScope(MartenStoreRegistry.DefaultStoreKey, string.Empty, PerTenantSkipFixture.Globex);
            string target = globex.ToString(System.Globalization.CultureInfo.InvariantCulture);

            Func<Task> skipping = () => fixture.Events.SkipEventAsync(globexScope, globex, Token);

            (await skipping.Should().ThrowAsync<StudioNotAuthorizedException>()).Which.Scope.TenantId.Should().Be(PerTenantSkipFixture.Globex);

            (await fixture.IsSkippedAsync(PerTenantSkipFixture.Globex, globex)).Should().BeFalse();

            StudioActionLogEntry entry = fixture.Audit.GetLatest()
                .First(x => x.Action == EventDataService.SkipEventAction && x.Target == target);
            entry.Succeeded.Should().BeFalse();
            entry.Message.Should().Be("Not authorized for this store, database or tenant.");
            entry.TenantId.Should().Be(PerTenantSkipFixture.Globex);

            fixture.Logs.Lines.Should().Contain(static x => x.EventId.Id == 9203 && x.Message.Contains(PerTenantSkipFixture.Globex, StringComparison.Ordinal),
                "a scope denial is logged as one");
        }
        finally
        {
            fixture.Policy.AllowEverything();
        }
    }

    /// <summary>
    /// From 9.51 the tenant proof still stands in front of the skip: a sequence acme's scope cannot see - a number past
    /// acme's last event that globex has - is refused as a scope denial, and globex's event does not move.
    /// </summary>
    [PostgresFact]
    public async Task A_number_only_another_tenant_has_is_refused_in_this_tenants_scope()
    {
        // globex is appended to until its last number is one acme does not have.
        long acmeLast = await fixture.AppendOneAsync(PerTenantSkipFixture.Acme);
        long globexOnly;
        do
        {
            globexOnly = await fixture.AppendOneAsync(PerTenantSkipFixture.Globex);
        }
        while (globexOnly <= acmeLast);

        var acmeScope = new StudioScope(MartenStoreRegistry.DefaultStoreKey, string.Empty, PerTenantSkipFixture.Acme);

        Exception? refused = await Record.ExceptionAsync(() => fixture.Events.SkipEventAsync(acmeScope, globexOnly, Token));

        (await fixture.IsSkippedAsync(PerTenantSkipFixture.Globex, globexOnly)).Should().BeFalse();

        refused.Should().NotBeNull();
        if (TenantSkipping)
        {
            refused.Should().BeOfType<StudioNotAuthorizedException>("acme's scope cannot see that event");
        }
    }
}
