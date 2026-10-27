using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Integration.Tests.Database;

/// <summary>
/// Issue #22 on a plain database: <c>ViewDefinitions</c> shows the definitions of the schemas <c>BrowsableSchemas</c> names
/// exactly - a view's query, a function's body, a trigger's definition, a type, a sequence's value - and no row; a schema
/// only <c>"*"</c> admits keeps needing <c>BrowseDatabase</c>; <c>BrowseDatabase</c> implies it; the write policy is asked
/// with <c>ViewDefinitions</c> against the database as a whole; and <c>ReadOnly</c> refuses it with everything else.
/// </summary>
/// <remarks>
/// The database is this class's own. <c>app</c> is named exactly and holds a table, a view, a trigger and its function, an
/// SQL-bodied function, a type and an identity sequence; <c>archive</c> holds the same kinds and is admitted only through
/// <c>"*"</c>; <c>public</c> is empty and is admitted through <c>"*"</c> too.
/// </remarks>
public class ViewDefinitionsLiveTests(ViewDefinitionsLiveTests.Fixture database) : IClassFixture<ViewDefinitionsLiveTests.Fixture>
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static readonly DatabaseObjectRef AppView = new(DatabaseObjectKind.View, "app", "account_summary");
    private static readonly DatabaseObjectRef AppFunction = new(DatabaseObjectKind.Function, "app", "fee", "amount integer");
    private static readonly DatabaseObjectRef AppTrigger = new(DatabaseObjectKind.Trigger, "app", "accounts_touch", Table: "accounts");
    private static readonly DatabaseObjectRef AppType = new(DatabaseObjectKind.EnumType, "app", "tier");
    private static readonly DatabaseObjectRef ArchiveView = new(DatabaseObjectKind.View, "archive", "old_summary");
    private static readonly DatabaseObjectRef ArchiveFunction = new(DatabaseObjectKind.Function, "archive", "purge", string.Empty);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The class's database, seeded once.</summary>
    public sealed class Fixture(PostgresFixture postgres) : IAsyncLifetime
    {
        private OwnedDatabases? owned;

        /// <summary>The database's connection string.</summary>
        public string ConnectionString => owned![0];

        public async ValueTask InitializeAsync()
        {
            if (!DockerAvailability.IsAvailable)
            {
                return;
            }

            owned = await OwnedDatabases.CreateAsync(postgres, ["ps_viewdefs"], CancellationToken.None);
            await OwnedDatabases.ExecuteAsync(
                ConnectionString,
                """
                create schema app;
                create table app.accounts (
                    id int generated always as identity primary key,
                    owner text not null,
                    balance int not null check (balance >= 0));
                create view app.account_summary as select owner, balance + 41 as padded from app.accounts;
                create function app.touch() returns trigger language plpgsql as $$ begin new.owner := lower(new.owner); return new; end $$;
                create trigger accounts_touch before insert on app.accounts for each row execute function app.touch();
                create function app.fee(amount integer) returns integer language sql return amount * 7;
                create type app.tier as enum ('gold', 'silver');
                insert into app.accounts (owner, balance) values ('acme', 100), ('globex', 200);

                create schema archive;
                create table archive.old_accounts (id int primary key, owner text);
                create view archive.old_summary as select id from archive.old_accounts;
                create function archive.purge() returns integer language sql return 1;
                """,
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }

    [PostgresFact]
    public async Task ViewDefinitions_alone_shows_the_definitions_of_the_exactly_named_schema_and_the_policy_is_asked_with_it()
    {
        await using StudioHost host = Host(static o => o.Capabilities.ViewDefinitions = true, writePolicy: static (_, _) => true);
        await using StudioCircuit circuit = host.Circuit();
        IDatabaseObjectService objects = circuit.Get<IDatabaseObjectService>();

        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(Scope, Token);
        overview.Refusal.Should().Be(DatabaseRefusal.None, overview.Reason);
        overview.Schemas.Select(static x => x.Name).Should().Equal(["app"], "the schema named exactly is the data source's own");
        overview.Schemas.Should().OnlyContain(static x => !x.RowsBrowsable, "no row is browsable");
        overview.Access.WithheldSchemaCount.Should().Be(2, "archive and public are admitted only through \"*\", and are counted, never named");
        overview.Access.DefinitionsOpen.Should().BeTrue();
        overview.Access.DefinitionSchemas.Should().Equal("app");
        overview.Access.IsOpen.Should().BeFalse("BrowseDatabase is not on");

        DatabaseObjectDefinition view = await objects.GetDefinitionAsync(Scope, AppView, Token);
        view.Found.Should().BeTrue(view.Reason);
        view.Sql.Should().Contain("accounts").And.Contain("41");

        DatabaseObjectDefinition function = await objects.GetDefinitionAsync(Scope, AppFunction, Token);
        function.Found.Should().BeTrue(function.Reason);
        function.Sql.Should().Contain("fee").And.Contain("7");

        DatabaseObjectDefinition trigger = await objects.GetDefinitionAsync(Scope, AppTrigger, Token);
        trigger.Found.Should().BeTrue(trigger.Reason);
        trigger.Sql.Should().Contain("accounts_touch").And.Contain("EXECUTE FUNCTION app.touch()");

        DatabaseObjectDefinition type = await objects.GetDefinitionAsync(Scope, AppType, Token);
        type.Found.Should().BeTrue(type.Reason);
        type.Type!.Labels.Should().Equal("gold", "silver");

        // A sequence's current value is data - it says how many rows the table has had - so it is BrowseDatabase's alone.
        DatabaseSequenceValue sequence = await objects.GetSequenceValueAsync(Scope, "app", "accounts_id_seq", Token);
        sequence.Found.Should().BeFalse();
        sequence.LastValue.Should().BeNull();
        sequence.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, sequence.Reason);
        sequence.Reason.Should().Contain("BrowseDatabase");

        DatabaseObjectList sequences = await objects.ListAsync(Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Sequences, "app"), Token);
        sequences.Items.OfType<DatabaseSequenceSummary>().Should().ContainSingle(static x => x.Name == "accounts_id_seq")
            .Which.CanReadValue.Should().BeFalse("the list says so in advance, and still shows the sequence's own definition");

        // The lists say so in advance: a control drawn enabled is one whose call passes.
        DatabaseObjectList views = await objects.ListAsync(Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Views, "app"), Token);
        views.Refusal.Should().Be(DatabaseRefusal.None, views.Reason);
        views.Items.OfType<DatabaseRelationSummary>().Should().ContainSingle().Which.Should().Match<DatabaseRelationSummary>(
            static x => x.DefinitionAvailable && !x.Rows.Allowed && x.Rows.Refusal == DatabaseRefusal.CapabilityOff);

        host.Calls.All.Should().Contain(static x =>
            x.Policy == StudioHost.WritePolicy && x.Allowed
            && x.Resource == new PostgresStudioResource("default", "default", nameof(PostgresStudioCapabilities.ViewDefinitions)),
            "the write policy was asked with ViewDefinitions, about the database as a whole");
        host.Calls.All.Should().NotContain(static x => x.Resource.Capability == nameof(PostgresStudioCapabilities.BrowseDatabase),
            "BrowseDatabase is off, so nobody was asked about it");
    }

    [PostgresFact]
    public async Task ViewDefinitions_alone_refuses_the_rows_of_a_table_and_of_a_view_naming_BrowseDatabase()
    {
        await using StudioHost host = Host(static o => o.Capabilities.ViewDefinitions = true);
        await using StudioCircuit circuit = host.Circuit();
        ITableRowService rows = circuit.Get<ITableRowService>();

        TableRowPage table = await rows.ListRowsAsync(Scope, "app", "accounts", new TableRowRequest(), Token);
        table.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, table.Reason);
        table.Rows.Should().BeEmpty();
        table.Reason.Should().Contain("BrowseDatabase", "a refusal names the property a host would set");

        TableRowPage view = await rows.ListRowsAsync(Scope, "app", "account_summary", new TableRowRequest(), Token);
        view.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, view.Reason);
        view.Rows.Should().BeEmpty();
        view.Reason.Should().Contain("BrowseDatabase");

        host.Ring.GetLatest().Where(static x => x.Action == DatabaseAccess.RowsAction && !x.Succeeded).Select(static x => x.Target)
            .Should().Contain(["app.accounts", "app.account_summary"]);
        host.Ring.GetLatest().Where(static x => x.Action == DatabaseAccess.RowsAction)
            .Should().OnlyContain(static x => x.Capability == nameof(PostgresStudioCapabilities.BrowseDatabase));
    }

    [PostgresFact]
    public async Task A_schema_only_the_star_admits_is_still_refused_to_ViewDefinitions_and_the_refusal_is_audited_under_it()
    {
        await using StudioHost host = Host(static o => o.Capabilities.ViewDefinitions = true);
        await using StudioCircuit circuit = host.Circuit();
        IDatabaseObjectService objects = circuit.Get<IDatabaseObjectService>();

        foreach (DatabaseObjectRef reference in new[] { ArchiveView, ArchiveFunction })
        {
            DatabaseObjectDefinition refused = await objects.GetDefinitionAsync(Scope, reference, Token);

            refused.Found.Should().BeFalse(reference.Name);
            refused.Sql.Should().BeNull();
            refused.Refusal.Should().NotBe(DatabaseRefusal.None, "\"*\" is a statement about the database: the definitions of what it admits are BrowseDatabase's");
        }

        DatabaseObjectList list = await objects.ListAsync(Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Views, "archive"), Token);
        list.Items.Should().BeEmpty("the schema is withheld");
        list.Refusal.Should().Be(DatabaseRefusal.NotFound, list.Reason);

        DatabaseObjectDetail detail = await objects.GetObjectAsync(Scope, "archive", "old_accounts", Token);
        detail.Found.Should().BeFalse();

        host.Ring.GetLatest().Where(static x => x.Action == DatabaseAccess.DefinitionAction).Should().HaveCount(2)
            .And.OnlyContain(static x => !x.Succeeded && x.Capability == nameof(PostgresStudioCapabilities.ViewDefinitions)
                && x.Target.StartsWith("archive.", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task BrowseDatabase_implies_the_definitions_exactly_as_before_and_asks_the_policy_about_it_alone()
    {
        await using StudioHost host = Host(static o => o.Capabilities.BrowseDatabase = true, writePolicy: static (_, _) => true);
        await using StudioCircuit circuit = host.Circuit();
        IDatabaseObjectService objects = circuit.Get<IDatabaseObjectService>();

        (await objects.GetDefinitionAsync(Scope, AppView, Token)).Found.Should().BeTrue();
        (await objects.GetDefinitionAsync(Scope, ArchiveView, Token)).Found.Should().BeTrue("\"*\" admits archive to BrowseDatabase");
        (await objects.GetDefinitionAsync(Scope, ArchiveFunction, Token)).Found.Should().BeTrue();

        DatabaseSequenceValue sequence = await objects.GetSequenceValueAsync(Scope, "app", "accounts_id_seq", Token);
        sequence.Found.Should().BeTrue(sequence.Reason);
        sequence.LastValue.Should().Be(2, "BrowseDatabase reads a sequence's value, as it always did");

        TableRowPage rows = await circuit.Get<ITableRowService>().ListRowsAsync(Scope, "app", "accounts", new TableRowRequest(), Token);
        rows.State.Should().Be(TableRowPageState.Loaded, rows.Reason);
        rows.Rows.Should().HaveCount(2);

        host.Calls.All.Where(static x => x.Resource.Capability is not null).Select(static x => x.Resource.Capability)
            .Distinct().Should().Equal(nameof(PostgresStudioCapabilities.BrowseDatabase));
    }

    [PostgresFact]
    public async Task A_write_policy_that_refuses_ViewDefinitions_refuses_the_definition_and_audits_it()
    {
        await using StudioHost host = Host(
            static o => o.Capabilities.ViewDefinitions = true,
            writePolicy: static (resource, _) => resource.Capability != nameof(PostgresStudioCapabilities.ViewDefinitions));
        await using StudioCircuit circuit = host.Circuit();
        IDatabaseObjectService objects = circuit.Get<IDatabaseObjectService>();

        DatabaseObjectDefinition refused = await objects.GetDefinitionAsync(Scope, AppView, Token);

        refused.Found.Should().BeFalse();
        refused.Refusal.Should().Be(DatabaseRefusal.WritePolicy, refused.Reason);
        refused.Reason.Should().Contain("refused ViewDefinitions").And.Contain("WriteAuthorizationPolicy");

        StudioActionLogEntry entry = host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == DatabaseAccess.DefinitionAction).Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Capability.Should().Be(nameof(PostgresStudioCapabilities.ViewDefinitions));
        entry.Target.Should().Be("app.account_summary");

        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(Scope, Token);
        overview.Access.DefinitionsOpen.Should().BeFalse();
        overview.Access.DefinitionDenial.Should().Contain("refused ViewDefinitions");
        overview.Schemas.Select(static x => x.Name).Should().Equal(["app"], "the structure of the data source's own schemas never needed either");
    }

    [PostgresFact]
    public async Task A_visitor_the_policy_admits_to_ViewDefinitions_and_not_BrowseDatabase_reads_definitions_and_no_rows()
    {
        await using StudioHost host = Host(
            static o => o.Capabilities = PostgresStudioCapabilities.All(),
            writePolicy: static (resource, user) =>
                resource.Capability == nameof(PostgresStudioCapabilities.BrowseDatabase) ? user.Identity?.Name == "admin" : true);

        await using (StudioCircuit viewer = host.Circuit("viewer"))
        {
            IDatabaseObjectService objects = viewer.Get<IDatabaseObjectService>();

            (await objects.GetDefinitionAsync(Scope, AppView, Token)).Found.Should().BeTrue("ViewDefinitions admits the exactly named schema");

            DatabaseObjectDefinition starred = await objects.GetDefinitionAsync(Scope, ArchiveView, Token);
            starred.Found.Should().BeFalse("and not the one only \"*\" admits, which is BrowseDatabase's");

            TableRowPage rows = await viewer.Get<ITableRowService>().ListRowsAsync(Scope, "app", "accounts", new TableRowRequest(), Token);
            rows.Refusal.Should().Be(DatabaseRefusal.WritePolicy, rows.Reason);
            rows.Reason.Should().Contain("BrowseDatabase");

            DatabaseSequenceValue value = await objects.GetSequenceValueAsync(Scope, "app", "accounts_id_seq", Token);
            value.Refusal.Should().Be(DatabaseRefusal.WritePolicy, value.Reason);
            value.Reason.Should().Contain("BrowseDatabase", "a sequence's value is data: the write policy is asked about BrowseDatabase");
        }

        await using (StudioCircuit admin = host.Circuit("admin"))
        {
            (await admin.Get<IDatabaseObjectService>().GetDefinitionAsync(Scope, ArchiveView, Token)).Found.Should().BeTrue();
        }
    }

    [PostgresFact]
    public async Task ReadOnly_refuses_a_definition_whatever_the_capabilities_say()
    {
        await using StudioHost host = Host(static o =>
        {
            o.Capabilities = PostgresStudioCapabilities.All();
            o.ReadOnly = true;
        });
        await using StudioCircuit circuit = host.Circuit();
        IDatabaseObjectService objects = circuit.Get<IDatabaseObjectService>();

        DatabaseObjectDefinition refused = await objects.GetDefinitionAsync(Scope, AppView, Token);
        refused.Refusal.Should().Be(DatabaseRefusal.ReadOnly, refused.Reason);
        refused.Reason.Should().Contain("ViewDefinitions").And.Contain("BrowseDatabase");

        (await objects.GetSequenceValueAsync(Scope, "app", "accounts_id_seq", Token)).Refusal.Should().Be(DatabaseRefusal.ReadOnly);
    }

    [PostgresFact]
    public async Task With_neither_capability_on_a_definition_is_refused_naming_both()
    {
        await using StudioHost host = Host(static _ => { });
        await using StudioCircuit circuit = host.Circuit();

        DatabaseObjectDefinition refused = await circuit.Get<IDatabaseObjectService>().GetDefinitionAsync(Scope, AppView, Token);

        refused.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, refused.Reason);
        refused.Reason.Should().Contain("PostgresStudioOptions.Capabilities.ViewDefinitions")
            .And.Contain("PostgresStudioOptions.Capabilities.BrowseDatabase");
        host.Ring.GetLatest().Should().ContainSingle(static x => x.Action == DatabaseAccess.DefinitionAction).Which.Capability
            .Should().Be(nameof(PostgresStudioCapabilities.BrowseDatabase), "as every such refusal always was");
    }

    private StudioHost Host(
        Action<PostgresStudioOptions> studio,
        Func<PostgresStudioResource, System.Security.Claims.ClaimsPrincipal, bool>? writePolicy = null) =>
        StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(database.ConnectionString)),
            options =>
            {
                options.BrowsableSchemas.Add("app");
                options.BrowsableSchemas.Add("*");
                studio(options);
            },
            writePolicy: writePolicy);
}
