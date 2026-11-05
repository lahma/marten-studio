using AngleSharp.Dom;

using Bunit;

using MartenStudio.Services.Shapes;
using MartenStudio.Tests.Documents;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

using ListPage = MartenStudio.Components.Pages.Documents.Documents;

namespace MartenStudio.Tests.Shapes;

/// <summary>The Check shapes button, its panel and the findings table (D-SHAPE S10), over a hand-written fake of the check.</summary>
public class DocumentShapePanelTests
{
    private static readonly DateTimeOffset LongAgo = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private static ShapeCheckResult Result(
        ShapeCoverage coverage = ShapeCoverage.Complete,
        int examined = 37,
        IReadOnlyList<ShapeTypeResult>? types = null,
        string? reason = null,
        string? error = null,
        DateTimeOffset? at = null) =>
        new(
            "customer",
            coverage,
            at ?? LongAgo,
            TimeSpan.FromSeconds(1),
            examined,
            0,
            0,
            0,
            0,
            reason,
            null,
            [],
            types ?? [],
            null,
            error,
            [new ShapeStatement("select 1", ["@probe"])]);

    private static ShapeTypeResult Customer(params ShapeMemberFinding[] members) =>
        new("Customer", "Sample.Customer", 37, 0, 0, [], members, 0, 0);

    private static ShapeMemberFinding Dropped(string path, int count = 12, string id = "doc-1", string? tenant = null) =>
        new(ShapeMemberKind.Dropped, path, null, count, new ShapeItemKey(id, tenant));

