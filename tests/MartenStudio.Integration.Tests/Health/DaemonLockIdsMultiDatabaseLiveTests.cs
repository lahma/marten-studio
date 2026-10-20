using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Integration.Tests.Projections;
using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services.Health.Checks;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// D02's third distributor, pinned live: HotCold over several databases is JasperFx's
/// <c>MultiTenantedProjectionDistributor</c>, which takes one advisory lock per database, keyed <c>DaemonLockId</c>
/// itself - in that database, on that database's own connection.
/// </summary>
/// <remarks>
/// The second database is this class's own (not the one <c>MultiDatabaseDaemonLiveTests</c> creates), so the two classes
/// can run side by side.
/// </remarks>
public class DaemonLockIdsMultiDatabaseLiveTests(PostgresFixture postgres)
{
    private const string SecondDatabaseName = "martenstudio_h2_locks";

    /// <summary>
    /// A lock id of this class's own: the distributor takes it in the shared test database too, and holding the default
    /// 4444 there raced every other class that takes or expects 4444 (H2 re-review).
    /// </summary>
    private const int LockId = 7_740_030;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task HotCold_over_two_databases_holds_DaemonLockId_in_each_and_the_studio_recognises_it()
    {
        const string schema = "h2_locks_multi";
        const string eventSchema = schema + "_events";

        string first = postgres.ConnectionString;
        string second = await CreateSecondDatabaseAsync(first);

        await CreateSchemasAsync(first, schema, eventSchema);
        await CreateSchemasAsync(second, schema, eventSchema);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMarten(options =>
            {
                options.DatabaseSchemaName = schema;
                options.Events.DatabaseSchemaName = eventSchema;
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Projections.LeadershipPollingTime = 1_000;
                options.Projections.DaemonLockId = LockId;
                options.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async);

                options.MultiTenantedDatabases(tenancy =>
                {
                    tenancy.AddMultipleTenantDatabase(first, "primary").ForTenants("tenant-primary");
                    tenancy.AddMultipleTenantDatabase(second, "secondary").ForTenants("tenant-secondary");
                });
            })
            .UseLightweightSessions()
            .AddAsyncDaemon(DaemonMode.HotCold);

        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, Anonymous>();
        builder.Services.AddMartenStudio(static options => options.Capabilities = MartenStudioCapabilities.All());

        using IHost host = builder.Build();
        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await host.StartAsync(Token);

        try
        {
            IReadOnlyList<long> keys = DaemonCheckConfiguration.LockKeysFor(eventSchema, DaemonCheckConfiguration.AsyncShardsOf(store), LockId);

            foreach (string connectionString in (string[]) [first, second])
            {
                await ProjectionsFixture.WaitForAsync(
                    async () => (await HeldAsync(connectionString, keys)).Contains(LockId),
                    "the HotCold leader to take DaemonLockId in " + new NpgsqlConnectionStringBuilder(connectionString).Database,
                    TimeSpan.FromSeconds(60),
                    Token);

                (await HeldAsync(connectionString, keys)).Should().Equal([(long) LockId],
                    "the multi-database distributor takes DaemonLockId per database and nothing per shard");
            }
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(30));
        }
    }

    private static async Task<IReadOnlyList<long>> HeldAsync(string connectionString, IReadOnlyList<long> keys)
    {
        HealthQuery query = HealthDaemonQueries.DaemonLocks(keys);

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(query.Sql, connection);
        foreach (NpgsqlParameter parameter in query.Parameters)
        {
            command.Parameters.Add(parameter);
        }

        List<long> held = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            held.Add(reader.GetInt64(1));
        }

        return held;
    }

    private static async Task<string> CreateSecondDatabaseAsync(string connectionString)
    {
        await using (NpgsqlConnection maintenance = new(connectionString))
        {
            await maintenance.OpenAsync(Token);

            await using NpgsqlCommand exists = new("select 1 from pg_database where datname = @name", maintenance);
            exists.Parameters.AddWithValue("name", SecondDatabaseName);

            if (await exists.ExecuteScalarAsync(Token) is null)
            {
                await using NpgsqlCommand create = new("create database " + SqlIdentifier.Quote(SecondDatabaseName), maintenance);
                await create.ExecuteNonQueryAsync(Token);
            }
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Database = SecondDatabaseName }.ConnectionString;
    }

    private static async Task CreateSchemasAsync(string connectionString, string schema, string eventSchema)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);

        foreach (string name in (string[]) [schema, eventSchema])
        {
            await using NpgsqlCommand drop = new("drop schema if exists " + SqlIdentifier.Quote(name) + " cascade", connection);
            await drop.ExecuteNonQueryAsync(Token);

            await using NpgsqlCommand create = new("create schema " + SqlIdentifier.Quote(name), connection);
            await create.ExecuteNonQueryAsync(Token);
        }
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
