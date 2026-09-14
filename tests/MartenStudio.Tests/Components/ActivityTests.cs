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
        cells.Should().Contain("all", "a view that is not filtered to one tenant says so");
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
}
