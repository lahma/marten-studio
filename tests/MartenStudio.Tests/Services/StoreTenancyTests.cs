using System.Reflection;

using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Tests.Projections;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-1 S4: how a store lays its tenants out, in words, from <c>StoreOptions</c> alone. Every store here is built and
/// never connected to: the classifier reads the tenancy's type and its in-memory cardinality, and the document and event
/// configuration.
/// </summary>
public class StoreTenancyTests
{
    private const string Connection = "Host=scope1-tenancy.invalid;Database=one;Username=u;Password=p";

    private const string SecondConnection = "Host=scope1-tenancy-b.invalid;Database=two;Username=u;Password=p";

    [Fact]
    public async Task A_master_table_tenancy_is_a_database_per_tenant_from_a_master_table()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options => options.MultiTenantedDatabasesWithMasterDatabaseTable(Connection, "tenants"));

        tenancy.Kind.Should().Be(StoreTenancyKind.MasterTable);
        tenancy.Words.Should().Be("A database per tenant, from a master table");
    }

    [Fact]
    public async Task A_sharded_tenancy_is_tenants_pooled_across_databases()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options => options.MultiTenantedWithShardedDatabases(t => t.ConnectionString = Connection));

        tenancy.Kind.Should().Be(StoreTenancyKind.Sharded);
        tenancy.Words.Should().Be("Tenants pooled across databases (sharded)");
    }

    [Fact]
    public async Task A_single_server_tenancy_is_a_database_per_tenant_on_one_server()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options => options.MultiTenantedWithSingleServer(Connection));

        tenancy.Kind.Should().Be(StoreTenancyKind.OneServer);
        tenancy.Words.Should().Be("A database per tenant, on one server");
    }

    [Fact]
    public async Task A_static_tenancy_is_a_database_per_tenant_configured()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options => options.MultiTenantedDatabases(t =>
        {
            t.AddMultipleTenantDatabase(Connection, "tenants-a").ForTenants("acme");
            t.AddMultipleTenantDatabase(SecondConnection, "tenants-b").ForTenants("globex");
        }));

        tenancy.Kind.Should().Be(StoreTenancyKind.Static);
        tenancy.Words.Should().Be("A database per tenant, configured");
    }

    [Fact]
    public async Task One_database_with_nothing_conjoined_has_no_tenants()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options => options.Schema.For<PlainThing>());

        tenancy.Kind.Should().Be(StoreTenancyKind.Single);
        tenancy.Words.Should().Be("One database, no tenants");
    }

    [Fact]
    public async Task One_database_with_a_conjoined_document_type_keeps_tenants_in_shared_tables()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options => options.Schema.For<TenantedThing>().MultiTenanted());

        tenancy.Kind.Should().Be(StoreTenancyKind.Conjoined);
        tenancy.Words.Should().Be("Tenants in one database, in shared tables");
    }

    [Fact]
    public async Task One_database_with_only_a_conjoined_event_store_keeps_tenants_in_shared_tables()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options => options.Events.TenancyStyle = TenancyStyle.Conjoined);

        tenancy.Kind.Should().Be(StoreTenancyKind.Conjoined);
    }

    [Fact]
    public async Task A_document_type_partitioned_by_tenant_makes_the_store_partitioned()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options =>
            options.Schema.For<TenantedThing>().MultiTenantedWithPartitioning(static x => x.ByExternallyManagedListPartitions()));

        tenancy.Kind.Should().Be(StoreTenancyKind.ConjoinedPartitioned);
        tenancy.Words.Should().Be("Tenants in one database, partitioned by tenant");
    }

    [Fact]
    public async Task Marten_managed_tenant_partitions_make_the_store_partitioned()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options =>
        {
            options.Policies.PartitionMultiTenantedDocumentsUsingMartenManagement("tenants");
            options.Schema.For<TenantedThing>().MultiTenanted();
        });

        tenancy.Kind.Should().Be(StoreTenancyKind.ConjoinedPartitioned);
    }

    [Fact]
    public async Task Tenant_partitioned_events_make_the_store_partitioned()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options =>
        {
            options.Events.AppendMode = JasperFx.Events.EventAppendMode.Quick;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
        });

        tenancy.Kind.Should().Be(StoreTenancyKind.ConjoinedPartitioned);
    }

    [Fact]
    public async Task A_conjoined_document_type_the_host_hides_is_not_counted()
    {
        StoreTenancy hidden = await ClassifyAsync(
            static options => options.Schema.For<TenantedThing>().MultiTenanted(),
            static type => type != typeof(TenantedThing));

        hidden.Kind.Should().Be(StoreTenancyKind.Single, "a hidden type is absent (D27), and the sentence must not give it away");
    }

    [Fact]
    public async Task A_tenancy_of_the_applications_own_says_its_type_and_cardinality()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options =>
        {
            options.MultiTenantedDatabases(t => t.AddMultipleTenantDatabase(Connection, "a").ForTenants("acme"));
            options.Tenancy = (ITenancy) (object) CustomTenancy.Over(options.Tenancy, throwsCardinality: false);
        });

        tenancy.Kind.Should().Be(StoreTenancyKind.Custom);
        tenancy.Words.Should().StartWith("A tenancy of the application's own (").And.EndWith("), StaticMultiple");
    }

    [Fact]
    public async Task A_tenancy_whose_properties_throw_is_custom_and_the_classifier_does_not()
    {
        StoreTenancy tenancy = await ClassifyAsync(static options =>
        {
            options.MultiTenantedDatabases(t => t.AddMultipleTenantDatabase(Connection, "a").ForTenants("acme"));
            options.Tenancy = (ITenancy) (object) CustomTenancy.Over(options.Tenancy, throwsCardinality: true);
        });

        tenancy.Kind.Should().Be(StoreTenancyKind.Custom, "fail closed");
        tenancy.Words.Should().StartWith("A tenancy of the application's own (").And.NotContain("Multiple");
    }

    private static async Task<StoreTenancy> ClassifyAsync(Action<StoreOptions> configure, Func<Type, bool>? visible = null)
    {
        await using DocumentStore store = DocumentStore.For(options =>
        {
            options.DatabaseSchemaName = "scope1";

            // One database unless the test then picks another tenancy, which replaces this one.
            options.Connection(Connection);

            configure(options);
        });

        return StoreTenancy.Classify(store.Options, visible);
    }

    public sealed class PlainThing
    {
        public Guid Id { get; set; }
    }

    public sealed class TenantedThing
    {
        public Guid Id { get; set; }
    }

    /// <summary>A tenancy that is not one of Marten's, forwarding to the one it wraps, whose cardinality can throw.</summary>
    public class CustomTenancy : DispatchProxy
    {
        private ITenancy? inner;
        private bool throwsCardinality;

        internal static CustomTenancy Over(ITenancy tenancy, bool throwsCardinality)
        {
            ITenancy proxy = Create<ITenancy, CustomTenancy>();
            var custom = (CustomTenancy) (object) proxy;
            custom.inner = tenancy;
            custom.throwsCardinality = throwsCardinality;
            return custom;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == "get_Cardinality" && throwsCardinality)
            {
                throw new NotSupportedException("The application's tenancy cannot say.");
            }

            return UnlistableStore.Forward(targetMethod, inner!, args);
        }
    }
}
