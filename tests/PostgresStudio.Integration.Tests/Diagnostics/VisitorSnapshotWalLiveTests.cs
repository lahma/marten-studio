using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Replication;

namespace PostgresStudio.Integration.Tests.Diagnostics;

/// <summary>
/// Issue #36 on a real server: the Health page's Snapshot download (<c>VisitorSnapshotBuilder</c>, built in a visitor's circuit)
/// carries the write-ahead log section, read from the Replication page's own cached facts - so a measurement baseline is
/// Health › Snapshot › Download JSON, and the counters are the catalog's own, bracketed before and after.
/// </summary>
public class VisitorSnapshotWalLiveTests(VisitorSnapshotWalLiveTests.Fixture database) : IClassFixture<VisitorSnapshotWalLiveTests.Fixture>
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The class's database.</summary>
    public sealed class Fixture(PostgresFixture postgres) : IAsyncLifetime
    {
        private OwnedDatabases? owned;

        /// <summary>The database's connection string.</summary>
        public string ConnectionString => owned![0];

        public async ValueTask InitializeAsync()
        {
            if (!DockerAvailability.IsAvailable)
            {
                return;
            }

            owned = await OwnedDatabases.CreateAsync(postgres, ["ps_snapshot_wal"], CancellationToken.None);
            await OwnedDatabases.ExecuteAsync(
                ConnectionString,
                "create table public.events (id int primary key, note text); insert into public.events select g, 'n' || g from generate_series(1, 2000) g;",
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }

    [PostgresFact]
    public async Task The_download_carries_the_wal_counters_the_catalog_holds_and_shares_the_replication_pages_read()
    {
        long before = await RecordsAsync();

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(database.ConnectionString)),
            static options => options.Capabilities = PostgresStudioCapabilities.All());

        VisitorSnapshot snapshot = await host.CircuitAsync("admin", services => services.GetRequiredService<VisitorSnapshotBuilder>().BuildAsync(Scope, Token));

        long after = await RecordsAsync();

        using JsonDocument document = JsonDocument.Parse(snapshot.Json);
        JsonElement wal = document.RootElement.GetProperty("wal");

        wal.GetProperty("outcome").GetString().Should().Be("read");
        wal.GetProperty("records").GetInt64().Should().BeInRange(before, after, "the catalog's own count before and after brackets the snapshot's");
        wal.GetProperty("bytes").GetInt64().Should().BeGreaterThan(0);
        wal.GetProperty("fullPageImages").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        wal.GetProperty("writes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        wal.GetProperty("syncs").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        wal.GetProperty("statsReset").ValueKind.Should().Be(JsonValueKind.String);
        document.RootElement.GetProperty("withheld").TryGetProperty("wal", out _).Should().BeFalse();

        // The Replication page, a moment later, shows the same counters: its facts are the download's, cached for the interval.
        ReplicationView page = await host.CircuitAsync("admin", services => services.GetRequiredService<IReplicationService>().GetAsync(Scope, Token));
        page.Wal.Value!.Totals.Records.Should().Be(wal.GetProperty("records").GetInt64(), "one cached read serves the page and the download");
    }

    private async Task<long> RecordsAsync()
    {
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new("select wal_records from pg_catalog.pg_stat_wal", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }
}
