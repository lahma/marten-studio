using PostgresStudio.Services;
using PostgresStudio.Services.Database;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// Issue #22 against a real Postgres and a real Marten store: <c>ViewDefinitions</c> shows the definitions - and the structure
/// - of the schemas <c>BrowsableSchemas</c> names exactly, and no row; a schema only <c>"*"</c> admits keeps needing
/// <c>BrowseDatabase</c>; <c>BrowseDatabase</c> implies it; the write policy is asked with <c>ViewDefinitions</c> against the
/// database as a whole, with no tenant; and <c>ReadOnly</c> refuses it with everything else.
/// </summary>
/// <remarks>
/// The legacy schema is the one named exactly: it holds a table, a materialized view over nothing, a trigger and its
/// function, a PL/pgSQL function, a type and sequences. The Quartz schema is the one <c>"*"</c> admits, with a view and a
/// function of its own (<see cref="Fixture.ExtraSql" />). Neither the legacy schema's objects nor those read anything in a
/// schema the visitor may not see.
/// </remarks>
public class ViewDefinitionsLiveTests(ViewDefinitionsLiveTests.Fixture fixture) : IClassFixture<ViewDefinitionsLiveTests.Fixture>
{
    private const string StorePolicy = "store";
    private const string WritePolicy = "write";

