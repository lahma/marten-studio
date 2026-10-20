using JasperFx;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Integration.Tests.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

using Npgsql;

using AspNetCoreHealthReport = Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The opt-in health check against the sample domain's store on a real Postgres, probed through ASP.NET Core's
/// own <see cref="HealthCheckService" /> the way a host's health endpoint probes it - with every check the studio
/// ships, not scripted ones.
/// </summary>
/// <remarks>
/// <para>
/// Each test is a <see cref="HealthLiveHost" /> of its own - its own schemas, and a data source of its own, told
/// apart from every other test's pool on the shared server by its <c>Application Name</c> - with the health
/// check added through the host's <c>services</c> hook. The probe is <see cref="HealthCheckService" /> resolved
/// from the host's container; no visitor, no circuit and no policy is involved, because the export has none.
/// </para>
/// <para>
/// The healthy sample store is <see cref="MartenStudio.SampleDomain.SampleStore.Configure" /> as it is, in the
/// Development environment. It starts healthy - extended progression tracking on, advanced async tracking on as
/// Marten 9 ships it - so what it does show are Infos about the server (S02's <c>synchronous_commit</c> on a
/// Testcontainers Postgres) - which is still Healthy.
/// </para>
/// </remarks>
public class HealthExportLiveTests(PostgresFixture postgres)
{
    private const string Name = "marten-studio";

    [PostgresFact]
    public async Task The_healthy_sample_store_reports_Healthy()
    {
        await using HealthLiveHost host = await HostAsync("health_export_quiet", Environments.Development);

        HealthReportEntry entry = await ProbeAsync(host);
        TestContext.Current.TestOutputHelper?.WriteLine(entry.Description ?? "(no description)");

        entry.Status.Should().Be(HealthStatus.Healthy, entry.Description);
        entry.Data["databases"].Should().Be(1);
        ((string[]) entry.Data["critical"]).Should().BeEmpty(entry.Description);
        ((string[]) entry.Data["warning"]).Should().BeEmpty(entry.Description);
        ((string[]) entry.Data["failed"]).Should().BeEmpty(entry.Description);
        entry.Exception.Should().BeNull();
        host.Logs.WarningsOrWorse.Should().BeEmpty("a healthy store probed by a health endpoint is not something to log about");
    }

    /// <summary>
    /// K01 on a real store: AutoCreate.All in Production is a Warning, so the probe reports Degraded and names
    /// the code - and nothing else about the store.
    /// </summary>
    /// <remarks>
    /// The registry is K01 alone. The shared server is a Testcontainers one, started with <c>fsync</c> and
    /// <c>full_page_writes</c> off, which S02 rightly calls Critical in Production (H10) - so with every check the
    /// probe would be Unhealthy, and this test is about how a Warning reaches the probe, not about that server.
    /// </remarks>
    [PostgresFact]
    public async Task A_store_that_migrates_on_first_use_in_Production_reports_Degraded_with_K01()
    {
        await using HealthLiveHost host = await HostAsync(
            "health_export_k01",
            Environments.Production,
            static options => options.AutoCreateSchemaObjects = AutoCreate.All,
            static services => services.AddSingleton(global::PostgresStudio.Services.Health.HealthCheckRegistry.ForTypes(
                [typeof(global::MartenStudio.Services.Health.Checks.AutoCreateInProductionCheck)])));

        HealthReportEntry entry = await ProbeAsync(host);

        entry.Status.Should().Be(HealthStatus.Degraded, entry.Description);
        ((string[]) entry.Data["warning"]).Should().Contain("K01");
        entry.Description.Should().Contain("K01").And.NotContain("health_export_k01");
    }