    private static (DocumentsComponentContext Context, FakeShapeCheck Check) Rig(bool registered = true)
    {
        DocumentsComponentContext context = new();
        FakeShapeCheck check = new();
        context.Services.AddSingleton<IDocumentShapeCheck>(check);
        context.Data.Rail = registered
            ? FakeDocuments.Rail([FakeDocuments.Collection("customer")])
            : FakeDocuments.Rail([], [FakeDocuments.Collection("customer", registered: false)]);
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row("doc-1")]);
        return (context, check);
    }

    private static IRenderedComponent<ListPage> Render(DocumentsComponentContext context) =>
        context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));

    private static IElement? Button(IRenderedComponent<ListPage> page, string text) =>
        page.FindAll("button").FirstOrDefault(x => x.TextContent.Trim() == text);

    private static async Task ClickAsync(IRenderedComponent<ListPage> page, string text) =>
        await Button(page, text)!.ClickAsync(new MouseEventArgs());

    [Fact]
    public void A_discovered_collection_has_no_button_and_a_registered_one_has()
    {
        (DocumentsComponentContext discovered, _) = Rig(registered: false);
        using (discovered)
        {
            Button(Render(discovered), "Check shapes").Should().BeNull();
        }

        (DocumentsComponentContext registered, _) = Rig();
        using (registered)
        {
            Button(Render(registered), "Check shapes").Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Nothing_runs_until_the_button_is_pressed_and_then_the_check_runs_once_for_the_alias()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result();
            IRenderedComponent<ListPage> page = Render(context);

            check.Calls.Should().BeEmpty();

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => check.Calls.Should().Equal(("customer", false)));
            page.Find(".ms-shape-summary").TextContent.Should().Contain("Checked all 37 documents at");
        }
    }

    [Fact]
    public async Task A_finding_shows_the_sentence_the_count_and_a_link_built_from_this_visitors_scope()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(types: [Customer(Dropped("$.legacyName"))]);
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.FindAll(".ms-shape-table").Should().ContainSingle());
            string row = page.Find(".ms-shape-table tbody tr").TextContent;
            row.Should().Contain("12 of 37")
                .And.Contain("$.legacyName")
                .And.Contain("12 of 37 sampled documents have $.legacyName, which Customer no longer declares: the next save drops it.");

            IElement example = page.Find(".ms-shape-table a.ms-id-text");
            example.TextContent.Should().Be("doc-1");
            example.GetAttribute("href").Should().Contain("documents/customer/doc").And.Contain("id=doc-1");
        }
    }

    [Fact]
    public async Task Every_table_the_panel_draws_is_in_a_labelled_scroll_region()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(types: [Customer(Dropped("$.a")), Customer() with { TypeName = "Other" }]);
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            IReadOnlyList<IElement> tables = page.WaitForElements(".ms-shape-table");
            tables.Should().NotBeEmpty();
            foreach (IElement table in tables)
            {
                IElement? region = table.Closest(".ms-table-scroll");
                region.Should().NotBeNull();
                region!.GetAttribute("role").Should().Be("region");
                region.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace();
            }
        }
    }

    [Fact]
    public async Task A_type_with_nothing_found_says_every_sampled_document_fits_and_never_healthy()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(types: [Customer()]);
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Find(".ms-shape-fits").TextContent.Should().Be("Every sampled document fits Customer."));
            page.Markup.Should().NotContain("healthy");
            page.FindAll(".ms-shape-table").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_partial_result_says_where_it_stopped_and_a_failed_one_says_why()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(ShapeCoverage.Partial, 112, reason: ShapeCheckText.BudgetRanOut(TimeSpan.FromSeconds(15)));
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");
            page.WaitForAssertion(() => page.Find(".ms-shape-summary").TextContent
                .Should().Be("Stopped after 112 documents: the 15-second budget ran out."));

            check.Result = Result(ShapeCoverage.NotChecked, 0, error: "The shape check could not be completed: nope.");
            await ClickAsync(page, "Check again");

            page.WaitForAssertion(() => page.Find(".ms-error-alert").TextContent.Should().Contain("could not be completed"));
        }
    }

    [Fact]
    public async Task A_check_that_read_nothing_says_why_in_one_sentence()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(ShapeCoverage.NotChecked, 0, reason: ShapeCheckText.TableMissing);
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Find(".ms-shape-summary").TextContent.Should().Be(ShapeCheckText.TableMissing));
        }
    }

    [Fact]
    public async Task Cancel_ends_the_wait_and_the_panel_says_so()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            TaskCompletionSource<ShapeCheckResult> gate = new();
            check.Gate = gate;
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => Button(page, "Cancel").Should().NotBeNull());
            await ClickAsync(page, "Cancel");

            page.WaitForAssertion(() => page.Find(".ms-error-alert").TextContent.Should().Contain("cancelled"));
            Button(page, "Check again").Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Check_again_is_disabled_inside_the_recheck_window_and_says_when_it_opens()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(at: DateTimeOffset.UtcNow);
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => Button(page, "Check again")!.HasAttribute("disabled").Should().BeTrue());
            Button(page, "Check again")!.GetAttribute("title").Should().StartWith("Available at ");

            check.Result = Result(at: LongAgo);
            await ClickAsync(page, "Close");
            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => Button(page, "Check again")!.HasAttribute("disabled").Should().BeFalse());
        }
    }

    [Fact]
    public async Task A_result_the_cache_handed_back_says_how_long_ago_it_was_checked_and_offers_Check_again_beside_it()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(at: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(4));
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Find(".ms-shape-summary").TextContent.Should().Contain("(4 min ago, from the last check)"));

            IElement beside = page.Find(".ms-shape-summary-row .ms-shape-recheck");
            beside.TextContent.Trim().Should().Be("Check again");
            beside.HasAttribute("disabled").Should().BeFalse("a check four minutes old can be run again");
        }
    }

    [Fact]
    public async Task A_result_this_press_made_says_only_the_time()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(at: DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Find(".ms-shape-summary").TextContent.Should().Contain("Checked all 37 documents at "));
            page.Find(".ms-shape-summary").TextContent.Should().NotContain("from the last check");
            page.FindAll(".ms-shape-recheck").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Close_removes_the_panel_and_a_tenant_example_carries_the_tenant_in_its_link()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(types: [Customer(Dropped("$.a", tenant: "globex"))]);
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");
            page.WaitForAssertion(() => page.Find(".ms-shape-table a.ms-id-text").GetAttribute("href").Should().Contain("tenant=globex"));

            await ClickAsync(page, "Close");

            page.FindAll(".ms-shape-panel").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task The_disclosure_shows_the_statements_and_their_parameter_names()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result();
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Find(".ms-shape-panel .ms-sql-disclosure").TextContent
                .Should().Contain("select 1").And.Contain("@probe"));
        }
    }

    [Fact]
    public async Task The_rename_question_appears_under_a_dropped_and_a_defaulted_member_of_about_the_same_count()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(types:
            [
                Customer(
                    Dropped("$.customerName", 20),
                    new ShapeMemberFinding(ShapeMemberKind.Defaulted, "$.customer", null, 21, new ShapeItemKey("doc-2", null))),
            ]);
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => page.Markup.Should().Contain("was it renamed?"));
        }
    }

    [Fact]
    public async Task Check_again_re_enables_itself_when_its_window_opens()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            ManualClock clock = new(new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero));
            context.Services.AddSingleton<TimeProvider>(clock);
            check.Result = Result(at: clock.GetUtcNow());
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => Button(page, "Check again")!.HasAttribute("disabled").Should().BeTrue());
            clock.Timers.Should().ContainSingle().Which.Due.Should().Be(ShapeCheckLimits.RecheckAfter);

            clock.Advance(ShapeCheckLimits.RecheckAfter + TimeSpan.FromSeconds(1));
            clock.FireAll();

            page.WaitForAssertion(() => Button(page, "Check again")!.HasAttribute("disabled").Should().BeFalse());

            await ClickAsync(page, "Close");
            clock.Timers.Should().OnlyContain(static x => x.Disposed, "the timer goes with the panel");
        }
    }

    [Fact]
    public async Task A_failed_result_does_not_hold_check_again_back()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            check.Result = Result(ShapeCoverage.NotChecked, 0, error: "The shape check could not be completed: busy.", at: DateTimeOffset.UtcNow);
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");

            page.WaitForAssertion(() => Button(page, "Check again")!.HasAttribute("disabled").Should().BeFalse());
        }
    }

    [Fact]
    public async Task A_scope_change_re_creates_the_open_panel_and_asks_again_for_the_new_scope()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            context.Catalog.WithStore("second", "Second", databaseIdentities: "localhost.marten");
            check.Result = Result();
            IRenderedComponent<ListPage> page = Render(context);

            await ClickAsync(page, "Check shapes");
            page.WaitForAssertion(() => check.Scopes.Select(static x => x.StoreKey).Should().Equal("default"));

            await page.InvokeAsync(() => context.State.SetScopeAsync("second", null, null, Xunit.TestContext.Current.CancellationToken));

            page.WaitForAssertion(() => check.Scopes.Select(static x => x.StoreKey).Should().Equal("default", "second"));
            page.FindAll(".ms-shape-panel").Should().ContainSingle();
        }
    }

    [Fact]
    public async Task Example_links_are_built_from_the_scope_the_check_ran_with_not_the_one_active_at_render_time()
    {
        (DocumentsComponentContext context, FakeShapeCheck check) = Rig();
        using (context)
        {
            context.Catalog.WithStore("second", "Second", databaseIdentities: "localhost.marten");
            check.Result = Result(types: [Customer(Dropped("$.a"))]);
            await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);

            IRenderedComponent<MartenStudio.Components.Shapes.DocumentShapePanel> panel = context.Render<MartenStudio.Components.Shapes.DocumentShapePanel>(
                parameters => parameters.Add(x => x.Alias, "customer"));

            panel.WaitForAssertion(() => panel.Find("a.ms-id-text").GetAttribute("href").Should().Contain("store=default"));

            await panel.InvokeAsync(() => context.State.SetScopeAsync("second", null, null, Xunit.TestContext.Current.CancellationToken));
            panel.Render();

            panel.Find("a.ms-id-text").GetAttribute("href").Should().Contain("store=default").And.NotContain("store=second");
        }
    }

    /// <summary>A clock that moves when told, and whose timers fire when told.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public List<ManualTimer> Timers { get; } = [];

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;

        public void FireAll()
        {
            foreach (ManualTimer timer in Timers.Where(static x => !x.Disposed).ToList())
            {
                timer.Callback(timer.State);
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(callback, state, dueTime);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public TimeSpan Due { get; } = due;

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A hand-written check: a result to return, a gate to hold it, and a record of what was asked.</summary>
    private sealed class FakeShapeCheck : IDocumentShapeCheck
    {
        public ShapeCheckResult Result { get; set; } = ShapeCheckResult.NotChecked("customer", "unset", DateTimeOffset.UnixEpoch);

        public TaskCompletionSource<ShapeCheckResult>? Gate { get; set; }

        public List<(string Alias, bool Force)> Calls { get; } = [];

        public List<StudioScope> Scopes { get; } = [];

        public Task<ShapeCheckResult> CheckAsync(StudioScope scope, string alias, bool force, CancellationToken cancellationToken)
        {
            Calls.Add((alias, force));
            Scopes.Add(scope);
            return Gate is { } gate ? gate.Task.WaitAsync(cancellationToken) : Task.FromResult(Result);
        }
    }
}
