using Bunit;

using MartenStudio.Components.Pages;
using MartenStudio.Services;

using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// SEC-fix F2: a database-browser read names a non-Marten table and the columns a filter was on, and a SQL
    /// console run carries two hundred characters of its statement. The store policy says nothing about who may
    /// learn those, so a visitor the store policy allows and the write policy refuses read here what operators
    /// browsed. They are shown only to a visitor who may make that read: the capability on, and the write
    /// policy's yes with the capability named, against the entry's own scope.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void A_read_beyond_the_store_is_listed_only_for_a_visitor_who_may_make_it(bool capabilities, bool writePolicyAllows, bool listed)
    {
        using var context = ReadsBeyondTheStore(capabilities, writePolicyAllows);

        var cells = context.Render<Activity>().TextOfAll("tbody tr td");

        cells.Should().Contain("DeleteDocument", "an entry that is no read beyond the store is the store policy's alone");

        if (listed)
        {
            cells.Should().Contain("quartz.qrtz_triggers").And.Contain("select * from legacy.payroll");
            cells.Should().Contain("hr.salaries", "a store-policy refusal of a browser read is shown to a visitor who may browse");
        }
        else
        {
            cells.Should().NotContain("quartz.qrtz_triggers", "a browser read names a table the visitor may not browse")
                .And.NotContain("select * from legacy.payroll", "a console run carries the statement");
            cells.Should().NotContain(static x => x.Contains("grade", StringComparison.Ordinal), "nor the column the filter was on");
            cells.Should().NotContain("hr.salaries",
                "POLISH P2: a browser read the store policy refused names what was asked for, so it is under the capability too");
        }
    }

    /// <summary>
    /// The Overview's recent activity is the same filter over the same ring (<see cref="StudioActionLog.GetVisibleAsync" />):
    /// a visitor the write policy refuses the browser does not see an operator's reads there either.
    /// </summary>
    [Fact]
    public async Task The_overview_filters_reads_beyond_the_store_the_way_Activity_does()
    {
        using var context = ReadsBeyondTheStore(capabilities: true, writePolicyAllows: false);
        context.StoreInfo.WithStore();
        await context.ReadyAsync();

        var page = context.Render<Overview>();

        page.WaitForAssertion(() => page.TextOfAll(".ms-overview-activity .ms-overview-list-name").Should().Equal("DeleteDocument"));
    }

    /// <summary>
    /// A ring holding a database-browser read and a SQL console run by an operator, and a document delete, read
    /// by a visitor the store policy allows everywhere - and the write policy as <paramref name="writePolicyAllows" /> says.
    /// </summary>
    private static StudioComponentContext ReadsBeyondTheStore(bool capabilities, bool writePolicyAllows)
    {
        var context = new StudioComponentContext();
        context.Options.StoreAuthorizationPolicy = StudioComponentContext.StorePolicyName;
        context.Options.WriteAuthorizationPolicy = "MartenStudioWrite";
        context.Options.Capabilities.BrowseDatabase = capabilities;
        context.Options.Capabilities.RunSql = capabilities;
        context.Options.Capabilities.DeleteDocuments = true;

        context.AuthenticationState.SignIn("ops");
        var wholeDatabase = new StudioScope("default", "localhost.marten", null);

        context.ActionLog.Record(
            "Browse database rows", "quartz.qrtz_triggers", succeeded: true,
            "Rows read with 1 filter term(s) on grade, sorted by (key) asc.", StudioCapability.BrowseDatabase, wholeDatabase);
        context.ActionLog.Record(
            "Run SQL", "select * from legacy.payroll", succeeded: true, "12 rows", StudioCapability.RunSql, wholeDatabase);
        context.ActionLog.Record(
            "DeleteDocument", "customer/42", succeeded: true, "deleted", StudioCapability.DeleteDocuments, wholeDatabase);

        // POLISH P2: what DatabaseAccess records when the store policy refuses a browser read - the refused
        // name as the target, under BrowseDatabase (DatabaseAccessTests pins that it is recorded so).
        context.ActionLog.RecordScopeDenied(
            wholeDatabase, StudioComponentContext.StorePolicyName, "Open database object", "hr.salaries", StudioCapability.BrowseDatabase);

        context.AuthenticationState.SignIn("viewer");
        context.AuthorizationService.Allow(resource => resource.Capability is null || writePolicyAllows);

        return context;
    }

    // -------------------------------------------------------------------------------------------
    // POLISH P5 - one policy question per scope and capability per sweep, not two per entry
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A full ring of browser reads about one database, read by a visitor behind both policies: the store
    /// policy and the write policy are each asked once for the sweep - two evaluations, where it was a
    /// thousand - and every entry is still kept.
    /// </summary>
    [Fact]
    public async Task A_full_ring_of_one_scope_costs_one_question_per_policy()
    {
        using var context = ReadsOfOneScope(entries: StudioActionLogService.MaxEntries, scopes: 1);
        context.AuthorizationService.Calls.Clear();

        List<StudioActionLogEntry> visible = await VisibleAsync(context);

        visible.Should().HaveCount(StudioActionLogService.MaxEntries);
        context.AuthorizationService.Calls.Should().HaveCount(2, "one store-policy question and one write-policy question");
        context.AuthorizationService.Calls.Select(static x => x.Resource.Capability).Should()
            .BeEquivalentTo([null, nameof(StudioCapability.BrowseDatabase)]);
    }

    /// <summary>
    /// The memory is per question, never per sweep alone: entries about three databases cost three questions of
    /// each policy, and a database the write policy refuses is still refused on every one of its entries.
    /// </summary>
    [Fact]
    public async Task Each_scope_is_asked_once_and_answered_for_itself()
    {
        using var context = ReadsOfOneScope(entries: 300, scopes: 3);
        context.AuthorizationService.Allow(static resource => resource.Capability is null || resource.DatabaseIdentifier != "db-1");
        context.AuthorizationService.Calls.Clear();

        List<StudioActionLogEntry> visible = await VisibleAsync(context);

        context.AuthorizationService.Calls.Should().HaveCount(6, "three scopes, two policies each");
        visible.Should().HaveCount(200).And.OnlyContain(static x => x.DatabaseId != "db-1");
    }

    /// <summary>And the next sweep asks again: a policy's answer may have changed, and nothing outlives the sweep.</summary>
    [Fact]
    public async Task A_second_sweep_asks_again()
    {
        using var context = ReadsOfOneScope(entries: 50, scopes: 1);
        context.AuthorizationService.Calls.Clear();

        (await VisibleAsync(context)).Should().HaveCount(50);

        context.AuthorizationService.DenyEverything();

        (await VisibleAsync(context)).Should().BeEmpty("the refusal is read on the next sweep, not remembered past the last one");
        context.AuthorizationService.Calls.Should().HaveCount(3, "two questions for the first sweep, then the store policy's no");
    }

    private static StudioComponentContext ReadsOfOneScope(int entries, int scopes)
    {
        var context = new StudioComponentContext();
        context.Options.StoreAuthorizationPolicy = StudioComponentContext.StorePolicyName;
        context.Options.WriteAuthorizationPolicy = "MartenStudioWrite";
        context.Options.Capabilities.BrowseDatabase = true;
        context.AuthenticationState.SignIn("ops");

        for (int i = 0; i < entries; i++)
        {
            context.ActionLog.Record(
                "Browse database rows", "quartz.qrtz_triggers", succeeded: true, "Rows read.", StudioCapability.BrowseDatabase,
                new StudioScope("default", "db-" + (i % scopes), null));
        }

        context.AuthenticationState.SignIn("viewer");
        return context;
    }

    private static ValueTask<List<StudioActionLogEntry>> VisibleAsync(StudioComponentContext context) =>
        context.ActionLog.GetVisibleAsync(
            context.Services.GetRequiredService<StudioAuthorization>(),
            context.Services.GetRequiredService<StudioCapabilityGuard>(),
            cancellationToken: Xunit.TestContext.Current.CancellationToken);

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
