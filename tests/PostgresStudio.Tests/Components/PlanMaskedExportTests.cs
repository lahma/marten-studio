using Bunit;

using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.0-P3B (issue #28): the Statements drill-in's plan is offered as Markdown and for speedscope - built from the masked plan it
/// draws, so what the page masks is masked in the files - and never as JSON, which the drill-in has no masked form of (D32).
/// </summary>
public class PlanMaskedExportTests
{
    private const string Secret = "secret_ledger";
    private const string Literal = "hunter2";

    private const string Json = "[{\"Plan\": {\"Node Type\": \"Seq Scan\", \"Relation Name\": \"" + Secret + "\", \"Alias\": \"" + Secret + "\", "
        + "\"Total Cost\": 42, \"Plan Rows\": 3, \"Plan Width\": 8, \"Filter\": \"(note = '" + Literal + "'::text)\"}}]";

    /// <summary>The mask the Statements service gives the plan reader: a withheld name and a literal are both replaced.</summary>
    private static string Mask(string text) => text.Replace(Secret, "[withheld]", StringComparison.Ordinal).Replace(Literal, "[masked]", StringComparison.Ordinal);

    private static QueryPlan Masked() => QueryPlanReader.Read(Json, Mask).Plan!;

    private static async Task<PostgresStudioComponentContext> ContextAsync()
    {
        PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", static _ => true).SetResult(true);
        context.JSInterop.Setup<bool>("martenStudio.download.text", static _ => true).SetResult(true);
        return context;
    }

    private static IRenderedComponent<PlanView> Drill(PostgresStudioComponentContext context, QueryPlan plan, bool masked = true) =>
        context.Render<PlanView>(parameters => parameters.Add(x => x.Plan, plan).Add(x => x.MaskedExport, masked).Add(x => x.Label, "The generic plan of statement 1"));

    [Fact]
    public async Task The_control_is_a_known_literal_and_the_masked_plan_never_carries_it()
    {
        // The test has teeth: the same JSON read without the mask does carry the literal in what the exports are built from.
        QueryPlan raw = QueryPlanReader.Read(Json).Plan!;
        PostgresStudio.Services.Query.PlanTable.Markdown(raw).Should().Contain(Secret);
        PlanSpeedscope.Build(raw, "x", "e").Should().Contain(Secret);

        PlanTable.Markdown(Masked()).Should().NotContain(Secret).And.NotContain(Literal);
        PlanSpeedscope.Build(Masked(), "x", "e").Should().NotContain(Secret).And.NotContain(Literal);
    }

    [Fact]
    public async Task The_drill_in_offers_Markdown_and_speedscope_and_no_JSON()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Drill(context, Masked());

        view.Find(".ms-plan-copy-markdown").TextContent.Should().Be("Copy as Markdown");
        view.Find(".ms-plan-download-speedscope").TextContent.Should().Be("Download for speedscope");
        view.FindAll(".ms-plan-copy-json, .ms-plan-copy-text, .ms-plan-download, .ms-plan-raw, .ms-json-view").Should().BeEmpty("a drill-in has no masked EXPLAIN JSON to copy or download");
        view.Find(".ms-plan-export-note").TextContent.Should().Contain("what is masked on this page is masked in the files too");
        view.FindAll(".ms-plan-export a[href]").Should().BeEmpty();
    }

    [Fact]
    public async Task Copy_as_Markdown_hands_over_the_masked_plan_only()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Drill(context, Masked());

        view.Find(".ms-plan-copy-markdown").Click();

        view.WaitForAssertion(() => view.Find(".ms-plan-export-status").TextContent.Should().Be("Copied the plan as Markdown."));
        string copied = (string) context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Single().Arguments[0]!;

        copied.Should().Be(PlanTable.Markdown(Masked()));
        copied.Should().Contain("[withheld]").And.NotContain(Secret).And.NotContain(Literal);
    }

    [Fact]
    public async Task Download_for_speedscope_hands_over_the_masked_plan_only()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<PlanView> view = Drill(context, Masked());

        view.Find(".ms-plan-download-speedscope").Click();

        view.WaitForAssertion(() => view.Find(".ms-plan-export-status").TextContent.Should().Be("Saved plan.speedscope.json."));
        IReadOnlyList<object?> download = context.JSInterop.Invocations["martenStudio.download.text"].Single().Arguments;

        download[0].Should().Be("plan.speedscope.json");
        download[1].Should().Be("application/json");
        string file = (string) download[2]!;
        file.Should().Contain("[withheld]").And.NotContain(Secret).And.NotContain(Literal);
        file.Should().Contain("\"$schema\": \"https://www.speedscope.app/file-format-schema.json\"".Replace(": ", ":", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_is_offered_unless_the_page_asks_for_it()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = Drill(context, Masked(), masked: false);

        view.FindAll(".ms-plan-export").Should().BeEmpty("a plan a service read is exported only by a page that is sure it is masked");
    }

    [Fact]
    public async Task A_view_with_no_plan_to_draw_offers_nothing()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();

        IRenderedComponent<PlanView> view = context.Render<PlanView>(parameters => parameters.Add(x => x.MaskedExport, true).Add(x => x.Label, "x"));

        view.FindAll(".ms-plan-export").Should().BeEmpty();
    }
}
