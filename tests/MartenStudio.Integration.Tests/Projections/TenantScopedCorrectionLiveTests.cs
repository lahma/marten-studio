using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Integration.Tests.Documents;
using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// DB-0-fix-2, B1, live: a tenant-scoped progression correction on a conjoined database reaches every
/// tenant in it, and is refused unless the visitor may change that database as a whole.
/// </summary>
/// <remarks>
/// <para>
/// One real database, <c>acme</c> and <c>globex</c> both appending into it, no daemon. The visitor's
/// policy allows <c>(db, acme)</c> and refuses <c>(db, null)</c>. Marten 9.31's tenant overloads of the
/// two corrections run an untenanted <c>HighWaterDetector</c> over the tenant's database: the advance
/// writes the store-global <c>HighWaterMark</c> row, the correction rewrites every row of
/// <c>mt_event_progression</c>. Each test reads those rows on a connection of its own, before and after,
/// so "refused" means the database was not touched and "allowed" means it really was - globex's
/// projection row included, which is the whole point.
/// </para>
/// </remarks>
public class TenantScopedCorrectionLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Acme = "acme";

    private const string Globex = "globex";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private ServiceProvider? host;

    private string Schema => GetType().Name.ToLowerInvariant();

    private string EventSchema => Schema + "_events";

    private IDocumentStore Store => host?.GetRequiredService<IDocumentStore>()
        ?? throw new InvalidOperationException(
            "The store was not built. A test that needs it must be a [PostgresFact] so that it skips instead of "
            + "failing when Docker is absent.");

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(Schema, Token);
        await postgres.CreateSchemaAsync(EventSchema, Token);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMarten(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = EventSchema;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // Both tenants in one database, events and documents alike.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();

            options.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async);
        });

        host = services.BuildServiceProvider();

        await Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// The advance, refused: nothing is marked. And with <c>(db, null)</c> allowed too, the same call
    /// marks the database's high-water mark - the row every tenant's async projection is read against.
    /// </summary>
    [PostgresFact]
    public async Task A_tenant_scoped_advance_is_refused_unless_the_whole_database_is_allowed()
    {
        await AppendAsync(Acme, streams: 2);
        await AppendAsync(Globex, streams: 3);

        long highest = await ScalarAsync($"select coalesce(max(seq_id), 0) from {Qualified("mt_events")}");
        highest.Should().BeGreaterThan(0);

        await using (Studio refused = await BuildStudioAsync(allowWholeDatabase: false))
        {
            Func<Task> advancing = () => refused.Service.AdvanceHighWaterMarkAsync(refused.ScopeFor(Acme), Token);

            StudioNotAuthorizedException denial = (await advancing.Should().ThrowAsync<StudioNotAuthorizedException>(
                "the high-water mark is the database's, and globex is read against it too")).Which;

            denial.Scope.TenantId.Should().BeNull();
            refused.AssertRefusalAudited("AdvanceHighWaterMark");
        }

        (await HighWaterMarkRowAsync()).Should().BeNull("a refused advance marked nothing");

        await using (Studio allowed = await BuildStudioAsync(allowWholeDatabase: true))
        {
            await allowed.Service.AdvanceHighWaterMarkAsync(allowed.ScopeFor(Acme), Token);

            allowed.Ring.GetLatest().Should().Contain(x => x.Action == "AdvanceHighWaterMark" && x.Succeeded);
        }

        (await HighWaterMarkRowAsync()).Should().Be(
            highest, "Marten marked the database's mark at the highest committed event - globex's events included");
    }

    /// <summary>
    /// The correction, refused: globex's projection row is untouched. Allowed: it is rewritten along with
    /// acme's, which is what a tenant-scoped correction on a shared database has always really done.
    /// </summary>
    [PostgresFact]
    public async Task A_tenant_scoped_correction_is_refused_unless_the_whole_database_is_allowed()
    {
        await AppendAsync(Acme, streams: 1);
        await AppendAsync(Globex, streams: 1);

        long highest = await ScalarAsync($"select coalesce(max(seq_id), 0) from {Qualified("mt_events")}");

        // Progression that has run ahead of the events, which is the only state the correction changes.
        const long AheadOfTheEvents = 9_999;
        await WriteProgressionAsync("HighWaterMark", AheadOfTheEvents);
        await WriteProgressionAsync("DailySales:All", AheadOfTheEvents);

        await using (Studio refused = await BuildStudioAsync(allowWholeDatabase: false))
        {
            Func<Task> correcting = () => refused.Service.CorrectProgressionAsync(refused.ScopeFor(Acme), Token);

            (await correcting.Should().ThrowAsync<StudioNotAuthorizedException>()).Which.Scope.TenantId.Should().BeNull();
            refused.AssertRefusalAudited("CorrectProgression");
        }

        (await ProgressionAsync("DailySales:All")).Should().Be(AheadOfTheEvents, "a refused correction rewrote nothing");

        await using (Studio allowed = await BuildStudioAsync(allowWholeDatabase: true))
        {
            await allowed.Service.CorrectProgressionAsync(allowed.ScopeFor(Acme), Token);
        }

        (await ProgressionAsync("DailySales:All")).Should().Be(
            highest, "Marten rewrote every progression row of the database, not only acme's");
    }

    // ------------------------------------------------------------------------------------------------
    // The studio under test, and the database read around it
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A studio over the store this class built, with a store policy and a write policy that allow acme
    /// and refuse - unless <paramref name="allowWholeDatabase" /> - the database as a whole.
    /// </summary>
    private async Task<Studio> BuildStudioAsync(bool allowWholeDatabase)
    {
        string databaseId = (await Store.Storage.AllDatabases())[0].Id.Identity;

        var policy = new FakeStoreAuthorizationService();
        policy.Allow(resource => allowWholeDatabase || resource.TenantId is not null);

        var logs = new LogCapture();

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(logs);
        });

        services.AddSingleton(Store);
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, NamedVisitor>();

        // Registered after AddAuthorization so that this is the one resolved.
        services.AddSingleton<IAuthorizationService>(policy);

        services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = "studio-store";
            options.WriteAuthorizationPolicy = "studio-write";
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(Globex);
        });

        return new Studio(services.BuildServiceProvider(), logs, databaseId);
    }

    private string Qualified(string table) => SqlIdentifier.Qualify(EventSchema, table);

    private async Task AppendAsync(string tenantId, int streams)
    {
        await using IDocumentSession session = Store.LightweightSession(tenantId);

        for (int index = 0; index < streams; index++)
        {
            Guid streamId = Guid.NewGuid();
            session.Events.StartStream(streamId, new OrderPlaced(streamId, $"{tenantId} {index}", DateTimeOffset.UtcNow));
        }

        await session.SaveChangesAsync(Token);
    }

    private async Task WriteProgressionAsync(string name, long sequence)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using var command = new NpgsqlCommand(
            $"insert into {Qualified("mt_event_progression")} (name, last_seq_id) values (@name, @sequence) "
            + "on conflict (name) do update set last_seq_id = excluded.last_seq_id",
            connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("sequence", sequence);
        await command.ExecuteNonQueryAsync(Token);
    }

    private Task<long?> HighWaterMarkRowAsync() => ProgressionAsync("HighWaterMark");

    private async Task<long?> ProgressionAsync(string name)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using var command = new NpgsqlCommand(
            $"select last_seq_id from {Qualified("mt_event_progression")} where name = @name", connection);
        command.Parameters.AddWithValue("name", name);

        return await command.ExecuteScalarAsync(Token) is long value ? value : null;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>One studio container and what it logged.</summary>
    private sealed class Studio(ServiceProvider provider, LogCapture logs, string database) : IAsyncDisposable
    {
        private readonly IServiceScope scope = provider.CreateScope();

        public IProjectionDataService Service => scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        private string Database => database;

        public StudioScope ScopeFor(string tenantId) => new(MartenStoreRegistry.DefaultStoreKey, Database, tenantId);

        /// <summary>A scope denial, recorded against the database as a whole, and logged as one.</summary>
        public void AssertRefusalAudited(string action)
        {
            StudioActionLogEntry entry = Ring.GetLatest()
                .Should().ContainSingle(x => x.Action == action && !x.Succeeded).Subject;

            entry.DatabaseId.Should().Be(Database);
            entry.TenantId.Should().BeNull("the refused question was the database as a whole");

            logs.Lines.Should().Contain(x => x.EventId.Id == 9203, "a scope denial is logged as one");
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }

    /// <summary>A named visitor, so the audit has somebody to name.</summary>
    private sealed class NamedVisitor : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, "tenant-admin")], "test"))));
    }
}
