using Bunit;

using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using SchemaPage = MartenStudio.Components.Pages.Schema.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// DB-7: the Schema screen's Tables and Functions tabs, drawn with the database browser's classification
/// and gate. What the service decides - who owns a table, whether a partition count is withheld, whether a
/// body may be read - is given outright here; what is tested is that the tabs draw it and never go round it.
/// </summary>
public class SchemaAlignmentPageTests
{
    private const string TenantPartitioned = "mt_doc_invoice";

    // ------------------------------------------------------------------------------------------------
    // Tables
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_Quartz_table_in_the_event_schema_is_drawn_as_not_Marten_with_the_hint_and_never_as_the_event_store()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = Tables(Table("studio_events", "qrtz_locks",
            new DatabaseObjectOwnership(DatabaseObjectOwner.Other, RecognisedAs: "Quartz.NET")));

        var page = RenderTab(context, "tables");

        var owner = page.Find(".ms-schema-owner");
        owner.GetAttribute("data-owner").Should().Be(nameof(DatabaseObjectOwner.Other));
        owner.TextContent.Should().Contain("not Marten").And.Contain("Quartz.NET, recognised by name");
        owner.TextContent.Should().NotContain("event store");
    }

    [Fact]
    public void Each_kind_of_Marten_table_gets_its_own_badge()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = Tables(
            Table("studio", "mt_hilo", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure, "default")),
            Table("reporting", "flat_orders", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenProjectionOrExtended, "default")),
            Table("studio_events", "mt_events", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenEventStore, "default")));

        var page = RenderTab(context, "tables");

        Owners(page).Should().Equal(
            ("studio.mt_hilo", "Marten infrastructure"),
            ("reporting.flat_orders", "Marten-managed"),
            ("studio_events.mt_events", "event store"));
    }

    [Fact]
    public void The_event_stores_badge_links_to_Streams_with_the_scope()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = Tables(Table("studio_events", "mt_events",
            new DatabaseObjectOwnership(DatabaseObjectOwner.MartenEventStore, "default")));

        var page = RenderTab(context, "tables");

        string href = page.Find(".ms-schema-owner a.ms-index-flag").GetAttribute("href") ?? string.Empty;
        // Relative, like every studio link: it resolves against the studio-rooted <base href>, which is
        // what keeps a custom mount path working.
        href.Should().StartWith("events/streams?").And.Contain("store=default").And.Contain("db=localhost.marten");
    }

    /// <summary>Another store's table is labelled, and never linked into this store's Documents or Streams.</summary>
    [Fact]
    public void Another_stores_Marten_tables_are_labelled_without_a_link()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = Tables(
            Table("other_db", "mt_doc_dbinvoice", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "IOther", "dbinvoice"), ours: false),
            Table("other_db", "mt_events", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenEventStore, "IOther"), ours: false));

        var page = RenderTab(context, "tables");

        page.FindAll(".ms-schema-owner a").Should().BeEmpty();
        Owners(page).Select(x => x.Owner).Should().Equal("document collection", "event store");
    }

    /// <summary>
    /// Every table name opens the database browser's object detail, with the scope - the route and the
    /// parameter names of plan §4 (<c>/marten/database/object?schema=&amp;name=</c>), written relative to the
    /// studio's base like every other link.
    /// </summary>
    [Fact]
    public void Every_table_name_links_to_the_object_detail_with_the_scope()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = Tables(
            Table("studio", "mt_doc_customer", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "default", "customer"), alias: "customer"),
            Table("studio_events", "qrtz_locks", new DatabaseObjectOwnership(DatabaseObjectOwner.Other, RecognisedAs: "Quartz.NET")));

        var page = RenderTab(context, "tables");

        var links = page.FindAll(".ms-schema-object-link");
        links.Should().HaveCount(2);

        string first = links[0].GetAttribute("href") ?? string.Empty;
        first.Should().StartWith("database/object?", "relative, so it resolves against the studio-rooted <base href>");
        first.Should().Contain("schema=studio&").And.Contain("name=mt_doc_customer");
        first.Should().Contain("store=default").And.Contain("db=localhost.marten");

        links[1].GetAttribute("href").Should().Contain("schema=studio_events").And.Contain("name=qrtz_locks");
        links[0].TextContent.Should().Be("studio.mt_doc_customer");
    }

    /// <summary>
    /// F9: with the gate shut the service withholds the count, and the tab says "partitioned" and why -
    /// never a number.
    /// </summary>
    [Fact]
    public void A_partitioned_table_says_partitioned_without_a_number_when_the_count_is_withheld()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = Tables(
            [Table("studio", TenantPartitioned, new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "default", "invoice"),
                alias: "invoice", partitioned: true, partitions: null)],
            withheld: SchemaTableAssembler.PartitionCountsWithheld(false, false, null));

        var page = RenderTab(context, "tables");

        page.Find(".ms-schema-partitions").TextContent.Trim().Should().Be("partitioned");
        page.Find(".ms-schema-partition-note").TextContent.Should()
            .Contain("MartenStudioOptions.Capabilities.BrowseDatabase")
            .And.Contain("number of tenants");
        page.FindAll("tbody tr").Should().ContainSingle("a partitioned table is one row, whatever its partitions");
    }

    [Fact]
    public void A_partitioned_table_counts_its_partitions_when_the_gate_is_open()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = Tables(
            Table("studio", TenantPartitioned, new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "default", "invoice"),
                alias: "invoice", partitioned: true, partitions: 3));

        var page = RenderTab(context, "tables");

        page.Find(".ms-schema-partitions").TextContent.Trim().Should().Be("3 partitions");
        page.Find(".ms-schema-partition-note").TextContent.Should().NotContain("BrowseDatabase");
        page.FindAll("tbody tr").Should().ContainSingle();
    }

    [Fact]
    public void An_ordinary_table_says_nothing_about_partitions()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = Tables(Table("studio", "host_audit_log", new DatabaseObjectOwnership(DatabaseObjectOwner.Other)));

        var page = RenderTab(context, "tables");

        page.FindAll(".ms-schema-partitions").Should().BeEmpty();
        page.FindAll(".ms-schema-partition-note").Should().BeEmpty();
    }

    [Fact]
    public void The_Tables_tab_with_every_owner_still_scrolls_rather_than_being_clipped()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = Tables(
            Table("studio", "mt_doc_customer", new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "default", "customer"), alias: "customer"),
            Table("studio_events", "qrtz_locks", new DatabaseObjectOwnership(DatabaseObjectOwner.Other, RecognisedAs: "Quartz.NET")),
            Table("studio", TenantPartitioned, new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "default", "invoice"),
                alias: "invoice", partitioned: true, partitions: 12));

        RenderTab(context, "tables").ShouldPutEveryTableInALabelledScrollRegion();
    }

    // ------------------------------------------------------------------------------------------------
    // Functions
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// This tab used to read <c>pg_get_functiondef</c> for every function on arrival. Nothing is read
    /// until a row is opened now.
    /// </summary>
    [Fact]
    public void Opening_the_Functions_tab_reads_no_body()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = Functions(MartenFunction("mt_jsonb_patch", "jsonb, jsonb"), HostFunction("host_touch", refused: false));

        RenderTab(context, "functions");

        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty();
    }

    [Fact]
    public async Task Opening_a_row_reads_that_one_body_through_the_database_browsers_definition_read()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = Functions(MartenFunction("mt_jsonb_patch", "jsonb, jsonb"), MartenFunction("mt_jsonb_copy", "jsonb, text[]"));
        context.DatabaseObjects.Definition = new DatabaseObjectDefinition(
            new DatabaseObjectRef(DatabaseObjectKind.Function, "studio", "mt_jsonb_patch", "jsonb, jsonb"),
            "CREATE OR REPLACE FUNCTION studio.mt_jsonb_patch(jsonb, jsonb) RETURNS jsonb AS $function$ select 1 $function$",
            null,
            DatabaseRefusal.None,
            null);

        var page = RenderTab(context, "functions");

        await Row(page, "studio.mt_jsonb_patch").TriggerEventAsync("ontoggle", EventArgs.Empty);

        DatabaseObjectRef asked = context.DatabaseObjects.DefinitionsAsked.Should().ContainSingle().Subject;
        asked.Should().Be(new DatabaseObjectRef(DatabaseObjectKind.Function, "studio", "mt_jsonb_patch", "jsonb, jsonb"),
            "the overload is looked up by its identity arguments");
        Row(page, "studio.mt_jsonb_patch").TextContent.Should().Contain("CREATE OR REPLACE FUNCTION");
    }

    [Fact]
    public async Task Closing_and_opening_a_row_again_does_not_read_its_body_twice()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = Functions(MartenFunction("mt_jsonb_patch", "jsonb, jsonb"));

        var page = RenderTab(context, "functions");

        await Row(page, "studio.mt_jsonb_patch").TriggerEventAsync("ontoggle", EventArgs.Empty);
        await Row(page, "studio.mt_jsonb_patch").TriggerEventAsync("ontoggle", EventArgs.Empty);
        await Row(page, "studio.mt_jsonb_patch").TriggerEventAsync("ontoggle", EventArgs.Empty);

        context.DatabaseObjects.DefinitionsAsked.Should().ContainSingle();
    }

    /// <summary>
    /// The gate is shut for the host's own function: the row says so and names the option, and opening
    /// it asks the service nothing - there is nothing to ask for that the service would not refuse.
    /// </summary>
    [Fact]
    public async Task A_host_function_whose_body_is_refused_says_why_and_is_never_asked_for()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = Functions(HostFunction("host_touch", refused: true));

        var page = RenderTab(context, "functions");

        var row = Row(page, "studio.host_touch");
        row.QuerySelector(".ms-schema-owner-other")!.TextContent.Should().Be("not Marten");
        row.QuerySelector(".ms-function-withheld")!.TextContent.Should().Be("body withheld");
        row.QuerySelector(".ms-function-refusal")!.TextContent.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        await row.TriggerEventAsync("ontoggle", EventArgs.Empty);

        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty();
        page.Find(".ms-schema-summary").TextContent.Should().Contain("1 body withheld");
    }

    [Fact]
    public async Task A_host_function_past_the_gate_is_read_like_Martens_own()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = Functions(HostFunction("host_touch", refused: false));

        var page = RenderTab(context, "functions");

        Row(page, "studio.host_touch").QuerySelectorAll(".ms-function-withheld").Should().BeEmpty();

        await Row(page, "studio.host_touch").TriggerEventAsync("ontoggle", EventArgs.Empty);

        context.DatabaseObjects.DefinitionsAsked.Should().ContainSingle()
            .Which.Name.Should().Be("host_touch");
        Row(page, "studio.host_touch").QuerySelectorAll(".ms-sql-text").Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_procedure_says_it_is_one_and_its_body_is_read()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = Functions(new FunctionInfo("studio", "host_proc", string.Empty, DatabaseObjectKind.Procedure, false,
            new DatabaseObjectOwnership(DatabaseObjectOwner.Other), true, null));

        var page = RenderTab(context, "functions");

        Row(page, "studio.host_proc").QuerySelector(".ms-function-kind")!.TextContent.Should().Be("procedure");

        await Row(page, "studio.host_proc").TriggerEventAsync("ontoggle", EventArgs.Empty);

        context.DatabaseObjects.DefinitionsAsked.Should().ContainSingle()
            .Which.Kind.Should().Be(DatabaseObjectKind.Procedure);
    }

    /// <summary>An aggregate has no body Postgres will print: listed, labelled, never asked for, never "withheld".</summary>
    [Fact]
    public async Task An_aggregate_is_listed_without_a_body_and_never_asked_for_one()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = Functions(new FunctionInfo("studio", "host_sum", "integer", DatabaseObjectKind.Aggregate, false,
            new DatabaseObjectOwnership(DatabaseObjectOwner.Other), false, DatabaseObjectService.AggregateHasNoBody));

        var page = RenderTab(context, "functions");

        var row = Row(page, "studio.host_sum");
        row.QuerySelector(".ms-function-kind")!.TextContent.Should().Be("aggregate");
        row.QuerySelectorAll(".ms-function-withheld").Should().BeEmpty("nothing is withheld - there is nothing to show");
        row.QuerySelector(".ms-function-refusal")!.TextContent.Should().Contain("aggregate");

        await row.TriggerEventAsync("ontoggle", EventArgs.Empty);

        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty();
    }

    /// <summary>The service is the enforcement; whatever it answers is what the row says.</summary>
    [Fact]
    public async Task A_refusal_from_the_definition_read_is_drawn_as_the_rows_body()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = Functions(HostFunction("host_touch", refused: false));
        context.DatabaseObjects.Definition = DatabaseObjectDefinition.Unavailable(
            new DatabaseObjectRef(DatabaseObjectKind.Function, "studio", "host_touch", string.Empty),
            DatabaseRefusal.SchemaNotBrowsable,
            "Schema 'studio' is not one MartenStudioOptions.BrowsableSchemas admits.");

        var page = RenderTab(context, "functions");
        await Row(page, "studio.host_touch").TriggerEventAsync("ontoggle", EventArgs.Empty);

        Row(page, "studio.host_touch").QuerySelector(".ms-function-refusal")!.TextContent
            .Should().Contain("BrowsableSchemas");
    }

    [Fact]
    public async Task A_failed_read_is_drawn_with_a_retry_rather_than_escaping_the_tab()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = Functions(MartenFunction("mt_jsonb_patch", "jsonb, jsonb"));
        context.DatabaseObjects.Failure = new InvalidOperationException("the catalog went away");

        var page = RenderTab(context, "functions");
        await Row(page, "studio.mt_jsonb_patch").TriggerEventAsync("ontoggle", EventArgs.Empty);

        Row(page, "studio.mt_jsonb_patch").QuerySelector(".ms-error-alert")!.TextContent.Should().Contain("the catalog went away");
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private static IRenderedComponent<SchemaPage> RenderTab(StudioComponentContext context, string tab)
    {
        context.Navigate("/marten/schema?tab=" + tab);
        return context.Render<SchemaPage>();
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<SchemaPage> page, string qualifiedName) =>
        page.FindAll("details.ms-function")
            .Single(x => x.QuerySelector(".ms-function-summary")!.TextContent.Contains(qualifiedName, StringComparison.Ordinal));

    private static List<(string Table, string Owner)> Owners(IRenderedComponent<SchemaPage> page) =>
        [.. page.FindAll("tbody tr").Select(row => (
            row.QuerySelector(".ms-schema-object-link")!.TextContent.Trim(),
            row.QuerySelector(".ms-schema-owner")!.TextContent.Trim()))];

    private static SchemaTables Tables(params TableStats[] tables) => Tables(tables, withheld: null);

    private static SchemaTables Tables(IReadOnlyList<TableStats> tables, string? withheld) =>
        new(tables, [.. tables.Select(x => x.Schema).Distinct()], 1048576, null, withheld);

    private static TableStats Table(
        string schema,
        string table,
        DatabaseObjectOwnership ownership,
        string? alias = null,
        bool ours = true,
        bool partitioned = false,
        int? partitions = null) =>
        new(schema, table, 98304, 65536, 32768, 1200, 1200, 3, 4, 500, null, null, alias, alias is null ? null : "Thing",
            ownership, ours, partitioned, partitions);

    private static SchemaFunctions Functions(params FunctionInfo[] functions) => new(functions, null);

    private static FunctionInfo MartenFunction(string name, string arguments) =>
        new("studio", name, arguments, DatabaseObjectKind.Function, true,
            new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure), true, null);

    private static FunctionInfo HostFunction(string name, bool refused) =>
        new("studio", name, string.Empty, DatabaseObjectKind.Function, false,
            new DatabaseObjectOwnership(DatabaseObjectOwner.Other),
            !refused,
            refused ? DatabaseGate.CapabilityDenial : null);

    private static StudioComponentContext NewContext(out FakeSchemaDataService schema)
    {
        var fake = new FakeSchemaDataService();
        var context = new StudioComponentContext(services => services.AddSingleton<ISchemaDataService>(fake));
        context.WithStores("default");

        schema = fake;
        return context;
    }
}