    /// <summary>This class's schemas, with a view and a function in the Quartz schema and a Marten-named function in legacy.</summary>
    public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres)
    {
        protected override string ExtraSql =>
            """
            create view {q}.trigger_peek as select trigger_name from {q}.qrtz_triggers;
            create function {q}.next_fire() returns bigint language sql as $$ select 1::bigint $$;
            create function {l}.mt_helper() returns int language sql as $$ select 2 $$;
            """;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private DatabaseObjectRef LegacyView => new(DatabaseObjectKind.MaterializedView, fixture.LegacySchema, "populated_stats");

    private DatabaseObjectRef LegacyFunction => new(DatabaseObjectKind.Function, fixture.LegacySchema, "recalculate", "order_id bigint");

    private DatabaseObjectRef LegacyTrigger => new(DatabaseObjectKind.Trigger, fixture.LegacySchema, "orders_enabled", Table: "orders");

    private DatabaseObjectRef LegacyType => new(DatabaseObjectKind.EnumType, fixture.LegacySchema, "order_status");

    private DatabaseObjectRef QuartzView => new(DatabaseObjectKind.View, fixture.QuartzSchema, "trigger_peek");

    private DatabaseObjectRef QuartzFunction => new(DatabaseObjectKind.Function, fixture.QuartzSchema, "next_fire", string.Empty);

    /// <summary>A studio with <c>ViewDefinitions</c> alone: the legacy schema named exactly, the Quartz one admitted by <c>"*"</c>.</summary>
    private BrowserHost ViewDefinitionsOnly(ResourcePolicy policy, Action<MartenStudioOptions>? more = null) =>
        fixture.Host(
            options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                options.Capabilities.BrowseDatabase = false;
                options.Capabilities.ViewDefinitions = true;
                options.BrowsableSchemas.Clear();
                options.BrowsableSchemas.Add(fixture.LegacySchema);
                options.BrowsableSchemas.Add("*");
                more?.Invoke(options);
            },
            policy);

    [PostgresFact]
    public async Task ViewDefinitions_alone_shows_the_definitions_of_the_exactly_named_schema_and_the_policy_is_asked_with_it()
    {
        var policy = new ResourcePolicy(static _ => true);

        await using BrowserHost host = ViewDefinitionsOnly(policy);

        DatabaseBrowserOverview overview = await host.ObjectsAsync(x => x.GetOverviewAsync(BrowserHost.Scope, Token));
        overview.Refusal.Should().Be(DatabaseRefusal.None, overview.Reason);
        overview.Schemas.Select(static x => x.Name).Should().Contain(fixture.LegacySchema)
            .And.NotContain(fixture.QuartzSchema, "\"*\" admits it, and \"*\" is BrowseDatabase's: it is withheld, and counted");
        overview.Schemas.Where(x => x.Name == fixture.LegacySchema).Should().OnlyContain(static x => !x.RowsBrowsable);
        overview.Access.DefinitionsOpen.Should().BeTrue();
        overview.Access.IsOpen.Should().BeFalse("BrowseDatabase is not on");
        overview.Access.DefinitionSchemas.Should().Equal(fixture.LegacySchema);
        overview.Access.WithheldSchemaCount.Should().BeGreaterThan(0);

        DatabaseObjectDefinition view = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, LegacyView, Token));
        view.Found.Should().BeTrue(view.Reason);

        DatabaseObjectDefinition function = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, LegacyFunction, Token));
        function.Found.Should().BeTrue(function.Reason);
        function.Sql.Should().Contain("recalculate");

        DatabaseObjectDefinition trigger = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, LegacyTrigger, Token));
        trigger.Found.Should().BeTrue(trigger.Reason);
        trigger.Sql.Should().Contain("orders_enabled");

        DatabaseObjectDefinition type = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, LegacyType, Token));
        type.Found.Should().BeTrue(type.Reason);
        type.Type!.Labels.Should().Equal("pending", "shipped", "cancelled");

        // A sequence's current value is data - it says how many rows a table has had - so it is BrowseDatabase's alone.
        DatabaseSequenceValue sequence = await host.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.LegacySchema, "standalone_numbers", Token));
        sequence.Found.Should().BeFalse();
        sequence.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, sequence.Reason);
        sequence.Reason.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        DatabaseObjectList sequences = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Sequences, fixture.LegacySchema), Token));
        sequences.Items.OfType<DatabaseSequenceSummary>().Single(static x => x.Name == "standalone_numbers").CanReadValue.Should().BeFalse(
            "the list says so in advance");

        // The listing says so in advance: a control drawn enabled is one whose call passes, and the rows are refused.
        DatabaseObjectList views = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Views, fixture.LegacySchema), Token));
        views.Refusal.Should().Be(DatabaseRefusal.None, views.Reason);
        views.Items.OfType<DatabaseRelationSummary>().Single(static x => x.Name == "populated_stats").Should().Match<DatabaseRelationSummary>(
            static x => x.DefinitionAvailable && !x.Rows.Allowed && x.Rows.Refusal == DatabaseRefusal.CapabilityOff);

        lock (policy.Calls)
        {
            policy.Calls.Should().Contain(static x =>
                x.Policy == WritePolicy && x.Resource.Capability == nameof(MartenStudioCapabilities.ViewDefinitions) && x.Resource.TenantId == null,
                "the write policy was asked with ViewDefinitions, about the database as a whole, with no tenant");
            policy.Calls.Should().NotContain(static x => x.Resource.Capability == nameof(MartenStudioCapabilities.BrowseDatabase),
                "BrowseDatabase is off, so nobody was asked about it");
        }
    }

    [PostgresFact]
    public async Task ViewDefinitions_alone_refuses_the_rows_of_a_table_and_of_a_view_naming_BrowseDatabase()
    {
        await using BrowserHost host = ViewDefinitionsOnly(new ResourcePolicy(static _ => true));

        foreach (string name in new[] { "orders", "populated_stats" })
        {
            DatabaseRowAccessResult rows = await host.AccessAsync(x =>
                x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, name, cancellationToken: Token));

            rows.Allowed.Should().BeFalse(name);
            rows.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, rows.Reason);
            rows.Reason.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase", "a refusal names the property a host would set");
        }

        host.Ring.GetLatest().Where(static x => x.Action == DatabaseAccess.RowsAction).Should().HaveCount(2)
            .And.OnlyContain(static x => !x.Succeeded && x.Capability == nameof(MartenStudioCapabilities.BrowseDatabase));
    }

    [PostgresFact]
    public async Task A_schema_only_the_star_admits_is_still_refused_to_ViewDefinitions_and_the_refusal_is_audited_under_it()
    {
        await using BrowserHost host = ViewDefinitionsOnly(new ResourcePolicy(static _ => true));

        foreach (DatabaseObjectRef reference in new[] { QuartzView, QuartzFunction })
        {
            DatabaseObjectDefinition refused = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, reference, Token));

            refused.Found.Should().BeFalse(reference.Name);
            refused.Sql.Should().BeNull();
            refused.Refusal.Should().NotBe(DatabaseRefusal.None, "\"*\" is a statement about the database: its definitions are BrowseDatabase's");
        }

        DatabaseObjectList list = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Views, fixture.QuartzSchema), Token));
        list.Items.Should().BeEmpty();
        list.Refusal.Should().Be(DatabaseRefusal.NotFound, list.Reason);

        host.Ring.GetLatest().Where(static x => x.Action == DatabaseAccess.DefinitionAction).Should().HaveCount(2)
            .And.OnlyContain(static x => !x.Succeeded && x.Capability == nameof(MartenStudioCapabilities.ViewDefinitions));
    }

    [PostgresFact]
    public async Task An_object_Marten_owns_outside_the_stores_schemas_stays_BrowseDatabases()
    {
        var helper = new DatabaseObjectRef(DatabaseObjectKind.Function, fixture.LegacySchema, "mt_helper", string.Empty);

        await using (BrowserHost viewer = ViewDefinitionsOnly(new ResourcePolicy(static _ => true)))
        {
            DatabaseObjectDefinition refused = await viewer.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, helper, Token));

            refused.Found.Should().BeFalse();
            refused.Refusal.Should().Be(DatabaseRefusal.MartenOwned, refused.Reason);
            refused.Reason.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");
        }

        await using BrowserHost browser = fixture.Host(options =>
        {
            options.BrowsableSchemas.Clear();
            options.BrowsableSchemas.Add(fixture.LegacySchema);
        });

        (await browser.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, helper, Token))).Found.Should().BeTrue(
            "BrowseDatabase reads it, as it did before ViewDefinitions existed");
    }

    [PostgresFact]
    public async Task BrowseDatabase_implies_the_definitions_exactly_as_before_and_asks_the_policy_about_it_alone()
    {
        var policy = new ResourcePolicy(static _ => true);

        await using BrowserHost host = fixture.Host(
            options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
            },
            policy);

        (await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, LegacyFunction, Token))).Found.Should().BeTrue();
        (await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, QuartzView, Token))).Found.Should().BeTrue(
            "the Quartz schema is listed, so BrowseDatabase admits it");
        (await host.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.LegacySchema, "standalone_numbers", Token))).Found.Should().BeTrue(
            "BrowseDatabase reads a sequence's value, as it always did");

        DatabaseRowAccessResult rows = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.QuartzSchema, "qrtz_triggers", cancellationToken: Token));
        rows.Allowed.Should().BeTrue(rows.Reason);

        lock (policy.Calls)
        {
            policy.Calls.Where(static x => x.Resource.Capability is not null).Select(static x => x.Resource.Capability)
                .Distinct().Should().Equal(nameof(MartenStudioCapabilities.BrowseDatabase));
        }
    }

    [PostgresFact]
    public async Task A_write_policy_that_refuses_ViewDefinitions_refuses_the_definition_and_audits_it()
    {
        var policy = new ResourcePolicy(static resource => resource.Capability != nameof(MartenStudioCapabilities.ViewDefinitions));

        await using BrowserHost host = ViewDefinitionsOnly(policy);

        DatabaseObjectDefinition refused = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, LegacyView, Token));

        refused.Found.Should().BeFalse();
        refused.Refusal.Should().Be(DatabaseRefusal.WritePolicy, refused.Reason);
        refused.Reason.Should().Contain("refused ViewDefinitions").And.Contain("WriteAuthorizationPolicy");

        StudioActionLogEntry entry = host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == DatabaseAccess.DefinitionAction).Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Capability.Should().Be(nameof(MartenStudioCapabilities.ViewDefinitions));
        entry.TenantId.Should().BeNull("the refused scope is the database as a whole");
        entry.Target.Should().Be(fixture.LegacySchema + ".populated_stats");

        DatabaseBrowserOverview overview = await host.ObjectsAsync(x => x.GetOverviewAsync(BrowserHost.Scope, Token));
        overview.Access.DefinitionsOpen.Should().BeFalse();
        overview.Access.DefinitionDenial.Should().Contain("refused ViewDefinitions");
        overview.Schemas.Select(static x => x.Name).Should().NotContain(fixture.LegacySchema,
            "in Marten Studio the exactly named schemas' structure is ViewDefinitions' or BrowseDatabase's, and neither passes");
    }

    [PostgresFact]
    public async Task A_visitor_the_policy_admits_to_ViewDefinitions_and_not_BrowseDatabase_reads_definitions_and_no_rows()
    {
        var policy = new ResourcePolicy(static resource => resource.Capability != nameof(MartenStudioCapabilities.BrowseDatabase));

        await using BrowserHost host = fixture.Host(
            options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                options.Capabilities = MartenStudioCapabilities.All();
                options.BrowsableSchemas.Clear();
                options.BrowsableSchemas.Add(fixture.LegacySchema);
                options.BrowsableSchemas.Add("*");
            },
            policy);

        (await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, LegacyView, Token))).Found.Should().BeTrue(
            "ViewDefinitions admits the exactly named schema");
        (await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, QuartzView, Token))).Found.Should().BeFalse(
            "and not the one only \"*\" admits");

        DatabaseRowAccessResult rows = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "orders", cancellationToken: Token));
        rows.Refusal.Should().Be(DatabaseRefusal.WritePolicy, rows.Reason);

        DatabaseSequenceValue value = await host.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.LegacySchema, "standalone_numbers", Token));
        value.Refusal.Should().Be(DatabaseRefusal.WritePolicy, value.Reason);
        value.Reason.Should().Contain("BrowseDatabase", "a sequence's value is data: the write policy is asked about BrowseDatabase");
    }

    [PostgresFact]
    public async Task ReadOnly_refuses_a_definition_whatever_the_capabilities_say()
    {
        await using BrowserHost host = ViewDefinitionsOnly(
            new ResourcePolicy(static _ => true),
            static options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.ReadOnly = true;
            });

        DatabaseObjectDefinition refused = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, LegacyView, Token));

        refused.Refusal.Should().Be(DatabaseRefusal.ReadOnly, refused.Reason);
        refused.Reason.Should().Contain("MartenStudioOptions.Capabilities.ViewDefinitions").And.Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        (await host.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.LegacySchema, "standalone_numbers", Token)))
            .Refusal.Should().Be(DatabaseRefusal.ReadOnly);
    }

    [PostgresFact]
    public async Task The_stores_own_structure_needs_neither_capability_and_the_hosts_own_function_in_it_needs_one()
    {
        await using BrowserHost host = fixture.Host(options =>
        {
            options.Capabilities.BrowseDatabase = false;
            options.BrowsableSchemas.Clear();
        });

        DatabaseObjectDetail settings = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.DocumentSchema, "host_settings", Token));
        settings.Found.Should().BeTrue("the store's own structure is free: " + settings.Reason);

        DatabaseObjectDefinition refused = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.Function, fixture.DocumentSchema, "host_fn", string.Empty), Token));

        refused.Found.Should().BeFalse("the host's own function in the store's schema is a definition read, whichever way");
        refused.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, refused.Reason);
        refused.Reason.Should().Contain("MartenStudioOptions.Capabilities.ViewDefinitions")
            .And.Contain("MartenStudioOptions.Capabilities.BrowseDatabase");
    }
}