    /// <summary>
    /// A probe is a guest: on a store built to migrate the moment anything lets it (CreateOrUpdate, two empty
    /// schemas, a numeric id that wakes Marten's HiLo feature), a probe's enumeration and sweep leave the
    /// database exactly as they found it.
    /// </summary>
    [PostgresFact]
    public async Task A_probe_creates_nothing_in_the_database()
    {
        const string schema = "health_export_noddl";

        await using HealthLiveHost host = await HostAsync(
            schema,
            Environments.Development,
            static options =>
            {
                options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;
                options.Schema.For<NumberedHealthThing>();
            });

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events");

        HealthReportEntry entry = await ProbeAsync(host);

        entry.Data["databases"].Should().Be(1, entry.Description);

        IReadOnlySet<string> after = await HealthSweepProbe.SnapshotAsync(postgres, schema, schema + "_events");
        after.Should().BeEquivalentTo(before, "a health probe must never run DDL - not even Marten's HiLo bookkeeping");
    }

    /// <summary>
    /// H8-fix B1: on a master-table tenancy that has resolved one tenant and never been enumerated, a probe checks
    /// that one database and makes the tenancy do nothing else - no apply of its lookup table, no seed upsert, no
    /// read of the master table that would load its other tenants.
    /// </summary>
    /// <remarks>
    /// The two tenant rows point at two databases of their own, so an enumeration would be seen as a second
    /// database rather than folded into the first by identity. The tail is the anti-vacuity half: the same
    /// store's own <c>AllDatabases()</c> does load the other tenant and does upsert the seed, so the probe's
    /// silence is a measurement, not a blind spot.
    /// </remarks>
    [PostgresFact]
    public async Task A_master_table_tenancy_is_checked_from_what_it_holds_and_never_enumerated()
    {
        const string master = "health_export_mt_master";
        string table = SqlIdentifier.Qualify(master, "mt_tenant_databases");
        string tenantA = await TenancyDatabases.CreateAsync(postgres, "health_export_mt_a", Token);
        string tenantB = await TenancyDatabases.CreateAsync(postgres, "health_export_mt_b", Token);

        try
        {
            await postgres.CreateSchemaAsync(master, Token);

            // The lookup table and its seed row, as an earlier run of the application left them; then two tenants.
            await using (DocumentStore earlier = DocumentStore.For(options => MasterTable(options, master)))
            {
                _ = await earlier.Storage.AllDatabases();
            }

            await TenancyDatabases.ExecuteAsync(
                postgres,
                $"insert into {table} (tenant_id, connection_string) values ('tenant_a', '{tenantA}'), ('tenant_b', '{tenantB}')",
                Token);

            await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
                postgres,
                "health_export_mt",
                Environments.Development,
                store: options => MasterTable(options, master),
                services: static services => services.AddHealthChecks().AddMartenStudio(name: Name));

            // The application resolves one tenant, as its own traffic would: no apply, no seed, one row read.
            ITenancy tenancy = host.Store.Options.Tenancy;
            _ = await tenancy.GetTenantAsync("tenant_a");
            ((ITenantedSource<string>) tenancy).AllActive().Should().ContainSingle("the premise: one tenant held");

            IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, master);
            string seedBefore = await TenancyDatabases.RowVersionAsync(postgres, table, "seeded", Token);

            HealthReportEntry entry = await ProbeAsync(host);

            entry.Data["databases"].Should().Be(1, entry.Description);
            ((ITenantedSource<string>) tenancy).AllActive().Should().ContainSingle(
                "a probe that enumerated would have loaded tenant_b and the seed from the master table");
            (await HealthSweepProbe.SnapshotAsync(postgres, master)).Should().BeEquivalentTo(before, "no apply of the lookup table");
            (await TenancyDatabases.RowVersionAsync(postgres, table, "seeded", Token)).Should().Be(seedBefore, "no seed upsert");

