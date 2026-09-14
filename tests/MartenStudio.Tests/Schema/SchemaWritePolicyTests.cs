using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using SchemaPage = MartenStudio.Components.Pages.Schema.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// The Schema page's Apply against <see cref="MartenStudioOptions.WriteAuthorizationPolicy" />, which is
/// asked about the visitor rather than about the process.
/// </summary>
/// <remarks>
/// <para>
/// Applying is the most consequential write the studio offers - <c>CreateOrUpdate</c> is not additive,
/// and the preview lists the drops - so a studio mounted for a team where one person may run migrations
/// and the rest may not is exactly the shape a host configures a write policy for. Before this, all of
/// them saw a live Apply and found out after typing the database identity into the confirmation.
/// </para>
/// <para>
/// Disabled and not hidden, with the sentence on the page as well as in the hover text. The service
/// refuses regardless: <c>ISchemaDataService.ApplyAsync</c> resolves the scope with
/// <c>ApplySchemaChanges</c> and throws (AGENTS.md hard rule 5).
/// </para>
/// </remarks>
public class SchemaWritePolicyTests
{
    private const string WritePolicy = "MartenStudioWriter";

    [Fact]
    public void Apply_is_disabled_and_says_why_for_a_visitor_the_write_policy_refuses()
    {
        using StudioComponentContext context = Context(static resource => resource.Capability is null, out _);

        var page = context.Render<SchemaPage>();
        ClickPreview(page);

        IElement apply = page.WaitForElement(".ms-schema-apply");
        apply.HasAttribute("disabled").Should().BeTrue();
        apply.GetAttribute("title").Should().Contain("may not apply schema changes here");

        page.Find(".ms-write-refusal").TextContent
            .Should().Be(WritePolicyRefusal.For(StudioCapability.ApplySchemaChanges));
    }

    /// <summary>
    /// The button stays. Hiding it would make a studio whose host enabled <c>ApplySchemaChanges</c> look
    /// identical to one whose host did not, and those are two different things to go and ask about.
    /// </summary>
    [Fact]
    public void The_refused_apply_button_is_rendered_rather_than_removed()
    {
        using StudioComponentContext context = Context(static resource => resource.Capability is null, out _);

        var page = context.Render<SchemaPage>();

        page.FindAll(".ms-schema-apply").Should().ContainSingle();
        page.FindAll(".ms-capability-disabled").Should().BeEmpty(
            "the capability is on; it is the visitor who was refused");
    }

    /// <summary>
    /// And with a policy that allows, the same page after the same preview offers it - so the test above
    /// is about the policy and not about the migration being empty.
    /// </summary>
    [Fact]
    public void Apply_is_live_for_a_visitor_the_same_policy_allows()
    {
        using StudioComponentContext context = Context(static _ => true, out _);

        var page = context.Render<SchemaPage>();
        ClickPreview(page);

        page.WaitForAssertion(() =>
            page.Find(".ms-schema-apply").HasAttribute("disabled").Should().BeFalse());
        page.FindAll(".ms-write-refusal").Should().BeEmpty();
    }

    /// <summary>
    /// The read half of the tab is untouched: Check and Preview are reads, and a person who may not
    /// migrate may perfectly well need to know whether the schema has drifted.
    /// </summary>
    [Fact]
    public void Checking_and_previewing_stay_available_to_a_refused_visitor()
    {
        using StudioComponentContext context = Context(static resource => resource.Capability is null, out FakeSchemaDataService schema);

        var page = context.Render<SchemaPage>();
        ClickPreview(page);

        page.WaitForAssertion(() => schema.Previews.Should().Be(1));
        page.FindAll(".ms-schema-toolbar .ms-button")
            .Should().AllSatisfy(static x => x.HasAttribute("disabled").Should().BeFalse());
    }

    /// <summary>
    /// A capability that is off names the option and says nothing about the account: that refusal is
    /// about the application, and the guard reaches it before a scope is ever resolved.
    /// </summary>
    [Fact]
    public void A_capability_that_is_off_names_the_option_rather_than_the_account()
    {
        var fake = new FakeSchemaDataService();
        using var context = new StudioComponentContext(services => services.AddSingleton<ISchemaDataService>(fake));
        context.WithStores("default");
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.DenyEverything();

        var page = context.Render<SchemaPage>();

        page.Find(".ms-capability-disabled").TextContent
            .Should().Contain("MartenStudioOptions.Capabilities.ApplySchemaChanges");
        page.FindAll(".ms-write-refusal").Should().BeEmpty();
        page.FindAll(".ms-schema-apply").Should().BeEmpty();
    }

    /// <summary>
    /// Clicks the Drift tab's Preview button, found by its text and re-found on every call: clicking it
    /// re-renders the toolbar, and an element captured before the render is no longer in the document.
    /// </summary>
    private static void ClickPreview(IRenderedComponent<SchemaPage> page) =>
        page.FindAll(".ms-schema-toolbar .ms-button")
            .Single(static x => x.TextContent.Contains("Preview migration", StringComparison.Ordinal))
            .Click();

    /// <summary>
    /// A studio whose host enabled every capability, with a migration ready to preview so Apply is
    /// offered for anything but the policy.
    /// </summary>
    private static StudioComponentContext Context(Func<MartenStoreResource, bool> rule, out FakeSchemaDataService schema)
    {
        var fake = new FakeSchemaDataService
        {
            Preview = new MigrationPreview("create index x on y (z);", 1, [], "Update", null),
            DatabaseIdentity = "localhost.marten",
        };

        var context = new StudioComponentContext(services => services.AddSingleton<ISchemaDataService>(fake));
        context.WithStores("default");
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.Allow(rule);

        schema = fake;
        return context;
    }
}
