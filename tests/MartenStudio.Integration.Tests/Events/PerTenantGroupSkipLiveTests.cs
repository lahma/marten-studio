using System.Globalization;

using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Integration.Tests.Documents;
using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// A store with <c>UseTenantPartitionedEvents</c>, where acme's event #N and globex's event #N are two events, and one
/// dead-letter group over both: acme's letter names acme's #N, globex's letter globex's #N.
/// </summary>
public sealed class PerTenantGroupSkipFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The tenant the skip is asked from.</summary>
    public const string Acme = "acme";

    /// <summary>The tenant whose same-numbered event must not move.</summary>
    public const string Globex = "globex";

    private ServiceProvider? provider;

    /// <summary>The grouped view's cache clock, moved on when a test writes letters past the studio.</summary>
    private readonly Health.MovableClock groupClock = new(DateTimeOffset.UtcNow);

    /// <summary>The schema.</summary>
    public string Schema { get; } = "m3_per_tenant_group_skip";

    /// <summary>The store.</summary>
    public DocumentStore Store { get; private set; } = null!;

    /// <summary>The studio's event service, every capability on.</summary>
    internal IEventDataService Events { get; private set; } = null!;

    /// <summary>The audit ring.</summary>
    internal StudioActionLogService Audit { get; private set; } = null!;

    /// <summary>The store and write policies' answers - everything allowed unless a test says otherwise.</summary>
    internal FakeStoreAuthorizationService Policy { get; } = new();

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
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            options.Events.EnableEventSkippingInProjectionsOrSubscriptions = true;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Events.AddEventType<GroupPoison>();
        });

        await Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await Store.Advanced.AddMartenManagedTenantsAsync(CancellationToken.None, Acme, Globex);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton<IDocumentStore>(Store);
        services.AddSingleton<IAuthorizationService>(Policy);
        services.AddSingleton(new DeadLetterGroupCache(groupClock, TimeSpan.FromSeconds(5)));
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

    /// <summary>
    /// One event for each tenant with one number, and a dead letter for each in a group of its own (by projection name),
    /// shaped as the daemon shapes them - and that number.
    /// </summary>
    public async Task<long> SeedGroupAsync(string projection)
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

        await using IDocumentSession session = Store.LightweightSession();

        foreach (string tenant in new[] { Acme, Globex })
        {
            session.Store(new DeadLetterEvent
            {
                Id = Guid.CreateVersion7(),
                ProjectionName = projection,
                ShardName = "All",
                Timestamp = DateTimeOffset.UtcNow,
                ExceptionMessage = "Failure to apply event #" + acme.ToString(CultureInfo.InvariantCulture) + " Id(" + Guid.NewGuid() + ")",
                ExceptionType = "InvalidOperationException",
                EventSequence = acme,
                TenantId = tenant,
            });
        }

        await session.SaveChangesAsync();

        // Written past the studio, so its shared read of the groups (one per interval, D10) is moved past its interval.
        groupClock.Advance(TimeSpan.FromMinutes(1));

        return acme;
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

    private async Task<long> AppendOneAsync(string tenant)
    {
        Guid id = Guid.CreateVersion7();

        await using (IDocumentSession session = Store.LightweightSession(tenant))
        {
            session.Events.StartStream(id, new GroupPoison(tenant));
            await session.SaveChangesAsync();
        }

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select seq_id from " + SqlIdentifier.Qualify(Schema, "mt_events") + " where stream_id = @id", connection);
        command.Parameters.AddWithValue("id", id);

        return (long) (await command.ExecuteScalarAsync())!;
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
/// M3 on a store that numbers events per tenant: the group is grouped across tenants by <c>(seq_id, tenant_id)</c>, and
/// "Skip this group" is single Skip's tenant overload - from Marten 9.51 one tenant's events and no other's, below it
/// refused as single Skip is (SEC1-fix4), and with no tenant refused at every version.
/// </summary>
/// <remarks>
/// Which half runs is the light-up probe's answer for the Marten this run resolved (<c>RuntimeFeature.TenantEventSkipping</c>),
/// as <c>PerTenantSkipLiveTests</c> does: the floor and the latest-tested legs each prove theirs, and the assertion both
/// share - globex's event of the same number never moves - is the one the refusal and the light-up both exist for.
/// </remarks>
public class PerTenantGroupSkipLiveTests(PerTenantGroupSkipFixture fixture) : IClassFixture<PerTenantGroupSkipFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool TenantSkipping => RuntimeFeature.TenantEventSkipping.Probe() == true;

    private static StudioScope Scope(string? tenant) => new(MartenStoreRegistry.DefaultStoreKey, string.Empty, tenant);

    /// <summary>
    /// Two letters naming #N, one per tenant: two events, not one, and each letter's event type found in its own tenant's
    /// partition - at every version, because grouping reads and writes nothing.
    /// </summary>
    [PostgresFact]
    public async Task A_group_across_tenants_counts_each_tenants_event_of_one_number_once()
    {
        await fixture.SeedGroupAsync("CountingProjection");

        DeadLetterGroupList list = await fixture.Events.ListDeadLetterGroupsAsync(Scope(null), Token);

        DeadLetterGroup group = list.Groups.Should().ContainSingle(static x => x.Key.ProjectionName == "CountingProjection").Subject;
        group.Letters.Should().Be(2);
        group.Events.Should().Be(2, "acme's #N and globex's #N are two events");
        group.Key.EventType.Should().Be("group_poison");
        group.TenantNames.Should().Equal(PerTenantGroupSkipFixture.Acme, PerTenantGroupSkipFixture.Globex);
    }

    /// <summary>
    /// From acme's scope - with a visitor the policies allow only acme - the group skips acme's #N alone from 9.51, without
    /// the database as a whole ever being asked about; below 9.51 it is refused with single Skip's sentence. Globex's #N
    /// never moves.
    /// </summary>
    [PostgresFact]
    public async Task A_tenants_skip_of_the_group_skips_only_that_tenants_event_and_is_refused_at_the_floor()
    {
        long sequence = await fixture.SeedGroupAsync("TenantProjection");
        StudioScope acme = Scope(PerTenantGroupSkipFixture.Acme);

        DeadLetterGroup group = (await fixture.Events.ListDeadLetterGroupsAsync(acme, Token)).Groups
            .Should().ContainSingle(static x => x.Key.ProjectionName == "TenantProjection").Subject;

        fixture.Policy.Allow(static resource => resource.TenantId == PerTenantGroupSkipFixture.Acme);

        try
        {
            int asked = fixture.Policy.Calls.Count;

            Exception? outcome = await Record.ExceptionAsync(() => fixture.Events.SkipDeadLetterGroupAsync(acme, group.Key, "1", Token));

            fixture.Policy.Calls.Skip(asked).Should().NotContain(
                static x => x.Resource.TenantId == null && x.Resource.Capability == nameof(StudioCapability.ManageDeadLetters),
                "the skip reaches one tenant's events, so the database as a whole is never asked about");

            (await fixture.IsSkippedAsync(PerTenantGroupSkipFixture.Globex, sequence)).Should().BeFalse("globex's event of the same number is not acme's");

            StudioActionLogEntry entry = fixture.Audit.GetLatest()[0];
            entry.Action.Should().Be(EventDataService.SkipGroupAction);
            entry.TenantId.Should().Be(PerTenantGroupSkipFixture.Acme);

            if (TenantSkipping)
            {
                outcome.Should().BeNull();
                (await fixture.IsSkippedAsync(PerTenantGroupSkipFixture.Acme, sequence)).Should().BeTrue();
                entry.Succeeded.Should().BeTrue();
                entry.Message.Should().Be("1 event marked as skipped in tenant " + PerTenantGroupSkipFixture.Acme);
                return;
            }

            outcome.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);
            (await fixture.IsSkippedAsync(PerTenantGroupSkipFixture.Acme, sequence)).Should().BeFalse("and nothing was written at all");
            entry.Succeeded.Should().BeFalse();
            entry.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);
        }
        finally
        {
            fixture.Policy.AllowEverything();
        }
    }

    /// <summary>With no tenant in scope the group's skip is refused at every version, as single Skip's is, and neither event moves.</summary>
    [PostgresFact]
    public async Task With_no_tenant_the_groups_skip_is_refused_and_moves_no_event()
    {
        long sequence = await fixture.SeedGroupAsync("EveryoneProjection");

        DeadLetterGroup group = (await fixture.Events.ListDeadLetterGroupsAsync(Scope(null), Token)).Groups
            .Should().ContainSingle(static x => x.Key.ProjectionName == "EveryoneProjection").Subject;

        Exception? refused = await Record.ExceptionAsync(() => fixture.Events.SkipDeadLetterGroupAsync(Scope(null), group.Key, "2", Token));

        refused.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(
            TenantSkipping ? StreamCompactionRules.SkipNeedsTenant : StreamCompactionRules.SkipNeedsOneSequence);
        (await fixture.IsSkippedAsync(PerTenantGroupSkipFixture.Acme, sequence)).Should().BeFalse();
        (await fixture.IsSkippedAsync(PerTenantGroupSkipFixture.Globex, sequence)).Should().BeFalse();

        (await fixture.Events.DescribeDeadLetterGroupSkipAsync(Scope(null), group.Key, Token)).Refusal.Should().Be(
            TenantSkipping ? StreamCompactionRules.SkipNeedsTenant : StreamCompactionRules.SkipNeedsOneSequence,
            "the dialog is told what the skip would be refused with");
    }
}
