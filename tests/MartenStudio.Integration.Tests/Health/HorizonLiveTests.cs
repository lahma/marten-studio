using System.Globalization;

using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services.Configuration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Testcontainers.PostgreSql;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// A Postgres server of this class's own, for the conditions the shared one cannot be put into: an
/// <c>autovacuum_freeze_max_age</c> of 100,000 and an <c>autovacuum_multixact_freeze_max_age</c> of 10,000 (both
/// Postgres' minimums), so a test can carry a database past the forced-vacuum line in seconds; prepared transactions
/// switched on; and <c>wal_level = logical</c>, for a real logical replication slot. Never reused: its settings are this
/// fixture's alone.
/// </summary>
public sealed class HorizonServerFixture : IAsyncLifetime
{
    /// <summary><c>autovacuum_freeze_max_age</c>.</summary>
    public const long FreezeMaxAge = 100_000;

    /// <summary><c>autovacuum_multixact_freeze_max_age</c>.</summary>
    public const long MultixactFreezeMaxAge = 10_000;

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "autovacuum_freeze_max_age=" + FreezeMaxAge.ToString(CultureInfo.InvariantCulture))
        .WithCommand("-c", "autovacuum_multixact_freeze_max_age=" + MultixactFreezeMaxAge.ToString(CultureInfo.InvariantCulture))
        .WithCommand("-c", "max_prepared_transactions=4")
        .WithCommand("-c", "wal_level=logical")
        .WithReuse(false)
        .Build();

    private bool started;

    /// <summary>The server's superuser connection string.</summary>
    public string ConnectionString => started
        ? container.GetConnectionString()
        : throw new InvalidOperationException("The horizon server is not running; a test that needs it must be a [PostgresFact].");

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await container.StartAsync();
        started = true;
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();
}

/// <summary>What the horizon live tests share: spending transaction ids and multixacts fast, and holding a snapshot.</summary>
internal static class HorizonLive
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Spends <paramref name="count" /> transaction ids, one committed transaction each, in one round trip: a <c>DO</c> block
    /// that commits in its loop, which runs about 250,000 a second - alone on its connection, because a statement that
    /// commits cannot share an implicit transaction with another.
    /// </summary>
    public static async Task SpendTransactionIdsAsync(string connectionString, int count)
    {
        await using NpgsqlConnection connection = new(Unpooled(connectionString));
        await connection.OpenAsync(Token);

        await using NpgsqlCommand spend = new(
            "do $$ begin for i in 1.." + count.ToString(CultureInfo.InvariantCulture)
            + " loop perform pg_catalog.pg_current_xact_id(); commit; end loop; end $$",
            connection) { CommandTimeout = 300 };
        await spend.ExecuteNonQueryAsync(Token);
    }

    /// <summary>A session that holds a repeatable-read snapshot - and so the horizon - until it is disposed.</summary>
    public static async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> HoldSnapshotAsync(string connectionString, string application)
    {
        NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(Unpooled(connectionString)) { ApplicationName = application }.ConnectionString);
        await connection.OpenAsync(Token);

        NpgsqlTransaction transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, Token);
        await using NpgsqlCommand read = new("select 1", connection, transaction);
        await read.ExecuteScalarAsync(Token);

        return (connection, transaction);
    }

    /// <summary>
    /// <paramref name="count" /> multixacts on one row: while another session's open transaction share-locks it,
    /// each statement here share-locks it too, in a transaction of its own - an error barrier ends each one - and every one
    /// makes a new multixact of the open transaction and itself.
    /// </summary>
    public static async Task MakeMultixactsAsync(string connectionString, string table, int count)
    {
        await using NpgsqlConnection connection = new(Unpooled(connectionString));
        await connection.OpenAsync(Token);

        for (int done = 0; done < count; done += 1_000)
        {
            await using NpgsqlBatch batch = new(connection) { EnableErrorBarriers = true };

            for (int i = 0; i < Math.Min(1_000, count - done); i++)
            {
                batch.BatchCommands.Add(new NpgsqlBatchCommand("select 1 from " + table + " where id = 1 for share"));
            }

            await batch.ExecuteNonQueryAsync(Token);
        }
    }

    /// <summary>The database's next multixact id, as T13 samples it.</summary>
    public static async Task<long> MultixactCounterAsync(string connectionString) =>
        await LiveWait.ScalarAsync<long>(
            connectionString,
            "select (d.datminmxid::text::bigint + pg_catalog.mxid_age(d.datminmxid)) % 4294967296 from pg_catalog.pg_database d where d.datname = pg_catalog.current_database()");

    /// <summary>
    /// Runs statements on one connection of the test's own, one command each, reading nothing back - one command each
    /// because PREPARE TRANSACTION needs a real transaction block, which a batch's implicit one is not (H3's finding).
    /// </summary>
    public static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using NpgsqlConnection connection = new(Unpooled(connectionString));
        await connection.OpenAsync(Token);

        foreach (string sql in statements)
        {
            await using NpgsqlCommand command = new(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync(Token);
        }
    }

    public static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    /// <summary>The Marten this run resolved, for the measurements the HotCold tests write to their output.</summary>
    public static Version Marten => MartenRuntime.Current.VersionOf(RuntimePackage.Marten) ?? throw new InvalidOperationException("No Marten version.");

    /// <summary>
    /// The leadership lock in a transaction: Marten's default at 9.39 and 9.40, and from 9.41 the setting a host behind a
    /// transaction-mode pooler keeps - <c>EventGraph.cs:290</c> at V9.50.2, read into Weasel's
    /// <c>AdvisoryLockOptions.TransactionalLockEnabled</c> - so the leader's own transaction is measured at every Marten.
    /// </summary>
    public static void TransactionScoped(StoreOptions options) => options.Events.UseAdvisoryLockTransaction = true;

    /// <summary>
    /// A host whose HotCold daemon leads <paramref name="database" />, started and waited on until its leader holds one of the
    /// store's daemon locks; the caller stops it.
    /// </summary>
    public static async Task<(IHost Daemon, long[] LockIds)> LeadAsync(string database, Action<StoreOptions>? configure = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services
            .AddMarten(options =>
            {
                SampleStore.Configure(options, database);
                configure?.Invoke(options);
            })
            .AddAsyncDaemon(DaemonMode.HotCold);

        IHost daemon = builder.Build();
        IDocumentStore store = daemon.Services.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await daemon.StartAsync(Token);

        long[] ids = [.. MartenStudio.Services.MartenSessionLockLabeler.For(store, "default", null).DaemonLockIds];

        // Held to this database: pg_locks is the server's, and every store built from the sample's configuration computes
        // the same lock ids, so on the shared server another class's HotCold leader would otherwise answer for this one.
        await LiveWait.UntilAsync(
            async () => await LiveWait.ScalarAsync<bool>(
                database,
                "select exists (select 1 from pg_catalog.pg_locks l where l.locktype = 'advisory' and l.granted"
                + " and l.database = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database())"
                + " and ((l.classid::bigint << 32) | l.objid::bigint) = any(@ids))",
                ("ids", ids)),
            TimeSpan.FromSeconds(60),
            "the HotCold leader to hold one of Marten's daemon locks");

        return (daemon, ids);
    }

    /// <summary>The finding of one code, or nothing.</summary>
    public static HealthReportFinding? FindingOf(HealthReport report, string code) =>
        report.Findings.SingleOrDefault(x => string.Equals(x.Code, code, StringComparison.Ordinal));

    /// <summary>One evidence line's value, as this visitor is shown it.</summary>
    public static string EvidenceOf(HealthReportFinding finding, string label) =>
        finding.Evidence.Single(x => string.Equals(x.Label, label, StringComparison.Ordinal)).Value;
}

