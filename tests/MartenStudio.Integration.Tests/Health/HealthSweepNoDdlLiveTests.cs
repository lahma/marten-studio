using JasperFx;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// A health sweep runs no DDL and takes no lock beyond what a catalog read takes - measured with
/// <see cref="HealthSweepProbe" />, the helper every later Health packet reuses, and proven able to fail.
/// </summary>
/// <remarks>
/// <para>
/// The store is built to make a migration happen if anything lets one, the way <c>DatabaseNoDdlLiveTests</c>
/// builds it: two empty schemas, Marten's own <c>AutoCreate.CreateOrUpdate</c>, and a document type with a
/// <c>long</c> id so Marten's lazy HiLo feature is active. If a sweep reached <c>AllSchemaNames()</c>,
/// <c>AllObjects()</c> or a Weasel migration, <c>mt_hilo</c> would appear (hard rule 14).
/// </para>
/// <para>
/// H1's own checks are configuration checks and run no SQL, so the lock half is exercised by two made-up
/// catalog-tier checks registered beside them: one that reads <c>pg_class</c>, which must hold nothing beyond
/// catalog <c>AccessShareLock</c>s, and one that reads a user table, which the probe must catch.
/// </para>
/// </remarks>
public class HealthSweepNoDdlLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_sweep_creates_nothing_and_holds_no_lock_beyond_a_catalog_read()
    {
        const string schema = "health_noddl";
        HealthSweepProbe probe = new(postgres);

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            schema,
            store: static options =>
            {
                options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
                options.Schema.For<NumberedHealthThing>();
            },
            studio: static options => options.Capabilities.ViewDatabaseActivity = true,
            services: services => services
                .AddSingleton<IHealthStatementObserver>(probe)
                .AddSingleton(Registry(typeof(ReadsTheCatalogCheck))));

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events");
        before.Should().BeEquivalentTo(["schema:" + schema, "schema:" + schema + "_events"], "the test created two empty schemas and nothing else");

        // Every tier: the regular sweep, "Run deeper checks", and the statement checks the activity gate opens -
        // so a tier-2 or tier-3 check a later packet adds is under this assertion the day it lands.
        HealthReport report = await host.ReportAsync(new HealthReportRequest(HealthDeeperChecks.Run, HealthDeeperChecks.Run));

        report.Refusal.Should().Be(HealthRefusal.None, report.Reason);
        HealthLiveHost.Status(report, "T98").Outcome.Should().Be(HealthCheckOutcome.Checked, HealthLiveHost.Status(report, "T98").Problem);
        report.Checks.Select(static x => x.Code).Should().Contain(["K01", "K02", "K03"], "the shipped checks ran in the same sweep");

        probe.StatementsObserved.Should().BePositive("so 'no lock' is not 'nothing ran'");
        probe.Locks.Should().Contain(static x => x.IsCatalogRead, "reading pg_class takes catalog AccessShareLocks, and the probe sees them");
        probe.BeyondCatalogReads.Should().BeEmpty("a health statement reads the catalog and locks nothing it lists");

        IReadOnlySet<string> after = await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events");
        after.Should().BeEquivalentTo(before, "a sweep must never run DDL - not even Marten's HiLo bookkeeping");
    }

    /// <summary>The anti-vacuity half: the probe sees a lock a check should not take.</summary>
    [PostgresFact]
    public async Task The_probe_catches_a_check_that_reads_a_user_table()
    {
        const string schema = "health_noddl_target";
        HealthSweepProbe probe = new(postgres);

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            schema,
            services: services => services
                .AddSingleton<IHealthStatementObserver>(probe)
                .AddSingleton(Registry(typeof(ReadsAUserTableCheck))));

        await using (NpgsqlConnection connection = await postgres.OpenAsync(Token))
        await using (NpgsqlCommand create = new($"create table {SqlIdentifier.Qualify(schema, "probe_target")} (id int)", connection))
        {
            await create.ExecuteNonQueryAsync(Token);
        }

        HealthReport report = await host.ReportAsync();

        HealthLiveHost.Status(report, "T99").Outcome.Should().Be(HealthCheckOutcome.Checked, HealthLiveHost.Status(report, "T99").Problem);
        probe.BeyondCatalogReads.Should().Contain(
            x => x.Relation == "probe_target" && x.Schema == schema && x.Mode == "AccessShareLock",
            "a check that reads a user table holds a lock on it, and the helper must say so");
    }

    /// <summary>
    /// Review item 7: a builder may hand out one <see cref="HealthQuery" /> many times - a static one, a cached
    /// one - because the runner gives each command copies of its parameters.
    /// </summary>
    [PostgresFact]
    public async Task A_query_a_builder_hands_out_twice_runs_twice()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "health_cached_query",
            services: static services => services.AddSingleton(Registry(typeof(RunsACachedQueryTwiceCheck))));

        HealthReport report = await host.ReportAsync();

        HealthCheckStatus status = HealthLiveHost.Status(report, "T97");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Statements.Should().HaveCount(2);
    }

    /// <summary>The anti-vacuity half of the snapshot: an object created between two snapshots shows.</summary>
    [PostgresFact]
    public async Task The_snapshot_sees_an_object_created_between_two_reads()
    {
        string schema = await postgres.CreateSchemaAsync("health_noddl_snapshot", Token);

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, schema);

        await using (NpgsqlConnection connection = await postgres.OpenAsync(Token))
        await using (NpgsqlCommand create = new($"create table {SqlIdentifier.Qualify(schema, "appeared")} (id int primary key)", connection))
        {
            await create.ExecuteNonQueryAsync(Token);
        }

        IReadOnlySet<string> after = await HealthSweepProbe.SnapshotAsync(postgres, schema);

        after.Except(before).Should().Contain(["relation:" + schema + ".appeared:r", "constraint:" + schema + ".appeared_pkey"]);
    }

    private static HealthCheckRegistry Registry(Type extra) =>
        HealthCheckRegistry.ForTypes([.. new HealthCheckRegistry().All.Select(static x => x.Info.CheckType), extra]);
}

