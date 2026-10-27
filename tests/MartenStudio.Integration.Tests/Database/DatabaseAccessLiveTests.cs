using PostgresStudio.Services.Database;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// The database browser's gate against a real Postgres: the row gate DB-3 will call, the
/// <c>SqlConsoleRole</c> narrowing, and a tenant-restricted policy - each refusal audited.
/// </summary>
public class DatabaseAccessLiveTests(DatabaseAccessLiveTests.Fixture fixture) : IClassFixture<DatabaseAccessLiveTests.Fixture>
{
    private const string StorePolicy = "store";
    private const string WritePolicy = "write";

    /// <summary>This class's schemas.</summary>
    public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A grant carries the catalog's own names and the relation's key: what a row reader quotes is what
    /// Postgres said, never what the URL said.
    /// </summary>
    [PostgresFact]
    public async Task The_row_gate_grants_a_Quartz_table_with_the_catalogs_names_and_its_key()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseRowAccessResult result = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.QuartzSchema, "qrtz_triggers", cancellationToken: Token));

        result.Allowed.Should().BeTrue(result.Reason);
        result.Grant!.Relation.Relation.Schema.Should().Be(fixture.QuartzSchema);
        result.Grant.Relation.Relation.Name.Should().Be("qrtz_triggers");
        result.Grant.RowKey!.Columns.Should().Equal("sched_name", "trigger_name", "trigger_group");
        result.Grant.Columns.Should().HaveCount(6);
        result.Grant.Resolved.Scope.TenantId.Should().BeNull();
    }

    [PostgresFact]
    public async Task The_row_gate_refuses_Marten_tables_hidden_views_and_what_is_not_there_and_audits_each()
    {
        await using BrowserHost host = fixture.Host(options =>
        {
            options.BrowsableSchemas.Add(fixture.DocumentSchema);
            options.BrowsableSchemas.Add(fixture.EventSchema);
        });

        async Task<DatabaseRowAccessResult> Rows(string schema, string name) =>
            await host.AccessAsync(x => x.RequireRowAccessAsync(BrowserHost.Scope, schema, name, cancellationToken: Token));

        (await Rows(fixture.DocumentSchema, fixture.VisibleTable)).Refusal.Should().Be(DatabaseRefusal.MartenOwned);
        (await Rows(fixture.EventSchema, "mt_events")).Refusal.Should().Be(DatabaseRefusal.MartenOwned);
        (await Rows(fixture.LegacySchema, "secret_peek")).Refusal.Should().Be(DatabaseRefusal.HiddenDependency);
        (await Rows(fixture.LegacySchema, "measurements_2025")).Refusal.Should().Be(DatabaseRefusal.NotFound,
            "a partition is never a relation of its own here");
        (await Rows(fixture.DocumentSchema, fixture.HiddenTable)).Refusal.Should().Be(DatabaseRefusal.NotFound);
        (await Rows(fixture.LegacySchema, "no_such_table")).Refusal.Should().Be(DatabaseRefusal.NotFound);

        host.Ring.GetLatest().Where(static x => x.Action == DatabaseAccess.RowsAction)
            .Should().HaveCount(6).And.OnlyContain(static x => !x.Succeeded && x.TenantId == null);
    }

    /// <summary>
    /// <c>SqlConsoleRole</c> narrows the browser exactly as it narrows the console: the catalog is read as
    /// the role, so a table it cannot select from is listed as not readable and its rows are refused.
    /// </summary>
    [PostgresFact]
    public async Task A_table_the_SqlConsoleRole_cannot_select_from_is_not_readable()
    {
        await using BrowserHost host = fixture.Host(options => options.SqlConsoleRole = fixture.Role);

        DatabaseObjectDetail payroll = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "payroll"));

        payroll.Found.Should().BeTrue(payroll.Reason);
        payroll.Relation!.Readable.Should().BeFalse();
        payroll.Relation.Rows.Refusal.Should().Be(DatabaseRefusal.NoPrivilege);
        payroll.Relation.Rows.Reason.Should().Contain(fixture.Role);

        DatabaseObjectDetail triggers = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.QuartzSchema, "qrtz_triggers"));
        triggers.Relation!.Readable.Should().BeTrue();
        triggers.Relation.Rows.Allowed.Should().BeTrue(triggers.Relation.Rows.Reason);

        DatabaseRowAccessResult rows = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "payroll", cancellationToken: Token));

        rows.Refusal.Should().Be(DatabaseRefusal.NoPrivilege);
    }

    /// <summary>
    /// H1's R4 (H1b): the value of Marten's own sequence is the Schema screen's structure, read with the visitor's own
    /// scope, tenant and all. When <c>SqlConsoleRole</c> may not read it, the refusal named the role - and the name of the
    /// role the studio reads as is Database-gate information (D29), which a visitor the store policy passes only for its
    /// tenant does not pass. So that visitor is told the role cannot read it, not which role; one past the gate - the store
    /// policy for the database as a whole - is told its name, as before.
    /// </summary>
    [PostgresFact]
    public async Task A_Marten_sequence_the_SqlConsoleRole_cannot_read_names_the_role_only_past_the_database_gate()
    {
        var acme = new StudioScope("default", string.Empty, "acme");
        var tenantOnly = new ResourcePolicy(static resource => resource.TenantId == "acme");

        await using (BrowserHost host = fixture.Host(
                         options =>
                         {
                             options.StoreAuthorizationPolicy = StorePolicy;
                             options.WriteAuthorizationPolicy = WritePolicy;
                             options.KnownTenantIds.Add("acme");
                             options.SqlConsoleRole = fixture.Role;
                         },
                         tenantOnly))
        {
            DatabaseSequenceValue refused = await host.ObjectsAsync(x => x.GetSequenceValueAsync(acme, fixture.EventSchema, "mt_events_sequence", Token));

            refused.Refusal.Should().Be(DatabaseRefusal.NoPrivilege, refused.Reason);
            refused.Reason.Should().Be(DatabaseObjectService.SequenceNoPrivilegeUnnamed).And.NotContain(fixture.Role);
            tenantOnly.Calls.Should().Contain(static x => x.Policy == StorePolicy && x.Resource.TenantId == null,
                "the Database gate was asked: the store policy, about the database as a whole");
        }

        var everyone = new ResourcePolicy(static _ => true);

        await using (BrowserHost host = fixture.Host(
                         options =>
                         {
                             options.StoreAuthorizationPolicy = StorePolicy;
                             options.KnownTenantIds.Add("acme");
                             options.SqlConsoleRole = fixture.Role;
                         },
                         everyone))
        {
            DatabaseSequenceValue named = await host.ObjectsAsync(x => x.GetSequenceValueAsync(acme, fixture.EventSchema, "mt_events_sequence", Token));

            named.Refusal.Should().Be(DatabaseRefusal.NoPrivilege, named.Reason);
            named.Reason.Should().Contain(fixture.Role).And.Contain("SqlConsoleRole", "past the gate, the role is named and so is the option");

            DatabaseSequenceValue whole = await host.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.EventSchema, "mt_events_sequence", Token));
            whole.Reason.Should().Be(named.Reason, "a scope with no tenant has passed the gate already, by resolving");
        }
    }

    /// <summary>
    /// Acceptance 7, the last case: a handler that confines a visitor to their own tenant refuses
    /// <c>TenantId == null</c>, so it refuses the database browser's reads - and the refusal is in the
    /// audit ring, naming the policy that said no: the store policy, asked first (F10). The store's own
    /// structure, read with the visitor's tenant, is still theirs.
    /// </summary>
    [PostgresFact]
    public async Task A_tenant_restricted_policy_refuses_the_definition_read_and_the_audit_records_it()
    {
        var policy = new ResourcePolicy(static resource => resource.TenantId == "acme");

        await using BrowserHost host = fixture.Host(
            static options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                options.KnownTenantIds.Add("acme");
            },
            policy);

        var acme = new StudioScope("default", string.Empty, "acme");

        DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            acme, new DatabaseObjectRef(DatabaseObjectKind.View, fixture.LegacySchema, "job_summary")));

        definition.Found.Should().BeFalse();
        definition.Refusal.Should().Be(DatabaseRefusal.StorePolicy, "the store policy is asked first, and refuses a resource with no tenant");
        definition.Reason.Should().Contain("StoreAuthorizationPolicy");
        definition.Sql.Should().BeNull();

        StudioActionLogEntry entry = host.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Action.Should().Be(DatabaseAccess.DefinitionAction);
        entry.Succeeded.Should().BeFalse();
        entry.TenantId.Should().BeNull("the refused scope is the database as a whole");
        entry.Target.Should().Be(fixture.LegacySchema + ".job_summary");

        policy.Calls.Should().Contain(static x => x.Resource.TenantId == null,
            "the policy was asked about the database with no tenant");

        DatabaseRowAccessResult rows = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(acme, fixture.QuartzSchema, "qrtz_triggers", cancellationToken: Token));

        rows.Refusal.Should().Be(DatabaseRefusal.StorePolicy);

        DatabaseObjectDetail settings = await host.ObjectsAsync(x => x.GetObjectAsync(acme, fixture.DocumentSchema, "host_settings"));
        settings.Found.Should().BeTrue("the store's own structure is read with the visitor's own tenant: " + settings.Reason);
    }
}
