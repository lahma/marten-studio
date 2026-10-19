using System.Globalization;
using System.Security.Claims;

using Marten;

using MartenStudio.Integration.Tests.Health;
using MartenStudio.SampleDomain;
using MartenStudio.Services;
using MartenStudio.Services.Health;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// The 0.4 plan's whole-sweep cost budget: one regular health sweep of every area over the hundred-thousand-row set,
/// through the studio's own sweeper, stays inside a stated statement count and database time, holds no lock beyond a
/// catalog read and the plain read of Marten's own bookkeeping tables its daemon and HiLo checks make, and runs no DDL.
/// </summary>
/// <remarks>
/// <para>
/// <b>In this class, on this class's fixture.</b> A partial of <see cref="MediumDataSetLiveTests" /> rather than a class
/// of its own, so the sweep runs over the very data set the responsiveness budgets measure, generated once: a second
/// <c>IClassFixture&lt;MediumDataSetFixture&gt;</c> would generate the hundred thousand documents and events a second
/// time in every run of the suite.
/// </para>
/// <para>
/// <b>Two sweeps, because the lock probe costs time.</b> <see cref="HealthSweepProbe" /> reads <c>pg_locks</c> from a
/// second connection inside every statement's transaction, which is what makes "no lock beyond a catalog read" an
/// observation rather than a hope - and which also lengthens every transaction it watches. So the time and the count are
/// read from a sweep through the fixture's own container, which has no observer, and the locks from a sweep through a
/// second container over the same schemas with the probe registered. Both are the first sweep of a fresh history, so
/// they run the same statements, and the test says so: the probe's count must equal the first sweep's.
/// </para>
/// <para>
/// <b>Database time is the sweep's own.</b> It is the sum of the gather times of the checks that ran SQL - each little
/// more than that check's read-only transactions, begin to rollback, round trips included, which is what
/// <c>StudioDatabaseTime</c> meters for the Health page's "Studio cost" line, measured the same way, client side, so an
/// upper bound on what the server spent. The meter itself is not read here: it is process-wide
/// and keyed by host, port and database, and every parallel test class in this assembly reads the same database through
/// the same key, so its total over a sweep is the sweep's plus whatever the rest of the suite did meanwhile.
/// </para>
/// <para>
/// <b>Statements</b> are the health statements a sweep runs - what each finding's "Show SQL" lists - counted once each
/// however many checks share one (<see cref="HealthSweepMemo" />). The session set-up each read-only transaction carries
/// (<c>BEGIN READ ONLY</c>, its timeouts, the pinned <c>search_path</c>, the rollback) is per statement and not counted
/// separately.
/// </para>
/// </remarks>
public partial class MediumDataSetLiveTests
{
    /// <summary>
    /// The health statements one regular sweep of this set ran when it was measured, on 2026-10-19: 27 from 32 of the 42
    /// checks, at the Marten floor (9.39.0) and at the latest tested (9.50.2) alike - so one budget serves both. See
    /// <see cref="SweepStatementBudget" /> for the margin.
    /// </summary>
    private const int MeasuredSweepStatements = 27;

    /// <summary>
    /// The statement budget: the measured count plus two. A check that adds a statement on a path this data set takes is
    /// a reviewed change to this number, and two is room for one conditional read - such as D02's lock-holder query, read
    /// only when a shard lags - appearing or not between runs, not for a check that reads per table.
    /// </summary>
    private const int SweepStatementBudget = MeasuredSweepStatements + 2;

