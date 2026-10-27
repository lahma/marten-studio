using AngleSharp.Dom;

using Bunit;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Conventions;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>
/// M3, Events › Dead letters grouped by cause: the view in the query string (D9), the groups with their counts,
/// tenants and sample, a group opened in the list, and "Skip this group" - gated, and confirmed by typing the count.
/// </summary>
public class DeadLetterGroupsTests
{
    private const string GroupedUri = "marten/events/dead-letters?view=grouped";

    // ------------------------------------------------------------------------------------------------
    // The view
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_grouped_view_is_in_the_query_string_and_reads_the_groups_not_the_list()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group());

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);

        page.FindAll(".ms-dl-group").Should().ContainSingle();
        context.EventData.GroupReads.Should().ContainSingle();
        context.EventData.DeadLetterQueries.Should().BeEmpty("the list is not read under the grouped view");
        page.FindAll("#ms-dl-projection").Should().BeEmpty("the list's filters are the list's");

        IElement active = page.Find(".ms-dl-views .ms-tab-active");
        active.TextContent.Should().Be("Grouped by cause");
        active.GetAttribute("aria-current").Should().Be("page");
    }

    [Fact]
    public async Task The_list_is_the_default_view_and_links_to_the_grouped_one()
    {
        using StudioComponentContext context = await ContextAsync();

        IRenderedComponent<DeadLetters> page = Render(context, "marten/events/dead-letters");

        page.Find(".ms-dl-views .ms-tab-active").TextContent.Should().Be("List");
        page.FindAll(".ms-dl-views a").Single(static x => x.TextContent == "Grouped by cause")
            .GetAttribute("href").Should().Contain("events/dead-letters?view=grouped");
        context.EventData.GroupReads.Should().BeEmpty();
        context.EventData.DeadLetterQueries.Should().ContainSingle();
    }

    /// <summary>Each group says what it is - projection and shard, event type, exception and fingerprint - and how big.</summary>
    [Fact]
    public async Task A_group_shows_its_key_its_counts_its_first_and_last_seen_and_a_sample()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(
            FakeEventDataService.Group(letters: 1_204, events: 1_190, skipped: 12, tenantCount: 2, tenantNames: ["acme", "globex"]));

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);

        IElement row = page.Find(".ms-dl-group");
        row.QuerySelector(".ms-dl-group-projection")!.TextContent.Should().Be("OrderSummary");
        row.QuerySelector(".ms-dl-group-shard")!.TextContent.Should().Be("All");
        row.QuerySelector(".ms-dl-group-type")!.TextContent.Should().Be("order_shipped");
        row.QuerySelector(".ms-dl-group-exception")!.TextContent.Should().Be("InvalidOperationException");
        row.QuerySelector(".ms-dl-group-fingerprint")!.TextContent.Should().Be("Failure to apply event #{n} Id({guid})");
        row.QuerySelector(".ms-dl-group-sample pre")!.TextContent.Should().StartWith("Failure to apply event #42");
        row.QuerySelectorAll(".ms-dl-group-count")[0].TextContent.Trim().Should().Be("1,204");
        row.QuerySelectorAll(".ms-dl-group-count")[1].TextContent.Should().Contain("1,190").And.Contain("12 skipped");
        row.QuerySelector(".ms-dl-group-tenant-count")!.TextContent.Should().Be("2");
        row.QuerySelector(".ms-dl-group-tenant-names")!.TextContent.Should().Be("acme, globex");
        row.QuerySelectorAll(".ms-time-ago").Should().HaveCount(2, "first and last seen");

        page.Find(".ms-dl-groups-summary").TextContent.Should().Be("1,204 dead letters in 1 group");
    }

    /// <summary>A letter whose event is gone has no event type, and says so rather than leaving a blank.</summary>
    [Fact]
    public async Task A_group_whose_event_is_gone_says_so()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group(eventType: null));

        Render(context, GroupedUri).Find(".ms-dl-group-type-missing").TextContent.Should().Be("not in mt_events");
    }

    /// <summary>
    /// D29: a group counts all of its tenants, and names only those this visitor's store policy passes - the service
    /// decides which; the row shows how many it did not name.
    /// </summary>
    [Fact]
    public async Task A_group_counts_every_tenant_and_names_only_the_ones_it_was_given()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group(tenantCount: 3, tenantNames: ["acme"]));

        IElement tenants = Render(context, GroupedUri).Find(".ms-dl-group-tenants");

        tenants.QuerySelector(".ms-dl-group-tenant-count")!.TextContent.Should().Be("3");
        tenants.QuerySelector(".ms-dl-group-tenant-names")!.TextContent.Should().Be("acme");
        tenants.QuerySelector(".ms-dl-group-tenants-unnamed")!.TextContent.Should().Be("2 not named here");
    }

    /// <summary>Opening a group is a link to the list with the group in the query string (D9).</summary>
    [Fact]
    public async Task Opening_a_group_links_to_the_list_prefiltered_to_it()
    {
        using StudioComponentContext context = await ContextAsync();
        DeadLetterGroup group = FakeEventDataService.Group();
        context.EventData.Groups = FakeEventDataService.GroupList(group);

        string href = Render(context, GroupedUri).Find(".ms-dl-group-open").GetAttribute("href")!;

        href.Should().Contain("events/dead-letters?group=" + group.Key.Encode());
    }

    /// <summary>The list, opened on a group: the same component and filters, the group handed to the service, and a way back.</summary>
    [Fact]
    public async Task The_list_opened_on_a_group_asks_for_that_group_and_says_which_it_is()
    {
        using StudioComponentContext context = await ContextAsync();
        DeadLetterGroupKey key = FakeEventDataService.Group().Key;
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(Guid.NewGuid())], false);

        IRenderedComponent<DeadLetters> page = Render(context, "marten/events/dead-letters?group=" + key.Encode() + "&projection=OrderSummary");

        DeadLetterQuery query = context.EventData.DeadLetterQueries[^1];
        query.Group.Should().Be(key);
        query.ProjectionName.Should().Be("OrderSummary", "the list's own filters still apply on top");

        page.Find(".ms-dl-group-filter-key").TextContent.Should().Be(key.Describe());
        page.FindAll("#ms-dl-projection").Should().ContainSingle("the list's own filters are there");
        page.FindAll(".ms-dead-letter-actions").Should().BeEmpty("until a letter is opened, as ever");
        page.Find(".ms-dl-group-filter").TextContent.Should().Contain("Back to the groups").And.Contain("Every dead letter");
    }

    /// <summary>
    /// SEC-FIX: a group's letters are found among the newest letters the grouped view groups; when the scope holds more, the
    /// list says so in the grouped view's own sentence - with letters listed or none.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task The_list_opened_on_a_group_whose_window_cut_says_so_in_the_grouped_views_sentence(int letters)
    {
        using StudioComponentContext context = await ContextAsync();
        DeadLetterGroupKey key = FakeEventDataService.Group().Key;
        context.EventData.DeadLetters = new DeadLetterPage(
            [.. Enumerable.Range(0, letters).Select(static _ => FakeEventDataService.DeadLetter(Guid.NewGuid()))], false) { LettersCut = true };

        IRenderedComponent<DeadLetters> page = Render(context, "marten/events/dead-letters?group=" + key.Encode());

        page.Find(".ms-dl-groups-cut").TextContent.Should().Be(DeadLetterGroupText.LettersCut);
        DeadLetterGroupText.LettersCut.Should().Contain("a group opened in the list shows its letters among them");
    }

    [Fact]
    public async Task The_list_opened_on_a_group_whose_window_did_not_cut_says_nothing_of_it()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(Guid.NewGuid())], false);

        IRenderedComponent<DeadLetters> page = Render(context, "marten/events/dead-letters?group=" + FakeEventDataService.Group().Key.Encode());

        page.FindAll(".ms-dl-groups-cut").Should().BeEmpty();
    }

    [Fact]
    public async Task A_group_link_the_page_cannot_read_lists_every_dead_letter_and_says_so()
    {
        using StudioComponentContext context = await ContextAsync();

        IRenderedComponent<DeadLetters> page = Render(context, "marten/events/dead-letters?group=not-a-group");

        context.EventData.DeadLetterQueries[^1].Group.Should().BeNull();
        page.Find(".ms-dl-group-filter").TextContent.Should().Contain("cannot read");
    }

    [Fact]
    public async Task Every_table_is_in_a_scroll_region()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group(), FakeEventDataService.Group(projection: "Other"));

        Render(context, GroupedUri).ShouldPutEveryTableInALabelledScrollRegion();
    }

    [Fact]
    public async Task No_dead_letters_is_said_plainly()
    {
        using StudioComponentContext context = await ContextAsync();

        Render(context, GroupedUri).Find(".ms-empty-title").TextContent.Should().Be("No dead letters");
    }

    /// <summary>Hard rule 14, as the list: a database with no event tables is not asked to group anything.</summary>
    [Fact]
    public async Task A_database_with_no_event_tables_is_not_asked_to_group()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Shape = new EventStoreShape { EventTablesExist = false };

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);

        context.EventData.GroupReads.Should().BeEmpty();
        page.Find(".ms-empty-title").TextContent.Should().Be("This store has no event storage yet");
    }

    [Fact]
    public async Task More_groups_than_were_read_are_counted()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group()) with { TotalGroups = 412 };

        Render(context, GroupedUri).Find(".ms-dl-groups-note").TextContent.Trim()
            .Should().Be("Showing the 1 largest of 412 groups.");
    }

    [Fact]
    public async Task A_read_cut_at_its_bound_says_the_groups_are_the_newest_letters()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group()) with { LettersTruncated = true };

        Render(context, GroupedUri).Find(".ms-dl-groups-cut").TextContent.Should().Be(DeadLetterGroupText.LettersCut);
    }

    // ------------------------------------------------------------------------------------------------
    // Skip this group: gated
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_the_capability_there_is_no_skip_and_the_option_is_named()
    {
        using StudioComponentContext context = await ContextAsync(capabilities: false);
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group());

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);

        page.FindAll(".ms-dl-group-skip").Should().BeEmpty();
        page.FindAll(".ms-dl-group-open").Should().ContainSingle("opening a group is a read");
        page.Find(".ms-dl-groups .ms-capability-disabled").TextContent.Should().Contain("Capabilities.ManageDeadLetters");
    }

    [Fact]
    public async Task A_visitor_the_write_policy_refuses_sees_the_skip_disabled_and_why()
    {
        using StudioComponentContext context = await ContextAsync(rule: static resource => resource.Capability is null);
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group());

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);

        IElement skip = page.Find(".ms-dl-group-skip");
        skip.HasAttribute("disabled").Should().BeTrue();
        skip.GetAttribute("title").Should().Be(WritePolicyRefusal.For(StudioCapability.ManageDeadLetters));
        page.Find(".ms-dl-groups .ms-write-refusal").TextContent.Should().Be(WritePolicyRefusal.For(StudioCapability.ManageDeadLetters));
    }

    [Fact]
    public async Task A_visitor_the_write_policy_allows_sees_the_skip_offered()
    {
        using StudioComponentContext context = await ContextAsync(rule: static _ => true);
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group());

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);

        page.Find(".ms-dl-group-skip").HasAttribute("disabled").Should().BeFalse();
        page.FindAll(".ms-write-refusal").Should().BeEmpty();
    }

    [Fact]
    public async Task A_store_that_records_no_skips_disables_the_skip_with_single_skips_sentence()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group()) with { RecordsSkips = false };

        IElement skip = Render(context, GroupedUri).Find(".ms-dl-group-skip");

        skip.HasAttribute("disabled").Should().BeTrue();
        skip.GetAttribute("title").Should().Be(EventDataService.NoSkipColumnMessage);
    }

    [Fact]
    public async Task A_group_with_nothing_left_to_skip_says_so()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group(events: 4, skipped: 4));

        IElement skip = Render(context, GroupedUri).Find(".ms-dl-group-skip");

        skip.HasAttribute("disabled").Should().BeTrue();
        skip.GetAttribute("title").Should().Be(DeadLetterGroupText.NothingLeft);
    }

    /// <summary>Per-tenant sequences below Marten 9.51: refused, with single Skip's own sentence.</summary>
    [Fact]
    public async Task Per_tenant_sequences_on_a_Marten_that_skips_by_number_alone_disable_the_skip()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Shape = context.EventData.Shape with { PerTenantSequences = true, TenantSkipping = false };
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group(tenantNames: ["acme"]));

        IElement skip = Render(context, GroupedUri).Find(".ms-dl-group-skip");

        skip.HasAttribute("disabled").Should().BeTrue();
        skip.GetAttribute("title").Should().Be(StreamCompactionRules.SkipNeedsOneSequence);
    }

    /// <summary>
    /// Per-tenant sequences from 9.51, "All tenants" selected: a group of one tenant is skipped in that tenant (single
    /// Skip's rule), and a group of several asks for a tenant.
    /// </summary>
    [Fact]
    public async Task Per_tenant_sequences_with_all_tenants_skip_a_one_tenant_group_in_its_tenant_and_ask_for_one_otherwise()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Shape = context.EventData.Shape with { PerTenantSequences = true, TenantSkipping = true };
        context.EventData.Groups = FakeEventDataService.GroupList(
            FakeEventDataService.Group(projection: "One", tenantCount: 1, tenantNames: ["acme"]),
            FakeEventDataService.Group(projection: "Two", tenantCount: 2, tenantNames: ["acme", "globex"]));
        context.EventData.SkipPlan = new DeadLetterGroupSkipPlan(2, 2, 0, "acme");

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);

        IElement[] skips = [.. page.FindAll(".ms-dl-group-skip")];
        skips[1].HasAttribute("disabled").Should().BeTrue();
        skips[1].GetAttribute("title").Should().Be(DeadLetterGroupText.SkipNeedsOneTenant(2));

        await skips[0].ClickAsync(new());

        context.EventData.PlanRequests.Should().ContainSingle().Which.Scope.TenantId.Should().Be("acme");
        page.Find(".ms-confirm-dialog").TextContent.Should().Contain("of tenant acme");

        await TypeAndConfirmAsync(page, "2");

        context.EventData.GroupSkips.Should().ContainSingle().Which.Scope.TenantId.Should().Be("acme");
    }

    // ------------------------------------------------------------------------------------------------
    // Skip this group: confirmed by the count
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The dialog shows the count the service would skip now - asked when it opens, not taken from the table - and the
    /// count is what has to be typed; the typed text is what reaches the service.
    /// </summary>
    [Fact]
    public async Task The_confirmation_shows_the_count_and_the_typed_count_reaches_the_service()
    {
        using StudioComponentContext context = await ContextAsync();
        DeadLetterGroup group = FakeEventDataService.Group(events: 40, skipped: 3);
        context.EventData.Groups = FakeEventDataService.GroupList(group);
        context.EventData.SkipPlan = new DeadLetterGroupSkipPlan(37, 37, 3, null);

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);
        await page.Find(".ms-dl-group-skip").ClickAsync(new());

        IElement dialog = page.Find(".ms-confirm-dialog");
        dialog.QuerySelector(".ms-dl-group-plan-count")!.TextContent.Should().Be("37");
        dialog.QuerySelector(".ms-confirm-label code")!.TextContent.Should().Be("37", "the count is the phrase");
        dialog.TextContent.Should().Contain(group.Key.Describe()).And.Contain("Already skipped");

        IElement confirm = page.Find(".ms-confirm-actions button.ms-button-danger");
        confirm.HasAttribute("disabled").Should().BeTrue("nothing is typed yet");

        await page.Find("#ms-confirm-input").InputAsync(new() { Value = "36" });
        page.Find(".ms-confirm-actions button.ms-button-danger").HasAttribute("disabled").Should().BeTrue("the wrong count");

        await TypeAndConfirmAsync(page, "37");

        context.EventData.GroupSkips.Should().ContainSingle().Which.Should().Be((context.State.ActiveScope!, group.Key, "37"));
    }

    /// <summary>A group larger than one action says so before the first, in the row and in the dialog.</summary>
    [Fact]
    public async Task A_group_larger_than_one_action_says_so()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group(letters: 2_500, events: 2_400, skipped: 0));
        context.EventData.SkipPlan = new DeadLetterGroupSkipPlan(1_000, 2_400, 0, null);

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);

        page.Find(".ms-dl-group .ms-dl-group-cap").TextContent.Should().Be(DeadLetterGroupText.CapNote(2_400));

        await page.Find(".ms-dl-group-skip").ClickAsync(new());

        IElement dialog = page.Find(".ms-confirm-dialog");
        dialog.QuerySelector(".ms-confirm-label code")!.TextContent.Should().Be("1000");
        dialog.TextContent.Should().Contain("Left for another action").And.Contain("1,400").And.Contain(DeadLetterGroupText.CapNote(2_400));
    }

    /// <summary>What the service would refuse with, the dialog does not offer: it says the sentence instead.</summary>
    [Fact]
    public async Task A_plan_the_service_would_refuse_opens_no_dialog_and_says_why()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group());
        context.EventData.SkipPlan = DeadLetterGroupSkipPlan.Refused(StreamCompactionRules.SkipNeedsTenant);

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);
        await page.Find(".ms-dl-group-skip").ClickAsync(new());

        page.FindAll(".ms-confirm-dialog").Should().BeEmpty();
        page.Find(".ms-dl-groups .ms-error-alert").TextContent.Should().Contain(StreamCompactionRules.SkipNeedsTenant);
        context.EventData.GroupSkips.Should().BeEmpty();
    }

    /// <summary>A refusal from the service after the dialog - the count changed, the policy said no - is shown, not swallowed.</summary>
    [Fact]
    public async Task A_refused_skip_is_shown()
    {
        using StudioComponentContext context = await ContextAsync();
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group());
        context.EventData.MutationFailure = new InvalidOperationException(EventDataService.ConfirmationMismatch(4));

        IRenderedComponent<DeadLetters> page = Render(context, GroupedUri);
        await page.Find(".ms-dl-group-skip").ClickAsync(new());
        await TypeAndConfirmAsync(page, "3");

        page.Find(".ms-dl-groups .ms-error-alert").TextContent.Should().Contain("the group changed after the dialog opened");
    }

    /// <summary>D26: the section is appended after 0.5's last, and every rule in it is its own.</summary>
    [Fact]
    public void The_stylesheet_section_is_appended_and_owns_every_selector_in_it()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css")).Replace("\r\n", "\n", StringComparison.Ordinal);

        int start = css.IndexOf("M3. Events > Dead letters, grouped by cause", StringComparison.Ordinal);
        start.Should().BeGreaterThan(css.IndexOf("W5-API. The Health page's Snapshot menu", StringComparison.Ordinal), "appended in D26 order");

        int next = css.IndexOf("/* ====", start, StringComparison.Ordinal);
        string section = next < 0 ? css[start..] : css[start..next];

        IEnumerable<string> selectors = section
            .Split('\n')
            .Select(static x => x.Trim())
            .Where(static x => x.EndsWith('{') && !x.StartsWith('@'))
            .SelectMany(static x => x.TrimEnd('{').Split(','))
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0);

        selectors.Should().NotBeEmpty().And.OnlyContain(
            static x => x.StartsWith(".ms-dl-group", StringComparison.Ordinal) || x.StartsWith(".ms-dl-views", StringComparison.Ordinal),
            "M3 re-declares nothing an earlier section owns");
        section.Should().Contain(".ms-dl-group-fingerprint {").And.Contain("overflow-wrap: anywhere;");
    }

    private static async Task TypeAndConfirmAsync(IRenderedComponent<DeadLetters> page, string typed)
    {
        await page.Find("#ms-confirm-input").InputAsync(new() { Value = typed });
        await page.Find(".ms-confirm-actions button.ms-button-danger").ClickAsync(new());
    }

    private static IRenderedComponent<DeadLetters> Render(StudioComponentContext context, string uri)
    {
        context.Navigate(uri);
        return context.Render<DeadLetters>();
    }

    private static async Task<StudioComponentContext> ContextAsync(
        bool capabilities = true,
        Func<MartenStoreResource, bool>? rule = null)
    {
        StudioComponentContext context = new();

        if (capabilities)
        {
            context.WithAllCapabilities();
        }

        if (rule is not null)
        {
            context.Options.WriteAuthorizationPolicy = "MartenStudioWriter";
            context.AuthorizationService.Allow(rule);
        }

        context.EventData.Shape = context.EventData.Shape with { HasIsSkipped = true };

        await context.ReadyAsync();
        return context;
    }
}
