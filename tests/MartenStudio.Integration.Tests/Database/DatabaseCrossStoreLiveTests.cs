using JasperFx;

using Marten;

using MartenStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;

using Weasel.Postgresql;
using Weasel.Postgresql.Tables;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// F4 against a real Postgres 17: a second registered store's objects in the same database keep their
/// Marten classification, but its identity is shown only to a visitor that store's store policy passes,
/// and the rows of its Marten-managed tables only to one its store policy and write policy both pass.
/// </summary>
public class DatabaseCrossStoreLiveTests(DatabaseCrossStoreLiveTests.Fixture fixture) : IClassFixture<DatabaseCrossStoreLiveTests.Fixture>
{
    private const string StorePolicy = "store";
    private const string WritePolicy = "write";
    private const string OtherStoreKey = nameof(IBrowserOtherStore);
    private const string ReportTable = "other_report";

    /// <summary>This class's schemas, and a second store's in a schema of its own.</summary>
    public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres)
    {
        /// <summary>The second store's schema.</summary>
        public string OtherSchema => LegacySchema.Replace("_legacy", "_other", StringComparison.Ordinal);

        /// <summary>The second store's document table, as Marten names it.</summary>
        public string InvoiceTable { get; private set; } = string.Empty;

        /// <inheritdoc />
        protected override async Task InitializeExtrasAsync()
        {
            await Postgres.CreateSchemaAsync(OtherSchema);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMartenStore<IBrowserOtherStore>(options => ConfigureOther(options, AutoCreate.All));

            await using ServiceProvider provider = services.BuildServiceProvider();
            IBrowserOtherStore store = provider.GetRequiredService<IBrowserOtherStore>();

            await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            InvoiceTable = store.Options.FindOrResolveDocumentType(typeof(BrowserInvoice)).TableName.Name;
        }

        /// <summary>Registers the second store, as a host with two stores would.</summary>
        public void AddOtherStore(IServiceCollection services) =>
            services.AddMartenStore<IBrowserOtherStore>(options => ConfigureOther(options, AutoCreate.None));

        private void ConfigureOther(StoreOptions options, AutoCreate autoCreate)
        {
            options.Connection(Postgres.ConnectionString);
            options.DatabaseSchemaName = OtherSchema;
            options.AutoCreateSchemaObjects = autoCreate;
            options.Schema.For<BrowserInvoice>();

            var report = new Table(new PostgresqlObjectName(OtherSchema, ReportTable, SchemaUtils.IdentifierUsage.General));
            report.AddColumn<int>("id").AsPrimaryKey();
            options.Storage.ExtendedSchemaObjects.Add(report);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_visitor_the_other_stores_policy_refuses_sees_neither_its_identity_nor_its_rows()
    {
        var policy = new ResourcePolicy(static resource => resource.StoreName == "default");

        await using BrowserHost host = Host(policy);

        DatabaseObjectList tables = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.OtherSchema), Token));

        tables.Refusal.Should().Be(DatabaseRefusal.None, tables.Reason);

        var byName = tables.Items.Cast<DatabaseRelationSummary>().ToDictionary(static x => x.Name);

        DatabaseRelationSummary invoice = byName[fixture.InvoiceTable];
        invoice.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenDocument, "still Marten's, so still never read raw");
        invoice.Ownership.StoreKey.Should().BeNull("a store the policy refuses and one that does not exist are one answer");
        invoice.Ownership.Alias.Should().BeNull();
        invoice.Rows.Refusal.Should().Be(DatabaseRefusal.MartenOwned);

        DatabaseRelationSummary report = byName[ReportTable];
        report.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);
        report.Ownership.StoreKey.Should().BeNull();
        report.Rows.Refusal.Should().Be(DatabaseRefusal.StorePolicy);

        DatabaseRowAccessResult rows = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.OtherSchema, ReportTable, cancellationToken: Token));

        rows.Allowed.Should().BeFalse("this store's tenant-less grant says nothing about the other store's data");
        rows.Refusal.Should().Be(DatabaseRefusal.StorePolicy);

        policy.Calls.Should().Contain(static x => x.Resource.StoreName == OtherStoreKey && x.Resource.TenantId == null,
            "the other store's own policy was asked, for the database as a whole");
    }

    [PostgresFact]
    public async Task A_visitor_the_other_stores_write_policy_refuses_sees_its_identity_and_not_its_rows()
    {
        var policy = new ResourcePolicy(static resource => resource.Capability is null || resource.StoreName == "default");

        await using BrowserHost host = Host(policy);

        DatabaseObjectList tables = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.OtherSchema), Token));

        var byName = tables.Items.Cast<DatabaseRelationSummary>().ToDictionary(static x => x.Name);

        byName[fixture.InvoiceTable].Ownership.StoreKey.Should().Be(OtherStoreKey);
        byName[ReportTable].Ownership.StoreKey.Should().Be(OtherStoreKey);
        byName[ReportTable].Rows.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
    }

    [PostgresFact]
    public async Task A_visitor_both_stores_pass_sees_the_other_stores_identity_and_rows()
    {
        var policy = new ResourcePolicy(static _ => true);

        await using BrowserHost host = Host(policy);

        DatabaseObjectList tables = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.OtherSchema), Token));

        var byName = tables.Items.Cast<DatabaseRelationSummary>().ToDictionary(static x => x.Name);

        byName[fixture.InvoiceTable].Ownership.StoreKey.Should().Be(OtherStoreKey);
        byName[fixture.InvoiceTable].Ownership.Alias.Should().NotBeNullOrEmpty();
        byName[ReportTable].Rows.Allowed.Should().BeTrue(byName[ReportTable].Rows.Reason);

        DatabaseRowAccessResult rows = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.OtherSchema, ReportTable, cancellationToken: Token));

        rows.Allowed.Should().BeTrue(rows.Reason);
    }

    private BrowserHost Host(ResourcePolicy policy) =>
        fixture.Host(
            options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                options.BrowsableSchemas.Add(fixture.OtherSchema);
            },
            policy,
            services: fixture.AddOtherStore);
}

/// <summary>A second store, registered beside the default one.</summary>
public interface IBrowserOtherStore : IDocumentStore;

/// <summary>The second store's document type.</summary>
public class BrowserInvoice
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }
}
