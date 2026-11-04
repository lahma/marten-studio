using AngleSharp.Dom;

using Bunit;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;

using PostgresStudio.Components.Layout;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-HEADER: the environment badge, the "Back to application" link and the copy-link button, in the shell's header. The shell is
/// PostgresStudio's and Marten Studio's pages are built on it, so these run over Marten Studio's context; PostgresStudio.Tests has
/// the same markup rendered over its own.
/// </summary>
public class HeaderAdditionsTests
{
    private static readonly RenderFragment Body = builder => builder.AddContent(0, string.Empty);

    private static StudioComponentContext Context(string? environment = "Production")
    {
        return new StudioComponentContext(services =>
        {
            if (environment is not null)
            {
                services.AddSingleton<IHostEnvironment>(new NamedHostEnvironment(environment));
            }
        });
    }

    // -----------------------------------------------------------------------------------------------
    // The environment badge
    // -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Production", "ms-env-badge-production")]
    [InlineData("production", "ms-env-badge-production")]
    [InlineData("Staging", "ms-env-badge-staging")]
    [InlineData("STAGING", "ms-env-badge-staging")]
    [InlineData("Development", "ms-env-badge-development")]
    [InlineData("development", "ms-env-badge-development")]
    [InlineData("QA", "ms-env-badge-other")]
    [InlineData("Preview-EU", "ms-env-badge-other")]
    [InlineData("Test", "ms-env-badge-other")]
    public void The_badge_shows_the_environment_name_in_the_colour_of_its_kind(string name, string expectedClass)
    {
        using StudioComponentContext context = Context(name);

        IRenderedComponent<EnvironmentBadge> badge = context.Render<EnvironmentBadge>();

        IElement element = badge.Find(".ms-env-badge");
        element.TextContent.Trim().Should().Be(name, "the name is the text, as the host spelled it");
        element.ClassList.Should().Contain(expectedClass);
        element.ClassList.Where(static x => x.StartsWith("ms-env-badge-", StringComparison.Ordinal)).Should().ContainSingle(
            "one kind and no other, so a name is never drawn in two colours");
        element.GetAttribute("data-ms-environment").Should().Be(name);
    }

    [Fact]
    public void A_host_with_no_IHostEnvironment_has_no_badge()
    {
        using StudioComponentContext context = Context(environment: null);

        context.Render<EnvironmentBadge>().FindAll(".ms-env-badge").Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_environment_name_draws_no_badge(string name)
    {
        using StudioComponentContext context = Context(name);

        context.Render<EnvironmentBadge>().FindAll(".ms-env-badge").Should().BeEmpty();
    }

    [Fact]
    public void The_header_carries_the_badge_once_beside_the_scope_not_among_the_actions()
    {
        using StudioComponentContext context = Context("Staging");
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, Body));

