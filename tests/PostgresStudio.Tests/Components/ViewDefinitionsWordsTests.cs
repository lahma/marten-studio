using Bunit;

using MartenStudio.Tests.Support;

using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// Issue #22 in Postgres Studio's own words: <c>ViewDefinitions</c> shows the definitions of the schemas
/// <c>BrowsableSchemas</c> names exactly - which are the data source's own, so their structure was already free - and no
/// row; <c>BrowseDatabase</c> keeps the rows and implies it. Every sentence a refusal says names both ways in, and
/// none of it is a Marten word.
/// </summary>
public class ViewDefinitionsWordsTests
{
    private static readonly StudioText Words = PostgresStudioText.Instance;

    private static readonly DatabasePolicyAnswer DefinitionsPass = new(true, true, DatabaseRefusal.None);

    /// <summary>The data source's own schemas are the ones named exactly; <c>"*"</c> admits the rest.</summary>
    private static readonly string[] Entries = ["app", "*"];

    private static readonly CatalogSchema[] Live =
    [
        new("information_schema", true, false),
        new("pg_catalog", true, false),
        new("app", true, false),
        new("archive", true, false),
    ];

    [Fact]
    public void A_plain_data_source_reads_a_definition_of_its_own_schemas_with_ViewDefinitions_and_no_row()
    {
        DatabaseGate gate = Gate(browse: false, authorized: null, DefinitionsPass);

        gate.IsOpen.Should().BeFalse();
        gate.DefinitionsOpen.Should().BeTrue();
        gate.VisibleSchemas.Should().Equal("app");
        gate.DefinitionAccess("app", new DatabaseObjectOwnership(DatabaseObjectOwner.Other)).Allowed.Should().BeTrue();

        DatabaseRowAccess rows = gate.DataAccess("app");
        rows.Allowed.Should().BeFalse();
        rows.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        rows.Reason.Should().Be(Words.BrowseCapabilityDenial)
            .And.Contain("PostgresStudioOptions.Capabilities.BrowseDatabase");

        DatabaseRowAccess archive = gate.DefinitionAccess("archive", new DatabaseObjectOwnership(DatabaseObjectOwner.Other));
        archive.Allowed.Should().BeFalse("only \"*\" admits it, and \"*\" is BrowseDatabase's");
        archive.Reason.Should().Contain("only through \"*\"")
            .And.Contain("PostgresStudioOptions.Capabilities.BrowseDatabase")
            .And.Contain("PostgresStudioOptions.Capabilities.ViewDefinitions");

        // An object named like a Marten store's is the Marten tables' rule: its definition is BrowseDatabase's, as it was.
        DatabaseRowAccess marten = gate.DefinitionAccess("app", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure));
        marten.Allowed.Should().BeFalse();
        marten.Reason.Should().Contain("PostgresStudioOptions.Capabilities.BrowseDatabase");
    }

    [Fact]
    public void BrowseDatabase_reads_every_schema_it_admits_as_it_always_did()
    {
        DatabaseGate gate = Gate(browse: true, authorized: true, definitions: null);

        gate.VisibleSchemas.Should().Equal("app", "archive");
        gate.DefinitionAccess("archive", new DatabaseObjectOwnership(DatabaseObjectOwner.Other)).Allowed.Should().BeTrue();
        gate.DataAccess("archive").Allowed.Should().BeTrue();
    }

    [Fact]
    public void Every_refusal_of_a_definition_names_both_ways_in_in_Postgres_Studios_words()
    {
        DatabaseGate off = Gate(browse: false, authorized: null, definitions: null);
        off.State.DefinitionDenial.Should().Be(Words.DefinitionCapabilityDenial)
            .And.Contain("PostgresStudioOptions.Capabilities.ViewDefinitions")
            .And.Contain("PostgresStudioOptions.Capabilities.BrowseDatabase");
        off.State.Denial.Should().Be(Words.BrowseCapabilityDenial).And.NotContain("definitions",
            "the rows' sentence is about rows: a definition has a sentence of its own");

        Gate(browse: true, authorized: true, DefinitionsPass, readOnly: true).State.DefinitionDenial.Should().Be(Words.DefinitionReadOnlyDenial)
            .And.Contain("PostgresStudioOptions.Capabilities.ViewDefinitions and PostgresStudioOptions.Capabilities.BrowseDatabase off");

        Gate(browse: true, authorized: false, new DatabasePolicyAnswer(true, false, DatabaseRefusal.WritePolicy)).State.DefinitionDenial
            .Should().Be(Words.DefinitionWritePolicyDenialFor("ViewDefinitions and BrowseDatabase"))
            .And.Contain("PostgresStudioOptions.WriteAuthorizationPolicy").And.Contain("SourceAuthorizationPolicy")
            .And.Contain("this data source and database");

        Gate(browse: false, authorized: null, new DatabasePolicyAnswer(true, false, DatabaseRefusal.WritePolicy)).State.DefinitionDenial
            .Should().Contain("refused ViewDefinitions for this data source");

        foreach (string sentence in new[]
                 {
                     Words.DefinitionReadOnlyDenial, Words.DefinitionCapabilityDenial, Words.DefinitionWritePolicyDenialFor("ViewDefinitions"),
                     DatabaseGate.DefinitionSchemaDenial("archive", Entries, Words),
                 })
        {
            sentence.Should().NotContain("Marten").And.NotContain("MartenStudioOptions").And.NotContain("store");
        }
    }

    [Fact]
    public async Task The_notice_with_nothing_on_says_what_each_capability_would_add_and_names_no_Marten_option()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Navigate("postgres/database");

        DatabaseAccessState access = Gate(browse: false, authorized: null, definitions: null).State;
        context.DatabaseObjects.Overview = FakeDatabaseObjects.Overview() with { Access = access };
        context.DatabaseObjects.List = FakeDatabaseObjects.List(new DatabaseObjectQuery(DatabaseObjectCategory.Tables)) with { Access = access };

        var page = context.Render<DatabaseObjects>();
        page.WaitForAssertion(() => page.FindAll(".ms-db-access").Should().ContainSingle());

        string notice = page.Find(".ms-db-access").TextContent;
        notice.Should().Contain(Words.BrowseCapabilityDenial).And.Contain(Words.DefinitionCapabilityDenial);
        notice.Should().Contain("Capabilities.ViewDefinitions").And.Contain("Capabilities.BrowseDatabase");
        notice.Should().NotContain("MartenStudioOptions").And.NotContain("store");
    }

    [Fact]
    public async Task The_notice_with_only_ViewDefinitions_says_definitions_are_shown_and_rows_need_BrowseDatabase()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Navigate("postgres/database");

        DatabaseAccessState access = Gate(browse: false, authorized: null, DefinitionsPass).State;
        access.DefinitionsOpen.Should().BeTrue();

        context.DatabaseObjects.Overview = FakeDatabaseObjects.Overview() with { Access = access };
        context.DatabaseObjects.List = FakeDatabaseObjects.List(new DatabaseObjectQuery(DatabaseObjectCategory.Tables)) with { Access = access };

        var page = context.Render<DatabaseObjects>();
        page.WaitForAssertion(() => page.FindAll(".ms-db-access").Should().ContainSingle());

        var notice = page.Find(".ms-db-access");
        notice.QuerySelector(".ms-db-access-title")!.TextContent.Trim().Should().Be("Definitions are shown; browsing rows is not enabled.");
        notice.TextContent.Should().Contain("PostgresStudioOptions.Capabilities.BrowseDatabase")
            .And.Contain("Capabilities.ViewDefinitions")
            .And.Contain("names exactly are shown")
            .And.NotContain("MartenStudioOptions");
    }

    private static DatabaseGate Gate(bool browse, bool? authorized, DatabasePolicyAnswer? definitions, bool readOnly = false) =>
        new(
            PostgresDatabaseDeclarationsSource.OwnSchemas(Entries),
            Live,
            Entries,
            browse,
            readOnly,
            authorized,
            new DatabaseObjectClassifier([], hidesDocumentTypes: false),
            text: Words,
            definitions: definitions);
}
