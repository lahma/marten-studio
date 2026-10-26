using System.Globalization;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// What a moment in Postgres looks like to the table checks, against a real server: a concurrent index build
/// still running, and a partitioned table whose parameters live only on its partitions.
/// </summary>
public class TableHealthBuildLiveTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The review's case, produced for real: a session's open snapshot holds a <c>CREATE INDEX CONCURRENTLY</c> in
    /// "waiting for old snapshots", where the index is <c>indisready</c> and not yet <c>indisvalid</c> - exactly
    /// what a failed build leaves. Re-applying then would queue a <c>DROP INDEX</c> for an
    /// <c>AccessExclusiveLock</c> behind the build, so the sweep must not call it Critical: it notes the build and
    /// judges nothing until it ends.
    /// </summary>
    [PostgresFact]
    public async Task An_index_a_concurrent_build_is_still_working_on_is_no_critical_T03_only_a_note()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t03_building", store: static o => o.Schema.For<HealthIndexedThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthIndexedThing>(host.Store);
        await InsertAsync(postgres, table, 1_000);

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using NpgsqlConnection builder = await postgres.OpenAsync(Token);
        await using NpgsqlConnection watcher = await postgres.OpenAsync(Token);

        int holderPid = await ScalarAsync<int>(holder, "select pg_catalog.pg_backend_pid()");

        // The snapshot the build will wait for: a statement that runs until it is cancelled below.
        await using NpgsqlCommand sleep = new("select pg_catalog.pg_sleep(120)", holder) { CommandTimeout = 0 };
        Task sleeping = sleep.ExecuteNonQueryAsync(Token);

        await WaitForAsync(
            async () => await ScalarAsync<bool>(watcher,
                "select exists (select 1 from pg_catalog.pg_stat_activity where pid = " + holderPid.ToString(CultureInfo.InvariantCulture)
                + " and state = 'active' and query like 'select pg_catalog.pg_sleep%')"),
            "the holding session's sleep to start");

        await using NpgsqlCommand create = new("create index concurrently t03_building on " + table + " ((data ->> 'Name'))", builder) { CommandTimeout = 0 };
        Task building = create.ExecuteNonQueryAsync(Token);

        try
        {
            await WaitForAsync(
                async () => await ScalarAsync<bool>(watcher,
                    "select exists (select 1 from pg_catalog.pg_stat_progress_create_index p join pg_catalog.pg_index i on i.indexrelid = p.index_relid "
                    + "where p.relid = '" + table.Replace("'", "''", StringComparison.Ordinal) + "'::regclass and p.phase = 'waiting for old snapshots' "
                    + "and i.indisready and not i.indisvalid)"),
                "the build to reach 'waiting for old snapshots' with its index ready and invalid");

            HealthReport report = await ReportAsync(host);

            report.Findings.Should().NotContain(static x => x.Code == "T03",
                "an index a build is still working on is not a failed build, and re-applying mid-build is the harm");

            HealthCheckStatus status = HealthLiveHost.Status(report, "T03");
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
            status.Note.Should().Contain("Being built right now").And.Contain("t03_building (waiting for old snapshots)");
        }
        finally
        {
            await ExecuteOnAsync(watcher, "select pg_catalog.pg_cancel_backend(" + holderPid.ToString(CultureInfo.InvariantCulture) + ")");

            Func<Task> cancelled = () => sleeping;
            (await cancelled.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("57014", "the sleep was cancelled");
        }

        await building;

        // And once the build has finished, the index is valid and there is still nothing to say.
        (await ScalarAsync<bool>(watcher, "select indisvalid from pg_catalog.pg_index where indexrelid = '" + SqlIdentifier.Qualify(
            host.Store.Options.FindOrResolveDocumentType(typeof(HealthIndexedThing)).TableName.Schema, "t03_building") + "'::regclass"))
            .Should().BeTrue();
    }

    /// <summary>
    /// The H4 re-review's probe, kept: an older failed index (<c>old_failed</c>) stays invalid while a
    /// <c>REINDEX INDEX CONCURRENTLY</c> of another index on the same table runs - and a build of another index is
    /// no reason this one is invalid, so it must stay a finding rather than drop out as "being built" for as long
    /// as a nightly rolling reindex runs. The reindex's own <c>_ccnew</c> copy is invalid too, and is the build's:
    /// in its first phase (held here by a writer) the progress view reports the index it copies, not the copy,
    /// so the copy is matched by name; later (held by an old snapshot) it reports the copy itself.
    /// </summary>
    [PostgresTheory]
    [InlineData("writer", "waiting for writers before build")]
    [InlineData("snapshot", "waiting for old snapshots")]
    public async Task An_older_failed_index_stays_a_T03_while_a_REINDEX_CONCURRENTLY_of_another_index_runs(string holding, string phase)
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "health_t03_reindex_" + holding, store: static o => o.Schema.For<HealthIndexedThing>());
        await ApplyAsync(host.Store);

        string schema = host.Store.Options.FindOrResolveDocumentType(typeof(HealthIndexedThing)).TableName.Schema;
        string table = TableOf<HealthIndexedThing>(host.Store);
        await InsertAsync(postgres, table, 1_000);
        await InsertAsync(postgres, table, 2, "jsonb_build_object('Name', 'same', 'Rank', 0)");

        Func<Task> failing = () => ExecuteAsync(postgres, "create unique index concurrently old_failed on " + table + " ((data ->> 'Name'))");
        (await failing.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505", "two documents share the name");
        await ExecuteAsync(postgres, "create index by_y on " + table + " ((data ->> 'Rank'))");

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using NpgsqlConnection builder = await postgres.OpenAsync(Token);
        await using NpgsqlConnection watcher = await postgres.OpenAsync(Token);

        int holderPid = await ScalarAsync<int>(holder, "select pg_catalog.pg_backend_pid()");
        await using NpgsqlTransaction? writing = holding == "writer" ? await holder.BeginTransactionAsync(Token) : null;

        if (writing is not null)
        {
            // A writer the reindex must wait for before it builds: a row inserted in a transaction still open.
            await using NpgsqlCommand insert = new("insert into " + table + " (id, data) values (gen_random_uuid(), '{}'::jsonb)", holder, writing);
            await insert.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlCommand sleep = new("select pg_catalog.pg_sleep(120)", holder, writing) { CommandTimeout = 0 };
        Task sleeping = sleep.ExecuteNonQueryAsync(Token);

        await WaitForAsync(
            async () => await ScalarAsync<bool>(watcher,
                "select exists (select 1 from pg_catalog.pg_stat_activity where pid = " + holderPid.ToString(CultureInfo.InvariantCulture)
                + " and state = 'active' and query like 'select pg_catalog.pg_sleep%')"),
            "the holding session's sleep to start");

        await using NpgsqlCommand reindex = new("reindex index concurrently " + SqlIdentifier.Qualify(schema, "by_y"), builder) { CommandTimeout = 0 };
        Task reindexing = reindex.ExecuteNonQueryAsync(Token);

        try
        {
            await WaitForAsync(
                async () => await ScalarAsync<bool>(watcher,
                    "select exists (select 1 from pg_catalog.pg_stat_progress_create_index p "
                    + "where p.relid = '" + table.Replace("'", "''", StringComparison.Ordinal) + "'::regclass "
                    + "and p.command = 'REINDEX CONCURRENTLY' and p.phase = '" + phase + "') "
                    + "and exists (select 1 from pg_catalog.pg_class c where c.relname = 'by_y_ccnew')"),
                "the reindex to reach '" + phase + "' with its _ccnew copy in the catalog");

            HealthReport report = await ReportAsync(host);
            string key = KeyOf<HealthIndexedThing>(host.Store);

            HealthReportFinding finding = Of(report, "T03").Should().ContainSingle(
                "old_failed is a finding, and the reindex's copy is not: {0}", string.Join(" | ", Of(report, "T03").Select(static x => x.Finding.Subject))).Subject;
            finding.Finding.Subject.Should().Be(key + ".old_failed");
            finding.Severity.Should().Be(HealthSeverity.Critical, "every build is in view, and none of them is old_failed's");
            finding.Finding.Caveat.Should().Contain("A build of another index on this table is running right now (" + phase + ")");

            HealthCheckStatus status = HealthLiveHost.Status(report, "T03");
            status.Note.Should().Contain("by_y_ccnew (" + phase + ")", "the copy is the build's, and only a note");
        }
        finally
        {
            await ExecuteOnAsync(watcher, "select pg_catalog.pg_cancel_backend(" + holderPid.ToString(CultureInfo.InvariantCulture) + ")");

            Func<Task> cancelled = () => sleeping;
            (await cancelled.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("57014", "the sleep was cancelled");

            if (writing is not null)
            {
                await writing.RollbackAsync(Token);
            }
        }

        await reindexing;

        (await ScalarAsync<bool>(watcher, "select indisvalid from pg_catalog.pg_index where indexrelid = '"
            + SqlIdentifier.Qualify(schema, "old_failed") + "'::regclass")).Should().BeFalse("nothing repaired the older failure meanwhile");
    }

    /// <summary>
    /// Postgres keeps no storage parameters on a partitioned table itself, so a DBA's
    /// <c>autovacuum_enabled = false</c> on every partition is invisible on the parent - and T07 and T08 would
    /// wait ten minutes for an autovacuum that is not coming. The leaves' rollup is what the table checks read.
    /// </summary>
    [PostgresFact]
    public async Task Autovacuum_switched_off_on_every_partition_is_switched_off_for_the_partitioned_table()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            "health_partition_options",
            store: static o => o.Schema.For<HealthTenantThing>().MultiTenantedWithPartitioning(static x =>
            {
                var list = x.ByList();
                list.AddPartition("red", "red");
                list.AddPartition("blue", "blue");
            }));
        await ApplyAsync(host.Store);

        IReadOnlyList<string> leaves = await LeavesAsync(TableOf<HealthTenantThing>(host.Store));
        leaves.Should().HaveCountGreaterThanOrEqualTo(2, "the store really is tenant-partitioned");

        await ExecuteAsync(postgres, "alter table " + leaves[0] + " set (autovacuum_enabled = false)");

        TableFacts partly = await PartitionedTableAsync(host);
        partly.AutovacuumOffForTable.Should().BeFalse("one partition of several is not the table");
        partly.Row.DifferingStorageOptions.Should().ContainSingle("the partitions disagree, and that is said by the parameter's name alone")
            .Which.Should().Be("autovacuum_enabled");
        Autovacuum.State(partly, "autovacuum_vacuum_scale_factor", "0.02", PostgresStudio.Services.Settings.StudioText.Marten)
            .Should().Contain("may be switched off for some");

        await ExecuteAsync(postgres, [.. leaves.Select(static x => "alter table " + x + " set (autovacuum_enabled = false)")]);

        TableFacts off = await PartitionedTableAsync(host);
        off.AutovacuumOffForTable.Should().BeTrue("every partition has it off, which is the partitioned table's whole storage");
        off.Row.DifferingStorageOptions.Should().BeEmpty();
        off.Row.PartitionLeaves.Should().Be(leaves.Count);
        Autovacuum.State(off, "autovacuum_vacuum_scale_factor", "0.02", PostgresStudio.Services.Settings.StudioText.Marten)
            .Should().Contain("on every partition");
    }

    /// <summary>The qualified, quoted names of a partitioned table's partitions - the test's own read, never the studio's.</summary>
    private async Task<IReadOnlyList<string>> LeavesAsync(string table)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select pg_catalog.quote_ident(n.nspname) || '.' || pg_catalog.quote_ident(c.relname) from pg_catalog.pg_inherits i "
            + "join pg_catalog.pg_class c on c.oid = i.inhrelid join pg_catalog.pg_namespace n on n.oid = c.relnamespace "
            + "where i.inhparent = @table::regclass order by c.relname",
            connection);
        command.Parameters.AddWithValue("table", table);

        List<string> leaves = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            leaves.Add(reader.GetString(0));
        }

        return leaves;
    }

    private static async Task<TableFacts> PartitionedTableAsync(HealthLiveHost host)
    {
        await using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        await using HealthConnection connection = new(MartenStudioDatabase.Of(host.Store.Storage.Database));

        HealthCheckContext context = new(
            new ResolvedScope(
                host.Scope,
                new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)),
                host.Store,
                host.Store.Storage.Database),
            MartenStudioSettings.Of(new MartenStudioOptions()),
            MartenStudioRuntime.Current,
            services,
            new HealthSqlRunner(connection, new ReadOnlySqlOptions(), null),
            DateTimeOffset.UtcNow,
            new HealthSweepMemo(),
            Token);

        IReadOnlyList<TableFacts> tables = await MartenTables.TablesAsync(context);
        return tables.Single(static x => x.Table.Table == "mt_doc_healthtenantthing");
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (T) (await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task ExecuteOnAsync(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Waits until <paramref name="condition" /> holds, or fails naming what it was waiting for - never a fixed delay.</summary>
    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + Deadline;

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        while (true)
        {
            if (await condition())
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Waited {Deadline.TotalSeconds:0} s for {what} and it did not happen.");
            }

            await timer.WaitForNextTickAsync(Token);
        }
    }
}
