using System.Reflection;

using JasperFx;

using Marten;

using MartenStudio.Services.Configuration;

using IJasperFxEventStore = JasperFx.Events.IEventStore;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Tenancy;

/// <summary>
/// F8-D3's anti-vacuity facts, and the pins under AGENTS.md D27's known-limit sentence: enumerating a dynamic
/// tenancy's databases still writes, at every Marten this repository builds against.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FirstNavigationLiveTests" /> proves a first navigation changes nothing for every shape whose
/// enumeration is a walk of what the store holds, and for a master-table tenancy whose lookup table is already
/// there. This is the other side. Each fact is a write a cold enumeration makes - the lookup table applied, the
/// seed rows upserted, a database created - by each of the three calls that enumerate, so the read-only describe
/// the F8 investigation looked for is not there to light up.
/// </para>
/// <para>
/// <b>A Marten that fixes this fails here, and that failure is the signal.</b> When a release stops writing on
/// enumeration (the read-only <c>BuildDatabases()</c> twin drafted for upstream), the matching fact goes red at
/// that version: the studio can then probe for the new member through <c>RuntimeFeature</c>, use it, and D27's
/// sentence changes with it. Until then these are true at the floor and at 9.50.2 alike.
/// </para>
/// </remarks>
public class EnumerationAppliesLiveTests(PostgresFixture postgres)
{
    private const string LookupTable = "mt_tenant_databases";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task AllDatabases_creates_the_lookup_table_on_a_cold_master_table_store()
    {
        const string schema = "f8d3_apply_all";
        string table = await ColdLookupTableAsync(schema);

        await using (DocumentStore store = DocumentStore.For(options => MasterTable(options, schema)))
        {
            _ = await store.Storage.AllDatabases();
        }

        (await TenancyDatabases.RelationExistsAsync(postgres, table, Token)).Should().BeTrue(
            "AllDatabases() runs Tenancy.BuildDatabases(), whose first call per store instance applies the lookup table (D27)");
    }

    [PostgresFact]
    public async Task DescribeDatabasesAsync_creates_it_too()
    {
        const string schema = "f8d3_apply_describe";
        string table = await ColdLookupTableAsync(schema);

        await using (DocumentStore store = DocumentStore.For(options => MasterTable(options, schema)))
        {
            _ = await store.Options.Tenancy.DescribeDatabasesAsync(Token);
        }

        (await TenancyDatabases.RelationExistsAsync(postgres, table, Token)).Should().BeTrue(
            "MasterTableTenancy.DescribeDatabasesAsync begins with BuildDatabases()");
    }

    /// <summary>
    /// Pinned at both ends, as <c>MartenRuntimeProbeTests</c> pins a probe: absent below the JasperFx.Events
    /// that brings it, and where present, no more of a read than the two calls above.
    /// </summary>
    [PostgresFact]
    public async Task TryDescribeDatabasesAsync_arrives_in_Marten_9_50_1_and_creates_it_too()
    {
        MethodInfo? tryDescribe = typeof(IJasperFxEventStore).GetMethod(
            "TryDescribeDatabasesAsync", BindingFlags.Public | BindingFlags.Instance, [typeof(CancellationToken)]);

        Version? jasperFxEvents = MartenRuntime.Current.VersionOf(RuntimePackage.JasperFxEvents);
        jasperFxEvents.Should().NotBeNull("the studio reads the JasperFx.Events version it runs against");

        if (jasperFxEvents < new Version(2, 87, 1))
        {
            tryDescribe.Should().BeNull(
                "JasperFx.Events 2.87.1 brings it, first in Marten 9.50.1, and this run is on {0}",
                MartenRuntime.Current.Describe(RuntimePackage.Marten));
            return;
        }

        tryDescribe.Should().NotBeNull("JasperFx.Events {0} declares it", jasperFxEvents);

        const string schema = "f8d3_apply_try_describe";
        string table = await ColdLookupTableAsync(schema);

        await using (DocumentStore store = DocumentStore.For(options => MasterTable(options, schema)))
        {
            var described = (Task) tryDescribe!.Invoke(store, [Token])!;
            await described;
        }

        (await TenancyDatabases.RelationExistsAsync(postgres, table, Token)).Should().BeTrue(
            "Marten's TryDescribeDatabasesAsync is Tenancy.DescribeDatabasesAsync, which is BuildDatabases()");
    }

