using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services.Projections;
using MartenStudio.Tests.Components;

using PostgresStudio.Services;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// 1.2-PROJ (a): the Remove button on an orphaned progression row - shown where Marten's call is shown, off without
/// <c>CorrectProgression</c> and saying so, confirmed first, and reporting a refusal at the click as a value.
/// </summary>
public class OrphanRemovalPageTests
{
    private const string ConfirmButton = ".ms-confirm-actions .ms-button-danger";

    private const string ConfirmDialog = ".ms-confirm-dialog";

    private const string Remove = "[data-orphaned-row='RetiredProjection:All'] .ms-orphan-remove";

    [Fact]
    public async Task Remove_is_offered_where_marten_s_call_is_shown_and_nowhere_else()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData
            .WithProjection("DailySales")
            .WithOrphan("RetiredProjection:All")
            .WithOrphan("DailySales:V1:All", reason: OrphanReason.OtherVersion, readByDaemon: true);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(Remove).HasAttribute("disabled").Should().BeFalse();
        page.Find(".ms-orphaned-progression-calls").TextContent.Should().Contain("RetiredProjection:All");

        page.FindAll("[data-orphaned-row='DailySales:V1:All'] .ms-orphan-remove").Should().BeEmpty(
            "the daemon may still read that row: it has no call shown, and no button");
        page.FindAll(".ms-orphan-remove").Should().ContainSingle();
    }

    [Fact]
    public async Task Without_the_capability_the_button_is_disabled_and_names_the_option()
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.WithProjection("DailySales").WithOrphan("RetiredProjection:All");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        IElement button = page.Find(Remove);
        button.HasAttribute("disabled").Should().BeTrue();
        button.GetAttribute("title").Should().Contain("MartenStudioOptions.Capabilities.CorrectProgression");

        page.Find(".ms-orphaned-progression .ms-capability-disabled").TextContent
            .Should().Contain("CorrectProgression").And.Contain("Removing an orphaned progression row");

        context.ProjectionData.Calls.Should().NotContain(x => x.StartsWith("RemoveOrphan:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The row is the database's, so the button answers the question the service asks - <c>CorrectProgression</c> at
    /// (store, database, null) - and not the tenant-scoped one the Advance buttons answer: a visitor the write policy allows one
    /// tenant and refuses the database as a whole sees the refusal, not a live button and an error toast.
    /// </summary>
    [Fact]
    public async Task A_visitor_the_policy_refuses_the_database_as_a_whole_sees_the_refusal_not_a_live_button()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = "studio-write";
        context.AuthorizationService.Allow(static resource => resource.Capability is null || resource.TenantId is not null);
        context.ProjectionData.WithProjection("DailySales").WithOrphan("RetiredProjection:All");
        await context.ReadyAsync();
        context.Catalog.WithTenants("default", new TenantList(["acme"], IsTruncated: false, TenantListSource.Configured));
        await context.State.RefreshScopeListingsAsync(Xunit.TestContext.Current.CancellationToken);
        await context.State.SetScopeAsync("default", null, "acme", Xunit.TestContext.Current.CancellationToken);

        context.State.ActiveScope!.TenantId.Should().Be("acme", "the tenant is selected");

        var page = context.Render<Page>();

        page.Find(".ms-daemon-advance-high-water").HasAttribute("disabled").Should().BeFalse("the tenant is allowed that");
        page.Find(Remove).HasAttribute("disabled").Should().BeTrue();
        page.Find(Remove).GetAttribute("title").Should().Be(WritePolicyRefusal.For(StudioCapability.CorrectProgression));
        page.Find(".ms-orphaned-progression .ms-write-refusal").TextContent
            .Should().Be(WritePolicyRefusal.For(StudioCapability.CorrectProgression));
    }

    [Fact]
    public async Task Under_ReadOnly_the_button_is_disabled()
    {
        await using var context = new StudioComponentContext().WithReadOnly();
        context.ProjectionData.WithProjection("DailySales").WithOrphan("RetiredProjection:All");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(Remove).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task A_names_withheld_visitor_is_offered_no_button()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithOrphan("RetiredProjection:All").WithOrphanNamesWithheld();
        await context.ReadyAsync();

        context.Render<Page>().FindAll(".ms-orphan-remove").Should().BeEmpty();
    }

    [Fact]
    public async Task Pressing_Remove_asks_first_in_the_words_of_the_sql_note_and_then_reaches_the_service()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithOrphan("RetiredProjection:All");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.Find(Remove).Click();

        IElement dialog = page.WaitForElement(ConfirmDialog);
        dialog.TextContent.Should().Contain(OrphanWording.RemoveTitle);
        dialog.TextContent.Should().Contain("RetiredProjection:All");
        dialog.TextContent.Should().Contain("ensures the event store's storage under the store's AutoCreate");
        dialog.TextContent.Should().Contain("(CreateOrUpdate for this database)");
        dialog.TextContent.Should().Contain("the documents and tables the projection wrote stay");
        dialog.TextContent.Should().Contain("whatever tenant is selected");
        context.ProjectionData.Calls.Should().NotContain("RemoveOrphan:RetiredProjection:All", "nothing happens before the confirmation");

        page.Find(ConfirmButton).Click();

        page.WaitForAssertion(() => context.ProjectionData.Calls.Should().Contain("RemoveOrphan:RetiredProjection:All"));
    }

    [Fact]
    public async Task Cancelling_the_dialog_removes_nothing()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithOrphan("RetiredProjection:All");
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(Remove).Click();
        page.WaitForElement(ConfirmDialog);

        page.Find(".ms-confirm-actions .ms-button:not(.ms-button-danger)").Click();

        page.WaitForAssertion(() => page.FindAll(ConfirmDialog).Should().BeEmpty());
        context.ProjectionData.Calls.Should().NotContain(x => x.StartsWith("RemoveOrphan:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refusal_at_the_click_is_shown_as_a_message_not_a_failure()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.OrphanRemovalRefusal = OrphanWording.RemovalNoLongerOrphan;
        context.ProjectionData.WithProjection("DailySales").WithOrphan("RetiredProjection:All");
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(Remove).Click();
        page.WaitForElement(ConfirmDialog);
        page.Find(ConfirmButton).Click();

        page.WaitForAssertion(() => context.ProjectionData.Calls.Should().Contain("RemoveOrphan:RetiredProjection:All"));
        page.WaitForAssertion(() => page.FindAll(ConfirmDialog).Should().BeEmpty());

        // An Info toast carrying the service's words - not the red one a failure gets, and not the green one a removal gets.
        page.WaitForAssertion(() => context.Toasts.Messages.Should().ContainSingle()
            .Which.Should().Be(new ToastMessage(context.Toasts.Messages[0].Id, OrphanWording.RemovalNoLongerOrphan, ToastLevel.Info)));
    }

    [Fact]
    public async Task A_removal_is_a_success_toast()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithOrphan("RetiredProjection:All");
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(Remove).Click();
        page.WaitForElement(ConfirmDialog);
        page.Find(ConfirmButton).Click();

        page.WaitForAssertion(() => context.Toasts.Messages.Should().ContainSingle()
            .Which.Should().Match<ToastMessage>(x => x.Level == ToastLevel.Success && x.Message == "Removed the progression row RetiredProjection:All."));
    }

    [Fact]
    public async Task The_dialog_shows_when_the_row_was_last_written_and_the_rolling_deploy_clause()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales").WithOrphan("RetiredProjection:All");
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(Remove).Click();

        string text = page.WaitForElement(ConfirmDialog).TextContent;
        text.Should().Contain("The row was last written 1970-01-01").And.Contain("(database server time)");
        text.Should().Contain("Remove it once the rename or removal has shipped");
    }

    [Fact]
    public void The_wording_says_what_the_service_does_and_does_not_leave_to_the_host()
    {
        OrphanWording.HandOver.Should().Contain("Capabilities.CorrectProgression").And.NotContain("Marten Studio never does");
        OrphanWording.RemoveMessage("X:All", null).Should()
            .Contain("removes the row X:All from mt_event_progression in this database, and nothing else")
            .And.NotContain("for this database)");
    }
}