/// <summary>
/// T12's forecast, T13's and T14's holders against conditions only a server of this class's own can be put into
/// (<see cref="HorizonServerFixture" />): a database carried past its forced-vacuum line by a held snapshot and real
/// transaction ids, a prepared transaction, a real logical replication slot, multixacts pinned by an open member, and a
/// role without <c>pg_read_all_stats</c> - each read through the studio's own services.
/// </summary>
public class HorizonServerLiveTests(HorizonServerFixture server) : IClassFixture<HorizonServerFixture>
{
    private const string HolderApplication = "MartenStudio.Integration.Tests.horizon-holder";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Everything(MartenStudioOptions options) => options.Capabilities = MartenStudioCapabilities.All();

    /// <summary>
    /// Acceptance 1, T12's forecast on a stepped clock with real transaction-id consumption: a session holds a snapshot, so
    /// vacuum cannot freeze past it; 150,000 real transaction ids carry the database past the server's
    /// <c>autovacuum_freeze_max_age</c> of 100,000, and 600,000 more over four sweeps 31 minutes apart by the studio's clock
    /// give the rate. T12 waits while the age has stayed past the line for less than 29 min 30 s of samples - half what the
    /// history holds at the default minute's interval (follow-up 2, fix 4) - then speaks, worded at the rate of the last 31
    /// minutes - and T14 names the session holding the horizon, behind the activity gate.
    /// </summary>
    [PostgresFact]
    [HealthFires("T12")]
    [HealthFires("T14")]
    public async Task A_held_snapshot_and_real_transaction_ids_fire_T12s_forecast_and_T14_names_the_session()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_forecast");

        ManualClock clock = new(Start);
        await using ConnectionsLiveHost open = await ConnectionsLiveHost.CreateAsync(database, studio: Everything, clock: clock);
        await using ConnectionsLiveHost closed = await ConnectionsLiveHost.CreateAsync(database, clock: clock);

        (NpgsqlConnection holder, NpgsqlTransaction held) = await HorizonLive.HoldSnapshotAsync(database, HolderApplication);

        try
        {
            // Past the forced line before the first sample, so the 31 minutes below are all past it.
            await HorizonLive.SpendTransactionIdsAsync(database, 150_000);

            HealthReport first = await open.ReportAsync();

            HorizonLive.FindingOf(first, "T12").Should().BeNull("one sample is not a rate");
            ConnectionsLiveHost.Status(first, "T12").Note.Should().Contain("A forecast needs two samples");

            HealthReport? second = null;

            foreach (int minutes in new[] { 10, 10, 11 })
            {
                await HorizonLive.SpendTransactionIdsAsync(database, 200_000);
                clock.Advance(TimeSpan.FromMinutes(minutes));

                second = await open.ReportAsync();

                if (minutes == 10)
                {
                    HorizonLive.FindingOf(second, "T12").Should().BeNull("a forecast alone waits for 29 min 30 s of samples past the line");
                    ConnectionsLiveHost.Status(second, "T12").Note.Should().Contain("once it has been for 29 min 30 s");
                }
            }

            HealthReportFinding forecast = HorizonLive.FindingOf(second!, "T12")
                ?? throw new InvalidOperationException("No T12 finding: " + ConnectionsLiveHost.Status(second!, "T12").Note);

            forecast.Severity.Should().Be(HealthSeverity.Warning, "about 77 days to the refusal line at 600,000 every 31 minutes");
            forecast.Finding.Title.Should().StartWith("At the rate of the last 31 minutes, Postgres would stop accepting writes in about");
            HorizonLive.EvidenceOf(forecast, "Writes refused in").Should().Contain("at the rate of the last 31 minutes");
            HorizonLive.EvidenceOf(forecast, "autovacuum_freeze_max_age").Should().Be("100,000");

            HealthReportFinding horizon = HorizonLive.FindingOf(second!, "T14")
                ?? throw new InvalidOperationException("No T14 finding: " + ConnectionsLiveHost.Status(second!, "T14").Note);

            horizon.Severity.Should().Be(HealthSeverity.Warning, "the session is further back than autovacuum_freeze_max_age: the forced freeze cannot get past it");
            HorizonLive.EvidenceOf(horizon, "Held back by").Should().Be("a session's snapshot");
            long behind = long.Parse(HorizonLive.EvidenceOf(horizon, "Transactions behind"), NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            behind.Should().BeGreaterThanOrEqualTo(750_000);
            HorizonLive.EvidenceOf(horizon, "Who it is").Should().StartWith("pid " + holder.ProcessID.ToString(CultureInfo.InvariantCulture))
                .And.Contain("'" + HolderApplication + "'");

            // A visitor without ViewDatabaseActivity, through a host of its own: its first sweep, the same session.
            HealthReportFinding withheld = HorizonLive.FindingOf(await closed.ReportAsync(), "T14")
                ?? throw new InvalidOperationException("The closed host saw no T14 finding.");

            HorizonLive.EvidenceOf(withheld, "Who it is").Should().StartWith("withheld - ").And.NotContain(holder.ProcessID.ToString(CultureInfo.InvariantCulture));
            HorizonLive.EvidenceOf(withheld, "Held back by").Should().Be("a session's snapshot", "the kind and the age are the database's");
            withheld.Finding.Title.Should().NotContain(HolderApplication);

            open.Logs.WarningsOrWorse.Should().BeEmpty();
        }
        finally
        {
            await held.RollbackAsync(CancellationToken.None);
            await holder.DisposeAsync();
        }
    }

