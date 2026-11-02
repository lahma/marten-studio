using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Storage;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Maintenance;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Storage;

namespace PostgresStudio.Integration.Tests.Versions;

/// <summary>
/// 1.1-PG19 on the version matrix: what PostgreSQL 19 adds - a table's and an index's own <c>stats_reset</c>,
/// <c>pg_stat_autovacuum_scores</c>, <c>pg_get_multixact_stats()</c>, and <c>pg_stat_lock</c>'s fast-path figure beside T19 - is read
/// where the server has it, with its values proven, and is absent, never empty and never an error, on every release below.
/// </summary>
/// <remarks>
/// <para>
/// Each test makes a database of its own (<see cref="MatrixServer.CreateDatabaseAsync" />), so the seeded one the rest of the
/// matrix reads - and its no-DDL fingerprint - is untouched, and a statistics reset or a table's counters are this test's alone
/// (testing.md: statistics resets in a database of the test's own). Waits are on a condition, never on the clock.
/// </para>
/// <para>
/// <b>The twin below 19</b> is the same test on the other legs: it asserts the reads come back empty (<see langword="null" />)
/// and that no statement of the run names a 19-only object, which is what would be a <c>42P01</c> or <c>42883</c> there. The
/// feature gates themselves are held against each server's own catalog by <c>VersionMatrixLiveTests</c>.
/// </para>
/// </remarks>
public class Pg19ReadersLiveTests(PostgresVersionMatrix matrix)
{
    private static readonly string[] NineteenOnly = ["pg_stat_autovacuum_scores", "pg_get_multixact_stats", "pg_stat_lock"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The scores of the tables the sweep judges, and nothing of a table it does not: <c>busy</c> has fifty thousand rows nobody
    /// analysed, which is an analyze score of a thousand (50 rows are its trigger when <c>reltuples</c> is unknown), and
    /// <c>other.secret</c>, in a schema the studio does not admit, has just as many and is never named.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task Autovacuums_scores_are_read_for_the_judged_tables_alone_on_19_and_not_asked_below(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        bool nineteen = PostgresFeatures.Has(PostgresFeature.AutovacuumScores, server.VersionNumber);
        string database = await server.CreateDatabaseAsync("pg19_scores", Token);

        await server.ExecuteAsync(
            "create schema other; "
            + "create table public.busy (id int, v text) with (autovacuum_enabled = false); "
            + "insert into public.busy select g, 'x' from pg_catalog.generate_series(1, 50000) g; "
            + "create table public.quiet (id int) with (autovacuum_enabled = false); "
            + "create table other.secret (id int) with (autovacuum_enabled = false); "
            + "insert into other.secret select g from pg_catalog.generate_series(1, 50000) g; "
            + "create table public.parted (k int not null) partition by list (k); "
            + "create table public.parted_1 partition of public.parted for values in (1) with (autovacuum_enabled = false); "
            + "create table public.parted_2 partition of public.parted for values in (2) with (autovacuum_enabled = false); "
            + "insert into public.parted select 2 from pg_catalog.generate_series(1, 20000); "
            + "select pg_catalog.pg_stat_force_next_flush();",
            database,
            Token);

        await StorageLive.UntilAsync(
            async () => await ScalarAsync<long>(server, database, "select coalesce(max(n_mod_since_analyze), 0) from pg_catalog.pg_stat_user_tables where relname = 'busy'") >= 50_000,
            TimeSpan.FromSeconds(30),
            "busy's inserts to reach the statistics");
        await StorageLive.UntilAsync(
            async () => await ScalarAsync<long>(server, database, "select coalesce(max(n_mod_since_analyze), 0) from pg_catalog.pg_stat_user_tables where relname = 'parted_2'") >= 20_000,
            TimeSpan.FromSeconds(30),
            "parted_2's inserts to reach the statistics");

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        (HealthCheckContext context, HealthConnection connection) = host.Context();
        await using HealthConnection _ = connection;

        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context);
        tables.Select(static x => x.Key).Should().Contain(["public.busy", "public.quiet", "public.parted"]).And.NotContain("other.secret");

