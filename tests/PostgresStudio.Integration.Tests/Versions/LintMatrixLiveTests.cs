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
            "set role app; "
            + "create table public.p (id int generated by default as identity, at timestamptz not null, parent_id int) partition by range (at); "
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

        // The database browser's foreign-key list with W5-B1's index aggregate, over the locked child (W5-B1 review F4).
        MatrixRead browserKeys = new("browser: foreign keys with their indexes", true, static async (x, t) =>
        {
            var keys = await PostgresStudio.Internal.Sql.DatabaseCatalogQueries.ReadForeignKeysAsync(
                x.Connection, x.Transaction, ["public"], null, null, PostgresStudio.Services.Database.DatabaseCatalog.ListCap, x.Timeout, t);

            if (!keys.Items.Any(static k => k.Table == "child" && k.Covered == false))
            {
                throw new InvalidOperationException("public.child's key to public.parent, which no index covers, was not read with its coverage.");
            }
        });

        if (await host.RunAsync(browserKeys, LockTimeout) is { } browserFailure)
        {
            problems.Add("browser foreign keys behind the lock: " + browserFailure.Message);
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
    /// X4's two reads on the oldest and newest supported releases, behind somebody's <c>ACCESS EXCLUSIVE</c> on every table
    /// they read and on a partition of each partitioned one, under a 300 ms <c>lock_timeout</c>: T20's bounds parsed by the
    /// studio from each release's own text - a <c>bigint</c> range written bare and quoted, where its rows stop, and a
    /// <c>timestamptz</c> range read by a session in Helsinki, whose offsets differ either side of October's clock change - and
    /// T18's tenant shares, from a table's partitions and from <c>pg_stats</c>.
    /// </summary>
    [PostgresTheory]
    [InlineData(14)]
    [InlineData(18)]
    public async Task X4s_partition_and_skew_reads_parse_each_releases_text_and_return_behind_a_held_lock(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("x4_reads", Token);
        await server.ExecuteAsync(
            "alter database x4_reads set timezone = 'Europe/Helsinki'; "
            + "set role app; "
            + "create table public.ledger (id bigint not null) partition by range (id); "
            + "create table public.ledger_p0 partition of public.ledger for values from (-1000) to (0); "
            + "create table public.ledger_p1 partition of public.ledger for values from (0) to (1000); "
            + "create table public.ledger_p2 partition of public.ledger for values from (1000) to (2000); "
            + "insert into public.ledger select g from pg_catalog.generate_series(-500, 500) g; "
            + "create table public.events (at timestamptz not null) partition by range (at); "
            + "create table public.events_2026_10 partition of public.events for values from ('2026-10-01 00:00+03') to ('2026-11-01 00:00+02'); "
            + "create table public.events_default partition of public.events default; "
            + "create table public.tenants (tenant_id text not null, id int not null) partition by list (tenant_id); "
            + "create table public.tenants_acme partition of public.tenants for values in ('acme'); "
            + "create table public.tenants_globex partition of public.tenants for values in ('globex'); "
            + "insert into public.tenants select case when g % 10 = 0 then 'globex' else 'acme' end, g from pg_catalog.generate_series(1, 1000) g; "
            + "create table public.shared (tenant_id text not null, id int not null); "
            + "insert into public.shared select case when g % 4 = 0 then 'globex' else 'acme' end, g from pg_catalog.generate_series(1, 2000) g; "
            + "analyze public.ledger; analyze public.tenants; analyze public.shared;",
            database,
            Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        HealthTableTarget[] tables = [new("public", "ledger"), new("public", "events"), new("public", "tenants"), new("public", "shared")];

        await using NpgsqlConnection holder = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlTransaction holding = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand lockThem = new(
            "lock table public.ledger, public.ledger_p1, public.events, public.events_2026_10, public.tenants, public.tenants_acme, public.shared in access exclusive mode",
            holder,
            holding))
        {
            await lockThem.ExecuteNonQueryAsync(Token);
        }

        List<HealthPartitionedTableRow> partitions = [];
        List<HealthColumnSkewRow> skews = [];

        (await host.RunAsync(Read(PartitionQueries.Partitions(tables), PartitionQueries.ReadPartitionedTable, partitions), LockTimeout))
            .Should().BeNull("the partition read deparses bounds with no relation and counts rows from the catalog (D27)");
        (await host.RunAsync(Read(ColumnSkewQueries.Skew(tables, "tenant_id"), ColumnSkewQueries.ReadSkew, skews), LockTimeout))
            .Should().BeNull("the skew read takes pg_stats and the catalog, never the tables");

        await holding.RollbackAsync(Token);

        static double Epoch(int year, int month, int day, int hour) => new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() / 1000d;

        HealthPartitionedTableRow ledger = partitions.Single(static x => x.Table == "ledger");
        ledger.Should().BeEquivalentTo(
            new { KeyType = "bigint", NewestLower = 1_000d, NewestUpper = 2_000d, PopulatedUpper = 1_000d, EmptyAbove = 1, UnreadBounds = 0, BoundedPartitions = 3 },
            static o => o.ExcludingMissingMembers(),
            "'-1000' quoted and 1000 bare, as {0} writes them; the rows stop in [0, 1000)", major);

        HealthPartitionedTableRow events = partitions.Single(static x => x.Table == "events");
        events.TimeZone.Should().Be("Europe/Helsinki");
        events.DateStyle.Should().StartWith("ISO");
        events.HasDefault.Should().BeTrue();
        events.NewestLower.Should().Be(Epoch(2026, 9, 30, 21), "'2026-10-01 00:00:00+03' is Helsinki's summer offset");
        events.NewestUpper.Should().Be(Epoch(2026, 10, 31, 22), "'2026-11-01 00:00:00+02' its winter one");

        HealthColumnSkewRow tenants = skews.Single(static x => x.Table == "tenants");
        tenants.ByPartition.Should().BeTrue();
        tenants.Values.Should().Be(2);
        PartitionBoundText.SingleListValue(tenants.TopValues[0]).Should().Be("acme", "the largest partition's bound, read with no relation");
        tenants.TopRows[0].Should().Be(900);

        HealthColumnSkewRow shared = skews.Single(static x => x.Table == "shared");
        shared.ByPartition.Should().BeFalse();
        shared.ReadableLeaves.Should().Be(1, "app owns the table, so pg_stats shows it the column");
        shared.TopValues[0].Should().Be("acme");
        (shared.TopRows[0] / shared.RowsCounted).Should().BeApproximately(0.75, 0.01, "1,500 of 2,000 rows, every one of them in ANALYZE's sample");
    }

    /// <summary>A matrix read that runs <paramref name="query" /> and keeps every row it maps.</summary>
    private static MatrixRead Read<T>(HealthQuery query, Func<NpgsqlDataReader, T> map, List<T> into) => new(query.Sql, true, async (x, t) =>
    {
        await using NpgsqlCommand command = new(query.Sql, x.Connection, x.Transaction) { CommandTimeout = x.Timeout };
        command.Parameters.AddRange(query.Parameters.Select(static p => p.Clone()).ToArray());
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(t);

        while (await reader.ReadAsync(t))
        {
            into.Add(map(reader));
        }
    });

    /// <summary>
    /// The database browser's one sequence value read, by a role without the privilege to read the value: answered at once
    /// behind somebody's lock on the sequence, because the privilege is asked before <c>pg_sequence_last_value</c> - which on
    /// 18 is what keeps its lock away - while the owner's read waits for it (W5-B1 review F4).
    /// </summary>
    [PostgresTheory]
    [InlineData(14)]
    [InlineData(18)]
    public async Task A_sequence_value_the_role_may_not_read_is_answered_behind_a_lock_and_the_owners_waits(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("b1_seq", Token);
        await server.ExecuteAsync("set role app; create sequence public.locked_seq; select pg_catalog.nextval('public.locked_seq');", database, Token);

        await using NpgsqlConnection holder = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlTransaction holding = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand alter = new("alter sequence public.locked_seq increment by 1", holder, holding))
        {
            // ShareRowExclusiveLock until the transaction ends: pg_sequence_last_value's RowExclusiveLock waits for it.
            await alter.ExecuteNonQueryAsync(Token);
        }

        static MatrixRead Value(bool readable) => new("browser: sequence value", false, async (x, t) =>
        {
            var value = await PostgresStudio.Internal.Sql.DatabaseCatalogQueries.ReadSequenceValueAsync(x.Connection, x.Transaction, "public", "locked_seq", x.Timeout, t)
                ?? throw new InvalidOperationException("public.locked_seq was not found.");

            if (value.CanReadValue != readable)
            {
                throw new InvalidOperationException("public.locked_seq read as " + (value.CanReadValue ? "readable" : "unreadable") + ".");
            }
        });

        await using (MatrixHost probe = await MatrixHost.CreateAsync(server, MatrixRole.Probe, database))
        {
            (await probe.RunAsync(Value(readable: false), LockTimeout)).Should().BeNull("no privilege, no pg_sequence_last_value, no lock");
        }

        await using (MatrixHost owner = await MatrixHost.CreateAsync(server, MatrixRole.App, database))
        {
            (await owner.RunAsync(Value(readable: true), LockTimeout)).Should().BeOfType<PostgresException>("the owner's read of the value takes the sequence's lock")
                .Which.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
        }

        await holding.RollbackAsync(Token);
    }

    /// <summary>
    /// What T26's remedy relies on: altering an identity column to <c>bigint</c> widens its sequence with it, and altering a
    /// serial's does not - its sequence needs its own <c>ALTER SEQUENCE … AS bigint</c>. An explicit <c>MAXVALUE</c> survives
    /// both, and the remedy's <c>NO MAXVALUE</c> lifts it - and the integer-key read judges the sequence it caps.
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

        // W5-B1 review F8: an identity capped at MAXVALUE 1,000,000 keeps its cap through the column's widening.
        await server.ExecuteAsync(
            "create table public.capped (id int generated by default as identity (maxvalue 1000000) primary key); "
            + MigrationSqlText.WidenColumnToBigint("public", "capped", "id"),
            database,
            Token);
        (await SequenceOfAsync(server, database, "public.capped")).Should().Be(("bigint", 1_000_000), "a change of type keeps an explicit MAXVALUE");

        // The integer-key read now judges the capped bigint sequence; the remedy's NO MAXVALUE lifts it.
        await using (NpgsqlConnection connection = await server.OpenAsync(MatrixRole.Superuser, database, Token))
        {
            HealthQuery keys = IndexLintQueries.IntegerKeys([new HealthTableTarget("public", "capped")]);
            await using NpgsqlCommand command = new(keys.Sql, connection);
            command.Parameters.AddRange(keys.Parameters.ToArray());
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
            (await reader.ReadAsync(Token)).Should().BeTrue("a bigint sequence capped below bigint's maximum is judged");
            HealthIntegerKeyRow capped = IndexLintQueries.ReadIntegerKey(reader);
            capped.Should().BeEquivalentTo(new { Column = "id", ColumnType = "bigint", SequenceType = "bigint", FedBy = "identity", Maximum = 1_000_000L }, o => o.ExcludingMissingMembers());
        }

        await server.ExecuteAsync(MigrationSqlText.IdentityNoMaxValue("public", "capped", "id")!, database, Token);
        (await SequenceOfAsync(server, database, "public.capped")).Should().Be(("bigint", long.MaxValue), "SET NO MAXVALUE lifts the cap to the type's own");
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
