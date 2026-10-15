using System.Reflection;

using MartenStudio.Integration.Tests.Health;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Schema;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>
/// F6 against a real Postgres: the Tables tab's HOT %, dead %, TOAST and storage parameters - drift included on a
/// Marten that can declare them - and the Indexes tab's marks, joined to the advisor's cached findings by key.
/// </summary>
public class SchemaHealthColumnsLiveTests(PostgresFixture postgres)
{
    private static Task<SchemaTables> TablesAsync(HealthLiveHost host) =>
        InScopeAsync(host, services => services.GetRequiredService<ISchemaDataService>().TablesAsync(host.Scope, TestContext.Current.CancellationToken));

    [PostgresFact]
    public async Task The_Tables_tab_reads_HOT_and_dead_shares_TOAST_and_the_tables_own_storage_parameters()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "f6_tables", store: static o => o.Schema.For<HealthScanThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthScanThing>(host.Store);
        await InsertAsync(postgres, table, 12_000);
        await ExecuteAsync(
            postgres,
            "update " + table + " set data = data || jsonb_build_object('Rank', 0)",
            "alter table " + table + " set (autovacuum_enabled = false)");

        SchemaTables tables = await TablesAsync(host);

        tables.Reason.Should().BeNull();
        TableStats stats = tables.Tables.Single(static x => x.Table == "mt_doc_healthscanthing");
        stats.Updates.Should().Be(12_000);
        stats.HotShare.Should().BeLessThan(0.5, "updates to full pages are not HOT");
        stats.DeadShare.Should().BeGreaterThan(0.3, "every update left a dead row and nothing vacuumed");
        stats.ToastBytes.Should().BeGreaterThanOrEqualTo(0);
        stats.StorageParameters.Should().ContainSingle(static x => x.Name == "autovacuum_enabled")
            .Which.Should().Be(new StorageParameterState("autovacuum_enabled", null, "false", StorageParameterStatus.SetOutsideMarten),
                "set by hand and not declared: an apply does not reset it unless the apply rebuilds the table");
        stats.DeclaredParametersKnown.Should().Be(RuntimeFeature.DeclaredStorageParameters.IsAvailable(MartenRuntime.Current));
    }

    /// <summary>
    /// The version half: from Marten 9.47 a mapping declares a storage parameter, and a table that differs from
    /// the declaration is drift. The declaration is made by reflection so this compiles - and says so - at the
    /// floor, where there is nothing to declare and nothing is called drift.
    /// </summary>
    [PostgresFact]
    public async Task A_declared_fillfactor_the_table_differs_from_is_drift_on_a_Marten_that_can_declare_it()
    {
        bool canDeclare = RuntimeFeature.DocumentStorageParameter.IsAvailable(MartenRuntime.Current);

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "f6_drift", store: options =>
        {
            object expression = options.Schema.For<HealthIndexedThing>();

            if (canDeclare)
            {
                expression.GetType()
                    .GetMethod("StorageParameter", BindingFlags.Public | BindingFlags.Instance, [typeof(string), typeof(object)])!
                    .Invoke(expression, ["fillfactor", 80]);
            }
        });
        await ApplyAsync(host.Store);

        string table = TableOf<HealthIndexedThing>(host.Store);
        await ExecuteAsync(postgres, "alter table " + table + " set (fillfactor = 70)");

        SchemaTables tables = await TablesAsync(host);

        StorageParameterState fillfactor = tables.Tables.Single(static x => x.Table == "mt_doc_healthindexedthing")
            .StorageParameters.Single(static x => x.Name == "fillfactor");

        fillfactor.Should().Be(canDeclare
            ? new StorageParameterState("fillfactor", "80", "70", StorageParameterStatus.Drift)
            : new StorageParameterState("fillfactor", null, "70", StorageParameterStatus.SetOutsideMarten));
    }

    /// <summary>
    /// A partitioned table has no storage parameters of its own - Postgres keeps none on the parent, and Weasel
    /// writes and compares a declared one per partition - so the Tables tab compares the declaration with what its
    /// partitions agree on. On a Marten that can declare it, <c>FillFactor(80)</c> on a tenant-partitioned type is a
    /// match, never drift; a partition a DBA changed makes it "partitions differ", naming no partition. At the
    /// floor there is no declaration, and nothing is called drift.
    /// </summary>
    [PostgresFact]
    public async Task A_partitioned_table_s_declared_fillfactor_is_compared_with_what_its_partitions_agree_on()
    {
        bool canDeclare = RuntimeFeature.DocumentStorageParameter.IsAvailable(MartenRuntime.Current);

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "f6_partitioned", store: options =>
        {
            var expression = options.Schema.For<HealthTenantThing>();
            expression.MultiTenantedWithPartitioning(static x =>
            {
                var list = x.ByList();
                list.AddPartition("red", "red");
                list.AddPartition("blue", "blue");
            });

            if (canDeclare)
            {
                expression.GetType()
                    .GetMethod("StorageParameter", BindingFlags.Public | BindingFlags.Instance, [typeof(string), typeof(object)])!
                    .Invoke(expression, ["fillfactor", 80]);
            }
        });
        await ApplyAsync(host.Store);

        TableStats partitioned = (await TablesAsync(host)).Tables.Single(static x => x.Table == "mt_doc_healthtenantthing");

        if (canDeclare)
        {
            partitioned.StorageParameters.Should().ContainSingle(static x => x.Name == "fillfactor")
                .Which.Should().Be(new StorageParameterState("fillfactor", "80", "80", StorageParameterStatus.Matches),
                    "every partition has the declared value, which is all the storage a partitioned table has");
        }
        else
        {
            partitioned.StorageParameters.Should().NotContain(static x => x.Status == StorageParameterStatus.Drift);
        }

        var name = host.Store.Options.FindOrResolveDocumentType(typeof(HealthTenantThing)).TableName;
        await ExecuteAsync(postgres, "alter table " + SqlIdentifier.Qualify(name.Schema, name.Name + "_red") + " set (fillfactor = 70)");

        TableStats differing = (await TablesAsync(host)).Tables.Single(static x => x.Table == "mt_doc_healthtenantthing");
        StorageParameterState fillfactor = differing.StorageParameters.Single(static x => x.Name == "fillfactor");

        fillfactor.Should().Be(new StorageParameterState("fillfactor", canDeclare ? "80" : null, null, StorageParameterStatus.PartitionsDiffer),
            "one partition differs from the others, and the state names the parameter, never the partition");
        differing.StorageParameters.Should().NotContain(static x => x.Name.Contains("red", StringComparison.Ordinal));
    }

    /// <summary>
    /// The Indexes tab's marks come from the advisor's last sweep, by the index's own key: a sweep finds the
    /// invalid index, and the cache-only read hands the same finding back with a subject the tab's row matches.
    /// </summary>
    [PostgresFact]
    public async Task An_invalid_index_on_the_Indexes_tab_is_marked_from_the_cached_T03_finding()
    {
        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(postgres, "f6_marks", store: static o => o.Schema.For<HealthIndexedThing>());
        await ApplyAsync(host.Store);

        string table = TableOf<HealthIndexedThing>(host.Store);
        await InsertAsync(postgres, table, 2, "jsonb_build_object('Name', 'same')");

        try
        {
            await ExecuteAsync(postgres, "create unique index concurrently f6_invalid on " + table + " ((data ->> 'Name'))");
        }
        catch (PostgresException exception) when (exception.SqlState == "23505")
        {
            // The duplicate is the point: the failed build leaves the invalid index behind.
        }

        IReadOnlyList<HealthReportFinding> before = await InScopeAsync(host, services =>
            services.GetRequiredService<IHealthService>().CachedFindingsAsync(host.Scope, SchemaHealthMarks.IndexCodes, TestContext.Current.CancellationToken));
        before.Should().BeEmpty("nothing has swept yet, and the cache-only read never starts a sweep");

        await host.ReportAsync();

        IReadOnlyList<HealthReportFinding> cached = await InScopeAsync(host, services =>
            services.GetRequiredService<IHealthService>().CachedFindingsAsync(host.Scope, SchemaHealthMarks.IndexCodes, TestContext.Current.CancellationToken));

        SchemaIndexes indexes = await InScopeAsync(host, services =>
            services.GetRequiredService<ISchemaDataService>().IndexesAsync(host.Scope, TestContext.Current.CancellationToken));

        IndexInfo row = indexes.Indexes.Single(static x => x.Name == "f6_invalid");
        IReadOnlyDictionary<string, IReadOnlyList<IndexHealthMark>> marks = SchemaHealthMarks.ForIndexes(cached, static _ => "#");

        string key = SchemaKey.For(row.Schema, row.Table, row.Name);
        marks.Should().ContainKey(key);
        marks[key].Should().ContainSingle(static x => x.Code == "T03" && x.Label == "invalid");
    }
}