/// <summary>A document type with a numeric id, so Marten's lazy HiLo feature is active.</summary>
public sealed class NumberedHealthThing
{
    public long Id { get; set; }
}

/// <summary>A made-up catalog check that reads <c>pg_class</c> for the store's schema - what a tier-1 check does.</summary>
[HealthCheck("T98", HealthArea.Tables, HealthCostTier.Catalog, "Reads the catalog")]
internal sealed class ReadsTheCatalogCheck : HealthCheck<long>
{
    public override async Task<long> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        await context.Sql.ScalarAsync<long>(
            new HealthQuery(
                "select pg_catalog.count(*) from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = @schema",
                [new NpgsqlParameter("schema", context.StoreOptions.DatabaseSchemaName)]),
            cancellationToken);

    public override HealthVerdict Evaluate(long facts, HealthEvaluation evaluation) => HealthVerdict.Quiet();
}

/// <summary>A made-up check that runs one shared, parameterised query twice.</summary>
[HealthCheck("T97", HealthArea.Tables, HealthCostTier.Catalog, "Runs a cached query twice")]
internal sealed class RunsACachedQueryTwiceCheck : HealthCheck<int>
{
    private static readonly HealthQuery Cached = new("select @n::int", [new NpgsqlParameter("n", 7)]);

    public override async Task<int> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        await context.Sql.ScalarAsync<int>(Cached, cancellationToken) + await context.Sql.ScalarAsync<int>(Cached, cancellationToken);

    public override HealthVerdict Evaluate(int facts, HealthEvaluation evaluation) => HealthVerdict.Quiet();
}

/// <summary>A made-up check that reads a user table - which no real check may do outside tier 2.</summary>
[HealthCheck("T99", HealthArea.Tables, HealthCostTier.Catalog, "Reads a user table")]
internal sealed class ReadsAUserTableCheck : HealthCheck<long>
{
    public override async Task<long> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        await context.Sql.ScalarAsync<long>(
            new HealthQuery("select pg_catalog.count(*) from " + SqlIdentifier.Qualify(context.StoreOptions.DatabaseSchemaName, "probe_target")),
            cancellationToken);

    public override HealthVerdict Evaluate(long facts, HealthEvaluation evaluation) => HealthVerdict.Quiet();
}
