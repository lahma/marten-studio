using Bunit;

using MartenStudio.Services.Relationships;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using ReferencedByPanel = MartenStudio.Components.Pages.Documents.ReferencedBy;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// The inbound panel on document detail: which collections point at this document, how many of their
/// documents do, and where each row links to.
/// </summary>
public class ReferencedByTests
{
    private const string CustomerId = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

    [Fact]
    public void Every_pointing_collection_is_a_row_with_its_count()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, FakeRelationshipDataService.SampleReferencedBy());

        panel.FindAll(".ms-referenced-item").Should().HaveCount(2);
        panel.TextOfAll(".ms-referenced-count").Should().Equal(["1,000+", "3"]);
    }

    [Fact]
    public void A_count_that_hit_the_cap_is_rendered_as_more_than_rather_than_as_a_number()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, FakeRelationshipDataService.SampleReferencedBy());

        panel.Find(".ms-referenced-item").TextContent.Should().Contain("1,000+");
        panel.FindAll(".ms-referenced-link")[0].GetAttribute("title").Should().Contain("More than");
    }

    [Fact]
    public void A_row_links_to_the_pointing_collection_filtered_on_this_document()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, FakeRelationshipDataService.SampleReferencedBy());

        string href = panel.FindAll(".ms-referenced-link")[1].GetAttribute("href") ?? string.Empty;

        href.Should().Be($"documents/order?q=CustomerId%20%3D%20%22{CustomerId}%22",
            "the filter is written with the .NET member - the name a person reads in the search box; " +
            "FindDuplicated would have resolved the column name just as well");
    }

    [Fact]
    public void A_row_whose_member_could_not_be_resolved_links_to_the_unfiltered_collection()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, new ReferencedBy(
            [new ReferencedByEntry("note", "order_id", null, 120, 2, false, false, true)]));

        panel.Find(".ms-referenced-link").GetAttribute("href").Should().Be("documents/note",
            "no member means no DuplicatedField behind the column, so neither name resolves and a filter " +
            "written with either would answer 'nothing found' about data that is plainly there");
    }

    [Fact]
    public void An_id_containing_a_quote_is_escaped_the_way_the_grammar_spells_it()
    {
        using var context = new StudioComponentContext();

        var panel = Render(
            context,
            new ReferencedBy([new ReferencedByEntry("order", "ref", "Reference", 120, 1, false, true, true)]),
            id: "AB\"CD");

        string href = Uri.UnescapeDataString(panel.Find(".ms-referenced-link").GetAttribute("href") ?? string.Empty);

        href.Should().Contain("Reference = \"AB\"\"CD\"", "a doubled quote is how the grammar escapes one");
    }

    [Fact]
    public void A_key_the_database_does_not_have_is_marked_declared_only()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, new ReferencedBy(
            [new ReferencedByEntry("invoice", "customer_id", "CustomerId", 120, 4, false, true, false)]));

        panel.Find(".ms-badge").TextContent.Trim().Should().Be("declared only");
    }

    [Fact]
    public void A_constraint_nobody_declared_is_marked_database_only()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, new ReferencedBy(
            [new ReferencedByEntry("invoice", "customer_id", "CustomerId", 120, 4, false, false, true)]));

        panel.Find(".ms-badge").TextContent.Trim().Should().Be("database only");
    }

    [Fact]
    public void One_count_that_failed_does_not_blank_the_panel_that_names_the_others()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, new ReferencedBy(
            [
                new ReferencedByEntry("invoice", "customer_id", "CustomerId", 120, 0, false, true, true, "57014: canceling statement"),
                new ReferencedByEntry("order", "customer_id", "CustomerId", 215, 3, false, true, true),
            ]));

        panel.FindAll(".ms-referenced-item").Should().HaveCount(2);
        panel.TextOfAll(".ms-referenced-count").Should().Equal(["?", "3"]);
        panel.Find(".ms-badge").TextContent.Trim().Should().Be("not counted");
    }

    /// <summary>
    /// Two keys from the same collection through the same primary column are two rows, not a crash.
    /// </summary>
    /// <remarks>
    /// The rows were keyed on <c>FromAlias + '.' + Column</c>, which is not unique: a composite foreign
    /// key and a single-column one can lead with the same column, and so can two composites that differ
    /// only further along. A duplicate <c>@key</c> is not a rendering glitch — Blazor throws while
    /// diffing, which takes the circuit down and leaves the page dead with nothing on screen to say why.
    /// The ordinal is unique by construction, which is why the graph's edges and the unmatched rows
    /// already use it.
    /// </remarks>
    [Fact]
    public void Two_keys_from_one_collection_on_the_same_column_are_two_rows()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, new ReferencedBy(
            [
                new ReferencedByEntry("order", "customer_id", "CustomerId", 215, 3, false, true, true),
                new ReferencedByEntry("order", "customer_id", null, 215, 7, false, false, true),
            ]));

        panel.FindAll(".ms-referenced-item").Should().HaveCount(2);
        panel.TextOfAll(".ms-referenced-count").Should().Equal(["3", "7"]);
    }

    [Fact]
    public void Nothing_pointing_here_renders_nothing_at_all()
    {
        using var context = new StudioComponentContext();

        Render(context, ReferencedBy.None).Markup.Trim().Should().BeEmpty(
            "'nothing points at this document' is the ordinary case and does not deserve a panel");
    }

    [Fact]
    public void A_panel_that_could_not_be_read_says_so()
    {
        using var context = new StudioComponentContext();

        var panel = Render(context, ReferencedBy.Failed("the database could not be reached"));

        panel.Find(".ms-error-alert").TextContent.Should().Contain("could not be reached");
    }

    private static IRenderedComponent<ReferencedByPanel> Render(
        StudioComponentContext context,
        ReferencedBy model,
        string id = CustomerId) =>
        context.Render<ReferencedByPanel>(parameters => parameters
            .Add(x => x.Model, model)
            .Add(x => x.DocumentId, id)
            .Add(x => x.HrefFor, Href));

    /// <summary>
    /// What the detail page supplies: the studio's own link builder, which is where the scope and the
    /// escaping come from.
    /// </summary>
    private static string Href(string alias, string? filter) =>
        filter is null
            ? MartenStudio.Services.Documents.DocumentLinks.ToCollection(new MartenStudioOptions(), null, alias)
            : MartenStudio.Services.Documents.DocumentLinks.ToCollection(
                new MartenStudioOptions(),
                null,
                alias,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["q"] = filter });
}