    /// <summary>
    /// The database-time ceiling for one regular sweep of this set: 5 s, against 400-450 ms measured at the floor and
    /// 456 ms at 9.50.2 on 2026-10-19 (500-570 ms wall clock).
    /// </summary>
    /// <remarks>
    /// A ceiling, not a budget a regression must miss by a margin: ten times a measured sweep, because the assembly runs
    /// four collections beside this one against the same Postgres and a line a busy runner can cross measures the runner.
    /// What it catches is a sweep that runs into <c>statement_timeout</c> or multiplies its statements - per table, per
    /// shard, per tenant. It does <em>not</em> catch one warm sequential scan: a hundred thousand rows of
    /// <c>mt_events</c> scan in well under a second here, inside the ceiling. That a regular sweep reads no document
    /// table at all, and only Marten's bookkeeping beyond the catalog, is asserted by the lock probe instead.
    /// </remarks>
    private static readonly TimeSpan SweepDatabaseTimeBudget = TimeSpan.FromSeconds(5);

    [PostgresFact]
    public async Task One_regular_sweep_of_every_area_stays_inside_its_statement_and_time_budget_and_locks_and_creates_nothing()
    {
        string schema = fixture.Marten.Schema;
        string[] schemas = [schema, schema + "_events"];

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(fixture.Postgres, schemas);
        before.Should().Contain("schema:" + schema, "the generated set lives here");

        // The time and the count: the fixture's own container, which has no statement observer.
        HealthSweep timed = await RegularSweepAsync(fixture.Marten.Services, fixture.Marten.Scope);

        timed.Results.Select(static x => x.Check.Area).Distinct().Should().BeEquivalentTo(
            Enum.GetValues<HealthArea>(), "a regular sweep runs a check of every area");

        // Every catalog check runs to the end over this set, so its statements are all counted. A configuration check runs
        // none, and one may say it cannot judge this process - C01 cannot tell Marten's pool apart from the other test
        // classes' pools on the same connection string, which is a fact about a test assembly, not about the sweep.
        timed.Results
            .Where(static x => x.Outcome == HealthCheckOutcome.Failed
                || (x.Check.Tier == HealthCostTier.Catalog && x.Outcome is not (HealthCheckOutcome.Checked or HealthCheckOutcome.NotApplicable)))
            .Select(static x => x.Check.Code + ": " + x.Outcome + " - " + (x.Problem?.Reason ?? x.Note))
            .Should().BeEmpty("every check that reads the database runs to the end over this set");
        timed.Results.Where(static x => x.Check.Tier == HealthCostTier.InProcess).Should().OnlyContain(
            static x => x.Statements.Count == 0, "a configuration check reads no SQL");

        int statements = timed.Results
            .SelectMany(static x => x.Statements)
            .Cast<object>()
            .Distinct(ReferenceEqualityComparer.Instance)
            .Count();

        TimeSpan databaseTime = timed.Results
            .Where(static x => x.Statements.Count > 0)
            .Aggregate(TimeSpan.Zero, static (sum, x) => sum + x.Duration);

        Write(string.Format(
            CultureInfo.InvariantCulture,
            "Marten {0}: one regular sweep of {1} checks ran {2} health statements (budget {3}) in {4:N0} ms of database time "
            + "(budget {5:N0} ms), {6:N0} ms wall clock.",
            typeof(IDocumentStore).Assembly.GetName().Version,
            timed.Results.Count,
            statements,
            SweepStatementBudget,
            databaseTime.TotalMilliseconds,
            SweepDatabaseTimeBudget.TotalMilliseconds,
            timed.Duration.TotalMilliseconds));

        foreach (HealthCheckResult result in timed.Results.Where(static x => x.Statements.Count > 0).OrderByDescending(static x => x.Duration))
        {
            Write(string.Format(CultureInfo.InvariantCulture, "  {0,-4} {1,6:N0} ms  {2} statement(s)", result.Check.Code, result.Duration.TotalMilliseconds, result.Statements.Count));
        }

        statements.Should().BePositive("so the budget is not met by a sweep that read nothing");
        statements.Should().BeLessThanOrEqualTo(SweepStatementBudget,
            "one regular sweep of every area runs a bounded set of catalog statements, whatever the data set's size");
        databaseTime.Should().BeLessThanOrEqualTo(SweepDatabaseTimeBudget,
            "a regular sweep neither runs into statement_timeout nor multiplies its statements over this set");

        // The locks: a second container over the same schemas, with the probe watching every statement's session.
        HealthSweepProbe probe = new(fixture.Postgres);
        HealthSweep probed;

        await using (ServiceProvider probing = ProbedStudio(probe, schema))
        {
            try
            {
                probed = await RegularSweepAsync(probing, new StudioScope(MartenStoreRegistry.DefaultStoreKey, string.Empty, null));
            }
            finally
            {
                await probing.GetRequiredService<IDocumentStore>().DisposeAsync();
            }
        }

        probed.Results.Should().HaveCount(timed.Results.Count);
        probe.StatementsObserved.Should().Be(statements, "the probed sweep is the first of its history too, so it runs the same statements");
        probe.Locks.Should().Contain(static x => x.IsCatalogRead, "the probe sees the catalog AccessShareLocks the reads take");

        // Beyond the catalog and the statistics views, what a regular sweep reads is Marten's own bookkeeping, with the
        // plain AccessShareLock a read takes on the table, its sequence and its indexes: the daemon checks' progression,
        // high-water and skip rows, the boundary of mt_events above the mark and the dead letters, and T11's mt_hilo -
        // each relation allowed only to the statements of the checks that read it (SweepLockAllowList), so a table check
        // that started reading mt_events, or a daemon check that read mt_hilo, fails here. Never a document table, where
        // the hundred thousand documents are; never another mode; never an advisory lock.
        string events = schema + "_events";
        SweepLockAllowList allowed = SweepLockAllowList.From(probed, schema, events);

        ILookup<string, string> codesBySql = probed.Results
            .SelectMany(static x => x.Statements.Select(statement => (statement.Sql, x.Check.Code)))
            .ToLookup(static x => x.Sql, static x => x.Code, StringComparer.Ordinal);

        Write("Locked beyond the catalog, by the checks whose statement held it: " + string.Join(", ", probe.BeyondCatalogReads
            .Where(static x => !x.IsOwnTransaction)
            .Select(x => x.Mode + " " + x.Schema + "." + x.Relation + " <- " + string.Join("/", codesBySql[x.Statement].Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)));

        probe.BeyondCatalogReads.Where(x => !allowed.Allows(x)).Should().BeEmpty(
            "a regular sweep holds nothing but AccessShareLocks on catalog and statistics relations, its own virtual "
            + "transaction id, and Marten's own bookkeeping tables - each read only by the checks that are about it - on a "
            + "hundred thousand rows as on none");
        probe.Locks.Should().NotContain(x => x.Schema == schema && x.Relation != null && x.Relation.Contains("mt_doc_", StringComparison.Ordinal),
            "no check reads the generated documents' tables");

        IReadOnlySet<string> after = await HealthSweepProbe.SnapshotAsync(fixture.Postgres, schemas);
        after.Should().BeEquivalentTo(before, "two sweeps over the set created, dropped and altered nothing");
    }

    /// <summary>One regular sweep through the container's own sweeper, the scope resolved as a circuit resolves it.</summary>
    private static async Task<HealthSweep> RegularSweepAsync(IServiceProvider services, StudioScope scope)
    {
        await using AsyncServiceScope circuit = services.CreateAsyncScope();

        ResolvedScope resolved = await circuit.ServiceProvider.GetRequiredService<StudioScopeResolver>()
            .ResolveAsync(scope, null, Token);

        return await services.GetRequiredService<HealthSweeper>().SweepAsync(resolved, HealthSweepKind.Regular, Token);
    }

    /// <summary>
    /// A studio container over the generated set's schemas, as <see cref="MartenFixture" /> builds one but with
    /// <paramref name="probe" /> as its statement observer - and without touching the schemas, which hold the set.
    /// </summary>
    private ServiceProvider ProbedStudio(HealthSweepProbe probe, string schema)
    {
        ServiceCollection services = new();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, NobodyInParticular>();

        string connectionString = fixture.Postgres.ConnectionString;

        services.AddMarten(options =>
        {
            SampleStore.Configure(options, connectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema + "_events";
        });

        services.AddMartenStudio(static options => options.ReadOnly = false);
        services.AddSingleton<IHealthStatementObserver>(probe);

        return services.BuildServiceProvider();
    }

    /// <summary>No policy is configured, so the visitor is never asked about; the container needs one to build.</summary>
    private sealed class NobodyInParticular : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}

/// <summary>
/// The locks a regular sweep may hold, per relation and per statement: a catalog read and its own transaction's lock
/// from any statement, and a plain read of one of Marten's bookkeeping tables only from a statement of a check that is
/// about that table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per statement, as the daemon and table checks' own no-lock tests judge it</b> (<c>DaemonHealthLiveTests</c>,
/// <c>TableHealthSweepLiveTests</c>): an allow-list by relation alone would let a table check start reading
/// <c>mt_events</c>, or any check read <c>mt_hilo</c>, and stay green, because some other check reads the same table. The
/// statements behind each reader are the sweep's own (<see cref="HealthCheckResult.Statements" />), matched by text to
/// the statement the probe saw the lock under (<see cref="HeldLock.Statement" />).
/// </para>
/// <para>
/// <b>The readers</b>, measured over the hundred-thousand-row set on 2026-10-19: the progression table D01, D02, D03, D05
/// and D08; the boundary of <c>mt_events</c> above the mark and its sequence the high-water read D01, D02 and D03 share;
/// the high-water skips D07; the dead letters D06 - each with the table's indexes, which the planner opens with it - and
/// <c>mt_hilo</c> T11. The event checks of a regular sweep (E05, E07) read only the catalog and the statistics views, so a
/// lock of theirs on <c>mt_events</c> fails this.
/// </para>
/// </remarks>
internal sealed class SweepLockAllowList
{
    /// <summary>The event store's bookkeeping tables a regular sweep reads, matched as their indexes and sequence are named.</summary>
    public static readonly string[] EventBookkeeping = ["mt_event_progression", "mt_events", "mt_high_water_skips", "mt_doc_deadletterevent"];

    /// <summary>The checks whose statements may read <see cref="EventBookkeeping" />: the daemon checks, and no other.</summary>
    public static readonly string[] EventBookkeepingReaders = ["D01", "D02", "D03", "D04", "D05", "D06", "D07", "D08"];

    /// <summary>The checks whose statements may read <c>mt_hilo</c>.</summary>
    public static readonly string[] HiloReaders = ["T11"];

    private readonly string documentSchema;
    private readonly string eventSchema;
    private readonly IReadOnlySet<string> eventReaders;
    private readonly IReadOnlySet<string> hiloReaders;

    /// <summary>An allow-list over two sets of statement texts.</summary>
    public SweepLockAllowList(string documentSchema, string eventSchema, IReadOnlySet<string> eventReaders, IReadOnlySet<string> hiloReaders)
    {
        this.documentSchema = documentSchema;
        this.eventSchema = eventSchema;
        this.eventReaders = eventReaders;
        this.hiloReaders = hiloReaders;
    }

    /// <summary>The allow-list for one sweep: the statements its readers ran.</summary>
    public static SweepLockAllowList From(HealthSweep sweep, string documentSchema, string eventSchema)
    {
        ArgumentNullException.ThrowIfNull(sweep);

        return new SweepLockAllowList(
            documentSchema,
            eventSchema,
            StatementsOf(sweep, EventBookkeepingReaders),
            StatementsOf(sweep, HiloReaders));
    }

    /// <summary>Whether a regular sweep may hold <paramref name="held" />.</summary>
    public bool Allows(HeldLock held)
    {
        ArgumentNullException.ThrowIfNull(held);

        if (held.IsCatalogRead || held.IsOwnTransaction)
        {
            return true;
        }

        if (held is not { LockType: "relation", Mode: "AccessShareLock", Relation: { } relation })
        {
            return false;
        }

        if (held.Schema == eventSchema && Array.Exists(EventBookkeeping, table => relation.Contains(table, StringComparison.Ordinal)))
        {
            return eventReaders.Contains(held.Statement);
        }

        return held.Schema == documentSchema
            && relation.Contains("mt_hilo", StringComparison.Ordinal)
            && hiloReaders.Contains(held.Statement);
    }

    private static HashSet<string> StatementsOf(HealthSweep sweep, string[] codes) =>
        [.. sweep.Results
            .Where(x => codes.Contains(x.Check.Code, StringComparer.Ordinal))
            .SelectMany(static x => x.Statements)
            .Select(static x => x.Sql)];
}

/// <summary>
/// The anti-vacuity half of the whole-sweep lock assertion: <see cref="SweepLockAllowList" /> refuses a bookkeeping
/// table read from a check that is not about it, a mode a read does not take, a lock on a document table and an
/// advisory lock - and still admits what the sweep really holds. No database.
/// </summary>
public class SweepLockAllowListTests
{
    private const string DocumentSchema = "generation_medium";
    private const string EventSchema = "generation_medium_events";
    private const string DaemonStatement = "select ... from generation_medium_events.mt_event_progression";
    private const string HiloStatement = "select ... from generation_medium.mt_hilo";
    private const string TableStatement = "select ... from pg_catalog.pg_stat_user_tables";

    private static readonly SweepLockAllowList Allowed = new(
        DocumentSchema,
        EventSchema,
        new HashSet<string>(StringComparer.Ordinal) { DaemonStatement },
        new HashSet<string>(StringComparer.Ordinal) { HiloStatement });

    [Fact]
    public void It_admits_what_a_sweep_holds_and_refuses_everything_else()
    {
        Allowed.Allows(new HeldLock("relation", "AccessShareLock", "pg_catalog", "pg_class", TableStatement)).Should().BeTrue("a catalog read");
        Allowed.Allows(new HeldLock("virtualxid", "ExclusiveLock", null, null, TableStatement)).Should().BeTrue("the transaction's own lock");
        Allowed.Allows(new HeldLock("relation", "AccessShareLock", EventSchema, "mt_events", DaemonStatement)).Should().BeTrue("a daemon check reads mt_events");
        Allowed.Allows(new HeldLock("relation", "AccessShareLock", EventSchema, "pk_mt_events_stream_and_version", DaemonStatement))
            .Should().BeTrue("the planner opens a table's indexes with it");
        Allowed.Allows(new HeldLock("relation", "AccessShareLock", DocumentSchema, "mt_hilo", HiloStatement)).Should().BeTrue("T11 reads mt_hilo");

        Allowed.Allows(new HeldLock("relation", "AccessShareLock", EventSchema, "mt_events", TableStatement))
            .Should().BeFalse("a table check that reads mt_events is not a daemon check, whoever else reads the table");
        Allowed.Allows(new HeldLock("relation", "AccessShareLock", DocumentSchema, "mt_hilo", DaemonStatement))
            .Should().BeFalse("mt_hilo is T11's to read");
        Allowed.Allows(new HeldLock("relation", "RowExclusiveLock", EventSchema, "mt_event_progression", DaemonStatement))
            .Should().BeFalse("a read takes AccessShareLock and nothing stronger");
        Allowed.Allows(new HeldLock("relation", "AccessShareLock", DocumentSchema, "mt_doc_customer", DaemonStatement))
            .Should().BeFalse("no check reads a document table");
        Allowed.Allows(new HeldLock("advisory", "ExclusiveLock", null, null, DaemonStatement)).Should().BeFalse("no check takes an advisory lock");
        Allowed.Allows(new HeldLock("relation", "AccessShareLock", "elsewhere", "mt_events", DaemonStatement))
            .Should().BeFalse("the bookkeeping is this store's, in its own event schema");
    }
}