    /// <summary>
    /// "Once per process" is once per store <em>instance</em>, and the upsert is a write even when it changes no
    /// value: <c>on conflict do update</c> always writes a new row version.
    /// </summary>
    [PostgresFact]
    public async Task Every_store_instances_first_enumeration_upserts_the_seed_rows()
    {
        const string schema = "f8d3_apply_seeds";
        string table = await ColdLookupTableAsync(schema);
        void Configure(StoreOptions options) => MasterTable(options, schema, seedTenant: "acme");

        await using (DocumentStore first = DocumentStore.For(Configure))
        {
            _ = await first.Storage.AllDatabases();
        }

        string before = await TenancyDatabases.RowVersionAsync(postgres, table, "acme", Token);

        await using (DocumentStore second = DocumentStore.For(Configure))
        {
            _ = await second.Storage.AllDatabases();
        }

        string after = await TenancyDatabases.RowVersionAsync(postgres, table, "acme", Token);

        after.Should().NotBe(before, "a store instance's first enumeration upserts every seed row, whether or not it changed");
    }

    /// <summary>
    /// The tenancy's own <c>AutoCreate</c> gates the apply. Below Marten 9.51 the store's picks its mode, and Weasel
    /// promotes <c>None</c> to <c>CreateOrUpdate</c>; from 9.51 (#5692) the tenancy's own is the mode too - so at either
    /// end a store that says <c>None</c> still gets the table.
    /// </summary>
    [PostgresFact]
    public async Task Under_a_store_AutoCreate_of_None_the_tenancys_own_AutoCreate_still_applies_it()
    {
        const string schema = "f8d3_apply_none";
        string table = await ColdLookupTableAsync(schema);

        await using (DocumentStore store = DocumentStore.For(options =>
                   {
                       MasterTable(options, schema, tenancyAutoCreate: AutoCreate.CreateOrUpdate);
                       options.AutoCreateSchemaObjects = AutoCreate.None;
                   }))
        {
            _ = await store.Storage.AllDatabases();
        }

        (await TenancyDatabases.RelationExistsAsync(postgres, table, Token)).Should().BeTrue(
            "the tenancy's CreateOrUpdate gates the apply - and below 9.51 the store's None is its mode, applied as "
            + "CreateOrUpdate, while from 9.51 the tenancy's own CreateOrUpdate is");
    }

    [PostgresFact]
    public async Task A_single_server_tenancys_first_enumeration_creates_its_databases_under_no_AutoCreate()
    {
        string name = ServerSnapshot.EnumerationPrefix + "single_server";
        await TenancyDatabases.DropAsync(postgres, name, Token);

        try
        {
            (await TenancyDatabases.ExistsAsync(postgres, name, Token)).Should().BeFalse("the premise");

            await using (DocumentStore store = DocumentStore.For(options =>
                       {
                           options.MultiTenantedWithSingleServer(
                               postgres.ConnectionString,
                               tenancy => tenancy.WithTenants("f8d3-tenant").InDatabaseNamed(name));
                           options.AutoCreateSchemaObjects = AutoCreate.None;
                       }))
            {
                _ = await store.Storage.AllDatabases();
            }

            (await TenancyDatabases.ExistsAsync(postgres, name, Token)).Should().BeTrue(
                "a single-server tenancy's BuildDatabases() runs CREATE DATABASE for every configured database that "
                + "does not exist yet, and no AutoCreate governs it");
        }
        finally
        {
            await TenancyDatabases.DropAsync(postgres, name, CancellationToken.None);
        }
    }

    /// <summary>A fresh schema for a lookup table, and the table's qualified name - not there yet.</summary>
    private async Task<string> ColdLookupTableAsync(string schema)
    {
        await postgres.CreateSchemaAsync(schema, Token);

        string table = SqlIdentifier.Qualify(schema, LookupTable);
        (await TenancyDatabases.RelationExistsAsync(postgres, table, Token)).Should().BeFalse("the premise: a cold store");

        return table;
    }

    /// <summary>
    /// A master-table tenancy whose lookup table lives in <paramref name="schema" />, on the container's own
    /// database. No tenant database is needed: what is measured is the lookup table.
    /// </summary>
    private void MasterTable(
        StoreOptions options,
        string schema,
        string? seedTenant = null,
        AutoCreate? tenancyAutoCreate = null)
    {
        options.MultiTenantedDatabasesWithMasterDatabaseTable(tenancy =>
        {
            tenancy.ConnectionString = postgres.ConnectionString;
            tenancy.SchemaName = schema;

            if (tenancyAutoCreate is { } mode)
            {
                tenancy.AutoCreate = mode;
            }

            if (seedTenant is not null)
            {
                tenancy.RegisterDatabase(seedTenant, postgres.ConnectionString);
            }
        });

        options.DatabaseSchemaName = schema;
    }
}