    /// <summary>Acceptance 1, T14 for a prepared transaction: its gid and owner behind the gate, its age judged from <c>prepared</c>.</summary>
    [PostgresFact]
    [HealthFires("T14")]
    public async Task A_prepared_transaction_is_a_T14_holder_named_by_gid_and_owner()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_prepared");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Everything);

        const string Gid = "h9-orphan";

        await HorizonLive.ExecuteAsync(
            database,
            "create table h9_prepared_rows (id int)",
            "begin",
            "insert into h9_prepared_rows values (1)",
            "prepare transaction '" + Gid + "'");

        try
        {
            HorizonHeldCheck check = new();
            HorizonFacts facts = await host.GatherAsync(check);

            HorizonHolder prepared = facts.Holders.Should().ContainSingle(static x => x.Kind == HealthHorizonHolderKind.Prepared).Subject;
            prepared.Name.Should().Be(Gid);
            prepared.User.Should().Be("postgres");
            prepared.AtLeast.Should().NotBeNull().And.BeLessThan(TimeSpan.FromMinutes(1));

            check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt)).Findings.Should().BeEmpty("seconds old is not an hour");

            // The same read, judged two hours on: had nothing changed, it would have held the horizon for two hours.
            HealthFinding finding = check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt.AddHours(2))).Findings.Should().ContainSingle().Subject;

            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.Title.Should().StartWith("A prepared transaction has held back vacuum for at least 2 h");
            finding.Evidence.Single(static x => x.Label == "Who it is").Should().Be(
                new HealthEvidence("Who it is", "gid '" + Gid + "', owner postgres", HealthGate.Activity));
        }
        finally
        {
            await HorizonLive.ExecuteAsync(database, "rollback prepared '" + Gid + "'");
        }
    }

    /// <summary>
    /// Blocking 1, live. A transaction-scoped HotCold leader takes a fresh snapshot about once a minute - in its lock
    /// monitor's <c>pg_sleep(60)</c> at Weasel 9.33 (Marten 9.39), in DistributedLock's keepalive at Weasel 9.45 (Marten
    /// 9.50.2, measured by CL3) - on each of its sessions, one per lock; and a snapshot's <c>xmin</c> is the oldest
    /// transaction running anywhere - so while a prepared
    /// transaction stands, the leader's minute-old snapshot is pinned at that transaction's id, exactly as far back. Judged two
    /// hours on, both are over an hour old: T14 names the prepared transaction and says the leader is behind it. Anti-vacuity:
    /// the same read without the prepared transaction names the leader. The leader's lock is transaction-scoped here at every
    /// Marten (<see cref="HorizonLive.TransactionScoped" />): from 9.41 that is a setting rather than the default, and the
    /// case is the one a host keeps behind a pooler.
    /// </summary>
    [PostgresFact]
    [HealthFires("T14")]
    public async Task A_HotCold_leader_pinned_at_a_prepared_transactions_id_is_never_the_holder_named()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_pinned_leader");
        (IHost daemon, long[] ids) = await HorizonLive.LeadAsync(database, HorizonLive.TransactionScoped);

        const string Gid = "h9_probe";
        bool prepared = false;

        try
        {
            await using ConnectionsLiveHost studio = await ConnectionsLiveHost.CreateAsync(database, studio: Everything);

            await HorizonLive.ExecuteAsync(database, "begin", "select pg_catalog.pg_current_xact_id()", "prepare transaction '" + Gid + "'");
            prepared = true;

            long xid = await LiveWait.ScalarAsync<long>(
                database, "select p.transaction::text::bigint from pg_catalog.pg_prepared_xacts p where p.gid = @gid", ("gid", Gid));

            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select count(*) > 0 and coalesce(bool_and(a.backend_xid is null and a.backend_xmin::text::bigint = @xid), false)"
                    + " from pg_catalog.pg_stat_activity a join pg_catalog.pg_locks l on l.pid = a.pid"
                    + " where a.datname = pg_catalog.current_database()"
                    + " and l.locktype = 'advisory' and l.granted and ((l.classid::bigint << 32) | l.objid::bigint) = any(@ids)",
                    ("ids", ids),
                    ("xid", xid)),
                TimeSpan.FromSeconds(90),
                "every leader session's next snapshot - its lock monitor's sleep at Weasel 9.33, DistributedLock's keepalive at 9.45 - to be pinned at the prepared transaction's id");

            HorizonHeldCheck check = new();
            HorizonFacts? pinned = null;

            // The leader's monitor may be between snapshots at the moment of a read: read until both are seen as far back.
            await LiveWait.UntilAsync(
                async () =>
                {
                    HorizonFacts facts = await studio.GatherAsync(check);
                    HorizonHolder? transaction = facts.Holders.FirstOrDefault(static x => x.Kind == HealthHorizonHolderKind.Prepared);
                    HorizonHolder[] leaders = [.. facts.Holders.Where(static x => x.HoldsDaemonLock)];
                    pinned = transaction is not null && leaders.Length > 0 && leaders.All(x => x.XidAge == transaction.XidAge) ? facts : null;
                    return pinned is not null;
                },
                TimeSpan.FromSeconds(90),
                "a read that sees every session of the leader's and the prepared transaction equally far back");

            HorizonHolder holder = pinned!.Holders.Single(static x => x.Kind == HealthHorizonHolderKind.Prepared);
            HorizonHolder leader = pinned.Holders.First(x => x.HoldsDaemonLock && x.XidAge == holder.XidAge);

            leader.OwnXidAge.Should().BeNull("the leader has written nothing: its horizon is a snapshot of the prepared transaction's id");
            holder.OwnXidAge.Should().Be(holder.XidAge, "a prepared transaction owns the id it holds");
            HorizonHeldCheck.RootsOf(pinned.Holders)[leader].Should().BeSameAs(holder);

            HealthFinding finding = check.Evaluate(pinned, HealthEvaluation.At(pinned.ReadAt.AddHours(2))).Findings.Should().ContainSingle().Subject;

            finding.Evidence.Should().Contain(new HealthEvidence("Held back by", "a prepared transaction"));
            finding.Evidence.Single(static x => x.Label == "Who it is").Value.Should().Be("gid '" + Gid + "', owner postgres");
            finding.Evidence.Single(static x => x.Label == "Behind it, at the same point").Value.Should().StartWith("the store's own HotCold daemon leader");
            finding.Title.Should().StartWith("A prepared transaction has held back vacuum for at least 2 h");

            // Anti-vacuity: without the prepared transaction the same leader, two hours on, is the holder named.
            HorizonFacts alone = pinned with { Holders = [.. pinned.Holders.Where(static x => x.Kind != HealthHorizonHolderKind.Prepared)] };
            check.Evaluate(alone, HealthEvaluation.At(alone.ReadAt.AddHours(2))).Findings.Should().ContainSingle()
                .Which.Evidence.Should().Contain(new HealthEvidence("Held back by", "the store's own HotCold daemon leader"));
        }
        finally
        {
            if (prepared)
            {
                await HorizonLive.ExecuteAsync(database, "rollback prepared '" + Gid + "'");
            }

            await daemon.StopAsync(CancellationToken.None);
            daemon.Dispose();
        }
    }

    /// <summary>
    /// Acceptance 1, T14's slot case with a real logical slot (<c>wal_level = logical</c> on this class's server): a slot
    /// nobody consumes keeps its <c>catalog_xmin</c>, so 150,000 real transaction ids carry it past the forced-vacuum line
    /// and it is a catalog holder, named only behind the gate. A physical slot with no standby holds no horizon at all.
    /// </summary>
    [PostgresFact]
    [HealthFires("T14")]
    public async Task A_logical_slot_nobody_consumes_holds_the_catalogs_and_a_physical_slot_with_no_standby_holds_nothing()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_slots");
        await using ConnectionsLiveHost open = await ConnectionsLiveHost.CreateAsync(database, studio: Everything);
        await using ConnectionsLiveHost closed = await ConnectionsLiveHost.CreateAsync(database);

        await LiveWait.ScalarAsync<string>(database, "select slot_name::text from pg_catalog.pg_create_logical_replication_slot('h9_logical', 'pgoutput')");
        await LiveWait.ScalarAsync<string>(database, "select slot_name::text from pg_catalog.pg_create_physical_replication_slot('h9_physical', true)");

        try
        {
            await HorizonLive.SpendTransactionIdsAsync(database, 150_000);

            HorizonFacts facts = await open.GatherAsync(new HorizonHeldCheck());

            facts.Holders.Should().Contain(static x => x.Kind == HealthHorizonHolderKind.SlotCatalogXmin && x.Name == "h9_logical");
            facts.Holders.Should().NotContain(static x => x.Name == "h9_physical", "a physical slot holds an xmin only for a standby's feedback");

            HealthReport report = await open.ReportAsync();
            HealthReportFinding finding = HorizonLive.FindingOf(report, "T14")
                ?? throw new InvalidOperationException("No T14 finding: " + ConnectionsLiveHost.Status(report, "T14").Note);

            finding.Severity.Should().Be(HealthSeverity.Warning);
            HorizonLive.EvidenceOf(finding, "Held back by").Should().Be("a logical replication slot's hold on the system catalogs");
            HorizonLive.EvidenceOf(finding, "Who it is").Should().Be("slot 'h9_logical', plugin pgoutput, database h9_slots, inactive");
            finding.Finding.Title.Should().NotContain("h9_logical");

            HealthReportFinding withheld = HorizonLive.FindingOf(await closed.ReportAsync(), "T14")
                ?? throw new InvalidOperationException("The closed host saw no T14 finding.");
            HorizonLive.EvidenceOf(withheld, "Who it is").Should().StartWith("withheld - ").And.NotContain("h9_logical");
        }
        finally
        {
            await HorizonLive.ExecuteAsync(database,
                "select pg_catalog.pg_drop_replication_slot(s.slot_name) from pg_catalog.pg_replication_slots s where s.slot_name in ('h9_logical', 'h9_physical')");
        }
    }

    /// <summary>
    /// A replication slot read is the server's: a logical slot of <em>another</em> database - another tenant's, on a
    /// database-per-tenant store - still holds back the shared catalogs, so its age counts here, but its name, plugin and
    /// database are named at no gate, not even to a visitor with every capability.
    /// </summary>
    [PostgresFact]
    [HealthFires("T14")]
    public async Task A_logical_slot_of_another_database_counts_here_and_is_named_nowhere()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_slot_here");
        string elsewhere = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_slot_tenant_b");
        await using ConnectionsLiveHost open = await ConnectionsLiveHost.CreateAsync(database, studio: Everything);

        await HorizonLive.ExecuteAsync(elsewhere, "select pg_catalog.pg_create_logical_replication_slot('h9_tenant_b_cdc', 'pgoutput')");

        try
        {
            await HorizonLive.SpendTransactionIdsAsync(database, 150_000);

            HorizonFacts facts = await open.GatherAsync(new HorizonHeldCheck());
            HorizonHolder foreign = facts.Holders.Should().ContainSingle(static x => x.Kind == HealthHorizonHolderKind.SlotCatalogXmin).Subject;
            foreign.OtherDatabase.Should().BeTrue();
            foreign.Name.Should().BeNull("not even the facts keep another database's slot's name");

            HealthReport report = await open.ReportAsync();
            HealthReportFinding finding = HorizonLive.FindingOf(report, "T14")
                ?? throw new InvalidOperationException("No T14 finding: " + ConnectionsLiveHost.Status(report, "T14").Note);

            finding.Severity.Should().Be(HealthSeverity.Warning, "its hold on the shared catalogs stops this database's forced freeze too");
            HorizonLive.EvidenceOf(finding, "Held back by").Should().Be("a logical slot of another database on this server (its hold on the system catalogs)");
            finding.Evidence.Should().NotContain(static x => x.Label == "Who it is", "a visitor with every capability is not told whose it is");

            string shown = string.Join("\n", finding.Evidence.Select(static x => x.Value)) + "\n" + finding.Finding.Title + "\n"
                + finding.Finding.WhyItMatters + "\n" + finding.Finding.Caveat + "\n" + ConnectionsLiveHost.Status(report, "T14").Note;
            shown.Should().NotContain("h9_tenant_b_cdc").And.NotContain("h9_slot_tenant_b").And.NotContain("pgoutput");
        }
        finally
        {
            await HorizonLive.ExecuteAsync(elsewhere, "select pg_catalog.pg_drop_replication_slot('h9_tenant_b_cdc')");
        }
    }

    /// <summary>
    /// Acceptance 2's live half: with a <c>SqlConsoleRole</c> that is not a member of <c>pg_read_all_stats</c>, another
    /// role's session is still found - Postgres shows it the transaction ids - and named only behind the gate, while the
    /// caveat says how many sessions the role cannot see the start of. (The database-wide gate's two ways are the unit
    /// test's, <c>HorizonFindingGatesTests</c>: a live host passes it for everybody.)
    /// </summary>
    [PostgresFact]
    public async Task A_narrowed_role_still_finds_another_roles_session_and_says_what_it_cannot_see()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_narrowed");

        await HorizonLive.ExecuteAsync(
            server.ConnectionString,
            "do $$ begin"
            + " if not exists (select from pg_roles where rolname = 'h9_narrow') then create role h9_narrow nologin; end if;"
            + " if not exists (select from pg_roles where rolname = 'h9_other') then create role h9_other login password 'h9_other'; end if;"
            + " end $$");

        string asOther = new NpgsqlConnectionStringBuilder(database) { Username = "h9_other", Password = "h9_other" }.ConnectionString;
        (NpgsqlConnection other, NpgsqlTransaction held) = await HorizonLive.HoldSnapshotAsync(asOther, HolderApplication);

        static void Narrowed(MartenStudioOptions options) => options.SqlConsoleRole = "h9_narrow";

        await using ConnectionsLiveHost open = await ConnectionsLiveHost.CreateAsync(
            HorizonLive.Unpooled(database),
            studio: static options =>
            {
                Narrowed(options);
                Everything(options);
            });
        await using ConnectionsLiveHost closed = await ConnectionsLiveHost.CreateAsync(HorizonLive.Unpooled(database), studio: Narrowed);

        try
        {
            await HorizonLive.SpendTransactionIdsAsync(database, 150_000);

            HealthReport report = await open.ReportAsync();
            HealthReportFinding finding = HorizonLive.FindingOf(report, "T14")
                ?? throw new InvalidOperationException("No T14 finding: " + ConnectionsLiveHost.Status(report, "T14").Note);

            HorizonLive.EvidenceOf(finding, "Held back by").Should().Be("a session of another role (its state is not readable)");
            HorizonLive.EvidenceOf(finding, "Who it is").Should().StartWith("pid " + other.ProcessID.ToString(CultureInfo.InvariantCulture))
                .And.Contain("role h9_other")
                .And.Contain("application '" + HolderApplication + "'", "Postgres shows every role a session's application_name (measured on 17)")
                .And.Contain("from (not readable)", "and withholds its client address, with its state and timings");
            finding.Finding.Caveat.Should().MatchRegex(@"cannot see when \d+ sessions? of other roles in this database began their transactions");

            HealthReportFinding withheld = HorizonLive.FindingOf(await closed.ReportAsync(), "T14")
                ?? throw new InvalidOperationException("The closed host saw no T14 finding.");

            HorizonLive.EvidenceOf(withheld, "Who it is").Should().StartWith("withheld - ").And.NotContain("h9_other");
            withheld.Finding.Caveat.Should().MatchRegex(@"cannot see when \d+ sessions? of other roles", "the caveat is the database's, shown with the finding");
        }
        finally
        {
            await held.RollbackAsync(CancellationToken.None);
            await other.DisposeAsync();
        }
    }

    /// <summary>
    /// Acceptance 1, T13 for real: an open transaction that is a member of a multixact pins the oldest one, and 12,000 real
    /// multixacts on its row carry the database past this server's <c>autovacuum_multixact_freeze_max_age</c> of 10,000.
    /// 600,000 more over four sweeps 31 minutes apart by the studio's clock give the rate, and once the age has stayed past
    /// the line for 29 min 30 s of samples - the default minute's half-ring - the forecast fires.
    /// </summary>
    [PostgresFact]
    [HealthFires("T13")]
    public async Task An_open_member_and_real_multixacts_fire_T13s_forecast()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_multixacts");

        ManualClock clock = new(Start);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, clock: clock);

        await HorizonLive.ExecuteAsync(database, "create table h9_locked (id int primary key)", "insert into h9_locked values (1)");

        await using NpgsqlConnection member = new(HorizonLive.Unpooled(database));
        await member.OpenAsync(Token);
        await using NpgsqlTransaction open = await member.BeginTransactionAsync(Token);
        await using (NpgsqlCommand share = new("select 1 from h9_locked where id = 1 for share", member, open))
        {
            await share.ExecuteScalarAsync(Token);
        }

        try
        {
            await HorizonLive.MakeMultixactsAsync(database, "h9_locked", 12_000);
            HealthReport first = await host.ReportAsync();
            HorizonLive.FindingOf(first, "T13").Should().BeNull("one sample is not a rate");

            HealthReport? last = null;

            foreach (int minutes in new[] { 10, 10, 11 })
            {
                long before = await HorizonLive.MultixactCounterAsync(database);
                await HorizonLive.MakeMultixactsAsync(database, "h9_locked", 200_000);
                (await HorizonLive.MultixactCounterAsync(database) - before).Should().BeGreaterThanOrEqualTo(200_000, "each locking statement made one");

                clock.Advance(TimeSpan.FromMinutes(minutes));
                last = await host.ReportAsync();
            }

            HealthReportFinding finding = HorizonLive.FindingOf(last!, "T13")
                ?? throw new InvalidOperationException("No T13 finding: " + ConnectionsLiveHost.Status(last!, "T13").Note);

            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.Finding.Title.Should().StartWith("At the rate of the last 31 minutes, Postgres would stop creating multixacts in about");
            HorizonLive.EvidenceOf(finding, "autovacuum_multixact_freeze_max_age").Should().Be("10,000");
            HorizonLive.EvidenceOf(finding, "Multixacts created").Should().Contain("at the rate of the last 31 minutes");
        }
        finally
        {
            await open.RollbackAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// B1: a lazy VACUUM shows a <c>backend_xmin</c>, but Postgres marks it <c>PROC_IN_VACUUM</c> and leaves it out of the
    /// horizon - so a DBA's own VACUUM, throttled to run for minutes on a large table, is never named as what holds vacuum
    /// back. Anti-vacuity: the VACUUM is seen in <c>pg_stat_progress_vacuum</c> with a <c>backend_xmin</c> while it is read.
    /// </summary>
    [PostgresFact]
    public async Task A_throttled_manual_vacuum_shows_an_xmin_and_is_not_a_holder()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_vacuum");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Everything);

        await HorizonLive.ExecuteAsync(
            database,
            "create table h9_big (id int, pad text)",
            "insert into h9_big select g, repeat('x', 200) from generate_series(1, 300000) g");

        await using NpgsqlConnection vacuuming = new(HorizonLive.Unpooled(database));
        await vacuuming.OpenAsync(Token);
        int vacuumPid = vacuuming.ProcessID;

        await using (NpgsqlCommand slow = new("set vacuum_cost_delay = 100; set vacuum_cost_limit = 1", vacuuming))
        {
            await slow.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlCommand vacuum = new("vacuum h9_big", vacuuming) { CommandTimeout = 0 };
        Task running = vacuum.ExecuteNonQueryAsync(Token);

        try
        {
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select exists (select 1 from pg_stat_progress_vacuum v join pg_stat_activity a on a.pid = v.pid"
                    + " where v.pid = @pid and a.backend_xmin is not null)",
                    ("pid", vacuumPid)),
                TimeSpan.FromSeconds(30),
                "the throttled VACUUM to be running with a backend_xmin");

            HorizonFacts facts = await host.GatherAsync(new HorizonHeldCheck());

            facts.Holders.Should().NotContain(x => x.ProcessId == vacuumPid, "a lazy VACUUM is PROC_IN_VACUUM and holds nothing back");
            HorizonLive.FindingOf(await host.ReportAsync(), "T14").Should().BeNull();
        }
        finally
        {
            await LiveWait.ScalarAsync<bool>(database, "select pg_cancel_backend(@pid)", ("pid", vacuumPid));

            try
            {
                await running;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
            {
            }
        }
    }

    /// <summary>
    /// B2: a <c>DO</c> that commits in its loop is one statement across many short transactions, so its statement is older
    /// than its transaction - and the lower bound is the transaction's, never the statement's. Anti-vacuity: the statement has
    /// run for seconds when it is read, and the bound is under one.
    /// </summary>
    [PostgresFact]
    public async Task A_DO_that_commits_in_a_loop_is_bounded_by_its_transaction_not_its_statement()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_loop");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database, studio: Everything);

        await using NpgsqlConnection looping = new(HorizonLive.Unpooled(database));
        await looping.OpenAsync(Token);
        int loopPid = looping.ProcessID;

        await using NpgsqlCommand loop = new(
            "do $$ begin for i in 1..600 loop perform pg_catalog.pg_current_xact_id(); perform pg_catalog.pg_sleep(0.05); commit; end loop; end $$",
            looping) { CommandTimeout = 0 };
        Task running = loop.ExecuteNonQueryAsync(Token);

        try
        {
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select exists (select 1 from pg_stat_activity a where a.pid = @pid and a.state = 'active'"
                    + " and now() - a.query_start > interval '3 seconds')",
                    ("pid", loopPid)),
                TimeSpan.FromSeconds(30),
                "the committing DO to have run for three seconds");

            HorizonHolder? seen = null;

            await LiveWait.UntilAsync(
                async () =>
                {
                    HorizonFacts facts = await host.GatherAsync(new HorizonHeldCheck());
                    seen = facts.Holders.FirstOrDefault(x => x.ProcessId == loopPid);
                    return seen is not null;
                },
                TimeSpan.FromSeconds(20),
                "the committing DO to be read with a transaction id");

            seen!.Bound.Should().Be("transaction", "its statement is older than its transaction");
            seen.AtLeast.Should().NotBeNull().And.BeLessThan(TimeSpan.FromSeconds(1),
                "each of its transactions lives fifty milliseconds, whatever the statement's age");
            seen.AtLeast.Should().BeLessThanOrEqualTo(seen.AtMost!.Value);
        }
        finally
        {
            await LiveWait.ScalarAsync<bool>(database, "select pg_cancel_backend(@pid)", ("pid", loopPid));

            try
            {
                await running;
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
            {
            }
        }
    }

    /// <summary>
    /// T13's Marten angle, measured - and the packet's claim dropped because of it. An append inserts into <c>mt_events</c>
    /// under a foreign key to <c>mt_streams</c>, so two appends sharing the stream's row would make a multixact; but
    /// concurrent Quick appends to one stream serialise on <c>pk_mt_events_stream_and_version</c> - Quick append reads the
    /// stream's version without a lock - and every loser fails there, before the statement's foreign-key check ever runs.
    /// Eight writers make no multixact, and neither do the same appends one at a time. On this class's own server, because
    /// the multixact counter is the server's and the shared one has other classes writing beside it.
    /// </summary>
    [PostgresFact]
    public async Task Concurrent_appends_to_one_stream_serialise_on_the_version_key_and_make_no_multixacts()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(server.ConnectionString, "h9_hot_stream");
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(database);

        Guid stream = Guid.NewGuid();
        await using (IDocumentSession session = host.Store.LightweightSession())
        {
            session.Events.StartStream(stream, new OrderPlaced(stream, "acme", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        long before = await HorizonLive.MultixactCounterAsync(database);

        for (int i = 0; i < 100; i++)
        {
            await using IDocumentSession session = host.Store.LightweightSession();
            session.Events.Append(stream, new OrderPlaced(stream, "serial", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        long serial = await HorizonLive.MultixactCounterAsync(database) - before;

        int failed = 0;
        int landed = 0;
        long start = await HorizonLive.MultixactCounterAsync(database);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async writer =>
        {
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    await using IDocumentSession session = host.Store.LightweightSession();
                    session.Events.Append(stream, new OrderPlaced(stream, "writer-" + writer.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow));
                    await session.SaveChangesAsync(Token);
                    Interlocked.Increment(ref landed);
                }
                catch (JasperFx.Events.EventStreamUnexpectedMaxEventIdException)
                {
                    // The one failure this measures: the race for the stream's next version, lost on its key.
                    Interlocked.Increment(ref failed);
                }
            }
        }));

        long concurrent = await HorizonLive.MultixactCounterAsync(database) - start;

        TestContext.Current.TestOutputHelper?.WriteLine(
            "serial: " + serial + " multixacts; concurrent: " + concurrent + " multixacts, " + landed + " appends landed and " + failed
            + " lost the race");

        serial.Should().Be(0, "an append on its own locks the stream row in one transaction and makes no multixact");
        failed.Should().BePositive("concurrent appends to one stream race for its next version, and the losers fail on the key");
        landed.Should().BePositive("and the winners land: a test in which every append failed would measure nothing");
        (landed + failed).Should().Be(320, "every append either landed or lost the race - nothing else was caught");
        concurrent.Should().Be(0, "a loser fails on pk_mt_events_stream_and_version before its foreign-key check shares the stream's row");
    }
}

