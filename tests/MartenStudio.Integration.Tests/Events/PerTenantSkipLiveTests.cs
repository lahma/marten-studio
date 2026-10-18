using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton<IDocumentStore>(Store);
        services.AddMartenStudio(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
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
/// SEC1-fix4 item 1, live: Marten's <c>MarkEventsAsSkipped</c> is <c>where seq_id = ANY(:sequences)</c> with no tenant, so
/// on this store skipping acme's #N would skip globex's #N as well. The studio refuses the skip, audited, and neither
/// tenant's event moves - which, without the refusal, globex's does.
/// </summary>
public class PerTenantSkipLiveTests(PerTenantSkipFixture fixture) : IClassFixture<PerTenantSkipFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Skipping_a_tenants_event_is_refused_because_it_would_skip_every_tenants_event_of_that_number()
    {
        long acme = await fixture.AppendOneAsync(PerTenantSkipFixture.Acme);
        long globex = await fixture.AppendOneAsync(PerTenantSkipFixture.Globex);
        globex.Should().Be(acme, "the premise: each tenant numbers its own events, so the two share a seq_id");

        var acmeScope = new StudioScope(MartenStoreRegistry.DefaultStoreKey, string.Empty, PerTenantSkipFixture.Acme);

        // The rows first, then the refusal: what must never happen is globex's event moving, and that is the assertion a
        // service without the refusal fails.
        Exception? refused = await Record.ExceptionAsync(() => fixture.Events.SkipEventAsync(acmeScope, acme, Token));

        (await fixture.IsSkippedAsync(PerTenantSkipFixture.Globex, globex)).Should().BeFalse("globex's event with the same number is not acme's to skip");
        (await fixture.IsSkippedAsync(PerTenantSkipFixture.Acme, acme)).Should().BeFalse("and nothing was written at all");

        refused.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);

        StudioActionLogEntry entry = fixture.Audit.GetLatest()
            .First(x => x.Action == EventDataService.SkipEventAction && x.Target == acme.ToString(System.Globalization.CultureInfo.InvariantCulture));
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);
    }
}
