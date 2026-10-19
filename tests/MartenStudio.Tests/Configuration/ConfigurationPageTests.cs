using Bunit;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Health;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

    /// <summary>
    /// Four tables on one screen - the databases, and one document type's duplicated fields, indexes and
    /// foreign keys - each of which can be wider than the card it is in.
    /// </summary>
    /// <remarks>
    /// The three inside a card are named after the document type as well as after the table. Two cards
    /// open at once would otherwise announce two landmarks both called "Indexes table", which tells a
    /// screen-reader user less than no label at all would.
    /// </remarks>
    [Fact]
    public void Every_table_scrolls_and_the_ones_inside_a_card_are_named_after_their_document_type()
    {
        using var context = NewContext(out _);

        var page = context.Render<ConfigurationPage>();
        CardHeader(page, "customer").Click();

        page.ShouldPutEveryTableInALabelledScrollRegion();

        page.FindAll(".ms-table-scroll").Select(x => x.GetAttribute("aria-label")).Should().Equal(
            "Databases table",
            "customer duplicated fields table",
            "customer indexes table",
            "customer foreign keys table");
    }

    /// <summary>
    /// F1 on the page: the packages this process loaded, by version, and what the studio can light up on them -
    /// read from the assemblies, never from a project file.
    /// </summary>
    [Fact]
    public void The_runtime_card_lists_the_resolved_versions_and_the_light_up_features()
    {
        using var context = NewContext(out FakeConfigurationService configuration);
        configuration.Configuration = configuration.Configuration with
        {
            Runtime =
            [
                new RuntimeComponent(RuntimePackage.Marten, "Marten", new Version(9, 31, 0), "9.31.0+abc"),
                new RuntimeComponent(RuntimePackage.Npgsql, "Npgsql", null, null),
            ],
            Features = [new RuntimeFeatureStatus("DocumentFillFactor", "Marten 9.47+", false, "A document table's fill factor.")],
            HostEnvironment = "Production",
        };

        var page = context.Render<ConfigurationPage>();

        CardHeader(page, "Runtime").Should().NotBeNull();
        page.Find(".ms-config-runtime .ms-config-grid").TextContent.Should().Contain("Host environment").And.Contain("Production")
            .And.Contain("IHostEnvironment.EnvironmentName");
        page.Markup.Should().Contain("9.31.0").And.Contain("AssemblyInformationalVersion 9.31.0+abc");
        page.Markup.Should().Contain("no informational version on the assembly");
        page.TextOfAll(".ms-config-runtime tbody td").Should().Equal(
            "DocumentFillFactor", "Marten 9.47+", "not available", "A document table's fill factor.");
        page.FindAll(".ms-config-runtime [role=region]").Single().GetAttribute("aria-label").Should().Be("Runtime features table");
    }

    /// <summary>
    /// H1-fix item 6: K01 prints the host's environment name to every visitor of the scope, and a configuration
    /// finding says nothing the Configuration page does not - so the Runtime card prints the very value K01 reads,
    /// and says plainly when the host registers none or names none, which is when K01 says it cannot tell.
    /// </summary>
    [Theory]
    [InlineData("Production", "Production")]
    [InlineData("Staging", "Staging")]
    [InlineData("", "registered, with no name")]
    [InlineData(null, "not registered (the host has no IHostEnvironment)")]
    public void The_runtime_card_names_the_environment_K01_reads(string? name, string shown)
    {
        IHostEnvironment? environment = name is null ? null : new TestHostEnvironment(name);

        ConfigurationService.DescribeEnvironment(environment).Should().Be(shown);

        if (name is { Length: > 0 })
        {
            HealthVerdict verdict = new AutoCreateInProductionCheck().Evaluate(
                new AutoCreateFacts(JasperFx.AutoCreate.CreateOrUpdate, name),
                HealthEvaluation.At(DateTimeOffset.UnixEpoch));

            foreach (HealthEvidence printed in verdict.Findings.SelectMany(static x => x.Evidence).Where(static x => x.Label == "Environment"))
            {
                printed.Value.Should().Be(shown, "what K01 prints is what the card prints");
            }
        }
    }

    /// <summary>The real service fills the card from this process's own assemblies.</summary>
    [Fact]
    public void The_process_runtime_reads_Marten_and_every_package_it_brought()
    {
        MartenRuntime runtime = MartenRuntime.Current;

        runtime.Components.Select(static x => x.Package).Should().Equal(Enum.GetValues<RuntimePackage>());
        runtime.Components.Should().OnlyContain(static x => x.Version != null, "every package the studio ships against stamps an informational version");
        runtime.VersionOf(RuntimePackage.Marten).Should().BeGreaterThanOrEqualTo(new Version(9, 39, 0), "the floor");
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