            // Anti-vacuity: the enumeration the probe did not make loads both tenants and the seed, and upserts it.
            _ = await host.Store.Storage.AllDatabases();
            ((ITenantedSource<string>) tenancy).AllActive().Should().HaveCount(3);
            (await TenancyDatabases.RowVersionAsync(postgres, table, "seeded", Token)).Should().NotBe(seedBefore);
        }
        finally
        {
            await TenancyDatabases.DropAsync(postgres, "health_export_mt_a", CancellationToken.None);
            await TenancyDatabases.DropAsync(postgres, "health_export_mt_b", CancellationToken.None);
        }
    }

    /// <summary>
    /// H8-fix2: a sharded pool id the store's <c>TenantIdStyle</c> would correct - <c>Shard_A</c> under
    /// <c>ForceLowerCase</c> - would miss the tenancy's cache and read its master table, so it is not looked up: the
    /// probe checks the other database, and counts that one as skipped.
    /// </summary>
    [PostgresFact]
    public async Task A_sharded_pool_id_the_tenant_style_would_correct_is_counted_and_never_looked_up()
    {
        const string master = "health_export_sh_master";
        string shardA = await TenancyDatabases.CreateAsync(postgres, "health_export_sh_a", Token);
        string shardB = await TenancyDatabases.CreateAsync(postgres, "health_export_sh_b", Token);

        try
        {
            await postgres.CreateSchemaAsync(master, Token);

            await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
                postgres,
                "health_export_sh",
                Environments.Development,
                store: options =>
                {
                    options.MultiTenantedWithShardedDatabases(sharding =>
                    {
                        sharding.ConnectionString = postgres.ConnectionString;
                        sharding.SchemaName = master;
                        sharding.PartitionSchemaName = "health_export_sh_parts";
                        sharding.AddDatabase("Shard_A", shardA);
                        sharding.AddDatabase("shard_b", shardB);
                    });
                    options.TenantIdStyle = TenantIdStyle.ForceLowerCase;
                },
                services: static services => services.AddHealthChecks().AddMartenStudio(name: Name));

            // The application's own enumeration, as its daemon or its startup would make it: the pool, loaded.
            _ = await host.Store.Storage.AllDatabases();
            ((ITenantedSource<string>) host.Store.Options.Tenancy).AllActive()
                .Should().BeEquivalentTo(["Shard_A", "shard_b"], "the premise: both pool databases held");

            HealthReportEntry entry = await ProbeAsync(host);

            entry.Data["databases"].Should().Be(1, entry.Description);
            entry.Data["databasesSkipped"].Should().Be(1, "Shard_A is corrected to shard_a before the cache is asked, and would miss");
            entry.Description.Should().Contain("1 held database skipped");
        }
        finally
        {
            await TenancyDatabases.DropAsync(postgres, "health_export_sh_a", CancellationToken.None);
            await TenancyDatabases.DropAsync(postgres, "health_export_sh_b", CancellationToken.None);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A master-table tenancy over the container's database, with one seed tenant.</summary>
    private void MasterTable(StoreOptions options, string schema) =>
        options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
        {
            tenancy.ConnectionString = postgres.ConnectionString;
            tenancy.SchemaName = schema;
            tenancy.RegisterDatabase("seeded", postgres.ConnectionString);
        });

    private static async Task<HealthReportEntry> ProbeAsync(HealthLiveHost host)
    {
        AspNetCoreHealthReport report = await host.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(TestContext.Current.CancellationToken);

        return report.Entries[Name];
    }

    /// <summary>A host of the test's own, with a data source of its own and the export opted into.</summary>
    private Task<HealthLiveHost> HostAsync(
        string schema,
        string environment,
        Action<StoreOptions>? store = null,
        Action<IServiceCollection>? services = null)
    {
        // A pool that is this test's alone: two pools with one connection string are one pool to Postgres, and
        // a connections check would be counting another test's sessions as this store's.
        string connectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            ApplicationName = "marten-studio-" + schema,
        }.ConnectionString;

        return HealthLiveHost.CreateAsync(
            postgres,
            schema,
            environment,
            store: options =>
            {
                options.Connection(connectionString);
                store?.Invoke(options);
            },
            services: collection =>
            {
                collection.AddHealthChecks().AddMartenStudio(name: Name, tags: ["marten"]);

                // After AddMartenStudio's own registration, so a test's narrower registry is the one resolved.
                services?.Invoke(collection);
            });
    }
}
