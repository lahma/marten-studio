using Bunit;

using MartenStudio.Services;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using SchemaPage = MartenStudio.Components.Pages.Schema.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// The Schema page: five tabs, a drift badge that is three states rather than two, and an apply that is
/// gone unless the host opted in.
/// </summary>
/// <remarks>
/// The page component is reached through an alias because it is called <c>Schema</c> inside a namespace
/// whose last segment is also <c>Schema</c>: from this test's own namespace the bare name would bind to
/// the namespace and not compile.
/// </remarks>
public class SchemaPageTests
{
    [Fact]
    public void The_five_tabs_are_rendered_and_Drift_is_the_default()
    {
        using var context = NewContext(out _);

        var page = context.Render<SchemaPage>();

        page.TextOfAll(".ms-tab").Should().Equal("Drift", "Tables", "Indexes", "Functions", "DDL");
        page.Find(".ms-tab-active").TextContent.Trim().Should().Be("Drift");
    }

    [Fact]
    public void Every_tab_link_carries_the_scope_so_a_pasted_link_reopens_the_same_thing()
    {
        using var context = NewContext(out _);

        var page = context.Render<SchemaPage>();

        foreach (var link in page.FindAll(".ms-tab"))
        {
            string href = link.GetAttribute("href") ?? string.Empty;
            href.Should().Contain("store=default");
            href.Should().Contain("db=localhost.marten");
            href.Should().Contain("tab=");
        }
    }

    [Fact]
    public void The_tab_query_parameter_selects_the_tab()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Ddl = new DdlScript("create schema studio;", null);
        context.Navigate("/marten/schema?tab=ddl");

        var page = context.Render<SchemaPage>();

