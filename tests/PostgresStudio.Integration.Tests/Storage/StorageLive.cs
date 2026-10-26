using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Storage;

/// <summary>A clock a test sets, so a day of statistics needs no day: the studio's "now" moves, the server's reset times do not.</summary>
/// <param name="start">Where it starts.</param>
internal sealed class SettableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;

    public override DateTimeOffset GetUtcNow() => now;

    /// <summary>Puts the clock at <paramref name="at" />.</summary>
    public void Set(DateTimeOffset at) => now = at;
}

/// <summary>
/// What the Storage and C11 live tests share (W5-CAP): the fixtures they seed as <c>app</c> in a database of their own, the
/// reads they force, a wait on a condition (never on the clock), and the statistics window's start, which the clock is set
/// a day past.
/// </summary>
internal static class StorageLive
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// B0 G3's three shapes, in schema <c>store</c>: <c>docs</c>, a hundred thousand narrow rows with a primary key;
    /// <c>toasty</c>, two thousand rows of ~9.6 kB of random text each, stored out of line; and <c>hot</c>, a fillfactor-70
    /// table whose every row was updated once on a column no index reads - then vacuumed and analysed, so the catalog's page
    /// counts are the files' (B0: the estimates match the functions after VACUUM/ANALYZE).
    /// </summary>
    public const string SeedSql =
        """
        create schema store;
        create table store.docs (id bigint primary key, payload text);
        insert into store.docs select g, md5(g::text) from generate_series(1, 100000) g;
        create table store.toasty (id int primary key, body text);
        insert into store.toasty
            select g, (select string_agg(md5(random()::text || g::text || s::text), '') from generate_series(1, 300) s)
            from generate_series(1, 2000) g;
        create table store.hot (id int primary key, n int not null, note text) with (fillfactor = 70);
        insert into store.hot select g, 0, 'x' from generate_series(1, 2000) g;
        update store.hot set n = n + 1;
        """;

    /// <summary>What <see cref="SeedSql" /> is followed by, as a statement of its own: <c>VACUUM</c> runs in no batch and no transaction.</summary>
    public const string VacuumSql = "vacuum analyze store.docs, store.toasty, store.hot";

    /// <summary>Runs <paramref name="sql" /> on a connection of its own, which ends - and so flushes its statistics - with it.</summary>
    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>One scalar.</summary>
    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync(Token);
        return value is null or DBNull ? default : (T) Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Asks <paramref name="condition" /> every quarter of a second until it is true, or fails naming <paramref name="what" />.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(timeout);

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(250));

        while (!await condition())
        {
            try
            {
                await timer.WaitForNextTickAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!Token.IsCancellationRequested)
            {
                throw new TimeoutException("Gave up after " + timeout + " waiting for " + what + ".");
            }
        }
    }

    /// <summary>
    /// At least <paramref name="blocks" /> blocks read from outside shared buffers: a table larger than a quarter of
    /// <c>shared_buffers</c> is scanned through a small ring of buffers, so every scan reads it again (from the operating
    /// system's cache, which is what <c>blks_read</c> counts). Waits until <c>pg_stat_database</c> says so.
    /// </summary>
    public static async Task ForceReadsAsync(string connectionString, long blocks)
    {
        // About twice the default shared_buffers (128 MB), so it can never be held there whole: a 100 MB table, measured, ended up
        // in them after some forty scans despite the scans' ring, and then read nothing. CREATE TABLE AS writes through a
        // bulk-write ring, so the table does not start out there either.
        await ExecuteAsync(
            connectionString,
            "create table cold as select g::bigint as id, repeat(md5(g::text), 4) as filler from generate_series(1, 1600000) g");
        await ExecuteAsync(connectionString, "vacuum analyze cold");

        const string Read = "select blks_read from pg_stat_database where datname = current_database()";

        // Batches, each on a connection of its own that reports its counters as it ends, until the server has counted enough.
        for (int batch = 0; batch < 30 && await ScalarAsync<long>(connectionString, Read) < blocks; batch++)
        {
            await ExecuteAsync(connectionString, string.Concat(Enumerable.Repeat("select count(*) from cold; ", 20)));
        }

        await UntilAsync(
            async () => await ScalarAsync<long>(connectionString, Read) >= blocks,
            TimeSpan.FromSeconds(60),
            "pg_stat_database to count " + blocks + " blocks read");
    }

    /// <summary>A warm database: a small table, read through its index thousands of times - hits, and hardly a read.</summary>
    public static async Task WarmAsync(string connectionString)
    {
        await ExecuteAsync(
            connectionString,
            "create table warm (id int primary key, v text); insert into warm select g, md5(g::text) from generate_series(1, 1000) g; analyze warm;");

        await ExecuteAsync(
            connectionString,
            "do $$ declare x text; begin for i in 1..20000 loop select v into x from warm where id = 1 + i % 1000; end loop; end $$;");

        await UntilAsync(
            async () => await ScalarAsync<long>(connectionString, "select blks_hit from pg_stat_database where datname = current_database()") >= 40_000,
            TimeSpan.FromSeconds(60),
            "pg_stat_database to count the warm reads' hits");
    }

    /// <summary>The statistics window the studio reads of <paramref name="host" />'s database now, as the sweep would compute it.</summary>
    public static async Task<StatisticsWindow> WindowAsync(MatrixHost host)
    {
        (HealthCheckContext context, HealthConnection connection) = host.Context();

        await using (connection)
        {
            StatisticsReading reading = await StatisticsWindow.ReadAsync(context, Token);
            return StatisticsWindow.Compute(reading, host.Services.GetRequiredService<HealthHistory>().Read(host.Resolved.Source.Key, host.Resolved.Target.Identity));
        }
    }
}
