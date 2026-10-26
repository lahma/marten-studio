using Bunit;

using PostgresStudio.Components.Pages;
using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Components.Pages.Health;
using PostgresStudio.Components.Pages.Relationships;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// The shared screens in PostgresStudio's own words (PS-3b): no <c>StoreOptions</c>, no MartenStudio option and no
/// "Marten Studio" where the product is meant - and, where the database holds a Marten store's objects, one notice that
/// says where they are browsed.
/// </summary>
public class PostgresStudioWordsRenderTests
{
    [Fact]
    public async Task The_health_lead_promises_migration_SQL_and_no_StoreOptions_line()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var page = context.Render<HealthPage>();

        string lead = page.Find(".ms-health-lead").TextContent;
        lead.Should().Contain("for whoever owns the schema to put in a migration").And.Contain("never runs it");
        lead.Should().NotContain("StoreOptions").And.NotContain("store's configuration");
        HealthRemedy.MigrationHeading.Should().Be("For your own migrations - the studio never runs this");
    }

    [Fact]
    public async Task Activity_names_Postgres_Studio()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var page = context.Render<Activity>();

        page.WaitForAssertion(() => page.Markup.Should().Contain("actions taken through Postgres Studio in this"));
        page.Markup.Should().NotContain("Marten Studio");
    }

    [Fact]
    public async Task The_examples_say_where_they_come_from_in_Postgres_Studios_words()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().WithAllCapabilities().ReadyAsync();

        var page = context.Render<QueryPage>();

        page.WaitForAssertion(() => page.Find(".ms-query-examples-intro").TextContent.Should().Be(PostgresStudioText.Instance.ExamplesIntro));
        page.Markup.Should().NotContain("this store");
    }

    [Fact]
    public async Task The_database_page_points_a_Marten_stores_objects_at_Marten_Studio_and_names_no_MartenStudio_option()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.Navigate("postgres/database");

        var page = context.Render<DatabaseObjects>();

        page.WaitForAssertion(() => page.FindAll(".ms-db-marten-notice").Should().ContainSingle(
            "the fake catalog lists objects named like Marten's, so the page says where they are browsed"));
        page.Find(".ms-db-marten-notice").TextContent.Should().Contain(PostgresStudioText.Instance.MartenStoreNotice!);
        page.Markup.Should().NotContain("MartenStudioOptions").And.NotContain("Declared by this Marten store");
    }

    /// <summary>What a rendered PostgresStudio page must never say - MartenStudio's options, configuration and nouns.</summary>
    private static readonly string[] MartenStudioWords =
        ["MartenStudioOptions", "StoreOptions", "Marten Studio", "the store's", "this store", "LightweightSession", "AddMartenStudio"];

    public static TheoryData<string> Pages() => ["overview", "query", "database", "sessions", "health", "activity"];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task No_core_page_renders_MartenStudios_words(string page)
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        string markup = page switch
        {
            "overview" => Rendered(context.Render<StudioHome>(), ".ms-page-title"),
            "query" => Rendered(context.Render<QueryPage>(), ".ms-page-title"),
            "database" => RenderDatabase(context),
            "sessions" => Rendered(context.Render<Sessions>(), ".ms-sessions-stats"),
            "health" => Rendered(context.Render<HealthPage>(), ".ms-health-lead"),
            _ => Rendered(context.Render<Activity>(), ".ms-page-title"),
        };

        // The sentences that name Marten Studio on purpose: where a Marten store's objects are browsed.
        foreach (string onPurpose in new[]
                 {
                     PostgresStudioText.Instance.MartenStoreNotice!, PostgresStudioText.Instance.MartenInternalTitle,
                     PostgresStudioText.Instance.OwnerFilterMartenTitle, PostgresStudioText.Instance.MartenDependencyDenial,
                 })
        {
            markup = markup.Replace(onPurpose, string.Empty, StringComparison.Ordinal)
                .Replace(System.Net.WebUtility.HtmlEncode(onPurpose), string.Empty, StringComparison.Ordinal);
        }

        foreach (string word in MartenStudioWords)
        {
            markup.Should().NotContain(word, "the " + page + " page is Postgres Studio's");
        }
    }

    private static string RenderDatabase(PostgresStudioComponentContext context)
    {
        context.Navigate("postgres/database");
        return Rendered(context.Render<DatabaseObjects>(), ".ms-db-kind-tabs");
    }

    private static string Rendered<T>(IRenderedComponent<T> component, string ready)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        component.WaitForAssertion(() => component.FindAll(ready).Should().NotBeEmpty());
        return component.Markup;
    }

    [Fact]
    public async Task Relationships_say_what_Postgres_has_and_nothing_of_StoreOptions()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        var page = context.Render<Relationships>();

        page.WaitForAssertion(() => page.Find(".ms-page-title").TextContent.Should().Be("Relationships"));

        // The lead only: the shared fake draws document edges too, which PostgresStudio - with no node source - never has,
        // and whose drift states (declared, physical) only a store's configuration can produce.
        string lead = page.Find(".ms-schema-note").TextContent;
        lead.Should().Contain("as Postgres has them").And.NotContain("StoreOptions").And.NotContain("document types");
    }
}
