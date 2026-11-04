using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.JSInterop;

using PostgresStudio.Components.Layout;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-HEADER over Postgres Studio's own context: the environment badge, the "Back to application" link and the copy-link button are the
/// shared shell's, so the same markup has to be there under this studio's descriptor and settings. (MartenStudio.Tests holds the longer
/// set, over Marten Studio's.)
/// </summary>
public class HeaderAdditionsTests
{
    private static PostgresStudioComponentContext Context(string? environment = "Production") => new(services =>
    {
        if (environment is not null)
        {
            services.AddSingleton<IHostEnvironment>(new NamedEnvironment(environment));
        }
    });

    [Theory]
    [InlineData("Production", "ms-env-badge-production")]
    [InlineData("Staging", "ms-env-badge-staging")]
    [InlineData("Development", "ms-env-badge-development")]
    [InlineData("QA", "ms-env-badge-other")]
    public void The_header_shows_the_environment_in_the_colour_of_its_kind(string name, string expectedClass)
    {
        using PostgresStudioComponentContext context = Context(name);

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, static builder => builder.AddContent(0, string.Empty)));

        IElement badge = layout.Find(".ms-header-left > .ms-env-badge");
        badge.TextContent.Trim().Should().Be(name);
        badge.ClassList.Should().Contain(expectedClass);
    }

    [Fact]
    public void A_host_with_no_IHostEnvironment_has_no_badge()
    {
        using PostgresStudioComponentContext context = Context(environment: null);

        context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, static builder => builder.AddContent(0, string.Empty)))
            .FindAll(".ms-env-badge").Should().BeEmpty();
    }

    [Fact]
    public void The_back_link_is_there_only_when_the_option_is_set_and_goes_where_it_says()
    {
        using PostgresStudioComponentContext without = Context();
        without.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, static builder => builder.AddContent(0, string.Empty)))
            .FindAll(".ms-back-link").Should().BeEmpty();

        using PostgresStudioComponentContext with = Context();
        with.Options.ApplicationPath = "/";
        IElement link = with.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, static builder => builder.AddContent(0, string.Empty)))
            .Find(".ms-header-right > a.ms-back-link");

        link.GetAttribute("href").Should().Be("/", "not re-rooted under /postgres");
        link.TextContent.Trim().Should().Be("Back to application");
    }

    [Fact]
    public void Copy_link_puts_the_address_with_its_scope_on_the_clipboard()
    {
        using PostgresStudioComponentContext context = Context();
        context.Navigate("database?store=default&db=default");
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(true);

        IRenderedComponent<CopyLinkButton> button = context.Render<CopyLinkButton>();
        button.Find("button.ms-copy-link-button").Click();

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Should().ContainSingle()
            .Which.Arguments[0].Should().Be(context.CurrentUri);
        button.Find(".ms-copy-link-status").TextContent.Trim().Should().Be("Link copied");
    }

    [Fact]
    public void A_refused_copy_shows_the_text_box_and_a_lost_circuit_does_too()
    {
        using PostgresStudioComponentContext refused = Context();
        refused.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetResult(false);
        IRenderedComponent<CopyLinkButton> first = refused.Render<CopyLinkButton>();
        first.Find("button.ms-copy-link-button").Click();
        first.Find(".ms-copy-link-fallback input").GetAttribute("value").Should().Be(refused.CurrentUri);

        using PostgresStudioComponentContext gone = Context();
        gone.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", _ => true).SetException(new JSDisconnectedException("the circuit closed"));
        IRenderedComponent<CopyLinkButton> second = gone.Render<CopyLinkButton>();
        second.Invoking(x => x.Find("button.ms-copy-link-button").Click()).Should().NotThrow();
        second.FindAll(".ms-copy-link-fallback input").Should().ContainSingle();
    }

    private sealed class NamedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "PostgresStudio.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