/// <summary>
/// T12, T13 and T14 on the shared server: the healthy sample store is quiet, the HotCold leader at the defaults is never
/// old enough alone to raise, and a sweep takes no lock beyond the catalog's and creates nothing.
/// </summary>
public class HorizonLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] Codes = ["T12", "T13", "T14"];

    [PostgresFact]
    [HealthQuiet("T12")]
    [HealthQuiet("T13")]
    [HealthQuiet("T14")]
    public async Task The_seeded_sample_store_is_quiet_on_wraparound_and_the_horizon()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "h9_quiet");

        ManualClock clock = new(Start);
        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database, studio: static options => options.Capabilities = MartenStudioCapabilities.All(), clock: clock);

        await new SampleDataSeeder().Populate(host.Store, Token);

        HealthReport first = await host.ReportAsync();
        first.Findings.Should().NotContain(x => Codes.Contains(x.Code), string.Join(" | ", first.Findings.Select(static x => x.Finding.Title)));

        await HorizonLive.SpendTransactionIdsAsync(database, 5_000);
        clock.Advance(TimeSpan.FromMinutes(2));

        HealthReport second = await host.ReportAsync();
        second.Findings.Should().NotContain(x => Codes.Contains(x.Code), string.Join(" | ", second.Findings.Select(static x => x.Finding.Title)));

        foreach (string code in Codes)
        {
            HealthCheckStatus status = ConnectionsLiveHost.Status(second, code);
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} is quiet because it looked, not because it did not run: {1}", code, status.Problem);
            status.Statements.Should().NotBeEmpty("{0} read the catalog", code);
        }

        ConnectionsLiveHost.Status(second, "T12").Note.Should().Contain("autovacuum forces a freeze in").And.Contain("at the rate of the last 2 minutes");
        host.Logs.WarningsOrWorse.Should().BeEmpty("an expected state is never a warning");
    }

    /// <summary>
    /// The HotCold leader, with its lock monitor on (the default) and at read committed, is never old enough alone to raise -
    /// however long it leads, with its leadership lock in a transaction or on the session (and
    /// <see cref="HorizonServerLiveTests" /> shows it is never named for another holder's transaction). Each scope is set,
    /// at every Marten: <c>Events.UseAdvisoryLockTransaction</c> is on by default at 9.39 and 9.40 and off from 9.41, and a
    /// setting at both ends. In a transaction, the leader holds its leadership in a transaction it never ends, and its lock
    /// monitor (<c>Events.UseMonitoredAdvisoryLock</c>) runs <c>pg_sleep(60)</c> in that transaction - measured: the
    /// transaction grows older by the minute while the snapshot it holds the horizon with is never more than a minute old,
    /// which is the bound T14's remedy states. So T14 reads it as a holder whose transaction is older than its horizon, and
    /// judges it by the lower bound. On the session, no transaction outlives a statement: the lock monitor's statement still
    /// holds a snapshot while it runs, for at most about a minute at a time, so a daemon-lock holder may be read, and is never
    /// old.
    /// </summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    [HealthQuiet("T14")]
    public async Task The_HotCold_leaders_open_transaction_is_never_raised_however_long_it_leads(bool transactionScopedLock)
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(
            postgres.ConnectionString, "h9_hotcold_" + (transactionScopedLock ? "xact" : "session"));

        (IHost daemon, _) = await HorizonLive.LeadAsync(database, options => options.Events.UseAdvisoryLockTransaction = transactionScopedLock);

        try
        {
            await using ConnectionsLiveHost studio = await ConnectionsLiveHost.CreateAsync(database);
            HorizonHeldCheck check = new();

            if (transactionScopedLock)
            {
                // Wait until the leader's transaction is older than its monitor's sleep: then its transaction's age and its
                // horizon's have parted.
                HorizonFacts? seen = null;

                await LiveWait.UntilAsync(
                    async () =>
                    {
                        HorizonFacts facts = await studio.GatherAsync(check);
                        seen = facts.Holders.Any(static x => x.HoldsDaemonLock && x.AtMost > TimeSpan.FromSeconds(75)) ? facts : null;
                        return seen is not null;
                    },
                    TimeSpan.FromSeconds(150),
                    "the HotCold leader's transaction to be older than its lock monitor's minute");

                HorizonHolder leader = seen!.Holders.First(static x => x.HoldsDaemonLock && x.AtMost > TimeSpan.FromSeconds(75));
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Marten {HorizonLive.Marten}: the leader's transaction is {leader.AtMost?.TotalSeconds:0.0} s old and the snapshot it holds the horizon with {leader.AtLeast?.TotalSeconds:0.0} s.");
                leader.AtLeast.Should().NotBeNull().And.BeLessThan(TimeSpan.FromSeconds(65),
                    "its snapshot is its lock monitor's latest statement's - pg_sleep(60) at Weasel 9.33, DistributedLock's keepalive at 9.45 - taken afresh about every minute");
                HorizonHeldCheck.KindText(leader).Should().Be("the store's own HotCold daemon leader");
                check.Evaluate(seen, HealthEvaluation.At(seen.ReadAt)).Findings.Should().BeEmpty("a transaction's age is only the most the horizon can be held");
            }
            else
            {
                HorizonFacts facts = await studio.GatherAsync(check);

                // A session-scoped leadership lock leaves no transaction open - but the lock monitor's statement holds a
                // snapshot while it runs, so a read may find it, and never older than a minute.
                facts.Holders.Where(static x => x.HoldsDaemonLock).Should().AllSatisfy(static x =>
                {
                    x.AtLeast.Should().NotBeNull().And.BeLessThan(TimeSpan.FromSeconds(65), "a statement's snapshot, at most about a minute old");
                    x.AtMost.Should().NotBeNull().And.BeLessThan(TimeSpan.FromSeconds(65), "and no transaction outlives the statement");
                });
                check.Evaluate(facts, HealthEvaluation.At(facts.ReadAt)).Findings.Should().BeEmpty();
            }

            ConnectionsLiveHost.Status(await studio.ReportAsync(), "T14").Outcome.Should().Be(HealthCheckOutcome.Checked);
            HorizonLive.FindingOf(await studio.ReportAsync(), "T14").Should().BeNull();
        }
        finally
        {
            await daemon.StopAsync(CancellationToken.None);
            daemon.Dispose();
        }
    }

    /// <summary>
    /// Acceptance 3: a regular sweep's wraparound and horizon reads take no lock beyond the catalog's and create nothing -
    /// with another session's temporary table in the database for the "other tables" count to read. (No snapshot is held
    /// here: this is the shared database, and other classes' vacuum assertions run in it.)
    /// </summary>
    [PostgresFact]
    public async Task The_horizon_reads_lock_nothing_beyond_the_catalog_and_create_nothing()
    {
        HealthSweepProbe probe = new(postgres);
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "h9_no_lock",
            services: collection => collection.AddSingleton<IHealthStatementObserver>(probe));
        await ApplyAsync(host.Store);

        await using NpgsqlConnection temporary = await postgres.OpenAsync(Token);
        await using (NpgsqlCommand create = new("create temporary table h9_scratch (id int); insert into h9_scratch values (1)", temporary))
        {
            await create.ExecuteNonQueryAsync(Token);
        }

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, host.Schema, host.Schema + "_events");

        HealthReport report = await host.ReportAsync();

        foreach (string code in Codes)
        {
            HealthLiveHost.Status(report, code).Outcome.Should().Be(HealthCheckOutcome.Checked, HealthLiveHost.Status(report, code).Problem);
        }

        string[] ours = [HealthHorizonQueries.WraparoundSql, HealthHorizonQueries.HoldersSql, HealthServerQueries.ReplicationSlots().Sql];
        probe.Locks.Select(static x => x.Statement).Distinct(StringComparer.Ordinal).Should().Contain(ours, "every horizon statement ran under the probe");
        probe.BeyondCatalogReads.Where(x => ours.Contains(x.Statement)).Should().BeEmpty();

        (await HealthSweepProbe.SnapshotAsync(postgres, host.Schema, host.Schema + "_events")).Should().BeEquivalentTo(before);
    }
}
