using Bunit;

using MartenStudio.Components.Json;

namespace MartenStudio.Tests.Json;

/// <summary>
/// What happens to a search when the search bar is taken away.
/// </summary>
/// <remarks>
/// <see cref="JsonView.ShowSearch" /> is a parameter, so a host can turn it off on a viewer that is
/// already showing matches - an event card does exactly that when its toolbar closes. The highlighting,
/// the badges on collapsed containers and the match cursor are not part of the model, so nothing in the
/// rebuild path clears them; without this the reader is left with lit matches and no control that can
/// step through them, clear them or even say how many there are.
/// </remarks>
public class JsonViewSearchVisibilityTests
{
    private const string Sample = """
        {
          "firstName": "Ada",
          "address": { "street": "1 Analytical Way", "city": "Helsinki" },
          "items": [ { "sku": "A-1" } ]
        }
        """;

    [Fact]
    public void Taking_the_search_bar_away_takes_its_highlighting_with_it()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, Sample)
            .Add(c => c.ShowSearch, true)
            .Add(c => c.SearchDebounceMilliseconds, 0));

        view.Find(".ms-json-search-input").Input("Helsinki");

        view.WaitForAssertion(() => view.FindAll("mark.ms-json-hit").Should().NotBeEmpty());

        view.Render(p => p.Add(c => c.ShowSearch, false));

        view.FindAll(".ms-json-search").Should().BeEmpty("the bar is what ShowSearch=false removes");
        view.FindAll("mark.ms-json-hit").Should()
            .BeEmpty("highlighting nobody can step through or clear is worse than none");
        view.FindAll(".ms-json-badge").Should().BeEmpty();

        // The document itself is untouched: this clears a search, not the view.
        view.Find(".ms-json-tree").TextContent.Should().Contain("Helsinki");
    }

    /// <summary>
    /// The bar comes back empty, and it has to: it is a new component with an empty input, so a term
    /// left behind in the viewer would be a filter no visible control admits to.
    /// </summary>
    [Fact]
    public void The_search_bar_comes_back_with_nothing_in_it_and_nothing_lit()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, Sample)
            .Add(c => c.ShowSearch, true)
            .Add(c => c.SearchDebounceMilliseconds, 0));

        view.Find(".ms-json-search-input").Input("Helsinki");
        view.WaitForAssertion(() => view.FindAll("mark.ms-json-hit").Should().NotBeEmpty());

        view.Render(p => p.Add(c => c.ShowSearch, false));
        view.Render(p => p.Add(c => c.ShowSearch, true));

        view.Find(".ms-json-search-input").GetAttribute("value").Should().BeNullOrEmpty();
        view.FindAll("mark.ms-json-hit").Should().BeEmpty();
        view.FindAll(".ms-json-search-position").Should().BeEmpty("there is no match to be at");
    }

    /// <summary>
    /// A viewer whose search bar was never shown has nothing to clear, and a re-render that does not
    /// touch <see cref="JsonView.ShowSearch" /> must not clear one either.
    /// </summary>
    [Fact]
    public void A_re_render_that_leaves_the_search_bar_alone_leaves_the_search_alone()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, Sample)
            .Add(c => c.ShowSearch, true)
            .Add(c => c.SearchDebounceMilliseconds, 0));

        view.Find(".ms-json-search-input").Input("Helsinki");
        view.WaitForAssertion(() => view.FindAll("mark.ms-json-hit").Should().NotBeEmpty());

        // The same document, a different label: a parameter change that is about nothing here.
        view.Render(p => p.Add(c => c.Label, "Event body"));

        view.FindAll("mark.ms-json-hit").Should().NotBeEmpty("the reader is still searching");
        view.Find(".ms-json-tree").GetAttribute("aria-label").Should().Be("Event body");
    }
}
