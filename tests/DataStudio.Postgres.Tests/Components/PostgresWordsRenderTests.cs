using Bunit;

using DataStudio.Components.Pages;
using DataStudio.Postgres.Components.Pages;
using DataStudio.Postgres.Components.Pages.Database;
using DataStudio.Postgres.Components.Pages.Health;
using DataStudio.Postgres.Components.Pages.Relationships;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Postgres;

namespace DataStudio.Postgres.Tests.Components;

/// <summary>
/// The shared screens in the Postgres module's own words (PS-3b): no <c>StoreOptions</c>, no Marten module option and no
/// "the Marten module" where the product is meant - and, where the database holds a Marten store's objects, one notice that
/// says where they are browsed.
/// </summary>
public class PostgresWordsRenderTests
{
    [Fact]
    public async Task The_health_lead_promises_migration_SQL_and_no_StoreOptions_line()
    {
        await using PostgresComponentContext context = await new PostgresComponentContext().ReadyAsync();

        var page = context.Render<HealthPage>();

        string lead = page.Find(".ms-health-lead").TextContent;
        lead.Should().Contain("for whoever owns the schema to put in a migration").And.Contain("never runs it");
        lead.Should().NotContain("StoreOptions").And.NotContain("store's configuration");
        HealthRemedy.MigrationHeading.Should().Be("For your own migrations - the studio never runs this");
    }

    [Fact]
    public async Task Activity_is_the_shells_page_and_names_the_shell()
    {
        await using PostgresComponentContext context = await new PostgresComponentContext().ReadyAsync();

        var page = context.Render<Activity>();

        page.WaitForAssertion(() => page.Markup.Should().Contain("actions taken through Data Studio in this"));
        page.Markup.Should().NotContain("Marten Studio");
    }

    [Fact]
    public async Task The_examples_say_where_they_come_from_in_Postgres_Studios_words()
    {
        await using PostgresComponentContext context = await new PostgresComponentContext().WithAllCapabilities().ReadyAsync();

        var page = context.Render<QueryPage>();

        page.WaitForAssertion(() => page.Find(".ms-query-examples-intro").TextContent.Should().Be(PostgresText.Instance.ExamplesIntro));
        page.Markup.Should().NotContain("this store");
    }

    [Fact]
    public async Task The_database_page_points_a_Marten_stores_objects_at_Marten_Studio_and_names_no_Marten_option()
    {
        await using PostgresComponentContext context = await new PostgresComponentContext().ReadyAsync();
        context.Navigate("studio/postgres/database");

        var page = context.Render<DatabaseObjects>();

        page.WaitForAssertion(() => page.FindAll(".ms-db-marten-notice").Should().ContainSingle(
            "the fake catalog lists objects named like Marten's, so the page says where they are browsed"));
        page.Find(".ms-db-marten-notice").TextContent.Should().Contain(PostgresText.Instance.MartenStoreNotice!);
        page.Markup.Should().NotContain("MartenOptions").And.NotContain("Declared by this Marten store");
    }

    /// <summary>What a rendered the Postgres module page must never say - the Marten module's options, configuration and nouns.</summary>
    private static readonly string[] MartenWords =
        ["MartenOptions", "StoreOptions", "Marten Studio", "the store's", "this store", "LightweightSession", "AddMarten"];

    public static TheoryData<string> Pages() => ["overview", "query", "database", "sessions", "health", "storage", "activity"];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task No_core_page_renders_Marten_words(string page)
    {
        await using PostgresComponentContext context = await new PostgresComponentContext().ReadyAsync();

        string markup = page switch
        {
            "overview" => Rendered(context.Render<StudioHome>(), ".ms-page-title"),
            "query" => Rendered(context.Render<QueryPage>(), ".ms-page-title"),
            "database" => RenderDatabase(context),
            "sessions" => Rendered(context.Render<Sessions>(), ".ms-sessions-stats"),
            "health" => Rendered(context.Render<HealthPage>(), ".ms-health-lead"),
            "storage" => Rendered(context.Render<DataStudio.Postgres.Components.Pages.Storage.Storage>(), ".ms-storage-withheld"),
            _ => Rendered(context.Render<Activity>(), ".ms-page-title"),
        };

        // The sentences that name the Marten module on purpose: where a Marten store's objects are browsed.
        foreach (string onPurpose in new[]
                 {
                     PostgresText.Instance.MartenStoreNotice!, PostgresText.Instance.MartenInternalTitle,
                     PostgresText.Instance.OwnerFilterMartenTitle, PostgresText.Instance.MartenDependencyDenial,
                 })
        {
            markup = markup.Replace(onPurpose, string.Empty, StringComparison.Ordinal)
                .Replace(System.Net.WebUtility.HtmlEncode(onPurpose), string.Empty, StringComparison.Ordinal);
        }

        foreach (string word in MartenWords)
        {
            markup.Should().NotContain(word, "the " + page + " page is the Postgres module's");
        }
    }

    private static string RenderDatabase(PostgresComponentContext context)
    {
        context.Navigate("studio/postgres/database");
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
        await using PostgresComponentContext context = await new PostgresComponentContext().ReadyAsync();

        var page = context.Render<Relationships>();

        page.WaitForAssertion(() => page.Find(".ms-page-title").TextContent.Should().Be("Relationships"));

        // The lead only: the shared fake draws document edges too, which the Postgres module - with no node source - never has,
        // and whose drift states (declared, physical) only a store's configuration can produce.
        string lead = page.Find(".ms-schema-note").TextContent;
        lead.Should().Contain("as Postgres has them").And.NotContain("StoreOptions").And.NotContain("document types");
    }
}
