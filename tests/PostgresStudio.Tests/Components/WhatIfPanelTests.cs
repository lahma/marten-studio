using AngleSharp.Dom;

using Bunit;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Query;
using PostgresStudio.Tests.Statements;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// X2's "What if?" panel: offered under a plan that suggests an index, and only then; the two plans side by side with the
/// verdict and the hypothetical index's name; hypopg's absence and a role that may not use it said plainly, each in its own
/// words; and a run that throws said on the panel rather than lost.
/// </summary>
public class WhatIfPanelTests
{
    private const string Before = "[{\"Plan\": {\"Node Type\": \"Seq Scan\", \"Relation Name\": \"u\", \"Alias\": \"u\", \"Filter\": \"(v < 2)\", \"Total Cost\": 944}}]";

    private const string After =
        "[{\"Plan\": {\"Node Type\": \"Bitmap Heap Scan\", \"Relation Name\": \"u\", \"Alias\": \"u\", \"Total Cost\": 61.2, \"Plans\": [{\"Node Type\": "
        + "\"Bitmap Index Scan\", \"Parent Relationship\": \"Outer\", \"Index Name\": \"<13630>btree_u_v\", \"Total Cost\": 5}]}}]";

    private static async Task<PostgresStudioComponentContext> ContextAsync() => await new PostgresStudioComponentContext().ReadyAsync();

    private static QueryPlan PlanOf(string json) => QueryPlanReader.Read(json).Plan!;

    private static WhatIfResult Compared(HypotheticalIndex index) => WhatIfResult.From(index, new HypotheticalIndexRead
    {
        Availability = new HypotheticalIndexAvailability(HypotheticalIndexState.Available, "public", "1.4.4"),
        BeforeJson = Before,
        AfterJson = After,
        Made = new HypotheticalIndexMade(13630, "<13630>btree_u_v"),
    });

    private static IRenderedComponent<PlanView> Render(PostgresStudioComponentContext context, WhatIfSource? source, string json = Before) =>
        context.Render<PlanView>(parameters => parameters.Add(x => x.Json, json).Add(x => x.WhatIf, source));

    [Fact]
    public async Task A_plan_that_suggests_an_index_offers_it_and_a_what_if_draws_both_plans_side_by_side()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        List<HypotheticalIndex> asked = [];
        WhatIfSource source = new()
        {
            RunAsync = (index, _) =>
            {
                asked.Add(index);
                return Task.FromResult(Compared(index));
            },
        };

        IRenderedComponent<PlanView> view = Render(context, source);

        IElement candidate = view.Find(".ms-whatif-candidate");
        candidate.QuerySelector(".ms-whatif-caption")!.TextContent.Should().Be("An index on u (v)");
        candidate.QuerySelector(".ms-whatif-definition")!.TextContent.Should().Be("CREATE INDEX ON \"u\" (\"v\");");
        view.Find(".ms-whatif-lead").TextContent.Should().Contain("Nothing is created");

        await view.Find(".ms-whatif-run").ClickAsync(new());

        asked.Should().ContainSingle().Which.Definition.Should().Be("CREATE INDEX ON \"u\" (\"v\");");
        view.Find(".ms-whatif-verdict").TextContent.Should().Be("Postgres would use it: the plan's estimated cost goes from 944.00 to 61.20 (94% less).");
        view.Find(".ms-whatif-verdict").ClassList.Should().Contain("ms-whatif-used");
        view.Find(".ms-whatif-name").TextContent.Should().Contain("<13630>btree_u_v");

