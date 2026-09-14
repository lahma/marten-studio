using Bunit;

using MartenStudio.Services.Configuration;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using ConfigurationPage = MartenStudio.Components.Pages.Configuration.Configuration;

namespace MartenStudio.Tests.Configuration;

/// <summary>
/// The Configuration page: three kinds of card, each value naming the <c>StoreOptions</c> member it came
/// from, each value copyable, and no credential anywhere.
/// </summary>
public class ConfigurationPageTests
{
    [Fact]
    public void The_store_and_event_store_cards_render_with_the_member_behind_every_value()
    {
        using var context = NewContext(out _);

        var page = context.Render<ConfigurationPage>();

        page.Markup.Should().Contain("StoreOptions.DatabaseSchemaName");
        page.Markup.Should().Contain("StoreOptions.AutoCreateSchemaObjects");
        page.Markup.Should().Contain("StoreOptions.Events.StreamIdentity");
        page.Markup.Should().Contain("StoreOptions.Events.MetadataConfig.HeadersEnabled");
        page.Markup.Should().Contain("StoreOptions.Events.Daemon.AsyncMode");

        page.FindAll(".ms-config-member").Should().NotBeEmpty();
    }

    [Fact]
    public void A_database_is_named_by_its_server_port_database_and_schema_and_never_by_a_connection_string()
    {
        using var context = NewContext(out _);

        var page = context.Render<ConfigurationPage>();

        page.Markup.Should().Contain("localhost.marten");
        page.Markup.Should().Contain("5432");
        page.Markup.Should().Contain("studio_sample");
        page.Markup.Should().NotContain("Password", "no connection string is ever rendered");
    }

    [Fact]
    public void Every_value_has_a_copy_button_that_goes_through_the_studios_own_clipboard_helper()
    {
        using var context = NewContext(out _);

        var page = context.Render<ConfigurationPage>();

        page.FindAll(".ms-copy-inline").Should().NotBeEmpty();
        page.FindAll(".ms-copy-inline")[0].Click();

        context.JSInterop.VerifyInvoke("martenStudio.clipboard.copyText");
    }

    [Fact]
    public void A_copy_that_the_browser_refuses_says_so_rather_than_killing_the_circuit()
    {
        using var context = NewContext(out _);
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(false);

        var page = context.Render<ConfigurationPage>();
        page.FindAll(".ms-copy-inline")[0].Click();

        page.FindAll(".ms-copy-inline")[0].TextContent.Trim().Should().Be("Failed");
    }

    /// <summary>
    /// A closed browser tab throws <see cref="Microsoft.JSInterop.JSDisconnectedException" /> out of
    /// <c>martenStudio.clipboard.copyText</c>, and it derives from <see cref="Exception" /> rather than
    /// from <see cref="Microsoft.JSInterop.JSException" />. <c>ConfigurationValueRow</c>'s copy button has
    /// to say it failed the same honest way a refused clipboard does, not escape the page.
    /// </summary>
    [Fact]
    public void A_lost_circuit_during_copy_does_not_escape_and_still_says_Failed()
    {
        using var context = NewContext(out _);
        context.JSInterop.Disconnect<bool>("martenStudio.clipboard.copyText");

        var page = context.Render<ConfigurationPage>();

        Action copy = () => page.FindAll(".ms-copy-inline")[0].Click();

        copy.Should().NotThrow();
        page.FindAll(".ms-copy-inline")[0].TextContent.Trim().Should().Be("Failed");
    }

    [Fact]
    public void Document_type_cards_are_collapsed_until_they_are_opened()
    {
        using var context = NewContext(out _);

        var page = context.Render<ConfigurationPage>();

        page.TextOfAll(".ms-config-card-title").Should().Contain("customer").And.Contain("order");
        page.Markup.Should().NotContain("mt_doc_customer_idx_email", "the card starts collapsed");
    }

    [Fact]
    public void Opening_a_document_type_card_shows_its_duplicated_fields_indexes_keys_and_subclasses()
    {
        using var context = NewContext(out _);

        var page = context.Render<ConfigurationPage>();
        CardHeader(page, "customer").Click();

        page.Markup.Should().Contain("mt_doc_customer_idx_email");
        page.Markup.Should().Contain("varchar");
        page.Markup.Should().Contain("vip_customer");
        page.Markup.Should().Contain("mt_last_modified");
    }

    [Fact]
    public void The_search_box_narrows_the_document_type_cards()
    {
        using var context = NewContext(out _);

        var page = context.Render<ConfigurationPage>();

        // Typed into the real box rather than pushed through the callback, so the filter's own debounce
        // is part of what is tested - which is why this waits rather than asserting straight away.
        page.Find("input.ms-search-filter").Input("order");

        page.WaitForAssertion(() =>
        {
            page.TextOfAll(".ms-config-card-title").Should().NotContain("customer");
            page.TextOfAll(".ms-config-card-title").Should().Contain("order");
        });
    }

    [Fact]
    public void A_failure_is_put_on_the_page_rather_than_left_in_the_log()
    {
        using var context = NewContext(out FakeConfigurationService configuration);
        configuration.Failure = new InvalidOperationException("the store would not build");

        var page = context.Render<ConfigurationPage>();

        page.Find(".ms-error-alert").TextContent.Should().Contain("the store would not build");
    }

    private static AngleSharp.Dom.IElement CardHeader(IRenderedComponent<ConfigurationPage> page, string title)
    {
        foreach (var header in page.FindAll(".ms-config-card-header"))
        {
            if (string.Equals(header.QuerySelector(".ms-config-card-title")?.TextContent.Trim(), title, StringComparison.Ordinal))
            {
                return header;
            }
        }

        throw new InvalidOperationException($"The page rendered no card titled '{title}'.");
    }

    private static StudioComponentContext NewContext(out FakeConfigurationService configuration)
    {
        var fake = new FakeConfigurationService();
        var context = new StudioComponentContext(services => services.AddSingleton<IConfigurationService>(fake));
        context.WithStores("default");

        configuration = fake;
        return context;
    }
}
