using JasperFx;

using Marten;

using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Documents;
using PostgresStudio.Services.Database;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The statement checks (T02, T22, T23) against a real <c>pg_stat_statements</c>, on a server of their own with the
/// library preloaded: each fires on the condition it looks for and stays quiet on the healthy sample store; the page and
/// the findings are gated both ways; a statement's text is masked for its visitor; a narrowed console role hides nothing
/// of the store's own statements; no sweep reads the text of more statements than it keeps; and the only locks beyond a
/// catalog read are an EXPLAIN's <c>AccessShareLock</c>s.
/// </summary>
public class StatementChecksLiveTests(PostgresFixture postgres, StatementsServerFixture server) : IClassFixture<StatementsServerFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Activity(MartenStudioOptions options) => options.Capabilities.ViewDatabaseActivity = true;

    private static void Targets(StoreOptions options)
    {
        options.Schema.For<StatementTarget>();
        options.Schema.For<StatementHidden>();
    }

    private static string TargetTable => SqlIdentifier.Qualify(SampleStore.DocumentSchema, "mt_doc_statementtarget");

    private static async Task SeedAsync(ConnectionsLiveHost host, int count) =>
        await host.Store.BulkInsertAsync(
            [.. Enumerable.Range(0, count).Select(static i => new StatementTarget { Id = Guid.NewGuid(), Number = i % 1000, Color = "c" + (i % 7) })],
            cancellation: Token);

    /// <summary>
    /// Marten's own query for one member value, from four sessions at once, until <c>pg_stat_statements</c> - read on
    /// <paramref name="database" />, a superuser's connection - says it has executed for twice T02's floor. A condition, not
    /// a duration: on a loaded machine four sessions spend part of any fixed span waiting rather than executing.
    /// </summary>
    private static async Task QueryHardAsync(ConnectionsLiveHost host, string database) =>
        await LiveWait.UntilAsync(
            async () =>
            {
                await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => StatementsLive.RepeatAsync(TimeSpan.FromSeconds(2), async () =>
                {
                    await using IQuerySession session = host.Store.QuerySession();
                    await session.Query<StatementTarget>().Where(x => x.Number == 7).ToListAsync(Token);
                })));

                return await LiveWait.ScalarAsync<double>(
                    database,
                    "select coalesce(sum(total_exec_time), 0)::float8 from public.pg_stat_statements "
                    + "where dbid = (select oid from pg_catalog.pg_database where datname = current_database()) "
                    + "and query like @shape",
                    ("shape", "select % from " + SampleStore.DocumentSchema + ".mt_doc_statementtarget as d where %"))
                    >= 2 * ExpensiveStatementsCheck.MinExecutionMs;
            },
            TimeSpan.FromSeconds(120),
            "Marten's select to execute for twice T02's ten-second floor");

    private static HealthReportFinding SelectFinding(HealthReport report) =>
        report.Findings.Should().ContainSingle(static x => x.Code == "T02"
            && x.Evidence.Any(static e => e.Label == "Statement" && e.Value.StartsWith("select", StringComparison.OrdinalIgnoreCase))).Subject;

    // ------------------------------------------------------------------------------------------------
    // T02
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("T02")]
    public async Task An_expensive_select_on_a_store_table_is_T02_with_its_text_its_plan_and_no_lock_beyond_the_plans()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_t02_fires");

        // On the test's own database: pg_locks names a relation by oid, and only that database's pg_class resolves it.
        StatementLocksProbe probe = new(database);

        await using ConnectionsLiveHost open = await ConnectionsLiveHost.CreateAsync(
            database, store: Targets, studio: Activity, services: s => s.AddSingleton<IHealthStatementObserver>(probe));
        await using ConnectionsLiveHost closed = await ConnectionsLiveHost.CreateAsync(database, store: Targets);

        await SeedAsync(open, 30_000);
        await QueryHardAsync(open, database);

        // The regular sweep a report also runs is the other checks', and is taken first so that it is cached: what the
        // probe records is the statement sweep alone.
        await open.ReportAsync();
        probe.Recording = true;

        IReadOnlySet<string> before = await StatementsLive.SnapshotAsync(database, SampleStore.DocumentSchema, SampleStore.EventSchema);
        HealthReport report = await StatementsLive.StatementsAsync(open);
        IReadOnlySet<string> after = await StatementsLive.SnapshotAsync(database, SampleStore.DocumentSchema, SampleStore.EventSchema);

        HealthCheckStatus status = ConnectionsLiveHost.Status(report, "T02");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);

        HealthReportFinding finding = SelectFinding(report);
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().StartWith("A statement on the StatementTarget collection");
        finding.Finding.DocumentTypes.Should().Equal(typeof(StatementTarget));

        HealthEvidence statement = finding.Evidence.Single(static x => x.Label == "Statement");
        statement.Value.Should().Contain(SampleStore.DocumentSchema + ".mt_doc_statementtarget", "the store's own schema is not withheld");
        statement.Value.Should().Contain("$1", "it is the text pg_stat_statements recorded, constants and all replaced");
        statement.Statement.Should().BeNull();

        finding.Evidence.Single(static x => x.Label == "Plan").Value.Should()
            .Contain("Seq Scan on mt_doc_statementtarget, reading every row to filter on a JSON member the recorded statement does not name",
                "Marten writes the member's name as a constant, which pg_stat_statements records as a placeholder");
        finding.Finding.Remedy!.Code.Should().Contain("opts.Schema.For<StatementTarget>().Index(x => x.FilteredMember);");
        finding.Finding.Link!.Route.Should().Be("query");
        finding.Finding.Caveat.Should().Contain("generic plan");

        // Show SQL names no table, and the plan's own entry says where its text came from instead of printing it.
        finding.Finding.Statements.Should().NotContain(static x => x.Sql.Contains("mt_doc_statementtarget", StringComparison.Ordinal));
        finding.Finding.Statements.Should().Contain(static x => x.Sql.StartsWith(HealthStatementQueries.PlanPrefix, StringComparison.Ordinal)
            && x.Sql.Contains(HealthStatementQueries.StatementShown, StringComparison.Ordinal));

        // Acceptance 4: the text is read once, and the only locks beyond a catalog read are the plan's AccessShareLocks.
        probe.Statements.Count(static x => x.Contains("(showtext := true)", StringComparison.Ordinal)).Should().Be(1);
        probe.Statements.Count(static x => x.Contains("(showtext := false)", StringComparison.Ordinal)).Should().Be(1);
        // Two kinds, and nothing else: the extension's own info view, read by the counters, and what a plan plans.
        probe.BeyondCatalogReads.Should().OnlyContain(static x => x.LockType == "relation" && x.Mode == "AccessShareLock");
        probe.BeyondCatalogReads.Where(static x => x.Statement.StartsWith(HealthStatementQueries.ApplicationRoleComment, StringComparison.Ordinal))
            .Should().OnlyContain(static x => x.Relation == "pg_stat_statements_info", "the store role's reads touch only the extension");
        probe.BeyondCatalogReads.Where(static x => !x.Statement.StartsWith(HealthStatementQueries.ApplicationRoleComment, StringComparison.Ordinal))
            .Should().NotBeEmpty("a plan locks what it plans").And.OnlyContain(static x =>
                x.Schema == SampleStore.DocumentSchema && x.Statement.StartsWith(HealthStatementQueries.PlanPrefix, StringComparison.Ordinal));
        after.Should().BeEquivalentTo(before, "a sweep runs no DDL");

        // Acceptance 2: a visitor short of ViewDatabaseActivity cannot run them, and is shown nothing of them.
        HealthReport refused = await StatementsLive.StatementsAsync(closed);
        refused.StatementChecksAllowed.Should().BeFalse();
        refused.StatementChecksRefused.Should().Contain("ViewDatabaseActivity");
        refused.Checks.Should().NotContain(static x => x.Code == "T02" || x.Code == "T22" || x.Code == "T23");
        refused.Findings.Should().NotContain(static x => x.Code == "T02");

        open.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    [HealthQuiet("T02")]
    [HealthQuiet("T22")]
    [HealthQuiet("T23")]
    public async Task The_healthy_sample_store_is_quiet_for_every_statement_check()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_statements_quiet");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Activity);

        // A little of what the demo does: customers and their orders stored, loaded and looked up by email.
        List<Customer> customers = [.. Enumerable.Range(0, 50).Select(static i => new Customer { Id = Guid.NewGuid(), Name = "Customer " + i, Email = "c" + i + "@example.com" })];

        await using (IDocumentSession session = host.Store.LightweightSession())
        {
            session.Store([.. customers]);
            session.Store([.. customers.Select(static x => new Order { Id = OrderId.New(), CustomerId = x.Id, Reference = "R-" + x.Name, Total = 10 })]);
            await session.SaveChangesAsync(Token);
        }

        foreach (Customer customer in customers)
        {
            await using IQuerySession session = host.Store.QuerySession();
            await session.LoadAsync<Customer>(customer.Id, Token);
            await session.Query<Customer>().Where(x => x.Email == customer.Email).ToListAsync(Token);
        }

        HealthReport report = await StatementsLive.StatementsAsync(host);

        foreach (string code in (string[]) ["T02", "T22", "T23"])
        {
            HealthCheckStatus status = ConnectionsLiveHost.Status(report, code);
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, code + " really ran: " + status.Problem);
            report.Findings.Should().NotContain(x => x.Code == code, "the healthy sample store is quiet for " + code);
        }

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // T22
    // ------------------------------------------------------------------------------------------------

    /// <summary>Saves <paramref name="order" /> <paramref name="saves" /> times, every <paramref name="every" />th behind another session's save.</summary>
    private static async Task<int> SaveAsync(ConnectionsLiveHost host, OrderId order, int saves, int? every)
    {
        int conflicts = 0;

        for (int i = 0; i < saves; i++)
        {
            await using IDocumentSession mine = host.Store.LightweightSession();
            Order loaded = (await mine.LoadAsync<Order>(order, Token))!;

            if (every is { } n && i % n == 0)
            {
                // Two sessions load and save one document under an optimistic guard: the second to save loses.
                await using IDocumentSession theirs = host.Store.LightweightSession();
                Order first = (await theirs.LoadAsync<Order>(order, Token))!;
                first.Total += 1;
                theirs.Store(first);
                await theirs.SaveChangesAsync(Token);
            }

            loaded.Total += 1;
            mine.Store(loaded);

            try
            {
                await mine.SaveChangesAsync(Token);
            }
            catch (Exception exception) when (IsConflict(exception))
            {
                conflicts++;
            }
        }

        return conflicts;
    }

    private static bool IsConflict(Exception exception) =>
        exception is ConcurrencyException
        || (exception is AggregateException aggregate && aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsConflict));

    private static async Task<OrderId> OrderAsync(ConnectionsLiveHost host)
    {
        Customer customer = new() { Id = Guid.NewGuid(), Name = "Contended", Email = "contended@example.com" };
        Order order = new() { Id = OrderId.New(), CustomerId = customer.Id, Reference = "H7-1", Total = 1 };

        await using IDocumentSession session = host.Store.LightweightSession();
        session.Store(customer);
        session.Store(order);
        await session.SaveChangesAsync(Token);

        return order.Id;
    }

    [PostgresFact]
    [HealthFires("T22")]
    public async Task Two_sessions_saving_one_order_under_its_optimistic_guard_are_T22_at_the_rate_they_conflicted()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_t22_fires");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Activity);

        OrderId order = await OrderAsync(host);
        int conflicts = await SaveAsync(host, order, saves: 1100, every: 10);
        conflicts.Should().Be(110, "every tenth save lost to the other session's");

        // The shape Marten wrote at this version is the one T22 recognises.
        ConcurrencyFacts facts = await host.GatherAsync(new ConcurrencyConflictCheck());
        facts.Writes.Should().ContainSingle(static x => x.Kind == GuardedWriteKind.Upsert && x.Table.Table == "mt_doc_order");

        HealthReport report = await StatementsLive.StatementsAsync(host);

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "T22").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning, "110 of 1,210 guarded writes is 9.1%");
        finding.Finding.Title.Should().Be("9.1% of the Order collection's guarded writes found a newer version");
        finding.Finding.DocumentTypes.Should().Equal(typeof(Order));
        finding.Evidence.Should().Contain(new HealthEvidence("Conflicts", "110"), "pg_stat_statements counted exactly the saves that threw");

        // 1,100 saves and the other session's 110, and the order's first save when Marten wrote it with the same guarded upsert.
        int.Parse(finding.Evidence.Single(static x => x.Label == "Guarded writes").Value, System.Globalization.NumberStyles.AllowThousands,
            System.Globalization.CultureInfo.InvariantCulture).Should().BeInRange(1210, 1211);

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    [HealthQuiet("T22")]
    public async Task Guarded_writes_that_never_conflict_are_quiet_and_say_so()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_t22_quiet");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Activity);

        OrderId order = await OrderAsync(host);
        (await SaveAsync(host, order, saves: 1100, every: null)).Should().Be(0);

        HealthReport report = await StatementsLive.StatementsAsync(host);

        report.Findings.Should().NotContain(static x => x.Code == "T22");
        ConnectionsLiveHost.Status(report, "T22").Note.Should().Contain("none conflicted");
    }

    // ------------------------------------------------------------------------------------------------
    // T23
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("T23")]
    public async Task A_store_whose_statements_are_all_compiled_is_T23_with_the_data_source_line()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_t23_fires");

        // Every statement this store sends is compiled: a host whose jit_above_cost sits below its statements' costs.
        string compiling = new NpgsqlConnectionStringBuilder(database)
        {
            Options = "-c jit_above_cost=0 -c jit_inline_above_cost=0 -c jit_optimize_above_cost=0",
        }.ConnectionString;

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(compiling, studio: Activity);

        for (int i = 0; i < 150; i++)
        {
            await using IQuerySession session = host.Store.QuerySession();
            await session.Query<Customer>().Where(x => x.Email == "nobody@example.com").ToListAsync(Token);
        }

        HealthReport report = await StatementsLive.StatementsAsync(host);

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "T23").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Where(static x => x.Label.StartsWith("Statement ", StringComparison.Ordinal) && !x.Label.EndsWith(" JIT", StringComparison.Ordinal))
            .Should().Contain(static x => x.Value.Contains("mt_doc_customer", StringComparison.Ordinal));
        finding.Finding.Remedy!.Code.Should().Be(JitShareCheck.RemedyCode);
    }

    // ------------------------------------------------------------------------------------------------
    // Acceptance 2: the text is masked for its visitor
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_statement_naming_a_hidden_type_or_a_withheld_schema_is_masked_for_its_visitor()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_t02_masking");

        await using ConnectionsLiveHost activity = await ConnectionsLiveHost.CreateAsync(database, store: Targets, studio: static options =>
        {
            options.Capabilities.ViewDatabaseActivity = true;
            options.IsDocumentTypeVisible = static type => type != typeof(StatementHidden);
        });

        await using ConnectionsLiveHost browsing = await ConnectionsLiveHost.CreateAsync(database, store: Targets, studio: static options =>
        {
            options.Capabilities.ViewDatabaseActivity = true;
            options.Capabilities.BrowseDatabase = true;
            options.BrowsableSchemas.Add("h7_withheld");
            options.IsDocumentTypeVisible = static type => type != typeof(StatementHidden);
        });

        await SeedAsync(activity, 100);
        await StatementsLive.ExecuteAsync(database, "create schema h7_withheld; create table h7_withheld.secrets (id int); insert into h7_withheld.secrets values (1), (2);");

        string hidden = SqlIdentifier.Qualify(SampleStore.DocumentSchema, "mt_doc_statementhidden");

        // Two statements of the store's role, each on the visible table and one other thing, each slow enough to be
        // T02's - by sleeping, so that the test costs the machine nothing.
        static Task SlowlyAsync(string database, string inner, (string Name, object Value) parameter) =>
            StatementsLive.RepeatAsync(TimeSpan.Zero, async () =>
            {
                for (int i = 0; i < 12; i++)
                {
                    await StatementsLive.ExecuteAsync(database, "select pg_sleep(0.6) from (" + inner + ") x", parameter);
                }
            });

        await Task.WhenAll(
            SlowlyAsync(database, "select count(*) from " + TargetTable + " t left join " + hidden + " h on h.id = t.id where t.data ->> 'Color' = @color", ("color", "c3")),
            SlowlyAsync(database, "select count(*) from " + TargetTable + " t cross join h7_withheld.secrets s where s.id = @id", ("id", 1)));

        HealthReport masked = await StatementsLive.StatementsAsync(activity);

        List<string> statements = [.. masked.Findings.Where(static x => x.Code == "T02").Select(static x => x.Evidence.Single(static e => e.Label == "Statement").Value)];

        statements.Should().Contain(SessionQueryText.HiddenTypeNote, "a statement that names a hidden type's table is withheld outright");
        statements.Should().Contain(static x => x.Contains("‹withheld›.secrets", StringComparison.Ordinal));
        statements.Should().NotContain(static x => x.Contains("h7_withheld", StringComparison.Ordinal) || x.Contains("statementhidden", StringComparison.Ordinal));

        HealthReport shown = await StatementsLive.StatementsAsync(browsing);
        shown.Findings.Where(static x => x.Code == "T02").Select(static x => x.Evidence.Single(static e => e.Label == "Statement").Value)
            .Should().Contain(static x => x.Contains("h7_withheld.secrets", StringComparison.Ordinal), "a visitor who may browse the schema sees it as written");
    }

    // ------------------------------------------------------------------------------------------------
    // Acceptance 3: read as the store's own role
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_narrowed_console_role_does_not_hide_the_store_roles_own_statements()
    {
        await StatementsLive.EnsureRolesAsync(server.ConnectionString);
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_statements_role", owner: StatementsLive.AppRole);
        string asApp = StatementsLive.As(database, StatementsLive.AppRole, StatementsLive.AppPassword);

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(asApp, store: Targets, studio: static options =>
        {
            options.Capabilities.ViewDatabaseActivity = true;
            options.SqlConsoleRole = StatementsLive.ConsoleRole;
        });

        await SeedAsync(host, 30_000);
        await QueryHardAsync(host, database);

        // The control: the console role sees the store role's statements only as <insufficient privilege>.
        (long Total, long Readable) asConsole = await AsConsoleAsync(asApp);
        asConsole.Total.Should().BePositive("the console role sees that the statements are there");
        asConsole.Readable.Should().Be(0, "and none of their text - which is why the read is not made as it");

        HealthReport report = await StatementsLive.StatementsAsync(host);

        HealthReportFinding finding = SelectFinding(report);
        finding.Evidence.Single(static x => x.Label == "Statement").Value.Should().Contain("mt_doc_statementtarget");

        // The plan is still SqlConsoleRole's, like every EXPLAIN the studio runs: that role may not read the table.
        finding.Evidence.Single(static x => x.Label == "Plan").Value.Should().Contain("SQLSTATE 42501").And.Contain("SqlConsoleRole");
    }

    /// <summary>The store role's statements as the console role reads pg_stat_statements: how many, and how many with text.</summary>
    private static async Task<(long Total, long Readable)> AsConsoleAsync(string asApp)
    {
        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(asApp) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);

        await using (NpgsqlCommand become = new("select pg_catalog.set_config('role', @role, false)", connection))
        {
            become.Parameters.AddWithValue("role", StatementsLive.ConsoleRole);
            await become.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlCommand count = new(
            "select count(*), count(*) filter (where query <> '<insufficient privilege>' and queryid is not null) from public.pg_stat_statements "
            + "where userid = (select oid from pg_catalog.pg_roles where rolname = @app) "
            + "and dbid = (select oid from pg_catalog.pg_database where datname = current_database())",
            connection);
        count.Parameters.AddWithValue("app", StatementsLive.AppRole);

        await using NpgsqlDataReader reader = await count.ExecuteReaderAsync(Token);
        await reader.ReadAsync(Token);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    // ------------------------------------------------------------------------------------------------
    // Acceptance 4: no sweep reads the text of more statements than it keeps
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a live server can show of the cap: an extension holding more than may be kept, a read that kept no more than
    /// <see cref="HealthStatementQueries.MaxTexts" /> - the checks' twenty and the Statements page's hundred (W5-Q1; two
    /// hundred more on Aurora) - and one text call that returned no more rows than that.
    /// </summary>
    /// <remarks>
    /// Said plainly, because a test that cannot fail proves less than its name: this one cannot see the cap broken. The
    /// counters statement ends <c>limit @max</c> with <c>@max</c> at most <see cref="HealthStatementQueries.MaxTexts" />, and
    /// <see cref="HealthStatementQueries.Texts" /> refuses more ids than that, so no server can return a row past it.
    /// What pins the cap is the fast suite: <c>HealthStatementQueriesTests</c> holds the bound parameter, the refusal and
    /// <c>StatementSelection.SharesFit</c> - every share fits inside the limit, so the limit drops nobody's pick.
    /// </remarks>
    [PostgresFact]
    public async Task No_sweep_reads_the_text_of_more_statements_than_it_keeps_whatever_the_extension_holds()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_statements_cost");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: Targets, studio: Activity);
        await SeedAsync(host, 2_000);

        // More statements of distinct shapes on the store's table than the read may keep, so that the extension holds more
        // than is read.
        int shapes = HealthStatementQueries.MaxTexts + 30;

        await using (NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(database) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(Token);

            for (int shape = 1; shape <= shapes; shape++)
            {
                string columns = string.Join(", ", Enumerable.Range(1, shape).Select(static i => "t.data ->> 'Color' as c" + i));
                await using NpgsqlCommand command = new("select " + columns + " from " + TargetTable + " t limit 5", connection);
                await command.ExecuteNonQueryAsync(Token);
            }
        }

        StatementRead read = await host.GatherAsync(static (context, _) => StatementReads.ReadAsync(context));

        read.AllStatements.Should().BeGreaterThan(HealthStatementQueries.MaxTexts, "the extension holds more statements than are read");
        read.Statements.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(HealthStatementQueries.MaxTexts);
        read.Statements.Count.Should().BeLessThanOrEqualTo(StatementReads.PageTop + 20, "off Aurora the page's hundred and the checks' twenty");
        read.TextsRead.Should().Be(read.Statements.Count, "the text is read for exactly the statements kept");

        // pg_stat_statements' own count of the studio's text read: one call, and no more rows than it keeps.
        (long Calls, long Rows) text = await TextReadAsync(database);
        text.Calls.Should().Be(1);
        text.Rows.Should().BeLessThanOrEqualTo(HealthStatementQueries.MaxTexts);
    }

    private static async Task<(long Calls, long Rows)> TextReadAsync(string database)
    {
        await using NpgsqlConnection connection = new(database);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select coalesce(sum(calls), 0)::int8, coalesce(sum(rows), 0)::int8 from public.pg_stat_statements "
            // pg_stat_statements records `true` as a placeholder too, so the text read is found by what it selects.
            + "where dbid = (select oid from pg_database where datname = current_database()) and query like '%pg_catalog.left(s.query, $%'",
            connection);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        await reader.ReadAsync(Token);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    // ------------------------------------------------------------------------------------------------
    // What a plan leaves on its session, and what a sweep adds to the host's statistics
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Two functions that misbehave only under a generic plan - which a plan batch sets after its <c>prepare</c> and before
    /// its <c>explain</c>, so each fails or stalls with the studio's statement already prepared on the session.
    /// </summary>
    private static readonly string[] MisbehavingFunctions =
    [
        "create schema h7_fns",
        "create function h7_fns.refuse_generic_plans() returns int4 language plpgsql immutable as $$\n"
        + "begin\n"
        + "  if pg_catalog.current_setting('plan_cache_mode') = 'force_generic_plan' then\n"
        + "    raise exception 'planned under force_generic_plan';\n"
        + "  end if;\n"
        + "  return 1;\n"
        + "end $$",
        "create function h7_fns.sleep_through_generic_plans() returns int4 language plpgsql immutable as $$\n"
        + "begin\n"
        + "  if pg_catalog.current_setting('plan_cache_mode') = 'force_generic_plan' then\n"
        + "    perform pg_catalog.pg_sleep(60);\n"
        + "  end if;\n"
        + "  return 1;\n"
        + "end $$",
    ];

    /// <summary>A statement as the read would hand it to the planner: its text, and nothing else that matters to a plan.</summary>
    private static ObservedStatement Recorded(long queryId, string text) => new(new StatementCounterRow { QueryId = queryId }, text, false, []);

    /// <summary>The sweep connection's backend, and how many statements named like the studio's plan are prepared on it.</summary>
    private static async Task<(int Pid, int Prepared)> SessionAsync(HealthCheckContext context, CancellationToken token) =>
        (await context.Sql.QueryAsync(
            new HealthQuery(
                "select pg_catalog.pg_backend_pid(), "
                + "(select count(*) from pg_catalog.pg_prepared_statements where name like @prefix)::int4",
                [new NpgsqlParameter("prefix", HealthStatementQueries.PlanStatementName + "%")]),
            static reader => (reader.GetInt32(0), reader.GetInt32(1)),
            token))[0];

    /// <summary>One way out of a plan, and what the session held after it.</summary>
    private sealed record PlanExit(string Case, int Pid, int Prepared, StatementPlan? Plan, Exception? Thrown);

    [PostgresFact]
    public async Task A_plan_leaves_nothing_prepared_on_its_session_whether_it_succeeds_fails_or_is_cancelled()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_statements_prepared");

        foreach (string statement in MisbehavingFunctions)
        {
            await StatementsLive.ExecuteAsync(database, statement);
        }

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: Targets, studio: Activity);
        string from = " from " + TargetTable + " as d where d.id = $1";

        (int Before, List<PlanExit> Exits) seen = await host.GatherAsync(async (context, token) =>
        {
            int before = (await SessionAsync(context, token)).Pid;
            List<PlanExit> exits = [];

            async Task ExitAsync(string name, StatementPlan? plan, Exception? thrown)
            {
                (int pid, int prepared) = await SessionAsync(context, token);
                exits.Add(new PlanExit(name, pid, prepared, plan, thrown));
            }

            await ExitAsync("succeeds", await ExpensiveStatementsCheck.PlanAsync(context, Recorded(1, "select d.data" + from), token), null);
            await ExitAsync("fails in its explain", await ExpensiveStatementsCheck.PlanAsync(context, Recorded(2, "select h7_fns.refuse_generic_plans()" + from), token), null);

            using (CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                cancel.CancelAfter(TimeSpan.FromSeconds(1));
                Exception? thrown = await Record.ExceptionAsync(() =>
                    ExpensiveStatementsCheck.PlanAsync(context, Recorded(3, "select h7_fns.sleep_through_generic_plans()" + from), cancel.Token));
                await ExitAsync("cancelled in its explain", null, thrown);
            }

            // The name left on the session by a batch that could not take it back - which the probe must see, or it sees
            // nothing - is deallocated, and the plan made once more.
            await context.Sql.QueryAsync(
                new HealthQuery("prepare " + SqlIdentifier.Quote(HealthStatementQueries.PlanStatementName) + " as select 1"), static _ => 0, token);
            await ExitAsync("left by an earlier batch", null, null);
            await ExitAsync("planned over the name left behind", await ExpensiveStatementsCheck.PlanAsync(context, Recorded(4, "select d.data" + from), token), null);

            return (before, exits);
        });

        seen.Exits.Should().OnlyContain(x => x.Pid == seen.Before, "every case is read on the plan's own physical connection: " + string.Join("; ", seen.Exits));

        seen.Exits.Where(static x => x.Case != "left by an earlier batch").Should().OnlyContain(
            static x => x.Prepared == 0, "no way out of a plan leaves the studio's statement prepared on the host's session");
        seen.Exits.Single(static x => x.Case == "left by an earlier batch").Prepared.Should().Be(1, "the probe sees a prepared statement when there is one");

        PlanExit succeeded = seen.Exits.Single(static x => x.Case == "succeeds");
        succeeded.Plan!.NotPlanned.Should().BeNull();
        succeeded.Plan.Scans.Should().NotBeEmpty();

        seen.Exits.Single(static x => x.Case == "fails in its explain").Plan!.NotPlanned.Should().Contain("SQLSTATE P0001");
        seen.Exits.Single(static x => x.Case == "cancelled in its explain").Thrown.Should().BeAssignableTo<OperationCanceledException>();
        seen.Exits.Single(static x => x.Case == "planned over the name left behind").Plan!.NotPlanned.Should().BeNull("42P05 is taken back and planned once more");

        host.Logs.WarningsOrWorse.Should().BeEmpty("a refused plan is the statement's evidence, logged at Debug");
    }

    /// <summary>
    /// <c>ForgetAsync</c>'s second step on its own: the deallocate run outside any transaction runs on the very session
    /// that holds the plan's name - the same backend, read by pid - and takes it back.
    /// </summary>
    [PostgresFact]
    public async Task The_deallocate_outside_a_transaction_takes_the_plan_back_on_the_session_that_holds_it()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_statements_outside");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Activity);

        (int Before, int Held, int After, int Left) seen = await host.GatherAsync(async (context, token) =>
        {
            (int before, _) = await SessionAsync(context, token);

            await context.Sql.QueryAsync(
                new HealthQuery("prepare " + SqlIdentifier.Quote(HealthStatementQueries.PlanStatementName) + " as select 1"), static _ => 0, token);
            (_, int held) = await SessionAsync(context, token);

            await context.Sql.ExecuteOutsideTransactionAsync(HealthStatementQueries.Deallocate(), token);
            (int after, int left) = await SessionAsync(context, token);

            return (before, held, after, left);
        });

        seen.Held.Should().Be(1, "the name is on the session before the deallocate, or the test proves nothing");
        seen.After.Should().Be(seen.Before, "the deallocate ran on the same backend - the session that holds the plan");
        seen.Left.Should().Be(0, "and took the name back");

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>One statement sweep: what the cache held before it, what it kept, and what it ran.</summary>
    private sealed record StatementSweep(IReadOnlySet<long> KnownBefore, IReadOnlyList<long> Kept, int TextCalls, int Planned)
    {
        public bool AllKnown => Kept.All(KnownBefore.Contains);
    }

    /// <summary>The three statement checks' gatherers on one connection with one memo, which is what a statement sweep is.</summary>
    private static async Task<StatementSweep> StatementSweepAsync(HealthCheckContext context, StatementTextCache cache, CancellationToken token)
    {
        IReadOnlySet<long> known = cache.Known(context.StoreKey, context.DatabaseId);

        ExpensiveStatementFacts t02 = await new ExpensiveStatementsCheck().GatherAsync(context, token);
        await new ConcurrencyConflictCheck().GatherAsync(context, token);
        await new JitShareCheck().GatherAsync(context, token);

        return new StatementSweep(
            known,
            [.. t02.Read.Statements.Select(static x => x.QueryId)],
            context.Sql.Statements.Count(static x => x.Sql.Contains("(showtext := true)", StringComparison.Ordinal)),
            t02.Plans.Count(static x => x.NotPlanned is null));
    }

    /// <summary>How many entries <c>pg_stat_statements</c> holds for <paramref name="database" />, every role's.</summary>
    private static async Task<long> EntriesAsync(string database) =>
        await LiveWait.ScalarAsync<long>(
            database,
            "select count(*) from public.pg_stat_statements where dbid = (select oid from pg_catalog.pg_database where datname = current_database())");

    [PostgresFact]
    public async Task Statement_sweeps_after_the_first_add_nothing_to_the_hosts_statistics_and_read_no_text_they_already_hold()
    {
        string database = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_statements_footprint");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, store: Targets, studio: Activity);
        await SeedAsync(host, 200);

        // A statement on the store's table slow enough a call to be planned by every sweep: twelve calls of 110 ms.
        for (int run = 0; run < ExpensiveStatementsCheck.SlowMeanCalls + 2; run++)
        {
            await StatementsLive.ExecuteAsync(database, "select count(*) from " + TargetTable + " as t, pg_catalog.pg_sleep(0.11)");
        }

        StatementTextCache cache = host.Provider.GetRequiredService<StatementTextCache>();

        // The count's own entry, and the reset its pooled connection runs, are made here rather than by the first count.
        await EntriesAsync(database);
        await EntriesAsync(database);

        List<(StatementSweep Sweep, long Entries)> sweeps = [];

        // Until a sweep after the first keeps only statements whose text it holds: the studio's own statements enter the
        // extension in the first sweep, and the next few may keep some of them before the ranking settles.
        while (sweeps.Count < 2 || (!sweeps[^1].Sweep.AllKnown && sweeps.Count < 8))
        {
            StatementSweep sweep = await host.GatherAsync((context, token) => StatementSweepAsync(context, cache, token));
            sweeps.Add((sweep, await EntriesAsync(database)));
        }

        string trace = string.Join("; ", sweeps.Select(static (x, i) =>
            $"sweep {i + 1}: kept {x.Sweep.Kept.Count}, unknown {x.Sweep.Kept.Count(id => !x.Sweep.KnownBefore.Contains(id))}, "
            + $"text calls {x.Sweep.TextCalls}, planned {x.Sweep.Planned}, entries {x.Entries}"));

        // Item 2: every sweep plans, under the one name, and after the first adds no entry - so the plans' statements are
        // the same entries every time, and the host's statistics hold a fixed set of the studio's.
        sweeps.Should().OnlyContain(static x => x.Sweep.Planned >= 1, "the slow statement is planned by every sweep: " + trace);
        sweeps.Skip(1).Should().OnlyContain(x => x.Entries == sweeps[0].Entries, "no sweep after the first adds an entry: " + trace);

        // Item 3: the first sweep reads its texts; a sweep that holds every kept statement's text makes no text call, and
        // one that does not reads them once.
        sweeps[0].Sweep.TextCalls.Should().Be(1, trace);
        sweeps.Skip(1).Should().OnlyContain(static x => x.Sweep.TextCalls == (x.Sweep.AllKnown ? 0 : 1), trace);
        sweeps[^1].Sweep.AllKnown.Should().BeTrue("within eight sweeps the ranking settles on statements whose text is held: " + trace);

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // Expected absences
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Without_the_extension_or_without_its_library_the_checks_say_what_would_enable_them()
    {
        string notInstalled = await StatementsLive.CreateDatabaseAsync(server.ConnectionString, "health_statements_absent", extension: false);
        await using ConnectionsLiveHost absent = await ConnectionsLiveHost.CreateAsync(notInstalled, studio: Activity);

        // The shared server has the extension's files and not its library in shared_preload_libraries.
        string notPreloaded = await StatementsLive.CreateDatabaseAsync(postgres.ConnectionString, "health_statements_not_preloaded");
        await using ConnectionsLiveHost unloaded = await ConnectionsLiveHost.CreateAsync(notPreloaded, studio: Activity);

        HealthReport withoutExtension = await StatementsLive.StatementsAsync(absent);
        HealthReport withoutLibrary = await StatementsLive.StatementsAsync(unloaded);

        foreach (string code in (string[]) ["T02", "T22", "T23"])
        {
            HealthCheckStatus missing = ConnectionsLiveHost.Status(withoutExtension, code);
            missing.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
            missing.Problem.Should().Be(StatementReads.NotInstalled);

            HealthCheckStatus unloadedStatus = ConnectionsLiveHost.Status(withoutLibrary, code);
            unloadedStatus.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
            unloadedStatus.Problem.Should().Be(StatementReads.NotPreloaded);
        }

        absent.Logs.WarningsOrWorse.Should().BeEmpty("an extension the host did not install is a value, not a warning");
        unloaded.Logs.WarningsOrWorse.Should().BeEmpty();
    }
}
