using Bunit;

using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using SchemaPage = MartenStudio.Components.Pages.Schema.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// DB-7-fix: the Schema screen draws what the service decided - a withheld script, a degraded
/// classification, a tenancy table's withheld figures, a read that gave up behind a lock - and never goes
/// round it. The service's own refusals are tested live; here they are given outright.
/// </summary>
public class SchemaScriptGatePageTests
{
    private static readonly SchemaScriptRefusal StoreRefusal =
        new(DatabaseRefusal.StorePolicy, SchemaScriptGate.StorePolicyDenial(SchemaScript.Ddl));

    private static readonly SchemaScriptRefusal CapabilityRefusal =
        new(DatabaseRefusal.CapabilityOff, SchemaScriptGate.HiddenTypesDenial(SchemaScript.Check, DatabaseRefusal.CapabilityOff, null));

    // ------------------------------------------------------------------------------------------------
    // Item 1: the script is withheld
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_DDL_tab_draws_a_withheld_script_as_the_refusal_sentence_and_offers_nothing_to_copy()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Ddl = DdlScript.Refused(StoreRefusal);

        var page = RenderTab(context, "ddl");
        ClickButton(page, "Generate script");

        page.Find(".ms-schema-refusal").TextContent.Should()
            .Contain("MartenStudioOptions.StoreAuthorizationPolicy")
            .And.Contain("spans every tenant");
        page.FindAll(".ms-error-alert").Should().BeEmpty("a refusal is not an error, and a retry would change nothing");
        page.FindAll(".ms-sql-text").Should().BeEmpty();
        page.FindAll(".ms-empty").Should().BeEmpty("\"nothing to script\" would be false");
    }

    [Fact]
    public void The_Drift_tab_draws_a_withheld_check_as_the_refusal_rather_than_as_could_not_report()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Check = SchemaCheck.Refused(CapabilityRefusal);

        var page = RenderTab(context, "drift");
        ClickButton(page, "Check schema");

        page.Find(".ms-schema-refusal").TextContent.Should()
            .Contain("MartenStudioOptions.Capabilities.BrowseDatabase")
            .And.Contain("document types the host hides");
        page.Find(".ms-drift-badge").TextContent.Trim().Should().Be("Withheld");
        page.Markup.Should().NotContain("Could not report");
    }

    [Fact]
    public void The_Drift_tab_draws_a_withheld_preview_as_the_refusal_and_leaves_nothing_to_apply()
    {
        using var context = NewContext(out FakeSchemaDataService schema, capabilities: true);
        schema.Preview = MigrationPreview.Refused(new SchemaScriptRefusal(
            DatabaseRefusal.WritePolicy,
            SchemaScriptGate.HiddenTypesDenial(SchemaScript.Preview, DatabaseRefusal.WritePolicy, null)));

        var page = RenderTab(context, "drift");
        ClickButton(page, "Preview migration");

        page.Find(".ms-schema-refusal").TextContent.Should().Contain("MartenStudioOptions.WriteAuthorizationPolicy");
        page.FindAll(".ms-sql-text").Should().BeEmpty();
        page.Find(".ms-schema-apply").HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>The apply is refused by the service with a sentence, which the tab puts on screen.</summary>
    [Fact]
    public void A_refused_apply_is_drawn_with_the_refusal_sentence()
    {
        using var context = NewContext(out FakeSchemaDataService schema, capabilities: true);
        schema.Preview = new MigrationPreview("create index x on y (z);", 1, [], "Update", null);
        schema.ApplyResult = SchemaApplyResult.Refused(new SchemaScriptRefusal(
            DatabaseRefusal.StorePolicy, SchemaScriptGate.StorePolicyDenial(SchemaScript.Apply)));

        var page = RenderTab(context, "drift");
        ClickButton(page, "Preview migration");
        page.Find(".ms-schema-apply").Click();
        page.Find(".ms-confirm-input").Input("localhost.marten");
        page.Find(".ms-confirm-actions .ms-button-danger").Click();

        page.Find(".ms-alert-error[role=status]").TextContent.Should().Contain("The migration an apply runs spans every tenant");
    }

    /// <summary>R2: the dialog says what an apply reaches - the whole database, every tenant - before anybody types.</summary>
    [Fact]
    public void The_apply_dialog_says_it_migrates_the_whole_database_for_every_tenant()
    {
        using var context = NewContext(out FakeSchemaDataService schema, capabilities: true);
        schema.Preview = new MigrationPreview("create index x on y (z);", 1, [], "Update", null);

        var page = RenderTab(context, "drift");
        ClickButton(page, "Preview migration");
        page.Find(".ms-schema-apply").Click();

        page.Find(".ms-confirm-dialog").TextContent.Should()
            .Contain("the whole database, every tenant's tables and partitions included");
        page.Markup.Should().Contain("every tenant's</strong> tables and partitions");
    }

    // ------------------------------------------------------------------------------------------------
    // Item 2: the tenancy tables' figures
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_tenancy_tables_withheld_figures_say_withheld_and_the_note_says_why()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = new SchemaTables(
            [
                new TableStats("studio", "mt_tenant_partitions", 16384, 8192, 8192, -1, null, null, null, null, null, null, null, null,
                    new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure), FiguresWithheld: true),
            ],
            ["studio"],
            1048576,
            null,
            SchemaTableAssembler.PartitionCountsWithheld(false, false, null));

        var page = RenderTab(context, "tables");

        page.FindAll(".ms-schema-withheld").Select(static x => x.TextContent.Trim()).Should()
            .Equal("withheld", "withheld", "withheld", "withheld", "withheld");
        page.Markup.Should().NotContain("not analyzed", "the estimate is withheld, not missing");
        page.Find(".ms-schema-partition-note").TextContent.Should()
            .Contain("MartenStudioOptions.Capabilities.BrowseDatabase")
            .And.Contain("number of tenants");
    }

    // ------------------------------------------------------------------------------------------------
    // Item 3: a store that cannot be read degrades the tabs
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_Tables_tab_draws_the_classification_notice_above_what_it_still_lists()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = new SchemaTables(
            [
                new TableStats("studio", "mt_doc_customer", 98304, 65536, 32768, 1200, 1200, 3, 4, 500, null, null, "customer", "Customer",
                    new DatabaseObjectOwnership(DatabaseObjectOwner.MartenDocument, "default", "customer")),
            ],
            ["studio"],
            1048576,
            null)
        {
            ClassificationNotice = "Marten store 'IBrokenStore' could not be built (bad). 2 tables here that no readable store declares are withheld until then.",
        };

        var page = RenderTab(context, "tables");

        page.Find(".ms-schema-degraded").TextContent.Should().Contain("IBrokenStore").And.Contain("2 tables");
        page.FindAll("tbody tr").Should().ContainSingle("what a readable store declares is still listed");
    }

    [Fact]
    public void The_Indexes_tab_draws_the_classification_notice()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Indexes = SchemaIndexes.Empty with { ClassificationNotice = "The indexes on 1 table that no readable store declares are withheld." };

        var page = RenderTab(context, "indexes");

        page.Find(".ms-schema-degraded").TextContent.Should().Contain("The indexes on 1 table");
    }

    /// <summary>
    /// While the database browser reads nothing, the service hands Marten's own bodies over already read; the
    /// row draws that body and asks the browser for nothing.
    /// </summary>
    [Fact]
    public async Task A_body_the_service_already_read_is_drawn_without_asking_the_database_browser()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = new SchemaFunctions(
            [
                new FunctionInfo("studio", "mt_jsonb_patch", "jsonb, jsonb", DatabaseObjectKind.Function, true,
                    new DatabaseObjectOwnership(DatabaseObjectOwner.MartenInfrastructure), true, null,
                    "CREATE OR REPLACE FUNCTION studio.mt_jsonb_patch(jsonb, jsonb) RETURNS jsonb AS $function$ select 1 $function$"),
            ],
            null)
        {
            ClassificationNotice = "Marten store 'IBrokenStore' could not be built. Marten's own routines are listed with their bodies.",
        };

        var page = RenderTab(context, "functions");

        page.Find(".ms-schema-degraded").TextContent.Should().Contain("Marten's own routines are listed with their bodies");

        await page.Find("details.ms-function").TriggerEventAsync("ontoggle", EventArgs.Empty);

        page.Find("details.ms-function").TextContent.Should().Contain("CREATE OR REPLACE FUNCTION");
        context.DatabaseObjects.DefinitionsAsked.Should().BeEmpty("the body was read with the list");
    }

    // ------------------------------------------------------------------------------------------------
    // Item 6: a read that gave up behind a lock
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_read_that_gave_up_behind_a_lock_says_so_and_offers_to_try_again()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = SchemaTables.Locked("A table in this store's schemas is locked right now. Try again in a moment.");

        var page = RenderTab(context, "tables");

        page.FindAll(".ms-error-alert").Should().ContainSingle().Which.TextContent.Should().Contain("locked right now");
        page.Find(".ms-error-alert button").TextContent.Trim().Should().Be("Try again");
    }

    // ------------------------------------------------------------------------------------------------
    // Item 5: the object link
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_table_name_links_to_the_database_browsers_object_detail_built_by_its_own_links()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = new SchemaTables(
            [
                new TableStats("studio", "host settings", 8192, 8192, 0, 1, 1, 0, 0, 0, null, null, null, null,
                    new DatabaseObjectOwnership(DatabaseObjectOwner.Other)),
            ],
            ["studio"],
            1048576,
            null);

        var page = RenderTab(context, "tables");

        page.Find(".ms-schema-object-link").GetAttribute("href").Should().Be(
            DatabaseLinks.ToObject(context.Options, new MartenStudio.Services.StudioScope("default", "localhost.marten", null), "studio", "host settings"));
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private static IRenderedComponent<SchemaPage> RenderTab(StudioComponentContext context, string tab)
    {
        context.Navigate("/marten/schema?tab=" + tab);
        return context.Render<SchemaPage>();
    }

    private static void ClickButton(IRenderedComponent<SchemaPage> page, string text)
    {
        foreach (var button in page.FindAll(".ms-schema-toolbar .ms-button"))
        {
            if (button.TextContent.Contains(text, StringComparison.Ordinal))
            {
                button.Click();
                return;
            }
        }

        throw new InvalidOperationException($"No toolbar button matching '{text}' was rendered.");
    }

    private static StudioComponentContext NewContext(out FakeSchemaDataService schema, bool capabilities = false)
    {
        var fake = new FakeSchemaDataService();
        var context = new StudioComponentContext(services => services.AddSingleton<ISchemaDataService>(fake));
        context.WithStores("default");

        if (capabilities)
        {
            context.WithAllCapabilities();
        }

        schema = fake;
        return context;
    }
}
