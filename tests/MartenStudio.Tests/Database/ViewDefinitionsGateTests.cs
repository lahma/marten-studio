using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Issue #22, the gate's half: <c>ViewDefinitions</c> is a second way into a definition - and into the structure around
/// it - for the schemas <c>BrowsableSchemas</c> names exactly, with no row of any, while <c>BrowseDatabase</c> keeps the
/// rows and implies it. No database: the gate is pure, and these build it by hand as the object browser's gate
/// (<see cref="DatabaseAccess.ObjectGateAsync" />) does, from the two policy answers.
/// </summary>
public class ViewDefinitionsGateTests
{
    private static readonly DatabaseObjectOwnership Other = new(DatabaseObjectOwner.Other);
    private static readonly DatabaseObjectOwnership Marten = new(DatabaseObjectOwner.MartenInfrastructure, "default");

    private static readonly DatabasePolicyAnswer DefinitionsPass = new(true, true, DatabaseRefusal.None);
    private static readonly DatabasePolicyAnswer DefinitionsOff = new(false, null, DatabaseRefusal.None);

    // ---------------------------------------------------------------------------------------------
    // What ViewDefinitions alone opens
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ViewDefinitions_alone_shows_the_exactly_named_schemas_structure_and_definitions_and_no_row()
    {
        DatabaseGate gate = Gate(browse: false, browseAuthorized: null, ["quartz", "legacy"], DefinitionsPass);

        gate.IsOpen.Should().BeFalse("BrowseDatabase is not on: no row, and nothing that is only its");
        gate.DefinitionsOpen.Should().BeTrue();

        gate.VisibleSchemas.Should().Equal(DocumentSchema, EventSchema, ReportingSchema, "legacy", "quartz");
        gate.CanSeeStructure("quartz").Should().BeTrue();
        gate.CanSeeStructure("secrets").Should().BeFalse("it is not listed: withheld, and counted");
        gate.WithheldSchemaCount.Should().Be(1);

        gate.DefinitionAccess("quartz", Other).Allowed.Should().BeTrue();
        gate.DefinitionAccess("legacy", Other).Allowed.Should().BeTrue();

        DatabaseRowAccess rows = gate.DataAccess("quartz");
        rows.Allowed.Should().BeFalse("rows are BrowseDatabase's alone");
        rows.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        rows.Reason.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        DatabaseRowAccess table = gate.RowsFor(Relation("quartz", "qrtz_triggers"), Other, dependsOnHidden: false);
        table.Allowed.Should().BeFalse();
        table.Reason.Should().Be(StudioText.Marten.BrowseCapabilityDenial, "a table's rows are refused naming BrowseDatabase, in the sentence 0.4 wrote");

        DatabaseRowAccess view = gate.RowsFor(Relation("quartz", "job_summary", kind: "v"), Other, viewRefusal: null);
        view.Allowed.Should().BeFalse("a view's rows are rows");
        view.Reason.Should().Be(StudioText.Marten.BrowseCapabilityDenial);

        DatabaseAccessState state = gate.State;
        state.IsOpen.Should().BeFalse();
        state.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, "the rows' gate is shut, and says so in its own sentence");
        state.BrowsableSchemas.Should().BeEmpty("the schemas whose rows may be read");
        state.DefinitionsOpen.Should().BeTrue();
        state.DefinitionRefusal.Should().Be(DatabaseRefusal.None);
        state.DefinitionDenial.Should().BeNull();
        state.DefinitionSchemas.Should().Equal("legacy", "quartz");
    }

    [Fact]
    public void A_schema_only_the_star_admits_stays_withheld_and_its_definitions_still_need_BrowseDatabase()
    {
        DatabaseGate gate = Gate(browse: false, browseAuthorized: null, ["quartz", "*"], DefinitionsPass);

        gate.CanSeeStructure("quartz").Should().BeTrue("named exactly");
        gate.CanSeeStructure("legacy").Should().BeFalse("only \"*\" admits it, and \"*\" is a statement about the database");
        gate.CanSeeStructure("secrets").Should().BeFalse();
        gate.WithheldSchemas.Should().BeEquivalentTo("legacy", "secrets");
        gate.VisibleSchemas.Should().Equal(DocumentSchema, EventSchema, ReportingSchema, "quartz");

        DatabaseRowAccess starred = gate.DefinitionAccess("legacy", Other);
        starred.Allowed.Should().BeFalse();
        starred.Refusal.Should().Be(DatabaseRefusal.SchemaNotBrowsable);
        starred.Reason.Should().Contain("only through \"*\"")
            .And.Contain("MartenStudioOptions.Capabilities.BrowseDatabase")
            .And.Contain("MartenStudioOptions.Capabilities.ViewDefinitions");
    }

    [Fact]
    public void The_withheld_masking_still_applies_to_every_definition_ViewDefinitions_shows()
    {
        DatabaseGate gate = Gate(browse: false, browseAuthorized: null, ["quartz", "*"], DefinitionsPass);

        gate.Redact("select * from quartz.qrtz_triggers join legacy.orders using (id)").Should()
            .Contain("quartz.qrtz_triggers", "a schema this visitor may see is not masked")
            .And.NotContain("legacy.orders")
            .And.Contain(WithheldNames.Token, "the one only \"*\" admits is");

        // A view that reaches into such a schema is refused its query, as it is for any visitor short of that schema.
        DatabaseRowAccess refused = gate.ViewRefusal(
            [new CatalogViewDependency("quartz", "job_summary", "legacy", "orders", "r", 1)],
            []) ?? throw new InvalidOperationException("The view reads a schema this visitor may not see.");

        refused.Allowed.Should().BeFalse();
        refused.Refusal.Should().Be(DatabaseRefusal.WithheldDependency);
    }

    [Fact]
    public void An_object_Marten_owns_is_the_exemption_in_the_stores_own_schema_and_BrowseDatabase_s_anywhere_else()
    {
        DatabaseGate gate = Gate(browse: false, browseAuthorized: null, ["quartz"], DefinitionsPass);

        gate.DefinitionAccess(DocumentSchema, Marten).Allowed.Should().BeTrue("the Schema screen shows it without a capability, as it always did");

        DatabaseRowAccess elsewhere = gate.DefinitionAccess("quartz", Marten);
        elsewhere.Allowed.Should().BeFalse("today's rule: Marten's own objects outside the store's schemas follow BrowseDatabase");
        elsewhere.Refusal.Should().Be(DatabaseRefusal.MartenOwned);
        elsewhere.Reason.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        Gate(browse: true, browseAuthorized: true, ["quartz"], DefinitionsOff).DefinitionAccess("quartz", Marten).Allowed
            .Should().BeTrue("BrowseDatabase reads it, as it did before ViewDefinitions existed");
    }

    [Fact]
    public void A_sequences_current_value_is_BrowseDatabases_alone_and_refused_to_ViewDefinitions_naming_it()
    {
        DatabaseGate definitionsOnly = Gate(browse: false, browseAuthorized: null, ["quartz"], DefinitionsPass);

        DatabaseRowAccess refused = definitionsOnly.SequenceValueAccess("quartz", Other);
        refused.Allowed.Should().BeFalse("a value says how many rows a table has had: data, not a definition");
        refused.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        refused.Reason.Should().Be(StudioText.Marten.BrowseCapabilityDenial).And.Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        definitionsOnly.DefinitionAccess("quartz", Other).Allowed.Should().BeTrue("while its definition is shown, as the rest of the schema's");

        definitionsOnly.SequenceValueAccess(DocumentSchema, Marten).Allowed.Should().BeTrue(
            "Marten's own sequences in the store's own schemas are the Schema screen's, as they were");

        DatabaseGate browse = Gate(browse: true, browseAuthorized: true, ["quartz"], DefinitionsPass);
        browse.SequenceValueAccess("quartz", Other).Allowed.Should().BeTrue("BrowseDatabase reads it, as it always did");
        browse.SequenceValueAccess("legacy", Other).Allowed.Should().BeFalse("in a schema BrowsableSchemas lists, as it always was");
    }

    // ---------------------------------------------------------------------------------------------
    // BrowseDatabase implies it
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void BrowseDatabase_keeps_the_rows_and_implies_the_definitions_exactly_as_before()
    {
        DatabaseGate before = Gate(browse: true, browseAuthorized: true, ["quartz", "*"], definitions: null);
        DatabaseGate withBoth = Gate(browse: true, browseAuthorized: true, ["quartz", "*"], DefinitionsPass);
        DatabaseGate withOffAnswer = Gate(browse: true, browseAuthorized: true, ["quartz", "*"], DefinitionsOff);

        foreach (DatabaseGate gate in new[] { before, withBoth, withOffAnswer })
        {
            gate.IsOpen.Should().BeTrue();
            gate.DefinitionsOpen.Should().BeTrue();
            gate.VisibleSchemas.Should().Equal(before.VisibleSchemas, "ViewDefinitions adds nothing when BrowseDatabase is open");
            gate.State.DefinitionSchemas.Should().Equal(gate.State.BrowsableSchemas);
            gate.State.BrowsableSchemas.Should().Contain(["legacy", "quartz", "secrets"]);

            foreach (string schema in new[] { "quartz", "legacy", "secrets" })
            {
                gate.DefinitionAccess(schema, Other).Allowed.Should().BeTrue(schema + ": \"*\" admits it to BrowseDatabase");
                gate.DataAccess(schema).Allowed.Should().BeTrue(schema);
            }
        }
    }

    [Fact]
    public void BrowseDatabase_refused_by_the_write_policy_leaves_ViewDefinitions_the_exactly_named_schemas()
    {
        DatabaseGate gate = Gate(
            browse: true,
            browseAuthorized: false,
            ["quartz", "*"],
            DefinitionsPass,
            policyRefusal: DatabaseRefusal.WritePolicy);

        gate.IsOpen.Should().BeFalse();
        gate.DefinitionsOpen.Should().BeTrue("each capability is asked of the write policy on its own");
        gate.DefinitionAccess("quartz", Other).Allowed.Should().BeTrue();
        gate.DefinitionAccess("legacy", Other).Allowed.Should().BeFalse();
        gate.DataAccess("quartz").Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        gate.DataAccess("quartz").Reason.Should().Be(StudioText.Marten.BrowseWritePolicyDenial);
    }

    // ---------------------------------------------------------------------------------------------
    // Shut, and the sentence that names both ways in
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void With_neither_capability_on_a_definition_is_refused_in_the_sentence_that_names_both_ways_in()
    {
        DatabaseGate gate = Gate(browse: false, browseAuthorized: null, ["quartz"], DefinitionsOff);

        DatabaseRowAccess access = gate.DefinitionAccess("quartz", Other);
        access.Allowed.Should().BeFalse();
        access.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        access.Reason.Should().Be(StudioText.Marten.DefinitionCapabilityDenial)
            .And.Contain("MartenStudioOptions.Capabilities.ViewDefinitions")
            .And.Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        gate.State.DefinitionRefusal.Should().Be(DatabaseRefusal.CapabilityOff);
        gate.State.DefinitionDenial.Should().Be(StudioText.Marten.DefinitionCapabilityDenial);
        gate.State.Denial.Should().Be(StudioText.Marten.BrowseCapabilityDenial, "the rows' sentence is 0.4's, unchanged");
        gate.VisibleSchemas.Should().Equal(DocumentSchema, EventSchema, ReportingSchema); // no schema beyond the store's own
    }

    [Fact]
    public void ReadOnly_turns_both_ways_in_off_and_says_so()
    {
        DatabaseGate gate = Gate(browse: true, browseAuthorized: true, ["quartz"], DefinitionsPass, readOnly: true);

        gate.DefinitionsOpen.Should().BeFalse();
        gate.DefinitionAccess("quartz", Other).Allowed.Should().BeFalse();
        gate.State.DefinitionRefusal.Should().Be(DatabaseRefusal.ReadOnly);
        gate.State.DefinitionDenial.Should().Be(StudioText.Marten.DefinitionReadOnlyDenial)
            .And.Contain("MartenStudioOptions.Capabilities.ViewDefinitions")
            .And.Contain("MartenStudioOptions.Capabilities.BrowseDatabase");
        gate.State.Denial.Should().Be(StudioText.Marten.BrowseReadOnlyDenial, "the rows' sentence is 0.4's, unchanged");
    }

    [Theory]
    [InlineData(false, true, "ViewDefinitions")]
    [InlineData(true, false, "BrowseDatabase")]
    [InlineData(true, true, "ViewDefinitions and BrowseDatabase")]
    public void A_write_policy_that_refuses_names_each_capability_it_was_asked_about_and_no_other(
        bool browse,
        bool definitions,
        string refused)
    {
        DatabaseGate gate = Gate(
            browse,
            browse ? false : null,
            ["quartz"],
            definitions ? new DatabasePolicyAnswer(true, false, DatabaseRefusal.WritePolicy) : DefinitionsOff);

        gate.DefinitionsOpen.Should().BeFalse();

        DatabaseRowAccess access = gate.DefinitionAccess("quartz", Other);
        access.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        access.Reason.Should().Be(StudioText.Marten.DefinitionWritePolicyDenialFor(refused))
            .And.Contain("refused " + refused + " for this store");
    }

    [Fact]
    public void A_store_policy_that_refuses_the_database_refuses_a_definition_in_its_own_sentence()
    {
        DatabaseGate gate = Gate(
            browse: false,
            browseAuthorized: null,
            ["quartz"],
            new DatabasePolicyAnswer(true, false, DatabaseRefusal.StorePolicy));

        DatabaseRowAccess access = gate.DefinitionAccess("quartz", Other);
        access.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        access.Reason.Should().Be(StudioText.Marten.BrowseStorePolicyDenial);
    }

    [Fact]
    public void A_gate_built_without_ViewDefinitions_answer_is_the_gate_every_other_screen_reads()
    {
        DatabaseGate gate = Gate(browse: false, browseAuthorized: null, ["quartz"], definitions: null);

        gate.DefinitionsOpen.Should().BeFalse();
        // ViewDefinitions widens the object browser's gate alone; Health, Storage, Maintenance and the rest read this one.
        gate.VisibleSchemas.Should().Equal(DocumentSchema, EventSchema, ReportingSchema);
        gate.WithheldSchemas.Should().Contain("quartz");
    }

    // ---------------------------------------------------------------------------------------------
    // The pages' own questions, answered the same way
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_overview_lists_the_definitions_schemas_and_flags_none_of_them_as_row_browsable()
    {
        DatabaseGate gate = Gate(browse: false, browseAuthorized: null, ["quartz", "legacy"], DefinitionsPass);

        DatabaseBrowserOverview overview = DatabaseObjectAssembler.Overview(gate, new CatalogSnapshot(
            new CatalogList<CatalogRelation>([Relation("quartz", "qrtz_triggers"), Relation("secrets", "keys")], false),
            CatalogList<CatalogRoutine>.Empty,
            CatalogList<CatalogTrigger>.Empty,
            CatalogList<CatalogSequence>.Empty,
            CatalogList<CatalogType>.Empty,
            CatalogList<CatalogForeignKey>.Empty,
            CatalogList<CatalogViewDependency>.Empty));

        overview.Schemas.Select(static x => x.Name).Should().Equal(DocumentSchema, EventSchema, ReportingSchema, "legacy", "quartz");
        overview.Schemas.Should().OnlyContain(static x => !x.RowsBrowsable);
        overview.Access.WithheldSchemaCount.Should().Be(1);
    }

    [Fact]
    public void A_routine_a_trigger_and_a_type_in_a_named_schema_are_offered_a_sequence_value_is_not_and_the_starred_schemas_are_absent()
    {
        DatabaseGate gate = Gate(browse: false, browseAuthorized: null, ["quartz", "*"], DefinitionsPass);

        CatalogSnapshot snapshot = new(
            new CatalogList<CatalogRelation>([Relation("quartz", "qrtz_triggers"), Relation("legacy", "orders")], false),
            new CatalogList<CatalogRoutine>(
            [
                new("quartz", "qrtz_cleanup", string.Empty, "f", "integer", "sql", "v", false, [], null),
                new("legacy", "recalculate", string.Empty, "f", "integer", "sql", "v", false, [], null),
            ],
                false),
            new CatalogList<CatalogTrigger>([new("quartz", "qrtz_triggers", "qrtz_touch", 23, "D", "quartz", "touch", false)], false),
            new CatalogList<CatalogSequence>(
                [new("quartz", "qrtz_seq", "bigint", 1, 1, 1, long.MaxValue, false, true, 5, null, null, null, null)],
                false),
            new CatalogList<CatalogType>([new("quartz", "job_state", "e", null, false, null, ["waiting"], [], [], null, 1, null)], false),
            CatalogList<CatalogForeignKey>.Empty,
            CatalogList<CatalogViewDependency>.Empty);

        DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Functions)
            .Cast<DatabaseRoutineSummary>().Select(static x => (x.Name, x.DefinitionAvailable))
            .Should().Equal([("qrtz_cleanup", true)], "legacy is only \"*\"'s: its routine is not listed at all");

        DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Triggers)
            .Cast<DatabaseTriggerSummary>().Should().ContainSingle().Which.DefinitionAvailable.Should().BeTrue();

        DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Sequences)
            .Cast<DatabaseSequenceSummary>().Should().ContainSingle().Which.CanReadValue.Should().BeFalse(
                "a sequence's value is data, not a definition: BrowseDatabase's alone");

        DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Types)
            .Cast<DatabaseTypeSummary>().Should().ContainSingle().Which.DefinitionAvailable.Should().BeTrue();

        DatabaseObjectAssembler.Summaries(gate, snapshot, DatabaseObjectCategory.Tables)
            .Cast<DatabaseRelationSummary>().Should().ContainSingle().Which.Rows.Allowed.Should().BeFalse("its rows are not");
    }

    // ---------------------------------------------------------------------------------------------

    private static DatabaseGate Gate(
        bool browse,
        bool? browseAuthorized,
        IReadOnlyList<string> entries,
        DatabasePolicyAnswer? definitions,
        bool readOnly = false,
        DatabaseRefusal policyRefusal = DatabaseRefusal.WritePolicy) =>
        new(
            StoreSchemas(),
            LiveSchemas(),
            entries,
            browse,
            readOnly,
            browseAuthorized,
            Classifier(),
            policyRefusal: policyRefusal,
            definitions: definitions);
}