        IReadOnlyDictionary<string, AutovacuumScoreRow>? scores = await HealthTables.AutovacuumScoresAsync(context, tables);
        string[] asked = [.. context.Sql.Statements.Select(static x => x.Sql)];

        if (!nineteen)
        {
            scores.Should().BeNull("PostgreSQL " + server.ServerVersion + " has no pg_stat_autovacuum_scores");
            asked.Should().NotContain(static x => NineteenOnly.Any(name => x.Contains(name, StringComparison.Ordinal)), "the view is never named below 19");
            return;
        }

        scores.Should().NotBeNull();
        scores!.Keys.Should().BeSubsetOf(tables.Select(static x => x.Key), "a score is read for a judged table only")
            .And.NotContain("other.secret", "a table the studio does not judge is never named, whatever its score");

        AutovacuumScoreRow busy = scores["public.busy"];
        busy.AnalyzeScore.Should().BeGreaterThan(100, "50,000 rows changed against a trigger of about 50");
        busy.Score.Should().BeGreaterThanOrEqualTo(busy.AnalyzeScore, "the score is the largest of its parts");
        busy.Dominant.Should().BeOneOf("changes since analyze", "inserts");
        busy.Key.Should().Be("public.busy");

        scores["public.quiet"].Score.Should().BeLessThan(1, "nothing changed in it");

        // A partitioned table is scored by the partition autovacuum would take first, never the parent's own.
        scores["public.parted"].AnalyzeScore.Should().BeGreaterThan(100, "parted_2 holds the rows, parted_1 none");

