using Bunit;

using MartenStudio.Components.Pages;
using MartenStudio.Services;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The Activity page: what this process's studio has been asked to change, newest first, failures
/// included.
/// </summary>
public class ActivityTests
{
    private static void Record(StudioComponentContext context, string user, string action, bool succeeded)
    {
        context.AuthenticationState.SignIn(user);
        context.ActionLog.Record(action, "customer/42", succeeded, succeeded ? "done" : "it did not work");
    }

    [Fact]
    public void An_empty_ring_is_an_empty_state_rather_than_an_empty_table()
    {
        using var context = new StudioComponentContext();

        var page = context.Render<Activity>();

        page.Find(".ms-empty-title").TextContent.Should().Be("Nothing to show");
        page.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public void Every_column_of_the_entry_is_rendered()
    {
        using var context = new StudioComponentContext();
        context.AuthenticationState.SignIn("ops");
        context.ActionLog.Record("DeleteDocument", "customer/42", succeeded: true, "deleted", StudioCapability.DeleteDocuments);

        var page = context.Render<Activity>();

        var cells = page.TextOfAll("tbody tr td");
        cells.Should().Contain("ops");
        cells.Should().Contain("DeleteDocument");
        cells.Should().Contain("customer/42");
        cells.Should().Contain("succeeded");
        cells.Should().Contain(nameof(StudioCapability.DeleteDocuments));
        cells.Should().Contain("deleted");

        // Store, database and tenant used to be three columns of the same three values repeated on every
        // row; they are one cell now, and the tenant half of it still says "all" when the view is not
        // filtered to one.
        page.Find("tbody tr .ms-activity-scope").TextContent.Trim()
            .Should().EndWith("· all", "a view that is not filtered to one tenant says so");
    }

    /// <summary>
    /// The three scope columns are one. On a single-store host they carried "default", the same database
    /// identity and "all" on every row, which was 300px of the table's width to repeat one fact - and the
    /// table was 1212px wide inside a 1200px column because of it.
    /// </summary>
    [Fact]
    public void The_scope_is_one_cell_and_still_carries_all_three_values()
    {
        using var context = new StudioComponentContext();
        context.AuthenticationState.SignIn("ops");
        context.ActionLog.Record(
            "DeleteDocument",
            "customer/42",
            succeeded: true,
            "deleted",
            capability: null,
            new StudioScope("orders", "localhost.marten", "acme"));

        var page = context.Render<Activity>();

        page.TextOfAll("thead th").Should().Equal(
            "When", "User", "Scope", "Action", "Target", "Outcome", "Capability", "Message");

        var scope = page.Find("tbody tr .ms-activity-scope");
        scope.TextContent.Trim().Should().Be("orders · localhost.marten · acme");
        scope.GetAttribute("title").Should().Be("Store orders, database localhost.marten, tenant acme");
    }

    /// <summary>
    /// The timestamp is one line. Squeezed between nine other columns it wrapped
    /// <c>2026-09-19 10:13:47 +03:00</c> onto four of them and made every row of the log 80px tall.
    /// </summary>
    [Fact]
    public void The_when_cell_never_wraps_and_the_table_scrolls_rather_than_being_clipped()
    {
        using var context = new StudioComponentContext();
        Record(context, "ops", "DeleteDocument", succeeded: true);

        var page = context.Render<Activity>();

        page.Find("tbody tr td").ClassList.Should().Contain("ms-activity-when");
        page.ShouldPutEveryTableInALabelledScrollRegion();
    }

    [Fact]
    public void A_failure_is_recorded_too_and_reads_as_failed()
    {
        using var context = new StudioComponentContext();
        Record(context, "viewer", "ArchiveStream", succeeded: false);

        var page = context.Render<Activity>();

        page.TextOfAll("tbody tr td").Should().Contain("failed");
        page.Find(".ms-state-dot").ClassList.Should().Contain("ms-state-error");
    }

    [Fact]
    public void The_newest_action_is_first()
    {
        using var context = new StudioComponentContext();
        Record(context, "ops", "First", succeeded: true);
        Record(context, "ops", "Second", succeeded: true);

        var page = context.Render<Activity>();

        page.TextOfAll("tbody tr").Should().HaveCount(2);
        page.Find("tbody tr").TextContent.Should().Contain("Second");
    }

    [Theory]
    [InlineData("Archive", 1)]
    [InlineData("ops", 1)]
    [InlineData("failed", 1)]
    [InlineData("succeeded", 1)]
    [InlineData("nothing like this", 0)]
    public void The_filter_matches_action_user_and_outcome(string filter, int expected)
    {
        using var context = new StudioComponentContext();
        Record(context, "ops", "DeleteDocument", succeeded: true);
        Record(context, "viewer", "ArchiveStream", succeeded: false);

        var page = context.Render<Activity>();
        page.Find(".ms-search-filter").Input(filter);

        // The box debounces, so the filtered render arrives shortly after the keystroke.
        page.WaitForAssertion(() => page.TextOfAll("tbody tr").Should().HaveCount(expected));
    }

    [Fact]
    public void Timestamps_are_rendered_in_the_selected_time_zone()
    {
        using var context = new StudioComponentContext();
        context.State.SelectedTimeZoneId = TimeZoneInfo.Utc.Id;
        Record(context, "ops", "DeleteDocument", succeeded: true);

        var page = context.Render<Activity>();

        page.Find("tbody tr td").TextContent.Trim().Should().EndWith("+00:00");
    }

    // -------------------------------------------------------------------------------------------
    // The ring is process-wide, so what it shows is not
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The one place in the studio where one visitor's actions can be read by another: the action ring is
    /// a singleton and every circuit writes into it. Every entry goes back through the per-store policy,
    /// against the store, database and tenant the action was aimed at — the same question that was asked
    /// before the action could have run in the first place.
    /// </summary>
    [Fact]
    public void An_action_against_a_store_the_visitor_may_not_see_is_not_listed()
    {
        using var context = new StudioComponentContext().WithPolicy("mine");
        context.AuthenticationState.SignIn("ops");
        context.ActionLog.Record("MineAction", "customer/1", succeeded: true, "done", capability: null, new StudioScope("mine", "localhost.marten", null));
        context.ActionLog.Record("TheirsAction", "customer/2", succeeded: true, "done", capability: null, new StudioScope("theirs", "localhost.marten", null));

        var page = context.Render<Activity>();

        var actions = page.TextOfAll("tbody tr td");
        actions.Should().Contain("MineAction");
        actions.Should().NotContain("TheirsAction");
    }

    [Fact]
    public void With_no_store_policy_configured_nothing_is_filtered_out()
    {
        using var context = new StudioComponentContext();
        context.AuthenticationState.SignIn("ops");
        context.ActionLog.Record("MineAction", "customer/1", succeeded: true, "done", capability: null, new StudioScope("mine", "localhost.marten", null));
        context.ActionLog.Record("TheirsAction", "customer/2", succeeded: true, "done", capability: null, new StudioScope("theirs", "localhost.marten", null));

        var actions = context.Render<Activity>().TextOfAll("tbody tr td");

        actions.Should().Contain("MineAction").And.Contain("TheirsAction");
    }

    /// <summary>
    /// Rows are keyed by the ring's sequence number, not by the entry: the entry is a record with value
    /// equality, so two identical actions collide on one key and the renderer reuses the wrong row.
    /// </summary>
    [Fact]
    public void Two_identical_actions_are_two_rows()
    {
        using var context = new StudioComponentContext();
        context.AuthenticationState.SignIn("ops");
        context.ActionLog.Record("DeleteDocument", "customer/42", succeeded: true, "deleted");
        context.ActionLog.Record("DeleteDocument", "customer/42", succeeded: true, "deleted");

        context.Render<Activity>().TextOfAll("tbody tr").Should().HaveCount(2);
    }
}
