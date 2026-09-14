using Bunit;

using MartenStudio.Components.Pages;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The Overview page: real facts about the stores this application registered, and a region that breaks
/// on its own when one of them cannot be reached.
/// </summary>
public class OverviewTests
{
    [Fact]
    public void The_tiles_carry_the_facts_the_service_reported()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore(documentTypes: 7, postgresVersion: "17.2");

        var page = context.Render<Overview>();

        page.StatCardValue("Databases").Should().Be("1");
        page.StatCardValue("Document types").Should().Be("7");
        page.StatCardValue("Event types").Should().Be("4");
        page.StatCardValue("PostgreSQL").Should().Be("17.2");
        page.StatCardClasses("PostgreSQL").Should().Contain("ms-stat-card-success");
    }

    [Fact]
    public void The_event_store_configuration_is_rendered_as_facts_a_person_can_act_on()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore();

        var page = context.Render<Overview>();

        page.KeyValue("Stream identity").Should().Be("Guid");
        page.KeyValue("Append mode").Should().Be("Quick");
        page.KeyValue("Event tenancy").Should().Be("Single");
        page.KeyValue("Event schema").Should().Be("studio_sample_events");
        page.KeyValue("Tenancy").Should().Be("Single");
    }

    [Fact]
    public void Each_database_is_named_by_Martens_identity_and_never_by_a_connection_string()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore(databaseIdentity: "localhost.marten");

        var page = context.Render<Overview>();

        page.Find(".ms-database-title").TextContent.Trim().Should().Be("localhost.marten");
        page.KeyValue("Database").Should().Be("marten");
        page.KeyValue("Server").Should().Be("localhost");
        page.KeyValue("Schemas").Should().Be("studio_sample, studio_sample_events");
        page.Markup.Should().NotContain("Password", "no connection string is ever rendered");
    }

    /// <summary>
    /// A version nobody could read is drawn differently from one that is zero or missing: "cannot report"
    /// is a value (plan section 4.8).
    /// </summary>
    [Fact]
    public void A_Postgres_version_that_could_not_be_read_says_unknown_in_amber()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore(postgresVersion: null);

        var page = context.Render<Overview>();

        page.StatCardValue("PostgreSQL").Should().Be("unknown");
        page.StatCardClasses("PostgreSQL").Should().Contain("ms-stat-card-warning");
    }

    /// <summary>A database that cannot be reached breaks its own region and nothing else.</summary>
    [Fact]
    public void A_database_that_cannot_be_reached_shows_an_alert_in_its_own_region()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo
            .WithStore(key: "default", displayName: "Default")
            .WithStore(key: "IInvoicingStore", displayName: "Invoicing Store", databaseError: "28P01: password authentication failed");

        var page = context.Render<Overview>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("28P01");
        page.FindAll("section").Should().HaveCount(2, "the healthy store is still drawn");
        page.StatCardValue("Databases").Should().Be("1");
    }

    [Fact]
    public void A_store_that_will_not_build_is_an_alert_rather_than_a_missing_section()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithUnavailableStore("IInvoicingStore", "Invoicing Store", "no connection string was configured");

        var page = context.Render<Overview>();

        page.Find("h2").TextContent.Should().Contain("Invoicing Store");
        page.Find(".ms-error-alert").TextContent.Should().Contain("no connection string was configured");
    }

    [Fact]
    public void A_failure_to_load_is_shown_with_a_retry_that_asks_again()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.Failure = new InvalidOperationException("the store could not be reached");

        var page = context.Render<Overview>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("the store could not be reached");
        context.StoreInfo.Loads.Should().Be(1);

        context.StoreInfo.Failure = null;
        context.StoreInfo.WithStore();
        page.Find(".ms-error-alert button").Click();

        context.StoreInfo.Loads.Should().Be(2);
        page.FindAll(".ms-error-alert").Should().BeEmpty();
        page.StatCardValue("Databases").Should().Be("1");
    }

    /// <summary>
    /// No store, or none the visitor may see, is an empty state that names what a host would do about it
    /// - not a blank page.
    /// </summary>
    [Fact]
    public void No_store_at_all_is_an_empty_state_that_says_what_to_do()
    {
        using var context = new StudioComponentContext();

        var page = context.Render<Overview>();

        page.Find(".ms-empty-title").TextContent.Should().Be("No Marten store to show");
        page.Find(".ms-empty-description").TextContent.Should().Contain("AddMarten");
        page.Find(".ms-empty-description").TextContent.Should().Contain("StoreAuthorizationPolicy");
    }

    [Fact]
    public void The_registration_key_is_shown_beside_the_display_name()
    {
        using var context = new StudioComponentContext();
        context.StoreInfo.WithStore(key: "IInvoicingStore", displayName: "Invoicing Store");

        var page = context.Render<Overview>();

        page.Find(".ms-section-key").TextContent.Trim().Should().Be("IInvoicingStore");
    }
}
