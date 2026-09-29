using AngleSharp.Dom;

using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Components;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// Two things the projections screen says, from the DB-0-fix review: when nothing has advanced is worth an
/// alert, and how far a progression correction reaches.
/// </summary>
public class ProjectionsPageReachAndBannerTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // Item 8: the amber banner on a store that has never appended an event
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A store with no events yet has projections that have never advanced because there has been nothing
    /// to project. A <c>role="alert"</c> saying nothing appears to be running them - or that the external
    /// system "has not processed anything yet" - is an alarm about a fresh install.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_store_with_no_events_yet_raises_no_banner_whoever_runs_its_projections(bool externallyManaged)
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.HighWaterMark = 0;
        _ = externallyManaged ? context.ProjectionData.WithExternallyManagedDaemon() : context.ProjectionData.WithNoDaemonHere();
        context.ProjectionData.WithProjection("DailySales", sequence: 0, hasProgressRow: false);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-no-daemon-banner").Should().BeEmpty("there has been nothing to advance over");
        page.FindAll("[role='alert']").Should().BeEmpty();
    }

    /// <summary>
    /// The anti-vacuity half: the same page, once there is an event to have advanced over, does raise it -
    /// so the test above is about the high-water mark and not about a banner that no longer renders.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_same_store_with_events_nothing_has_advanced_over_does_raise_it(bool externallyManaged)
    {
        await using var context = new StudioComponentContext();
        context.ProjectionData.HighWaterMark = 1;
        _ = externallyManaged ? context.ProjectionData.WithExternallyManagedDaemon() : context.ProjectionData.WithNoDaemonHere();
        context.ProjectionData.WithProjection("DailySales", sequence: 0, hasProgressRow: false);
        await context.ReadyAsync();

        var page = context.Render<Page>();

        page.FindAll(".ms-no-daemon-banner").Should().ContainSingle();
    }

    [Fact]
    public void NoDaemonAnywhere_needs_something_to_have_advanced_over()
    {
        var daemon = new DaemonStatus(DaemonHostingState.NotHostedInThisProcess, false, "Disabled", [], false, null, "none");
        ProjectionInfo projection = new("DailySales", JasperFx.Events.Projections.ProjectionLifecycle.Async, "Projection", 1, "DailySalesProjection", ["DailySales:All"]);

        new ProjectionsView([projection], [], daemon, HighWaterMark: 0, DateTimeOffset.UnixEpoch).NoDaemonAnywhere.Should().BeFalse();
        new ProjectionsView([projection], [], daemon, HighWaterMark: 1, DateTimeOffset.UnixEpoch).NoDaemonAnywhere.Should().BeTrue();
    }

    // ------------------------------------------------------------------------------------------------
    // Item 9: a correction without a tenant reaches every database
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Marten 9.31 has no per-database form of either correction: without a tenant it runs against every
    /// database of the store, the service refuses it unless the visitor may change all of them, and the
    /// dialog says so - naming the databases this visitor can see.
    /// </summary>
    [Theory]
    [InlineData(".ms-daemon-advance-high-water", "Advance the high water mark?")]
    [InlineData(".ms-daemon-correct-progression", "Correct the progression?")]
    public async Task A_correction_on_a_multi_database_store_says_it_reaches_every_database(string button, string title)
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "db-a.marten", "db-b.marten");
        context.ProjectionData.WithProjection("DailySales");
        await context.State.EnsureInitializedAsync(Token);

        var page = context.Render<Page>();
        page.Find(button).Click();

        IElement dialog = page.WaitForElement(".ms-confirm-dialog");

        dialog.TextContent.Should().Contain(title);
        dialog.TextContent.Should().Contain("every database of this store, not only the one selected: db-a.marten, db-b.marten");
        dialog.TextContent.Should().Contain("refused unless you may change every one of them");
    }

    [Fact]
    public async Task A_correction_on_a_single_database_store_says_nothing_about_other_databases()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(".ms-daemon-advance-high-water").Click();

        page.WaitForElement(".ms-confirm-dialog").TextContent.Should().NotContain("every database of this store");
    }

    /// <summary>
    /// DB-0-fix-2, B1: with a tenant selected, Marten corrects that tenant's database - not every
    /// database, and not only that tenant either. The dialog used to say "that tenant's database only",
    /// which read as "that tenant only", while the high-water mark and the progression rows it moves are
    /// the database's and every tenant stored there is read against them.
    /// </summary>
    [Theory]
    [InlineData(".ms-daemon-advance-high-water")]
    [InlineData(".ms-daemon-correct-progression")]
    public async Task A_correction_with_a_tenant_selected_says_it_reaches_every_tenant_in_that_database(string button)
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.Catalog
            .WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "db-a.marten", "db-b.marten")
            .WithTenants("default", new TenantList(["acme"], IsTruncated: false, TenantListSource.Configured));
        context.ProjectionData.WithProjection("DailySales");
        await context.State.EnsureInitializedAsync(Token);
        await context.State.SetScopeAsync("default", "db-a.marten", "acme", Token);

        var page = context.Render<Page>();
        page.Find(button).Click();

        string text = page.WaitForElement(".ms-confirm-dialog").TextContent;

        text.Should().NotContain("every database of this store", "a tenant correction reaches the tenant's database, not all of them");
        text.Should().Contain("With tenant acme selected, Marten does this in that tenant's database, and it is not limited to that tenant");
        text.Should().Contain("every tenant stored in it is affected");
        text.Should().Contain("refused unless you may change that database as a whole, or it holds this tenant and no other");
    }

    /// <summary>
    /// DB-0-fix-2, B1: a rebuild asked from a tenant's scope is not that tenant's rebuild either - Marten
    /// empties the projection's storage for the whole database - and the typed-name dialog says so before
    /// anybody types the name.
    /// </summary>
    [Fact]
    public async Task The_rebuild_dialog_says_a_tenant_in_scope_does_not_narrow_the_rebuild()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.Catalog
            .WithStore("default", "Default", DatabaseCardinality.Single, "localhost.marten")
            .WithTenants("default", new TenantList(["acme"], IsTruncated: false, TenantListSource.Configured));
        context.ProjectionData.WithProjection("DailySales");
        await context.State.EnsureInitializedAsync(Token);
        await context.State.SetScopeAsync("default", "localhost.marten", "acme", Token);

        var page = context.Render<Page>();
        page.Find(".ms-projection-rebuild").Click();

        IElement note = page.WaitForElement(".ms-confirm-dialog .ms-rebuild-tenant-note");
        note.TextContent.Should().Contain("acme").And.Contain("is not limited to it")
            .And.Contain("for every tenant stored in this database");
    }

    /// <summary>Without a tenant in scope there is nothing to correct in the reader's mind, and no note.</summary>
    [Fact]
    public async Task The_rebuild_dialog_has_no_tenant_note_without_a_tenant()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(".ms-projection-rebuild").Click();

        page.WaitForElement(".ms-confirm-dialog");
        page.FindAll(".ms-rebuild-tenant-note").Should().BeEmpty();
    }
}