        page.Find(".ms-tab-active").TextContent.Trim().Should().Be("DDL");
        page.Markup.Should().Contain("create");
    }

    [Fact]
    public void An_unknown_tab_falls_back_to_Drift_rather_than_rendering_nothing()
    {
        using var context = NewContext(out _);
        context.Navigate("/marten/schema?tab=nonsense");

        var page = context.Render<SchemaPage>();

        page.Find(".ms-tab-active").TextContent.Trim().Should().Be("Drift");
    }

    /// <summary>
    /// Acceptance 7: creating a migration opens connections and reads the whole catalog, so navigating
    /// to the tab must not do it.
    /// </summary>
    [Fact]
    public void Opening_the_Drift_tab_runs_nothing_until_somebody_asks()
    {
        using var context = NewContext(out FakeSchemaDataService schema);

        var page = context.Render<SchemaPage>();

        schema.Checks.Should().Be(0);
        schema.Previews.Should().Be(0);
        page.Markup.Should().Contain("Not checked yet");
        page.Find(".ms-drift-badge").TextContent.Trim().Should().Be("Not checked");
    }

    [Fact]
    public void Checking_an_in_sync_database_shows_the_in_sync_badge_and_Martens_own_message()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Check = SchemaCheck.Matches(["studio", "studio_events"], "Marten reports that this database matches its configuration.");

        var page = context.Render<SchemaPage>();
        page.Find(".ms-schema-toolbar .ms-button-primary").Click();

        schema.Checks.Should().Be(1);
        page.Find(".ms-drift-badge").TextContent.Trim().Should().Be("In sync");
        page.Find(".ms-drift-badge").ClassList.Should().Contain("ms-drift-badge-ok");
        page.KeyValue("Marten says").Should().Contain("matches its configuration");
        page.KeyValue("Schemas").Should().Be("studio, studio_events");
    }

    [Fact]
    public void Checking_a_drifted_database_counts_the_differences_and_lists_the_objects()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Check = SchemaCheck.Differences(
            [
                new SchemaObjectDifference("studio.mt_doc_customer", "Table", "Update"),
                new SchemaObjectDifference("studio.mt_upsert_customer", "Function", "Update"),
            ],
            ["studio"],
            "Configured database does not match...");

        var page = context.Render<SchemaPage>();
        page.Find(".ms-schema-toolbar .ms-button-primary").Click();

        page.Find(".ms-drift-badge").TextContent.Trim().Should().Be("2 differences");
        page.Find(".ms-drift-badge").ClassList.Should().Contain("ms-drift-badge-warn");
        page.Markup.Should().Contain("studio.mt_doc_customer");
        page.Markup.Should().Contain("studio.mt_upsert_customer");
    }

    /// <summary>"Cannot report" is a value, drawn differently from "in sync" (plan §4.8).</summary>
    [Fact]
    public void A_database_that_could_not_answer_is_drawn_differently_from_one_that_matches()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Check = SchemaCheck.Unavailable("28P01: password authentication failed");

        var page = context.Render<SchemaPage>();
        page.Find(".ms-schema-toolbar .ms-button-primary").Click();

        page.Find(".ms-drift-badge").TextContent.Trim().Should().Be("Cannot report");
        page.Find(".ms-drift-badge").ClassList.Should().Contain("ms-drift-badge-unknown");
        page.KeyValue("Could not report").Should().Contain("28P01");
    }

    [Fact]
    public void Previewing_renders_the_SQL_and_never_applies_it()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Preview = new MigrationPreview(
            "create index mt_doc_customer_idx_email on studio.mt_doc_customer (email);",
            1,
            [new SchemaObjectDifference("studio.mt_doc_customer", "Table", "Update")],
            "Update",
            null);

        var page = context.Render<SchemaPage>();
        ClickPreview(page);

        schema.Previews.Should().Be(1);
        schema.Applies.Should().Be(0);
        page.Markup.Should().Contain("mt_doc_customer_idx_email");
        page.FindAll(".ms-sql-keyword").Select(x => x.TextContent).Should().Contain("create");
    }

    /// <summary>D4: a freshly mapped studio is a read-only browser, and the control says which option to set.</summary>
    [Fact]
    public void Apply_is_absent_and_named_when_the_capability_is_off()
    {
        using var context = NewContext(out _);

        var page = context.Render<SchemaPage>();

        page.FindAll(".ms-capability-disabled").Should().ContainSingle();
        page.Markup.Should().Contain("MartenStudioOptions.Capabilities.ApplySchemaChanges");
        page.FindAll(".ms-button-danger").Should().BeEmpty();
    }

    [Fact]
    public void Apply_is_present_but_does_nothing_until_a_migration_has_been_previewed()
    {
        using var context = NewContext(out _, capabilities: true);

        var page = context.Render<SchemaPage>();

        page.FindAll(".ms-capability-disabled").Should().BeEmpty();
        page.Find(".ms-button-danger").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Applying_asks_for_the_database_identity_and_passes_exactly_what_was_typed()
    {
        using var context = NewContext(out FakeSchemaDataService schema, capabilities: true);
        schema.Preview = new MigrationPreview("create index x on y (z);", 1, [], "Update", null);
        schema.DatabaseIdentity = "localhost.marten";

        var page = context.Render<SchemaPage>();
        ClickPreview(page);
        page.Find(".ms-button-danger").Click();

        // The dialog is up, the confirm button is disabled, and the box says what to type.
        page.Find(".ms-confirm-dialog").TextContent.Should().Contain("localhost.marten");
        page.Find(".ms-confirm-actions .ms-button-danger").HasAttribute("disabled").Should().BeTrue();

        page.Find(".ms-confirm-input").Input("localhost.marten");
        page.Find(".ms-confirm-actions .ms-button-danger").Click();

        schema.Applies.Should().Be(1);
        schema.LastConfirmation.Should().Be("localhost.marten");
    }

    [Fact]
    public void Typing_the_wrong_identity_leaves_the_confirm_button_disabled()
    {
        using var context = NewContext(out FakeSchemaDataService schema, capabilities: true);
        schema.Preview = new MigrationPreview("create index x on y (z);", 1, [], "Update", null);

        var page = context.Render<SchemaPage>();
        ClickPreview(page);
        page.Find(".ms-button-danger").Click();
        page.Find(".ms-confirm-input").Input("some other database");

        page.Find(".ms-confirm-actions .ms-button-danger").HasAttribute("disabled").Should().BeTrue();
        schema.Applies.Should().Be(0);
    }

    /// <summary>
    /// The service refuses regardless of what was rendered, and the page has to say so rather than
    /// die silently (AGENTS.md hard rule 5).
    /// </summary>
    [Fact]
    public void A_capability_refusal_from_the_service_is_rendered_with_the_option_it_names()
    {
        using var context = NewContext(out FakeSchemaDataService schema, capabilities: true);
        schema.Preview = new MigrationPreview("create index x on y (z);", 1, [], "Update", null);
        schema.ApplyFailure = new StudioCapabilityDeniedException(
            StudioCapability.ApplySchemaChanges,
            CapabilityDenialReason.Disabled);

        var page = context.Render<SchemaPage>();
        ClickPreview(page);
        page.Find(".ms-button-danger").Click();
        page.Find(".ms-confirm-input").Input("localhost.marten");
        page.Find(".ms-confirm-actions .ms-button-danger").Click();

        page.Find(".ms-error-alert").TextContent.Should()
            .Contain("MartenStudioOptions.Capabilities.ApplySchemaChanges");
    }

    [Fact]
    public void An_insufficient_privilege_refusal_gets_a_sentence_rather_than_a_five_character_code()
    {
        using var context = NewContext(out FakeSchemaDataService schema, capabilities: true);
        schema.Preview = new MigrationPreview("create index x on y (z);", 1, [], "Update", null);
        schema.ApplyResult = new SchemaApplyResult(false, "Invalid", 1, "permission denied for schema studio", "42501");

        var page = context.Render<SchemaPage>();
        ClickPreview(page);
        page.Find(".ms-button-danger").Click();
        page.Find(".ms-confirm-input").Input("localhost.marten");
        page.Find(".ms-confirm-actions .ms-button-danger").Click();

        page.Markup.Should().Contain("may not change the schema");
    }

    [Fact]
    public void The_Tables_tab_lists_the_document_tables_with_their_sizes_and_links_the_collection()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Tables = new SchemaTables(
            [
                new TableStats("studio", "mt_doc_customer", 98304, 65536, 32768, 1200, 1200, 3, 4, 500,
                    DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "customer", "Customer", false),
                new TableStats("studio_events", "mt_events", 16384, 8192, 8192, -1, null, null, null, null,
                    null, null, null, null, true),
            ],
            ["studio", "studio_events"],
            1048576,
            null);

        context.Navigate("/marten/schema?tab=tables");
        var page = context.Render<SchemaPage>();

        page.Markup.Should().Contain("studio.mt_doc_customer");
        page.Markup.Should().Contain("96.0 KiB");
        page.Markup.Should().Contain("~1,200");
        page.Markup.Should().Contain("not analyzed");
        page.Find(".ms-collection-link").GetAttribute("href").Should().Contain("documents/customer");
        page.Find(".ms-collection-link").GetAttribute("href").Should().Contain("store=default");
    }

    [Fact]
    public void The_Indexes_tab_flags_a_never_used_index_and_a_primary_key_only_collection()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Indexes = new SchemaIndexes(
            [
                new IndexInfo("studio", "mt_doc_customer", "mt_doc_customer_idx_name",
                    "CREATE INDEX ...", 8192, 0, 0, false, false, true, "customer", IndexAdvice.NeverUsedSuggestion),
            ],
            [],
            [new UnindexedCollection("note", "Note", "studio", "mt_doc_note",
                IndexAdvice.SuggestionsFor(new DeclaredCollection("note", "Note", "studio", "mt_doc_note", false, "Text")))],
            null);

        context.Navigate("/marten/schema?tab=indexes");
        var page = context.Render<SchemaPage>();

        page.TextOfAll(".ms-index-flag-warn").Should().Contain("never used");
        page.Markup.Should().Contain("pg_stat_reset()");
        page.Markup.Should().Contain("opts.Schema.For&lt;Note&gt;().Index(x =&gt; x.Text);");
    }

    [Fact]
    public void The_Functions_tab_renders_each_definition_through_the_tokenizer()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Functions = new SchemaFunctions(
            [
                new FunctionInfo("studio", "mt_upsert_customer", "mt_upsert_customer(jsonb, uuid)",
                    "CREATE FUNCTION studio.mt_upsert_customer() RETURNS void AS $function$ BEGIN END; $function$;", true),
            ],
            null);

        context.Navigate("/marten/schema?tab=functions");
        var page = context.Render<SchemaPage>();

        page.Markup.Should().Contain("studio.mt_upsert_customer");
        page.FindAll(".ms-sql-string").Select(x => x.TextContent).Should()
            .Contain(x => x.Contains("$function$", StringComparison.Ordinal));
    }

    [Fact]
    public void The_DDL_tab_offers_the_script_for_copy_and_download()
    {
        using var context = NewContext(out FakeSchemaDataService schema);
        schema.Ddl = new DdlScript("create table studio.mt_doc_customer (id uuid);", null);

        context.Navigate("/marten/schema?tab=ddl");
        var page = context.Render<SchemaPage>();

        page.TextOfAll(".ms-schema-toolbar .ms-button").Should().Contain("Download .sql");
        page.Markup.Should().Contain("mt_doc_customer");
    }

    /// <summary>
    /// Clicks the Drift tab's Preview button, found by its text.
    /// </summary>
    /// <remarks>
    /// Re-found on every call rather than held: clicking it re-renders the toolbar, and an element
    /// captured before the render is an element that is no longer in the document.
    /// </remarks>
    private static void ClickPreview(IRenderedComponent<SchemaPage> page)
    {
        foreach (var button in page.FindAll(".ms-schema-toolbar .ms-button"))
        {
            if (button.TextContent.Contains("Preview", StringComparison.Ordinal))
            {
                button.Click();
                return;
            }
        }

        throw new InvalidOperationException("The Drift tab rendered no Preview button.");
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
