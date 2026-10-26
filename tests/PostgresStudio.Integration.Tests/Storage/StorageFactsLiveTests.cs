using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Storage;

namespace PostgresStudio.Integration.Tests.Storage;

/// <summary>
/// The Storage page's facts on PostgreSQL 14, 16, 17 and 18 (W5-CAP, issue #8 G3), in a database of each test's own on the
/// version matrix: sizes above zero, the heap, index and TOAST split on B0's shapes, the HOT ratio of a fillfactor-70 table,
/// no growth before a day of statistics, the per-visitor view - and, behind somebody's <c>ACCESS EXCLUSIVE</c> on a listed
/// table, a read that returns under a 300 ms <c>lock_timeout</c> and creates nothing.
/// </summary>
public class StorageFactsLiveTests(PostgresVersionMatrix matrix)
{
    /// <summary>The legs: 15's facts are 14's and 16's together, and 19 is the matrix's own preview.</summary>
    public static TheoryData<int> Legs => [14, 16, 17, 18];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(MatrixServer Server, string Database)> SeededAsync(PostgresVersionMatrix matrix, int major, string name)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync(name, Token);
        await StorageLive.ExecuteAsync(server.ConnectionString(MatrixRole.App, database), StorageLive.SeedSql);
        await StorageLive.ExecuteAsync(server.ConnectionString(MatrixRole.App, database), StorageLive.VacuumSql);

        // A backend reports its counters as it ends; the reader waits for the update it made.
        await StorageLive.UntilAsync(
            async () => await StorageLive.ScalarAsync<long>(
                server.ConnectionString(MatrixRole.App, database),
                "select coalesce((select n_tup_upd from pg_stat_user_tables where schemaname = 'store' and relname = 'hot'), 0)") >= 2_000,
            TimeSpan.FromSeconds(30),
            "pg_stat_user_tables to count store.hot's updates");

        return (server, database);
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task The_facts_split_each_table_and_say_nothing_of_growth_before_a_day(int major)
    {
        (MatrixServer server, string database) = await SeededAsync(matrix, major, "cap_storage_facts");
        IReadOnlySet<string> before = await server.SnapshotAsync(database, Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        StorageFacts facts = await host.Services.GetRequiredService<IStorageFacts>().ReadAsync(host.Resolved, Token);

        facts.DatabaseBytes.Should().BePositive("the owner of the database has CONNECT");
        facts.EstimatedDatabaseBytes.Should().BePositive();
        facts.SizesAreEstimates.Should().BeTrue();

        StorageTableFacts docs = facts.LargestTables.Single(static x => x.Table.Table == "docs");
        StorageTableFacts toasty = facts.LargestTables.Single(static x => x.Table.Table == "toasty");
        StorageTableFacts hot = facts.LargestTables.Single(static x => x.Table.Table == "hot");

        docs.Row.HeapBytes.Should().BePositive();
        docs.Row.IndexBytes.Should().BePositive("its primary key");
        docs.EstimatedRows.Should().BeInRange(90_000, 110_000, "reltuples after ANALYZE");
        toasty.Row.ToastBytes.Should().BeGreaterThan(toasty.Row.HeapBytes * 10, "B0's toasty: the TOAST table holds nearly everything");
        toasty.Row.ToastBytes.Should().BeGreaterThan(8L << 20);

        hot.HotUpdateRatio.Should().NotBeNull().And.BeGreaterThan(0d, "fillfactor 70 left room for HOT updates (B0 measured 44 %)").And.BeLessThanOrEqualTo(1d);

        if (PostgresFeatures.Has(PostgresFeature.NewPageUpdates, server.VersionNumber))
        {
            hot.NewPageShare.Should().NotBeNull("n_tup_newpage_upd is read by name from 16");
        }
        else
        {
            hot.NewPageShare.Should().BeNull("14 has no n_tup_newpage_upd, and the statement still runs");
        }

        facts.Window.CoversADay.Should().BeFalse("the matrix server started minutes ago");
        facts.GrowthBytesPerDay.Should().BeNull();
        facts.LargestTables.Should().OnlyContain(x => x.GrowthBytesPerDay(facts.Window) == null);
        facts.Statements.Should().NotBeEmpty();

        StorageView view = await ViewAsync(host);
        view.Refusal.Should().Be(DatabaseRefusal.None, view.Reason);
        view.Tables.Should().Contain(static x => x.Name == "docs" && x.Link != null);
        view.GrowthPending.Should().StartWith("needs a day of statistics: ");

        (await server.SnapshotAsync(database, Token)).Should().BeEquivalentTo(before, "a read path creates nothing");
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task Behind_an_access_exclusive_lock_on_a_listed_table_the_read_returns_under_300ms(int major)
    {
        (MatrixServer server, string database) = await SeededAsync(matrix, major, "cap_storage_lock");
        HealthSweepProbe probe = new(cancellation => server.OpenAsync(MatrixRole.Superuser, database, cancellation));

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database, services: s => s.AddSingleton<IHealthStatementObserver>(probe));
        StorageFactsReader reader = (StorageFactsReader) host.Services.GetRequiredService<IStorageFacts>();
        reader.LockTimeout = TimeSpan.FromMilliseconds(300);

        await using NpgsqlConnection holder = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);

        await using (NpgsqlCommand lockThem = new("lock table store.docs in access exclusive mode; lock table store.toasty in access exclusive mode", holder, held))
        {
            await lockThem.ExecuteNonQueryAsync(Token);
        }

        Stopwatch clock = Stopwatch.StartNew();
        StorageFacts facts = await reader.ReadUncachedAsync(host.Resolved, Token);
        clock.Stop();

        facts.LargestTables.Select(static x => x.Table.Table).Should().Contain(["docs", "toasty"], "a locked table is still listed - from its catalog row");
        facts.DatabaseBytes.Should().BePositive("pg_database_size takes no relation lock (B0 G3)");
        probe.StatementsObserved.Should().BePositive("so 'no lock' is not 'nothing ran'");
        probe.BeyondCatalogReads.Should().BeEmpty("a list locks nothing it lists (D27)");

        TestContext.Current.TestOutputHelper?.WriteLine("PostgreSQL {0}: the Storage facts behind the lock in {1:0} ms", server.ServerVersion, clock.Elapsed.TotalMilliseconds);

        await held.RollbackAsync(Token);
    }

    /// <summary>The page's view, through the real service in a scope of the host's own, as a circuit would ask it.</summary>
    private static async Task<StorageView> ViewAsync(MatrixHost host)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStorageService>().GetAsync(new StudioScope("default", "default", null), Token);
    }
}