        asked.Should().Contain(static x => x.Contains("pg_stat_autovacuum_scores", StringComparison.Ordinal));
    }

    /// <summary>
    /// A table's and an index's <c>stats_reset</c> is read where somebody reset that one object, on 19 and nowhere else; the
    /// Storage and Maintenance facts carry the table's, and the Maintenance facts the scores the sweep's T07 read.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task A_table_and_an_index_reset_on_their_own_are_read_with_the_time_on_19_and_never_below(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        bool nineteen = PostgresFeatures.Has(PostgresFeature.StatsResetPerTable, server.VersionNumber);
        string database = await server.CreateDatabaseAsync("pg19_reset", Token);

        await server.ExecuteAsync(
            "create table public.reset_me (id int primary key, v int); "
            + "create index reset_me_v on public.reset_me (v); "
            + "create table public.untouched (id int primary key, v int); "
            + "insert into public.reset_me select g, g from pg_catalog.generate_series(1, 2000) g; "
            + "insert into public.untouched select g, g from pg_catalog.generate_series(1, 2000) g; "
            + "analyze public.reset_me; analyze public.untouched; "
            + "set enable_seqscan = off; set enable_bitmapscan = off; "
            + "select pg_catalog.count(*) from public.reset_me where v = 5; "
            + "select pg_catalog.pg_stat_force_next_flush();",
            database,
            Token);

        await StorageLive.UntilAsync(
            async () => await ScalarAsync<long>(server, database, "select coalesce(max(idx_scan), 0) from pg_catalog.pg_stat_user_indexes where indexrelname = 'reset_me_v'") >= 1,
            TimeSpan.FromSeconds(30),
            "the index scan to reach the statistics");

        // Before any reset: no table and no index reports one of its own.
        await using (MatrixHost before = await MatrixHost.CreateAsync(server, MatrixRole.App, database))
        {
            (HealthCheckContext context, HealthConnection connection) = before.Context();
            await using HealthConnection _ = connection;

            (await HealthTables.TablesAsync(context)).Should().OnlyContain(static x => x.Row.CountersReset == null, "nobody reset any table on its own");
            (await HealthTables.IndexReadAsync(context)).Indexes.Should().OnlyContain(static x => x.Row.CountersReset == null);
        }

        DateTimeOffset resetAt = DateTimeOffset.UtcNow;

        await server.ExecuteAsync("select pg_catalog.pg_stat_reset_single_table_counters('public.reset_me'::pg_catalog.regclass)", database, Token);
        await server.ExecuteAsync("select pg_catalog.pg_stat_reset_single_table_counters('public.reset_me_v'::pg_catalog.regclass)", database, Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        (HealthCheckContext after, HealthConnection afterConnection) = host.Context();
        await using HealthConnection __ = afterConnection;

        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(after);
        TableFacts reset = tables.Single(static x => x.Key == "public.reset_me");
        TableFacts untouched = tables.Single(static x => x.Key == "public.untouched");
        IndexFacts index = (await HealthTables.IndexReadAsync(after)).Indexes.Single(static x => x.Row.Name == "reset_me_v");

        untouched.Row.CountersReset.Should().BeNull("nobody reset it; its counters count from the database's statistics");

        if (nineteen)
        {
            reset.Row.CountersReset.Should().NotBeNull().And.BeOnOrAfter(resetAt.AddMinutes(-1)).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddMinutes(1));
            index.Row.CountersReset.Should().NotBeNull().And.BeOnOrAfter(resetAt.AddMinutes(-1));
            reset.Row.SequentialScans.Should().Be(0, "the reset zeroed its counters, which the time dates");
        }
        else
        {
            reset.Row.CountersReset.Should().BeNull("PostgreSQL " + server.ServerVersion + " keeps no reset time per table");
            index.Row.CountersReset.Should().BeNull();
        }

        // The pages' facts carry the same: Storage's table row, and Maintenance's view of the sweep (T07's scores included).
        StorageFacts storage = await host.Services.GetRequiredService<IStorageFacts>().ReadAsync(host.Resolved, Token);
        StorageTableFacts storageRow = storage.LargestTables.Single(static x => x.Table.Table == "reset_me");
        storage.StatsResetPerTableRead.Should().Be(nineteen);
        storageRow.Row.CountersReset.Should().Be(reset.Row.CountersReset);
        storage.LargestTables.Single(static x => x.Table.Table == "untouched").Row.CountersReset.Should().BeNull();

        MaintenanceFacts maintenance = await host.Services.GetRequiredService<IMaintenanceFacts>().ReadAsync(host.Resolved, Token);
        maintenance.Tables!.Single(static x => x.Key == "public.reset_me").Row.CountersReset.Should().Be(reset.Row.CountersReset);

        if (nineteen)
        {
            maintenance.AutovacuumScores.Should().NotBeNull("T07 reads the scores in every regular sweep on 19")
                .And.ContainKey("public.reset_me").And.ContainKey("public.untouched");
        }
        else
        {
            maintenance.AutovacuumScores.Should().BeNull();
        }
    }

    /// <summary>
    /// <c>pg_get_multixact_stats()</c>: two sessions that lock one row <c>FOR SHARE</c> at once make a multixact of two members,
    /// and the read says so; below 19 the function is never named.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task Multixact_usage_counts_the_members_of_a_row_two_sessions_share_on_19_and_is_not_asked_below(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        bool nineteen = PostgresFeatures.Has(PostgresFeature.MultixactStats, server.VersionNumber);
        string database = await server.CreateDatabaseAsync("pg19_multixact", Token);
        await server.ExecuteAsync("create table public.shared_row (id int primary key); insert into public.shared_row values (1)", database, Token);

        await using NpgsqlConnection first = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlConnection second = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlTransaction one = await first.BeginTransactionAsync(Token);
        await using NpgsqlTransaction two = await second.BeginTransactionAsync(Token);

        foreach ((NpgsqlConnection connection, NpgsqlTransaction transaction) in new[] { (first, one), (second, two) })
        {
            await using NpgsqlCommand share = new("select id from public.shared_row for share", connection, transaction);
            await share.ExecuteScalarAsync(Token);
        }

        // A role with pg_read_all_stats reads the figures; the application's own role, in neither it nor pg_monitor, is answered a
        // row of nulls (measured on 19rc1), which the evidence words as "not shown" and never as "none".
        MultixactUsage? privileged;
        MultixactUsage? plain;
        string[] asked;

        await using (MatrixHost stats = await MatrixHost.CreateAsync(server, MatrixRole.Stats, database))
        await using (MatrixHost app = await MatrixHost.CreateAsync(server, MatrixRole.App, database))
        {
            (HealthCheckContext statsContext, HealthConnection statsConnection) = stats.Context();
            (HealthCheckContext appContext, HealthConnection appConnection) = app.Context();
            await using HealthConnection _ = statsConnection;
            await using HealthConnection __ = appConnection;

            privileged = await MultixactAgeCheck.UsageAsync(statsContext, server.VersionNumber, Token);
            plain = await MultixactAgeCheck.UsageAsync(appContext, server.VersionNumber, Token);
            asked = [.. statsContext.Sql.Statements.Select(static x => x.Sql), .. appContext.Sql.Statements.Select(static x => x.Sql)];
        }

        await two.RollbackAsync(Token);
        await one.RollbackAsync(Token);

        if (!nineteen)
        {
            privileged.Should().BeNull();
            plain.Should().BeNull();
            asked.Should().BeEmpty("the function is never named below 19, so no statement ran at all");
            return;
        }

        privileged.Should().NotBeNull();
        privileged!.Withheld.Should().BeFalse();
        privileged.Multixacts.Should().BeGreaterThanOrEqualTo(1, "two sessions share one row's lock");
        privileged.Members.Should().BeGreaterThanOrEqualTo(2);
        privileged.MembersBytes.Should().BeGreaterThan(0);
        privileged.OldestMultixact.Should().BeGreaterThanOrEqualTo(1);
        asked.Should().HaveCount(2).And.OnlyContain(static x => x.Contains("pg_get_multixact_stats", StringComparison.Ordinal));

        MultixactAgeCheck.UsageEvidence(privileged).Value.Should().Contain("members in").And.Contain("of member storage");

        plain.Should().NotBeNull();
        plain!.Withheld.Should().BeTrue("the application's role is in neither pg_read_all_stats nor pg_monitor");
        MultixactAgeCheck.UsageEvidence(plain).Value.Should().StartWith("not shown:").And.Contain("pg_read_all_stats");
    }

    /// <summary>
    /// T19's evidence on a table whose unpruned query takes more locks than <c>max_locks_per_transaction</c> allows: on 19 it
    /// carries <c>pg_stat_lock</c>'s fast-path figure and the same finding is otherwise as it is below.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task T19_adds_the_fast_path_figure_on_19_and_the_same_finding_without_it_below(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        bool nineteen = PostgresFeatures.Has(PostgresFeature.PgStatLock, server.VersionNumber);
        string database = await server.CreateDatabaseAsync("pg19_locks", Token);

        // 1 + 300 partitions + their 300 primary-key indexes (+ the partitioned index from 16): past 64 and past 128 alike.
        await server.ExecuteAsync(
            "create table public.wide (k int not null, v int, primary key (k)) partition by list (k); "
            + "do $$ begin for i in 1..300 loop execute pg_catalog.format('create table public.wide_%s partition of public.wide for values in (%s)', i, i); end loop; end $$",
            database,
            Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);
        HealthCheckResult t19 = sweep.Results.Single(static x => x.Check.Code == "T19");

        t19.Outcome.Should().Be(HealthCheckOutcome.Checked, t19.Problem?.Reason);
        HealthFinding finding = t19.Findings.Should().ContainSingle(static x => x.Subject == "public.wide").Subject;

        HealthEvidence[] fastPath = [.. finding.Evidence.Where(static x => x.Label.StartsWith("Fast-path slots found full", StringComparison.Ordinal))];

        if (nineteen)
        {
            HealthEvidence found = fastPath.Should().ContainSingle().Subject;
            found.Label.Should().Be("Fast-path slots found full, whole server (PostgreSQL 19)");
            found.Value.Should().MatchRegex(@"^\d[\d,]* times? since .+; \d[\d,]* relation-lock requests? had to wait$");
            finding.Evidence.Select(static x => x.Label).Should().Contain("Fast-path slots a backend");
        }
        else
        {
            fastPath.Should().BeEmpty();
            t19.Statements.Select(static x => x.Sql).Should().NotContain(static x => x.Contains("pg_stat_lock", StringComparison.Ordinal));
        }
    }

    private static async Task<T> ScalarAsync<T>(MatrixServer server, string database, string sql)
    {
        await using NpgsqlConnection connection = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync(Token);
        return value is null or DBNull ? default! : (T) Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
