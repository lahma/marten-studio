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
    /// The database-time budget for one regular sweep of this set: 5 s, against 400-450 ms measured at the floor and
    /// 456 ms at 9.50.2 on 2026-10-19 (500-570 ms wall clock). Ten times a measured sweep rather than a few milliseconds
    /// over it, like every
    /// budget in <see cref="ResponsivenessBudgets" />: the assembly runs four collections beside this one against the same
    /// Postgres, and a budget a busy runner can miss measures the runner. A check whose cost grows with the data - a scan
    /// of mt_events, an exact count - misses it by orders of magnitude, not by a margin.
    /// </summary>
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
            "a regular sweep reads the catalog and the statistics views, never the hundred thousand rows");

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
        // high-water and skip rows, the boundary of mt_events above the mark and the dead letters (D01-D08, E05), and
        // T11's mt_hilo. Never a document table, where the hundred thousand documents are; never another mode; never an
        // advisory lock.
        string events = schema + "_events";
        Write("Locked beyond the catalog: " + string.Join(", ", probe.BeyondCatalogReads
            .Select(static x => x.Mode + " " + x.Schema + "." + x.Relation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));

        probe.BeyondCatalogReads.Where(x => !IsBookkeepingRead(x, schema, events)).Should().BeEmpty(
            "a regular sweep holds nothing but AccessShareLocks on catalog and statistics relations and on Marten's own "
            + "bookkeeping tables it reads, and its own virtual transaction id - on a hundred thousand rows as on none");
        probe.Locks.Should().NotContain(x => x.Schema == schema && x.Relation != null && x.Relation.Contains("mt_doc_", StringComparison.Ordinal),
            "no check reads the generated documents' tables");

        IReadOnlySet<string> after = await HealthSweepProbe.SnapshotAsync(fixture.Postgres, schemas);
        after.Should().BeEquivalentTo(before, "two sweeps over the set created, dropped and altered nothing");
    }

    /// <summary>The event store's bookkeeping tables a regular sweep reads, matched as their indexes and sequence are named.</summary>
    private static readonly string[] EventBookkeeping = ["mt_event_progression", "mt_events", "mt_high_water_skips", "mt_doc_deadletterevent"];

    /// <summary>
    /// A lock a regular sweep may hold: a catalog read, its own transaction's, or a plain read of Marten's bookkeeping -
    /// <see cref="EventBookkeeping" /> in the event schema, <c>mt_hilo</c> in the document schema. The daemon checks' own
    /// allow-list (<see cref="DaemonSweeps.IsAllowedRead" />) plus the two tables only a whole sweep adds: D07's skips
    /// and T11's HiLo rows.
    /// </summary>
    private static bool IsBookkeepingRead(HeldLock held, string schema, string events) =>
        held.IsCatalogRead
        || held.IsOwnTransaction
        || (held is { LockType: "relation", Mode: "AccessShareLock", Relation: { } relation }
            && ((held.Schema == events && Array.Exists(EventBookkeeping, table => relation.Contains(table, StringComparison.Ordinal)))
                || (held.Schema == schema && relation.Contains("mt_hilo", StringComparison.Ordinal))));

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