        IElement compare = view.Find(".ms-whatif-compare");
        compare.QuerySelector(".ms-whatif-before .ms-whatif-side-title")!.TextContent.Should().Be("Now");
        compare.QuerySelector(".ms-whatif-after .ms-whatif-side-title")!.TextContent.Should().Be("With the index");
        compare.QuerySelectorAll(".ms-plan").Should().HaveCount(2, "the plan now and the plan with the index, side by side");
        compare.QuerySelector(".ms-whatif-before .ms-plan-node-type")!.TextContent.Should().Be("Seq Scan");
        compare.QuerySelector(".ms-whatif-after")!.TextContent.Should().Contain("<13630>btree_u_v");
        compare.QuerySelectorAll(".ms-whatif").Should().BeEmpty("the plans a what-if draws offer no what-if of their own");
        compare.QuerySelectorAll(".ms-table-scroll").Should().HaveCount(2, "each plan's tree in its own labelled region (D25)");
        view.Find(".ms-whatif-caveat").TextContent.Should().Contain("never EXPLAIN ANALYZE");
    }

    [Theory]
    [InlineData(false, HypotheticalIndexAvailability.NotInstalledSentence)]
    [InlineData(true, HypotheticalIndexAvailability.NoPrivilegeSentence)]
    public async Task hypopgs_absence_and_a_role_that_may_not_use_it_are_said_in_their_own_sentence(bool installed, string sentence)
    {
        HypotheticalIndexState state = installed ? HypotheticalIndexState.NoPrivilege : HypotheticalIndexState.NotInstalled;
        await using PostgresStudioComponentContext context = await ContextAsync();
        WhatIfSource source = new() { RunAsync = (index, _) => Task.FromResult(WhatIfResult.NotCompared(index, sentence, state)) };

        IRenderedComponent<PlanView> view = Render(context, source);
        await view.Find(".ms-whatif-run").ClickAsync(new());

        view.Find(".ms-whatif-reason").TextContent.Should().Be(sentence);
        view.FindAll(".ms-whatif-compare").Should().BeEmpty("there are no plans to compare");
        view.FindAll(".ms-whatif-verdict").Should().BeEmpty();
    }

    [Fact]
    public async Task The_not_installed_and_no_privilege_sentences_say_which_and_whose_decision_it_is()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        WhatIfSource source = new()
        {
            RunAsync = (index, _) => Task.FromResult(WhatIfResult.NotCompared(index, HypotheticalIndexAvailability.NotInstalledSentence, HypotheticalIndexState.NotInstalled)),
        };

        IRenderedComponent<PlanView> view = Render(context, source);
        await view.Find(".ms-whatif-run").ClickAsync(new());

        view.Find(".ms-whatif-reason").TextContent.Should().StartWith("hypopg is not installed in this database").And.Contain("is the host's decision");
        HypotheticalIndexAvailability.NoPrivilegeSentence.Should().StartWith("hypopg is installed in this database, but").And.Contain("is the host's decision");
    }

    [Fact]
    public async Task A_plan_that_suggests_nothing_and_a_plan_with_no_source_offer_no_what_if()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        WhatIfSource source = new() { RunAsync = (index, _) => Task.FromResult(Compared(index)) };

        Render(context, source, ExplainFixtures.Get("17-seqfilter-plain")).FindAll(".ms-whatif").Should().BeEmpty("a leading-wildcard LIKE suggests no btree");
        Render(context, null).FindAll(".ms-whatif").Should().BeEmpty("no source: the surface offers none");
    }

    [Fact]
    public async Task Indexes_a_source_names_are_offered_in_place_of_the_plans_own_with_its_note()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        HypotheticalIndex marten = new()
        {
            Key = "marten:Color",
            Definition = "CREATE INDEX mt_doc_target_idx_color ON public.mt_doc_target USING btree ((data ->> 'Color'));",
            Caption = "The index Marten would create for options.Schema.For<Target>().Duplicate(x => x.Color);",
            Origin = HypotheticalIndexOrigin.Marten,
            Table = "mt_doc_target",
            Note = "Declare it in StoreOptions.",
        };
        WhatIfSource source = new() { Candidates = [marten], Note = "Marten's own DDL.", RunAsync = (index, _) => Task.FromResult(Compared(index)) };

        IRenderedComponent<WhatIfPanel> panel = context.Render<WhatIfPanel>(parameters => parameters.Add(x => x.Source, source));

        panel.FindAll(".ms-whatif-candidate").Should().ContainSingle();
        panel.Find(".ms-whatif-caption").TextContent.Should().StartWith("The index Marten would create for");
        panel.Find(".ms-whatif-candidate-note").TextContent.Should().Be("Declare it in StoreOptions.");
        panel.Find(".ms-whatif-note").TextContent.Should().Be("Marten's own DDL.");
    }

    [Fact]
    public async Task A_what_if_that_throws_is_said_on_the_panel()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        WhatIfSource source = new() { RunAsync = static (_, _) => Task.FromException<WhatIfResult>(new InvalidOperationException("boom")) };

        IRenderedComponent<PlanView> view = Render(context, source);
        await view.Find(".ms-whatif-run").ClickAsync(new());

        view.Find(".ms-whatif-failure").TextContent.Should().Be("The what-if could not run (InvalidOperationException).");
        view.Find(".ms-whatif-run").HasAttribute("disabled").Should().BeFalse("the panel can be asked again");
    }
}
