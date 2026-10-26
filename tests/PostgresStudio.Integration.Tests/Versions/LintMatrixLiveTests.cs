using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Integration.Tests.Versions;

/// <summary>
/// W5-B1 on the version matrix: T05's usage guard over a new database's young counters on 15 and 16, T20's bounds on 14
/// and 18, the lint reads behind a held <c>ACCESS EXCLUSIVE</c> on a partitioned table - beside the two deparses that do
/// queue, which is AGENTS.md D27's sentence - and what <c>ALTER COLUMN … TYPE bigint</c> does to an identity's and a
/// serial's sequence, which T26's remedy relies on.
/// </summary>
/// <remarks>
/// Each test makes a database of its own (<see cref="MatrixServer.CreateDatabaseAsync" />), so nothing here touches the
/// seeded one the rest of the matrix reads.
/// </remarks>
public class LintMatrixLiveTests(PostgresVersionMatrix matrix)
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(300);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A new database on 15 and 16 has no reset time of its own, so its counters are proven only from the oldest moment its
    /// statistics record: T05 withholds over them, and finds the unscanned index once the studio's clock is a week on.
    /// </summary>
    [PostgresTheory]
    [InlineData(15)]
    [InlineData(16)]
    public async Task T05_withholds_over_a_new_databases_young_counters_and_fires_once_they_have_counted_a_week(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("b1_t05", Token);
        await server.ExecuteAsync(
            "create table public.orders (id int primary key, name text not null); "
            + "create index orders_by_name on public.orders (name); "
            + "insert into public.orders select g, 'name ' || g from pg_catalog.generate_series(1, 110000) g; "
            + "select pg_catalog.pg_stat_force_next_flush();",
            database,
            Token);
        await server.ExecuteAsync("analyze public.orders", database, Token);

        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database, services: s => s.AddSingleton<TimeProvider>(clock));

        HealthCheckResult young = (await host.SweepAsync(HealthSweepKind.Regular)).Results.Single(static x => x.Check.Code == "T05");
        young.Outcome.Should().Be(HealthCheckOutcome.Checked, young.Problem?.Reason);
        young.Findings.Should().BeEmpty("counters minutes old prove nothing about an index's use");
        young.Note.Should().StartWith("Withheld: ");

        clock.Advance(TimeSpan.FromDays(8));

        HealthCheckResult week = (await host.SweepAsync(HealthSweepKind.Regular)).Results.Single(static x => x.Check.Code == "T05");
        HealthFinding finding = week.Findings.Should().ContainSingle(static x => x.Subject == "public.orders.orders_by_name", week.Note).Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(static x => x.Label == "Counters since");

        if (major >= 16)
        {
            finding.Evidence.Should().Contain(new HealthEvidence("Last scan", "never, since the counters began"), "16 records each index's last scan");
        }
        else
        {
            finding.Evidence.Should().NotContain(static x => x.Label == "Last scan", "15 has no last_idx_scan");
        }
    }

    /// <summary>T20's bounds, read through <c>pg_get_expr(relpartbound, 0)</c>, on the oldest and newest supported releases.</summary>
    [PostgresTheory]
    [InlineData(14)]
    [InlineData(18)]
    public async Task T20_reads_its_bounds_and_judges_them_on_14_and_18(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("b1_t20", Token);
        await server.ExecuteAsync(
            "create table public.events (at timestamptz not null, v int) partition by range (at); "
            + "create table public.events_2026_09 partition of public.events for values from ('2026-09-01 00:00+00') to ('2026-10-01 00:00+00'); "
            + "create table public.readings (at date not null, v int) partition by range (at); "
            + "create table public.readings_2026_12 partition of public.readings for values from ('2026-12-01') to ('2027-01-01'); "
            + "create table public.readings_default partition of public.readings default; "
            + "insert into public.readings select date '2027-02-01' + g % 20, g from pg_catalog.generate_series(1, 500) g; "
            + "create table public.kept (at timestamptz not null) partition by range (at); "
            + "create table public.kept_2026_12 partition of public.kept for values from ('2026-12-01 00:00+00') to ('2027-01-01 00:00+00'); "
            + "create table public.kept_default partition of public.kept default;",
            database,
            Token);
        await server.ExecuteAsync("analyze public.readings_default", database, Token);

        ManualClock clock = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database, services: s => s.AddSingleton<TimeProvider>(clock));

        HealthCheckResult t20 = (await host.SweepAsync(HealthSweepKind.Regular)).Results.Single(static x => x.Check.Code == "T20");
        t20.Outcome.Should().Be(HealthCheckOutcome.Checked, t20.Problem?.Reason);

        HealthFinding runningOut = t20.Findings.Should().ContainSingle(static x => x.Subject == "public.events").Subject;
        runningOut.Severity.Should().Be(HealthSeverity.Critical);
        runningOut.Remedy!.Code.Should().Be(
            "CREATE TABLE \"public\".\"events_p20261001\" PARTITION OF \"public\".\"events\" FOR VALUES FROM ('2026-10-01 00:00:00+00') TO ('2026-11-01 00:00:00+00');");

        t20.Findings.Should().ContainSingle(static x => x.Subject == "public.readings").Which.Variant.Should().Be(RangePartitionsCheck.DefaultRowsVariant);
        t20.Findings.Should().NotContain(static x => x.Subject == "public.kept", "67 days ahead with an empty DEFAULT is two widths and more");
    }

    /// <summary>
    /// The lint reads return behind somebody's <c>ACCESS EXCLUSIVE</c> on a partitioned table and its partitions, under a
    /// 300 ms <c>lock_timeout</c> - and the two deparses D27 names do not: <c>pg_get_partkeydef</c> and
    /// <c>pg_get_expr(relpartbound, &lt;relid&gt;)</c> open the relation and queue, <c>pg_get_expr(relpartbound, 0)</c> does not.
    /// </summary>
    [PostgresTheory]
    [InlineData(14)]
    [InlineData(18)]
    public async Task The_lint_reads_take_no_lock_where_partkeydef_and_a_relids_deparse_queue(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("b1_locks", Token);
        await server.ExecuteAsync(
            "create table public.p (id int generated by default as identity, at timestamptz not null, parent_id int) partition by range (at); "
            + "create index p_by_parent on public.p (parent_id); "
            + "create table public.p_2026_10 partition of public.p for values from ('2026-10-01 00:00+00') to ('2026-11-01 00:00+00'); "
            + "create table public.parent (id int primary key); "
            + "create table public.child (id serial primary key, parent_id int references public.parent (id));",
            database,
            Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        HealthTableTarget[] tables = [new("public", "p"), new("public", "child"), new("public", "parent")];

        await using NpgsqlConnection holder = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlTransaction holding = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand lockThem = new("lock table public.p, public.child in access exclusive mode", holder, holding))
        {
            await lockThem.ExecuteNonQueryAsync(Token);
        }

        List<string> problems = [];

        foreach ((string name, HealthQuery query) in new (string, HealthQuery)[]
        {
            ("partitions", PartitionQueries.Partitions(tables)),
            ("foreign keys", IndexLintQueries.ForeignKeys(tables)),
            ("integer keys", IndexLintQueries.IntegerKeys(tables)),
            ("counters age", IndexLintQueries.CountersAge(tables, lastScanColumns: PostgresStudio.Services.Server.PostgresFeatures.Has(PostgresStudio.Services.Server.PostgresFeature.LastScanColumns, major * 10_000))),
            ("partition bounds, no relation", new HealthQuery("select pg_catalog.pg_get_expr(c.relpartbound, 0) from pg_catalog.pg_class c where c.oid = 'public.p_2026_10'::pg_catalog.regclass", [])),
        })
        {
            if (await host.RunAsync(Run(query), LockTimeout) is { } failure)
            {
                problems.Add(name + " behind the lock: " + failure.Message);
            }
        }

        problems.Should().BeEmpty("a list locks nothing it lists (D27)");

        foreach ((string name, string sql) in new[]
        {
            ("pg_get_partkeydef", "select pg_catalog.pg_get_partkeydef('public.p'::pg_catalog.regclass)"),
            ("pg_get_expr with the partition's relid", "select pg_catalog.pg_get_expr(c.relpartbound, c.oid) from pg_catalog.pg_class c where c.oid = 'public.p_2026_10'::pg_catalog.regclass"),
        })
        {
            Exception? waited = await host.RunAsync(Run(new HealthQuery(sql, [])), LockTimeout);
            waited.Should().BeOfType<PostgresException>(name + " opens the relation, so it queues behind the lock")
                .Which.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
        }

        await holding.RollbackAsync(Token);
    }

    /// <summary>
    /// What T26's remedy relies on: altering an identity column to <c>bigint</c> widens its sequence with it, and altering a
    /// serial's does not - its sequence needs its own <c>ALTER SEQUENCE … AS bigint</c>.
    /// </summary>
    [PostgresTheory]
    [InlineData(14)]
    [InlineData(18)]
    public async Task Widening_an_identity_column_widens_its_sequence_and_a_serials_needs_its_own_statement(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("b1_widen", Token);
        await server.ExecuteAsync(
            "create table public.ident (id int generated by default as identity primary key); "
            + "create table public.ser (id serial primary key); "
            + "alter table public.ident alter column id type bigint; "
            + "alter table public.ser alter column id type bigint;",
            database,
            Token);

        (await SequenceOfAsync(server, database, "public.ident")).Should().Be(("bigint", long.MaxValue), "an identity's sequence follows its column");
        (await SequenceOfAsync(server, database, "public.ser")).Should().Be(("integer", int.MaxValue), "a serial's sequence keeps its own type");

        await server.ExecuteAsync(MigrationSqlText.WidenSequenceToBigint("public", "ser_id_seq")!, database, Token);
        (await SequenceOfAsync(server, database, "public.ser")).Should().Be(("bigint", long.MaxValue), "ALTER SEQUENCE ... AS bigint raises its maximum with its type");
    }

    private static async Task<(string Type, long Maximum)> SequenceOfAsync(MatrixServer server, string database, string table)
    {
        await using NpgsqlConnection connection = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlCommand command = new(
            "select s.seqtypid::pg_catalog.regtype::text, s.seqmax from pg_catalog.pg_sequence s "
            + "where s.seqrelid = pg_catalog.pg_get_serial_sequence(@table, 'id')::pg_catalog.regclass",
            connection);
        command.Parameters.AddWithValue("table", table);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).Should().BeTrue();
        return (reader.GetString(0), reader.GetInt64(1));
    }

    /// <summary>A matrix read that runs <paramref name="query" /> and reads every row.</summary>
    private static MatrixRead Run(HealthQuery query) => new(query.Sql, true, async (x, t) =>
    {
        await using NpgsqlCommand command = new(query.Sql, x.Connection, x.Transaction) { CommandTimeout = x.Timeout };
        command.Parameters.AddRange(query.Parameters.Select(static p => p.Clone()).ToArray());
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(t);

        while (await reader.ReadAsync(t))
        {
        }
    });
}
