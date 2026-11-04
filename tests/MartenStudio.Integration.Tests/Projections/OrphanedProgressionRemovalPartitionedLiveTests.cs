using System.Security.Claims;

using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

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
/// 1.2-PROJ (a), live, on a store with <c>Events.UseTenantPartitionedEvents</c>: a tenant's progression row is a shard's of a
/// registered projection - and then no orphan, and never removed - or an orphan's, and then removed alone, from a tenant-scoped
/// scope, under the database's own authorization and filed in the audit ring under the database and no tenant.
/// </summary>
/// <remarks>
/// A class of its own, because the store differs: the flag, conjoined tenancy and a quick append mode. The rows are written by
/// the test's own SQL (a real one needs per-tenant daemon agents), which is what "exactly that row" is measured against.
/// </remarks>
public class OrphanedProgressionRemovalPartitionedLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Acme = "acme";

    private const string Globex = "globex";

    private const string Retired = "DailySalesBeforeRename";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string schema = string.Empty;
    private string eventSchema = string.Empty;
    private ServiceProvider? provider;

    private ServiceProvider Provider => provider
        ?? throw new InvalidOperationException("The studio was not started: a test that needs it must be a [PostgresFact].");

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        schema = GetType().Name.ToLowerInvariant();
        eventSchema = schema + "_events";

        await postgres.CreateSchemaAsync(schema, Token);
        await postgres.CreateSchemaAsync(eventSchema, Token);

        provider = BuildStudio();

        await Provider.GetRequiredService<IDocumentStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        await WriteRowAsync("DailySales:All", 44);
        await WriteRowAsync($"DailySales:All:{Acme}", 11);
        await WriteRowAsync($"{Retired}:All:{Acme}", 7);
        await WriteRowAsync($"{Retired}:All:{Globex}", 8);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (provider is not null)
        {
            await provider.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task A_registered_projections_tenant_row_is_no_orphan_and_is_refused_and_stays()
    {
        (await ViewAsync(tenantId: null)).Orphans.Rows.Select(static x => x.Name)
            .Should().BeEquivalentTo([$"{Retired}:All:{Acme}", $"{Retired}:All:{Globex}"], "DailySales' own tenant row is its shard's");

        OrphanRemoval refused = await RemoveAsync($"DailySales:All:{Acme}", tenantId: Acme);

        refused.Removed.Should().BeFalse();
        refused.Message.Should().Be(OrphanWording.RemovalNoLongerOrphan);
        (await PositionAsync($"DailySales:All:{Acme}")).Should().Be(11);
    }

    [PostgresFact]
    public async Task An_orphans_tenant_row_is_removed_alone_from_a_tenant_scope_and_audited_under_the_database()
    {
        OrphanRemoval removal = await RemoveAsync($"{Retired}:All:{Acme}", tenantId: Acme);

        removal.Removed.Should().BeTrue();
        (await PositionAsync($"{Retired}:All:{Acme}")).Should().BeNull();
        (await PositionAsync($"{Retired}:All:{Globex}")).Should().Be(8, "another tenant's row of the same orphan is its own");
        (await PositionAsync($"DailySales:All:{Acme}")).Should().Be(11);
        (await PositionAsync("DailySales:All")).Should().Be(44);

        // The ring shows an entry to anyone admitted at its (store, database, tenant); the row's name - which carries its tenant -
        // is the database's, withheld from a visitor short of the whole database, so the entry is the database's too.
        StudioActionLogEntry entry = Provider.GetRequiredService<StudioActionLogService>().GetLatest()
            .Should().ContainSingle(x => x.Action == ProjectionDataService.RemoveOrphanAction && x.Succeeded).Subject;

        entry.TenantId.Should().BeNull("the URL named tenant acme, and the audit does not");
        entry.Target.Should().Be($"{Retired}:All:{Acme}");
    }

    [PostgresFact]
    public async Task A_refusal_and_a_removal_of_another_tenants_row_are_filed_under_the_database_too()
    {
        // Removing globex's row while the selector is on acme: the whole-database question is what was answered.
        (await RemoveAsync($"{Retired}:All:{Globex}", tenantId: Acme)).Removed.Should().BeTrue();
        (await RemoveAsync("NoSuchRow:All:globex", tenantId: Acme)).Removed.Should().BeFalse();

        Provider.GetRequiredService<StudioActionLogService>().GetLatest()
            .Where(static x => x.Action == ProjectionDataService.RemoveOrphanAction)
            .Should().HaveCount(2).And.OnlyContain(static x => x.TenantId == null);
    }

    private async Task<ProjectionsView> ViewAsync(string? tenantId)
    {
        await using AsyncServiceScope scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProjectionDataService>().GetProjectionsAsync(ScopeFor(tenantId), Token);
    }

    private async Task<OrphanRemoval> RemoveAsync(string name, string? tenantId)
    {
        await using AsyncServiceScope scope = Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IProjectionDataService>()
            .RemoveOrphanedProgressionAsync(ScopeFor(tenantId), name, Token);
    }

    private static StudioScope ScopeFor(string? tenantId) => new(MartenStoreRegistry.DefaultStoreKey, string.Empty, tenantId);

    private ServiceProvider BuildStudio()
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();

        services.AddMarten(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = eventSchema;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            options.Policies.AllDocumentsAreMultiTenanted();

            options.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async);
        });

        services.AddMartenStudio(options =>
        {
            options.AuthorizationPolicy = null;
            options.Capabilities.CorrectProgression = true;
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(Globex);
        });

        return services.BuildServiceProvider();
    }

    private async Task WriteRowAsync(string name, long sequence)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "insert into " + SqlIdentifier.Qualify(eventSchema, "mt_event_progression") + " (name, last_seq_id, last_updated) values (@name, @seq, now())",
            connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("seq", sequence);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<long?> PositionAsync(string name)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select last_seq_id from " + SqlIdentifier.Qualify(eventSchema, "mt_event_progression") + " where name = @name", connection);
        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteScalarAsync(Token) is long value ? value : null;
    }

    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
