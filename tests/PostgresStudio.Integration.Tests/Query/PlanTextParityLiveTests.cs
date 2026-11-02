using System.Globalization;
using System.Text;

using Npgsql;

using PostgresStudio.Integration.Tests.Browser;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services.Query;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Integration.Tests.Query;

/// <summary>
/// 1.0-P3A (issue #28): the text-format plan reader against the JSON reader, on the version matrix - 14 to 18, and 19's release
/// candidate as a preview leg. Each statement of the set - a sequential, index, index-only and bitmap scan, a hash, a merge and a
/// nested-loop join with a memoize, a sort that spills, a hash aggregate, an append over partitions with run-time pruning, a CTE,
/// an InitPlan and a SubPlan, a parallel gather, and the plans of writes - is explained as <c>FORMAT TEXT</c> and as <c>FORMAT
/// JSON</c> with the same options, and the two reads must agree on every field both carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>What can be compared.</b> A plain <c>EXPLAIN</c> is deterministic, so a text plan and a JSON plan of it are compared on
/// everything: the node types, the relations, the costs, the estimates, every condition and key line. An <c>ANALYZE</c> is run twice,
/// once for each format, so what the two runs measured differs - times, and which buffers were hit and which read - and only what the
/// statement determines is compared: rows, loops, rows removed, sort method and space, hash batches, heap fetches; a time is compared by
/// whether it is there, and buffers by their total of blocks hit and read on a table already warm. A parallel plan's actuals depend
/// on how many workers got scheduled, and are not compared at all.
/// </para>
/// <para>
/// <b>Fixtures.</b> With <c>MARTENSTUDIO_SAVE_PLAN_FIXTURES=1</c> the text of every statement on 14, 17 and 18 is written to
/// <c>tests/PostgresStudio.Tests/Statements/PlanCorpus/text-*.txt</c> (the TIMING-off variant on 17 only), and each plain plan's JSON
/// beside it as <c>pair-text-*.json</c>, for the fast suite to read offline. The files are checked in; this switch is how they were made.
/// </para>
/// <para>
/// <b>Statistics that hold still.</b> Each statement is explained twice, once a format, and the plain comparison includes the costs and
/// estimates, so the statistics must not change between the two runs. The seed's one <c>vacuum analyze</c> is the only analyze: every
/// table is created with <c>autovacuum_enabled = off</c> (the partitions, since a partitioned table takes no storage parameter), because an
/// autovacuum analyze that started after the inserts samples other rows and, committing later, moved a CTE scan's startup cost between
/// the text and the JSON run (CI, PostgreSQL 17, 2026-11-02).
/// </para>
/// </remarks>
public class PlanTextParityLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Seed =
    [
        "create table public.customers (id int primary key, region text not null) with (autovacuum_enabled = off)",
        "insert into public.customers select g, 'r' || (g % 10) from pg_catalog.generate_series(1, 2000) g",
        "create table public.orders (id int primary key, customer_id int not null, bucket int not null, amount numeric not null, placed date not null, note text) with (autovacuum_enabled = off)",
        "insert into public.orders select g, (g % 2000) + 1, g % 20, (g % 500)::numeric, date '2025-01-01' + (g % 365), 'note ' || g from pg_catalog.generate_series(1, 100000) g",
        "create index orders_customer_idx on public.orders (customer_id)",
        "create index orders_placed_idx on public.orders (placed)",
        "create table public.readings (id int not null, day date not null, v int not null) partition by range (day)",
        "create table public.readings_q1 partition of public.readings for values from ('2025-01-01') to ('2025-04-01') with (autovacuum_enabled = off)",
        "create table public.readings_q2 partition of public.readings for values from ('2025-04-01') to ('2025-07-01') with (autovacuum_enabled = off)",
        "create table public.readings_q3 partition of public.readings for values from ('2025-07-01') to ('2025-10-01') with (autovacuum_enabled = off)",
        "create table public.readings_q4 partition of public.readings for values from ('2025-10-01') to ('2026-01-01') with (autovacuum_enabled = off)",
        "insert into public.readings select g, date '2025-01-01' + (g % 360), g % 100 from pg_catalog.generate_series(1, 40000) g",
        "create index readings_day_idx on public.readings (day)",
        "create table public.parents (id int primary key) with (autovacuum_enabled = off)",
        "create table public.children (id int primary key, parent_id int not null references public.parents (id)) with (autovacuum_enabled = off)",
        "insert into public.parents select g from pg_catalog.generate_series(1, 100) g",
        "vacuum analyze",
    ];

    /// <summary>One statement of the set.</summary>
    /// <param name="Name">The fixture's name.</param>
    /// <param name="Sql">The statement.</param>
    /// <param name="Settings">Session settings that steer the planner to the shape, set before and reset after.</param>
    /// <param name="Parallel">Whether its actuals depend on scheduling.</param>
    /// <param name="PlainOnly">Whether it writes, and so is only planned.</param>
    /// <param name="Rollback">Whether it is analyzed inside a transaction that is rolled back.</param>
    private sealed record Statement(string Name, string Sql, string[]? Settings = null, bool Parallel = false, bool PlainOnly = false, bool Rollback = false);

    private static readonly Statement[] Statements =
    [
        new("seqscan", "select * from public.orders where note = 'note 5'"),
        new("indexscan", "select * from public.orders where id = 500"),
        new("indexonly", "select id from public.orders where id < 1000 order by id"),
        new("bitmap", "select * from public.orders where customer_id = 17", ["enable_indexscan = off"]),
        new("hashjoin", "select c.region, o.amount from public.orders o join public.customers c on c.id = o.customer_id where o.placed < date '2025-02-01'"),
        new("leftjoin", "select c.id, o.id from public.customers c left join public.orders o on o.customer_id = c.id and o.id < 100 where c.id < 50"),
        new("mergejoin", "select c.id, o.id from public.customers c join public.orders o on o.customer_id = c.id where c.id < 300", ["enable_hashjoin = off", "enable_nestloop = off"]),
        new("memoize", "select o.id, c.region from public.orders o join public.customers c on c.id = o.bucket + 1 where o.id < 5000", ["enable_hashjoin = off", "enable_mergejoin = off"]),
        new("sortspill", "select * from public.orders order by note", ["work_mem = '64kB'"]),
        new("topn", "select * from public.orders order by amount desc, id limit 10"),
        new("hashagg", "select customer_id, count(*), sum(amount) from public.orders group by customer_id"),
        new("groupagg", "select customer_id, count(*) from public.orders group by customer_id order by customer_id", ["enable_hashagg = off"]),
        new("append", "select * from public.readings where day >= date '2025-03-01' and day < date '2025-05-01'"),
        new("pruned", "select * from public.readings where day = (select date '2025-05-10')"),
        new("cte", "with c as materialized (select customer_id, count(*) n from public.orders group by customer_id) select * from c where n > 40"),
        new("subplans", "select c.id, (select max(o.amount) from public.orders o where o.customer_id = c.id) from public.customers c where c.id <= 20 and c.region = (select region from public.customers where id = 1)"),
        new("functionscan", "select g from pg_catalog.generate_series(1, 100) g where g % 7 = 0"),
        new("values", "select * from (values (1, 'a'), (2, 'b')) v (n, s) where n > 1"),
        new("setop", "select customer_id from public.orders where id < 500 intersect select id from public.customers where id < 400"),
        new("window", "select id, row_number() over (partition by customer_id order by id) from public.orders where id < 3000"),
        new("gather", "select count(*) from public.orders where note like '%9'", ["parallel_setup_cost = 0", "parallel_tuple_cost = 0", "min_parallel_table_scan_size = 0", "max_parallel_workers_per_gather = 2"], Parallel: true),
        new("insert", "insert into public.customers values (5000, 'x')", PlainOnly: true),
        new("update", "update public.orders set note = 'x' where id = 5", PlainOnly: true),
        new("delete", "delete from public.orders where customer_id = 5", PlainOnly: true),
        new("trigger", "insert into public.children values (1, 5)", Rollback: true),
    ];

    private sealed record Mode(string Name, string Options, bool Analyzed, bool Timed);

    private static IReadOnlyList<Mode> Modes(int major)
    {
        string extra = major >= 17 ? ", MEMORY" : string.Empty;

        return
        [
            new("plain", "VERBOSE, SETTINGS" + extra, false, false),
            new("analyze", "ANALYZE, BUFFERS, VERBOSE, SETTINGS" + extra, true, true),
            new("notiming", "ANALYZE, TIMING OFF, BUFFERS, VERBOSE, SETTINGS" + extra, true, false),
        ];
    }

    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task The_text_plan_reads_as_the_JSON_plan_does_on_every_version(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("p3a_parity", Token);

        await using (NpgsqlConnection seed = await server.OpenAsync(MatrixRole.Superuser, database, Token))
        {
            foreach (string sql in Seed)
            {
                await using NpgsqlCommand command = new(sql, seed);
                await command.ExecuteNonQueryAsync(Token);
            }
        }

        bool save = Environment.GetEnvironmentVariable("MARTENSTUDIO_SAVE_PLAN_FIXTURES") == "1" && major is 14 or 17 or 18;
        string corpus = Path.Combine(SampleHost.RepositoryRoot(), "tests", "PostgresStudio.Tests", "Statements", "PlanCorpus");

        List<string> problems = [];

        await using NpgsqlConnection connection = await server.OpenAsync(MatrixRole.Superuser, database, Token);

        foreach (Statement statement in Statements)
        {
            foreach (Mode mode in Modes(major))
            {
                if (mode.Analyzed && statement.PlainOnly)
                {
                    continue;
                }

                // A warm table: the first analyze of a statement reads blocks the second finds in the cache.
                if (mode.Analyzed)
                {
                    await RunAsync(connection, statement, "ANALYZE, TIMING OFF", "text");
                }

                string text = await RunAsync(connection, statement, mode.Options, "text");
                string json = await RunAsync(connection, statement, mode.Options, "json");

                QueryPlanRead fromText = QueryPlanReader.ReadAny(text);
                QueryPlanRead fromJson = QueryPlanReader.ReadAny(json);
                string label = "PostgreSQL " + major + " " + statement.Name + " (" + mode.Name + ")";

                if (fromText.Plan is null || fromJson.Plan is null)
                {
                    problems.Add(label + ": not read - text: " + (fromText.Failure ?? "ok") + "; json: " + (fromJson.Failure ?? "ok"));
                    continue;
                }

                problems.AddRange(PlanParity.Compare(fromJson.Plan, fromText.Plan, mode.Analyzed && !statement.Parallel, mode.Timed, label));

                if (save && (mode.Name != "notiming" || major == 17))
                {
                    string stem = "text-" + statement.Name + "-" + mode.Name + "-pg" + major.ToString(CultureInfo.InvariantCulture);
                    await File.WriteAllTextAsync(Path.Combine(corpus, stem + ".txt"), text + "\n", new UTF8Encoding(false), Token);

                    if (!mode.Analyzed)
                    {
                        await File.WriteAllTextAsync(Path.Combine(corpus, "pair-" + stem + ".json"), json + "\n", new UTF8Encoding(false), Token);
                    }
                }
            }
        }

        if (problems.Count > 0)
        {
            string report = string.Join(Environment.NewLine, problems.Take(400));

            if (server.Version.Preview)
            {
                TestContext.Current.TestOutputHelper?.WriteLine("Preview leg findings - {0}", report);
                Assert.Skip("Preview leg (" + server.Version.Image + "), reported and not failed: " + report);
            }

            problems.Should().BeEmpty(report);
        }
    }

    /// <summary>One <c>EXPLAIN</c> of a statement in a format, under the statement's own settings, rolled back where it writes.</summary>
    private static async Task<string> RunAsync(NpgsqlConnection connection, Statement statement, string options, string format)
    {
        await using (NpgsqlCommand begin = new("begin", connection))
        {
            await begin.ExecuteNonQueryAsync(Token);
        }

        try
        {
            foreach (string setting in statement.Settings ?? [])
            {
                await using NpgsqlCommand set = new("set local " + setting, connection);
                await set.ExecuteNonQueryAsync(Token);
            }

            await using NpgsqlCommand explain = new("explain (" + options + ", FORMAT " + format.ToUpperInvariant() + ") " + statement.Sql, connection);
            await using NpgsqlDataReader reader = await explain.ExecuteReaderAsync(Token);
            StringBuilder lines = new();

            while (await reader.ReadAsync(Token))
            {
                if (lines.Length > 0)
                {
                    lines.Append('\n');
                }

                lines.Append(reader.GetString(0));
            }

            return lines.ToString();
        }
        finally
        {
            await using NpgsqlCommand rollback = new("rollback", connection);
            await rollback.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