        layout.FindAll(".ms-env-badge").Should().ContainSingle();
        layout.FindAll(".ms-header-left > .ms-env-badge").Should().ContainSingle("the environment is context, like the scope");
        layout.FindAll(".ms-env-badge").Single().TextContent.Trim().Should().Be("Staging");
    }

    [Fact]
    public void The_badge_reads_the_environment_and_changes_nothing_in_the_host()
    {
        // It is a plain span: no button, no link, no tab stop, no handler the host's environment could be changed through.
        using StudioComponentContext context = Context("Production");

        IElement element = context.Render<EnvironmentBadge>().Find(".ms-env-badge");

        element.TagName.Should().Be("SPAN");
        element.HasAttribute("tabindex").Should().BeFalse();
        element.HasAttribute("href").Should().BeFalse();
    }

    // -----------------------------------------------------------------------------------------------
    // Back to application
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void There_is_no_link_unless_the_host_set_an_application_path()
    {
        using StudioComponentContext context = Context();
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, Body));

        layout.FindAll(".ms-back-link").Should().BeEmpty();
        layout.Markup.Should().NotContain("Back to application");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app")]
    [InlineData("/ops/portal?tab=1")]
    [InlineData("https://example.com/app")]
    public void The_link_goes_to_the_path_exactly_as_written_and_is_not_rooted_under_the_mount(string path)
    {
        using StudioComponentContext context = Context();
        context.Options.ApplicationPath = path;
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, Body));

        IElement link = layout.Find(".ms-header-right > a.ms-back-link");
        link.GetAttribute("href").Should().Be(path, "the href is the option, never re-rooted (the mount is /marten)");
        link.TextContent.Trim().Should().Be("Back to application");
        link.GetAttribute("data-enhance-nav").Should().Be("false", "leaving the studio is a normal navigation, not an enhanced one");
        link.HasAttribute("target").Should().BeFalse("it is the same tab; the studio is a part of the application");
    }

    // -----------------------------------------------------------------------------------------------
    // Copy link
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_copy_link_button_is_named_and_in_the_header()
    {
        using StudioComponentContext context = Context();
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, Body));

        IElement button = layout.Find(".ms-header-right .ms-copy-link-button");
        button.GetAttribute("type").Should().Be("button");
        button.GetAttribute("aria-label").Should().Be("Copy link to this page");
        button.GetAttribute("title").Should().Be("Copy link to this page");
        button.TextContent.Trim().Should().BeEmpty("the control is an icon");
        layout.Find(".ms-copy-link-status").GetAttribute("aria-live").Should().Be("polite");
    }

    [Fact]
    public void Clicking_it_copies_the_address_of_the_page_with_its_scope_and_says_so()
    {
        using StudioComponentContext context = Context();
        context.Navigate("documents?store=default&db=localhost.marten&tenant=acme");
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(true);

        IRenderedComponent<CopyLinkButton> button = context.Render<CopyLinkButton>();
        button.Find("button.ms-copy-link-button").Click();

        InvocationAssertions(context).Arguments[0].Should().Be(context.CurrentUri);
        context.CurrentUri.Should().Contain("store=default").And.Contain("tenant=acme", "the address carries the scope (D9)");

        button.Find(".ms-copy-link-status").TextContent.Trim().Should().Be("Link copied");
        button.FindAll(".ms-copy-link-fallback").Should().BeEmpty("it worked, so there is nothing to copy by hand");
    }

    [Fact]
    public void The_status_clears_itself()
    {
        using StudioComponentContext context = Context();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(true);

        IRenderedComponent<CopyLinkButton> button = context.Render<CopyLinkButton>();
        button.Find("button.ms-copy-link-button").Click();
        button.Find(".ms-copy-link-status").TextContent.Trim().Should().Be("Link copied");

        button.WaitForAssertion(
            () => button.Find(".ms-copy-link-status").TextContent.Trim().Should().BeEmpty(),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_clipboard_that_would_not_take_it_shows_the_address_to_copy_by_hand()
    {
        using StudioComponentContext context = Context();
        context.Navigate("query?store=default&db=localhost.marten");
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(false);

        IRenderedComponent<CopyLinkButton> button = context.Render<CopyLinkButton>();
        button.Find("button.ms-copy-link-button").Click();

        IElement box = button.Find(".ms-copy-link-fallback input.ms-copy-link-text");
        box.GetAttribute("value").Should().Be(context.CurrentUri);
        box.HasAttribute("readonly").Should().BeTrue();
        box.GetAttribute("data-ms-select-on-focus").Should().Be("true", "studio.js selects it on focus");
        button.Find(".ms-copy-link-fallback label").GetAttribute("for").Should().Be(box.Id, "the box is labelled");
        button.Find(".ms-copy-link-status").TextContent.Trim().Should().BeEmpty("nothing was copied, so nothing is claimed");

        button.Find(".ms-copy-link-fallback button").Click();
        button.FindAll(".ms-copy-link-fallback").Should().BeEmpty("Close puts it away");
    }

    [Fact]
    public void Interop_that_has_gone_falls_back_to_the_text_box_and_does_not_end_the_circuit()
    {
        using StudioComponentContext context = Context();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true)
            .SetException(new JSDisconnectedException("the circuit closed"));

        IRenderedComponent<CopyLinkButton> button = context.Render<CopyLinkButton>();

        button.Invoking(x => x.Find("button.ms-copy-link-button").Click()).Should().NotThrow();
        button.FindAll(".ms-copy-link-fallback input").Should().ContainSingle();
    }

    [Fact]
    public void A_failure_that_is_not_the_browser_going_away_still_surfaces()
    {
        using StudioComponentContext context = Context();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true)
            .SetException(new NotSupportedException("a real bug"));

        IRenderedComponent<CopyLinkButton> button = context.Render<CopyLinkButton>();

        button.Invoking(x => x.Find("button.ms-copy-link-button").Click()).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Moving_to_another_page_puts_a_stale_fallback_away()
    {
        using StudioComponentContext context = Context();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(false);

        IRenderedComponent<CopyLinkButton> button = context.Render<CopyLinkButton>();
        button.Find("button.ms-copy-link-button").Click();
        button.FindAll(".ms-copy-link-fallback").Should().ContainSingle();

        context.Navigate("events/streams");

        button.WaitForAssertion(() => button.FindAll(".ms-copy-link-fallback").Should().BeEmpty());
    }

    private static JSRuntimeInvocation InvocationAssertions(StudioComponentContext context) =>
        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Should().ContainSingle().Which;
}
