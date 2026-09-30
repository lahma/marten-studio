using System.Text.Json;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// F1, F2, F3 and F7 against a real Postgres 17, through the studio's own services: a withheld schema's
/// name never reaches a page, and a view is judged on everything it reads and calls - whatever it is
/// called - and on a hidden type Marten learned after the circuit opened.
/// </summary>
/// <remarks>
/// The shared fixture holds a schema no studio here may see (<see cref="DatabaseBrowserFixture.HrSchema" />)
/// and objects in the store's own schema and in the legacy schema that name it; the store's own
/// structure is visible without the capability, which is why the masking is asserted both ways.
/// </remarks>
public class DatabaseRedactionLiveTests(DatabaseRedactionLiveTests.Fixture fixture) : IClassFixture<DatabaseRedactionLiveTests.Fixture>
{
    private const string Mask = WithheldNames.Token;

    /// <summary>This class's schemas, and a hidden type's table that a fresh store has not learned.</summary>
    public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres)
    {
        /// <summary><see cref="BrowserLateSecret" />'s table, as Marten names it.</summary>
        public string LateTable { get; private set; } = string.Empty;

        /// <inheritdoc />
        private protected override async Task InitializeStoreAsync(BrowserHost setup)
        {
            // The table exists because another process - here, the setup store - once stored one; the stores
            // the tests build have never heard of the type.
            await using IDocumentSession session = setup.Store.LightweightSession();
            session.Store(new BrowserLateSecret { Id = Guid.NewGuid() });
            await session.SaveChangesAsync();

            LateTable = setup.Store.Options.FindOrResolveDocumentType(typeof(BrowserLateSecret)).TableName.Name;
        }

        /// <inheritdoc />
        protected override string ExtraSql =>
            "create view {l}.late_peek as select id from {d}." + SqlIdentifier.Quote(LateTable) + ";";
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // F1
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The store's own table, whose default, type, check, index, trigger and comment all name the withheld
    /// schema: every one is masked - with the capability, and without it, where the structure is still shown.
    /// </summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_withheld_schema_is_masked_in_the_store_schemas_own_structure(bool capability)
    {
        // The store's own schema is listed so that, with the capability, the trigger's definition gets as far
        // as the check on its function's schema; its structure needs neither.
        await using BrowserHost host = fixture.Host(options =>
        {
            options.Capabilities.BrowseDatabase = capability;
            options.BrowsableSchemas.Add(fixture.DocumentSchema);
        });

        DatabaseObjectDetail refs = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.DocumentSchema, "hr_refs", Token));

        refs.Found.Should().BeTrue(refs.Reason);

        refs.Columns.Single(static x => x.Name == "id").Default.Should().Be("nextval('" + Mask + ".refs_seq'::regclass)");
        refs.Columns.Single(static x => x.Name == "grade").Type.Should().Be(Mask + ".grade");
        refs.Constraints.Single(static x => x.Kind == DatabaseConstraintKind.Check).Definition.Should().Contain(Mask + ".is_ok(id)");
        refs.Indexes.Single(static x => x.Name == "hr_refs_norm").Definition.Should().Contain("(" + Mask + ".norm(id))");
        refs.Relation!.Comment.Should().Be("A copy of " + Mask + ".salaries.");

        DatabaseTriggerSummary audit = refs.Triggers.Should().ContainSingle().Which;
        audit.FunctionSchema.Should().BeNull();
        audit.FunctionName.Should().BeNull();
        audit.DefinitionAvailable.Should().BeFalse();

        JsonSerializer.Serialize(refs).Should().NotContain(fixture.HrSchema, "no field of the detail names the withheld schema");

        DatabaseObjectDefinition trigger = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope,
            new DatabaseObjectRef(DatabaseObjectKind.Trigger, fixture.DocumentSchema, "hr_refs_audit", Table: "hr_refs"),
            Token));

        trigger.Found.Should().BeFalse();
        trigger.Sql.Should().BeNull();
        trigger.Refusal.Should().Be(capability ? DatabaseRefusal.WithheldDependency : DatabaseRefusal.CapabilityOff);
    }

    [PostgresFact]
    public async Task A_view_over_a_withheld_schema_is_refused_its_rows_and_its_query()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectDetail view = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "hr_view", Token));

        view.Found.Should().BeTrue(view.Reason);
        view.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
        view.Relation.Rows.Reason.Should().Be("It reads from a schema you cannot see.");
        view.Relation.DefinitionAvailable.Should().BeFalse();
        view.Dependencies.Should().ContainSingle().Which.Visible.Should().BeFalse();

        DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.View, fixture.LegacySchema, "hr_view"), Token));

        definition.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
        definition.Sql.Should().BeNull();

        DatabaseRowAccessResult rows = await host.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "hr_view", cancellationToken: Token));

        rows.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
    }

    /// <summary>
    /// A routine's result, its <c>SET search_path</c> and its definition name the withheld schema: all
    /// masked, and the definition still found by the masked signature a list shows.
    /// </summary>
    [PostgresFact]
    public async Task A_routines_result_settings_and_definition_are_masked()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList functions = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Functions, fixture.LegacySchema), Token));

        var byName = functions.Items.Cast<DatabaseRoutineSummary>().ToDictionary(static x => x.Name);

        byName["pay_of"].Result.Should().Be("SETOF " + Mask + ".salaries");
        byName["pinned"].Config.Should().ContainSingle().Which.Should().StartWith("search_path=" + Mask);
        byName["pinned"].SearchPathPinned.Should().BeTrue();

        foreach (string name in new[] { "pay_of", "pinned" })
        {
            DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, byName[name].Ref, Token));

            definition.Found.Should().BeTrue(name + ": " + definition.Reason);
            definition.Sql.Should().NotContain(fixture.HrSchema, name + "'s definition names the withheld schema only masked");
            definition.Sql.Should().Contain(Mask);
        }

        // A signature that names the withheld schema was never shown by a list: somebody typed it.
        DatabaseObjectDefinition typed = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope,
            new DatabaseObjectRef(DatabaseObjectKind.Function, fixture.LegacySchema, "pay_of", "who integer, x " + fixture.HrSchema + ".grade"),
            Token));

        typed.Refusal.Should().Be(DatabaseRefusal.NotFound);
    }

    // ---------------------------------------------------------------------------------------------
    // F2
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>mt_peek</c> is named like Marten's own and sits in the store's own schema, so its query needs no
    /// capability to ask for - and it reads the hidden type's table. Refused both ways.
    /// </summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_mt_named_view_over_the_hidden_types_table_is_refused_its_query(bool capability)
    {
        await using BrowserHost host = fixture.Host(options => options.Capabilities.BrowseDatabase = capability);

        DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.View, fixture.DocumentSchema, "mt_peek"), Token));

        definition.Found.Should().BeFalse();
        definition.Sql.Should().BeNull();
        definition.Refusal.Should().Be(DatabaseRefusal.HiddenDependency);

        DatabaseObjectDetail detail = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.DocumentSchema, "mt_peek", Token));
        detail.Relation!.DefinitionAvailable.Should().BeFalse();
        detail.Relation.Rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency);
    }

    // ---------------------------------------------------------------------------------------------
    // F3
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A view that reads the hidden table through a function - called directly, or behind an operator -
    /// is refused its rows and its query while any type is hidden. With nothing hidden its query is shown,
    /// and its rows are still refused while the reading role can read a schema withheld from the visitor
    /// (the function could read that too); under <c>"*"</c>, where nothing the role can read is withheld,
    /// the same view is the host's own and readable.
    /// </summary>
    [PostgresFact]
    public async Task A_view_that_calls_a_function_is_refused_while_a_type_is_hidden()
    {
        await using BrowserHost host = fixture.Host();

        foreach (string view in new[] { "fn_peek", "op_peek" })
        {
            DatabaseObjectDetail detail = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, view, Token));

            detail.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency, view);
            detail.Relation.Rows.Reason.Should().Be("It calls a function; the studio cannot tell what that function reads.", view);
            detail.Relation.DefinitionAvailable.Should().BeFalse(view);

            DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(
                BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.View, fixture.LegacySchema, view), Token));

            definition.Refusal.Should().Be(DatabaseRefusal.HiddenDependency, view);

            DatabaseRowAccessResult rows = await host.AccessAsync(x =>
                x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, view, cancellationToken: Token));

            rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency, view);
        }

        DatabaseObjectList views = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Views, fixture.LegacySchema), Token));

        var listed = views.Items.Cast<DatabaseRelationSummary>().ToDictionary(static x => x.Name);
        listed["fn_peek"].Rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency, "the list judges it the same way");
        listed["job_summary"].Rows.Allowed.Should().BeTrue("count(*) is Postgres' own: " + listed["job_summary"].Rows.Reason);

        await using BrowserHost nothingHidden = fixture.Host(static options => options.IsDocumentTypeVisible = null);

        DatabaseRowAccessResult withheld = await nothingHidden.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "fn_peek", cancellationToken: Token));

        withheld.Refusal.Should().Be(DatabaseRefusal.WithheldDependency, "the role can read a schema this visitor may not see");
        withheld.Reason.Should().Be(DatabaseGate.OpaqueWithheldDenial);

        DatabaseObjectDefinition query = await nothingHidden.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.View, fixture.LegacySchema, "fn_peek"), Token));

        query.Refusal.Should().Be(DatabaseRefusal.None, "the query names the function, not what it reads: " + query.Reason);

        await using BrowserHost star = fixture.Host(static options =>
        {
            options.IsDocumentTypeVisible = null;
            options.BrowsableSchemas.Add("*");
        });

        DatabaseRowAccessResult open = await star.AccessAsync(x =>
            x.RequireRowAccessAsync(BrowserHost.Scope, fixture.LegacySchema, "fn_peek", cancellationToken: Token));

        open.Allowed.Should().BeTrue("with nothing hidden and nothing the role can read withheld, a function has nothing to hide: " + open.Reason);
    }

    // ---------------------------------------------------------------------------------------------
    // F7
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A hidden type Marten learns after the circuit opened is gone from the circuit's next list. Before
    /// that it is the documented limit - listed as Marten infrastructure by its <c>mt_</c> name, rows never
    /// read raw - and a view over its table is already refused, because hiding is configured and nobody
    /// declares that table.
    /// </summary>
    [PostgresFact]
    public async Task A_hidden_type_learned_after_the_circuit_opened_is_absent_from_its_next_list()
    {
        await using BrowserHost host = fixture.Host();
        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();

        IDatabaseObjectService objects = circuit.ServiceProvider.GetRequiredService<IDatabaseObjectService>();
        var tables = new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.DocumentSchema);

        DatabaseObjectList before = await objects.ListAsync(BrowserHost.Scope, tables, Token);

        DatabaseRelationSummary unknown = before.Items.Cast<DatabaseRelationSummary>().Single(x => x.Name == fixture.LateTable);
        unknown.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        unknown.Rows.Refusal.Should().Be(DatabaseRefusal.MartenOwned);

        DatabaseObjectDetail latePeek = await objects.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "late_peek", Token);
        latePeek.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency,
            "a document table nobody declares may be a hidden type Marten has not learned yet");

        // A session in this process touches the type for the first time.
        await using (IDocumentSession session = host.Store.LightweightSession())
        {
            session.Store(new BrowserLateSecret { Id = Guid.NewGuid() });
            await session.SaveChangesAsync(Token);
        }

        DatabaseObjectList after = await objects.ListAsync(BrowserHost.Scope, tables, Token);

        after.Items.Select(static x => x.Name).Should().NotContain(fixture.LateTable,
            "the circuit's classification was read again once Marten knew one more type");

        (await objects.GetObjectAsync(BrowserHost.Scope, fixture.DocumentSchema, fixture.LateTable, Token))
            .Refusal.Should().Be(DatabaseRefusal.NotFound);
    }
}
