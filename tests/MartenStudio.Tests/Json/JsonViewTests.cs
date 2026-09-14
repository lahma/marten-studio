using System.Globalization;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using MartenStudio.Components.Json;

namespace MartenStudio.Tests.Json;

public class JsonViewTests
{
    private const string Sample = """
        {
          "id": "9f8b1a7e-1c3d-4a55-9f0a-6d2c1b5e3a44",
          "firstName": "Ada",
          "address": { "street": "1 Analytical Way", "city": "Helsinki" },
          "items": [ { "sku": "A-1" } ],
          "active": true,
          "retiredOn": null
        }
        """;

    [Fact]
    public void The_tree_opens_expanded_to_two_levels()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        var rows = view.FindAll("[role=treeitem]");

        // Root, its six members, the two members of address and the one item - but not the item's own sku.
        rows.Should().HaveCount(10);
        TreeText(view).Should().Contain("firstName");
        TreeText(view).Should().Contain("Helsinki");
        TreeText(view).Should().NotContain("A-1", "the third level starts collapsed");
    }

    [Fact]
    public void A_collapsed_container_says_what_it_is_hiding()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, Sample)
            .Add(c => c.AutoExpandDepth, 1));

        view.Markup.Should().Contain("2 properties");
        view.Markup.Should().Contain("1 item");
    }

    [Fact]
    public void The_root_is_a_tree_and_its_rows_carry_the_aria_a_tree_needs()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        var tree = view.Find("[role=tree]");
        tree.GetAttribute("aria-label").Should().Be("Document JSON");

        var root = view.FindAll("[role=treeitem]")[0];
        root.GetAttribute("aria-level").Should().Be("1");
        root.GetAttribute("aria-expanded").Should().Be("true");
        root.GetAttribute("tabindex").Should().Be("0", "the tree has a single roving tabindex");

        var firstChild = view.FindAll("[role=treeitem]")[1];
        firstChild.GetAttribute("aria-level").Should().Be("2");
        firstChild.GetAttribute("aria-posinset").Should().Be("1");
        firstChild.GetAttribute("aria-setsize").Should().Be("6");
        firstChild.GetAttribute("tabindex").Should().Be("-1");
    }

    [Fact]
    public void Clicking_a_toggle_collapses_and_expands_the_rows_below_it()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));
        var before = view.FindAll("[role=treeitem]").Count;

        // The address object is the third row; its toggle is the first button in that row.
        var addressToggle = view.FindAll("[role=treeitem]")[3].QuerySelector(".ms-json-toggle")!;
        addressToggle.Click();

        view.FindAll("[role=treeitem]").Should().HaveCount(before - 2);

        view.FindAll("[role=treeitem]")[3].QuerySelector(".ms-json-toggle")!.Click();
        view.FindAll("[role=treeitem]").Should().HaveCount(before);
    }

    [Fact]
    public void Expand_all_and_collapse_all_reach_every_level_and_come_back()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        ButtonWithText(view, "Expand all").Click();
        TreeText(view).Should().Contain("A-1");

        ButtonWithText(view, "Collapse all").Click();

        // Collapse all means depth 1, the same thing the stepper's floor means: the root open and every
        // member of it shut. Collapsing the root as well would leave a single row saying nothing.
        view.FindAll("[role=treeitem]").Should().HaveCount(7);
        TreeText(view).Should().NotContain("Helsinki");
        view.Find(".ms-json-depth-value").TextContent.Should().Be("depth 1");
    }

    [Fact]
    public void A_large_array_is_windowed_with_a_way_to_see_the_rest()
    {
        using var context = new JsonTestContext();
        var json = "{\"items\":[" +
            string.Join(",", Enumerable.Range(0, 340).Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.AutoExpandNodeBudget, 10_000));

        // Root, the items array and the first hundred elements. The "show more" row is not one of them:
        // it has no position in the array, so a treeitem role could only lie about aria-posinset.
        view.FindAll("[role=treeitem]").Should().HaveCount(102);
        view.Find(".ms-json-row-more").GetAttribute("role").Should().Be("presentation");
        view.Markup.Should().Contain("Show all (340)");
        view.Markup.Should().Contain("100 of 340 shown");

        ButtonWithText(view, "Show 100 more").Click();
        view.Markup.Should().Contain("200 of 340 shown");

        ButtonWithText(view, "Show all (340)").Click();
        view.FindAll("[role=treeitem]").Should().HaveCount(342);
    }

    [Fact]
    public void The_show_more_buttons_are_out_of_the_tab_order_and_in_the_arrow_keys_instead()
    {
        using var context = new JsonTestContext();
        var json = "{\"items\":[" +
            string.Join(",", Enumerable.Range(0, 340).Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.AutoExpandNodeBudget, 10_000));

        // A tree is one tab stop. Two buttons per windowed container in the tab order would put a tab
        // stop in the middle of a page that is navigated with the arrow keys - so they are reachable the
        // way every other row is, and Enter on the focused button is then the browser's own doing.
        var buttons = view.FindAll(".ms-json-row-more button");
        buttons.Should().HaveCount(2);
        buttons.Should().AllSatisfy(b => b.GetAttribute("tabindex").Should().Be("-1"));

        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "End" });

        view.Find(".ms-json-row-more button").GetAttribute("tabindex")
            .Should().Be("0", "the arrow keys have to be able to reach what Tab no longer can");
        view.FindAll("[role=treeitem][tabindex='0']").Should().BeEmpty("a tree has one roving tabindex, not two");
    }

    [Fact]
    public void Enter_with_a_show_more_row_focused_does_not_open_the_previous_nodes_menu()
    {
        using var context = new JsonTestContext();
        var json = "{\"items\":[" +
            string.Join(",", Enumerable.Range(0, 340).Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.AutoExpandNodeBudget, 10_000));

        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "End" });
        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        context.JSInterop.Invocations.Identifiers.Should().NotContain("martenStudio.json.openMenu");
    }

    [Fact]
    public void The_tree_asks_the_browser_to_stop_scrolling_under_its_own_keys()
    {
        using var context = new JsonTestContext();

        context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        // Razor decides preventDefault at render time, which can only be "always" - swallowing Tab - or
        // "never". The condition is per key and per target, so it is answered in the lib module.
        context.JSInterop.Invocations["martenStudio.json.captureTreeKeys"].Should().ContainSingle();
    }

    [Fact]
    public void Auto_expansion_stops_at_the_node_budget()
    {
        using var context = new JsonTestContext();
        var json = "{\"a\":{" + string.Join(",", Enumerable.Range(0, 40).Select(i => $"\"k{i}\":{i}")) + "}}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.AutoExpandNodeBudget, 10));

        view.FindAll("[role=treeitem]").Should().HaveCount(2, "the root stays open but its 40-member child does not");
    }

    [Fact]
    public void Semantic_values_are_marked_up_for_what_they_are()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, """
            {
              "id": "9f8b1a7e-1c3d-4a55-9f0a-6d2c1b5e3a44",
              "when": "2026-09-14T08:30:00Z",
              "site": "https://example.com/x",
              "flag": false,
              "nothing": null
            }
            """));

        view.Find("a.ms-json-url").GetAttribute("rel").Should().Be("noopener noreferrer");
        view.Find("a.ms-json-url").GetAttribute("target").Should().Be("_blank");
        view.FindAll(".ms-json-guid").Should().ContainSingle();
        view.FindAll(".ms-json-date").Should().ContainSingle();
        view.Find(".ms-json-date .ms-json-string").GetAttribute("title")
            .Should().Be("2026-09-14T08:30:00Z", "the hover shows the ISO value; nothing here converts a time zone");
        view.FindAll(".ms-json-date svg").Should().ContainSingle("a date carries a glyph as well as a colour");
        view.Find(".ms-json-bool-glyph").TextContent.Should().Be("✗", "a boolean carries a non-colour cue");
        view.FindAll(".ms-json-null").Should().ContainSingle();
    }

    [Fact]
    public void A_long_string_is_truncated_with_the_size_of_what_is_hidden()
    {
        using var context = new JsonTestContext();
        var json = "{\"note\":\"" + new string('x', 400) + "\"}";

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, json));

        var more = view.Find(".ms-json-value .ms-json-more");
        more.TextContent.Trim().Should().Be("… +280 B");

        more.Click();
        view.Find(".ms-json-value .ms-json-more").TextContent.Trim().Should().Be("show less");
    }

    [Fact]
    public void A_truncated_string_is_cut_between_characters_and_never_through_one()
    {
        using var context = new JsonTestContext();
        var json = "{\"note\":\"" + string.Concat(Enumerable.Repeat("\U0001F600", 10)) + "\"}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.LongString, 5));

        // Five chars is two and a half emoji. Half of one is a lone surrogate: not a character, and
        // rendered as a replacement glyph right where the reader is trying to read the value.
        var shown = view.Find(".ms-json-string").TextContent;
        shown.Should().Be("\"\U0001F600\U0001F600\"");
        Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(shown)).Should().Be(shown);
    }

    [Fact]
    public void Searching_counts_the_matches_and_highlights_them()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, Sample)
            .Add(c => c.SearchDebounceMilliseconds, 0));

        view.Find(".ms-json-search-input").Input("Helsinki");

        view.WaitForAssertion(() => view.Find(".ms-json-search-position").TextContent.Trim().Should().Be("1 / 1"));
        view.FindAll("mark.ms-json-hit").Should().ContainSingle();
        view.Find("mark.ms-json-hit").TextContent.Should().Be("Helsinki");
    }

    [Fact]
    public void A_search_expands_the_way_to_its_match()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, Sample)
            .Add(c => c.SearchDebounceMilliseconds, 0));

        TreeText(view).Should().NotContain("A-1");

        view.Find(".ms-json-search-input").Input("A-1");

        view.WaitForAssertion(() => view.FindAll("mark.ms-json-hit").Should().NotBeEmpty());
        TreeText(view).Should().Contain("A-1", "the ancestors of a match are expanded so it can be seen");
    }

    [Fact]
    public void A_term_that_matches_nothing_says_so()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, Sample)
            .Add(c => c.SearchDebounceMilliseconds, 0));

        view.Find(".ms-json-search-input").Input("zzz");

        view.WaitForAssertion(() => view.Find(".ms-json-search-none").TextContent.Should().Be("no matches"));
    }

    [Fact]
    public void The_copy_menu_copies_through_the_shared_clipboard_helper()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, Sample)
            .Add(c => c.DocumentType, typeof(Person))
            .Add(c => c.NamingPolicy, JsonNamingPolicy.CamelCase));

        // The city member, three rows into the expanded address object.
        var cityRow = view.FindAll("[role=treeitem]").First(r => r.TextContent.Contains("Helsinki", StringComparison.Ordinal));
        cityRow.QuerySelector(".ms-json-key")!.Click();

        var menu = view.Find(".ms-copy-menu");
        menu.QuerySelector(".ms-copy-menu-path")!.TextContent.Should().Be("address.city");

        var items = menu.QuerySelectorAll(".ms-menu-item");
        items.Should().HaveCount(6);
        items[1].TextContent.Should().Contain("$.address.city");
        items[2].TextContent.Should().Contain("data -> 'address' ->> 'city'");
        items[3].TextContent.Should().Contain("data #>> '{address,city}'");
        items[4].TextContent.Should().Contain("x.Address.City");
        items[5].TextContent.Should().Contain("""{"address":{"city":"Helsinki"}}""");

        items[0].Click();

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Should().ContainSingle()
            .Which.Arguments[0].Should().Be("\"Helsinki\"");
    }

    [Fact]
    public void The_copy_menu_is_one_tab_stop_with_the_arrow_keys_inside_it()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        view.FindAll(".ms-json-key")[1].Click();

        // role="menu" is a promise about the keyboard: one tab stop, and a roving tabindex inside it.
        // Six tab stops with a menu role tells a screen-reader user this is a menu and then is not one.
        var items = view.FindAll(".ms-copy-menu .ms-menu-item");
        items.Select(i => i.GetAttribute("tabindex")).Should().Equal("0", "-1", "-1", "-1", "-1", "-1");

        var menu = view.Find(".ms-copy-menu");
        menu.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });
        view.FindAll(".ms-copy-menu .ms-menu-item")[1].GetAttribute("tabindex").Should().Be("0");

        menu.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "End" });
        view.FindAll(".ms-copy-menu .ms-menu-item")[^1].GetAttribute("tabindex").Should().Be("0");

        menu.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });
        view.FindAll(".ms-copy-menu .ms-menu-item")[0].GetAttribute("tabindex").Should().Be("0", "the menu wraps");

        menu.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Home" });
        view.FindAll(".ms-copy-menu .ms-menu-item")[0].GetAttribute("tabindex").Should().Be("0");

        // Every move takes DOM focus with it; a roving tabindex that nothing follows is decoration.
        context.JSInterop.Invocations["martenStudio.json.focusElement"].Should().HaveCount(4);
    }

    [Fact]
    public void Tab_is_left_alone_inside_the_copy_menu_because_leaving_is_what_tab_is_for()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));
        view.FindAll(".ms-json-key")[1].Click();

        var menu = view.Find(".ms-copy-menu");
        menu.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Tab" });
        menu.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        view.FindAll(".ms-copy-menu .ms-menu-item")[0].GetAttribute("tabindex").Should().Be("0");
        context.JSInterop.Invocations.Identifiers.Should().NotContain("martenStudio.json.focusElement");
    }

    [Fact]
    public void A_node_the_clr_type_cannot_explain_still_copies_and_says_why()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, """{"unknown":1}""")
            .Add(c => c.DocumentType, typeof(Person))
            .Add(c => c.NamingPolicy, JsonNamingPolicy.CamelCase));

        view.FindAll(".ms-json-key")[1].Click();

        view.Find(".ms-menu-item-warning").TextContent.Should().Be("could not resolve 'unknown' on Person");
    }

    [Fact]
    public void Copying_a_container_reads_its_value_back_out_of_the_source()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        var addressRow = view.FindAll("[role=treeitem]")[3];
        addressRow.QuerySelector(".ms-json-key")!.Click();
        view.Find(".ms-copy-menu .ms-menu-item").Click();

        var copied = context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Single().Arguments[0] as string;
        copied.Should().NotBeNull();
        JsonDocument.Parse(copied!).RootElement.GetProperty("city").GetString().Should().Be("Helsinki");
    }

    [Fact]
    public void Copying_the_whole_document_copies_the_pretty_printed_form()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, """{"a":1}"""));

        ButtonWithText(view, "Copy JSON").Click();

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Single().Arguments[0]
            .Should().Be("{\n  \"a\": 1\n}");
    }

    [Fact]
    public void A_document_over_the_tree_threshold_shows_raw_with_a_way_to_insist()
    {
        using var context = new JsonTestContext();
        var json = "{\"blob\":\"" + new string('x', 4_000) + "\"}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.MaxBytesForTree, 1_000));

        view.FindAll("[role=tree]").Should().BeEmpty();
        view.Find(".ms-json-banner").TextContent.Should().Contain("limit for building a tree");
        view.FindAll(".ms-code-line").Should().NotBeEmpty();

        ButtonWithText(view, "Build the tree anyway").Click();

        view.FindAll("[role=tree]").Should().ContainSingle();
        view.FindAll("[role=treeitem]").Should().HaveCount(2);
    }

    [Fact]
    public void Invalid_json_is_reported_and_shown_as_text()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, "{ not json"));

        view.Find("[role=alert]").TextContent.Should().Contain("This is not valid JSON");
        view.FindAll("[role=tree]").Should().BeEmpty();
    }

    [Fact]
    public void The_segmented_control_switches_between_the_tree_and_the_raw_view()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        ButtonWithText(view, "Raw").Click();
        view.FindAll("[role=tree]").Should().BeEmpty();
        view.FindAll(".ms-code-line").Should().NotBeEmpty();

        ButtonWithText(view, "Tree").Click();
        view.FindAll("[role=tree]").Should().ContainSingle();
    }

    [Fact]
    public void The_depth_stepper_expands_and_collapses_whole_levels()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));
        view.Find(".ms-json-depth-value").TextContent.Should().Be("depth 2");

        view.Find("[aria-label='Expand one level more']").Click();

        view.Find(".ms-json-depth-value").TextContent.Should().Be("depth 3");
        TreeText(view).Should().Contain("A-1");

        view.Find("[aria-label='Expand one level less']").Click();
        view.Find("[aria-label='Expand one level less']").Click();

        view.Find(".ms-json-depth-value").TextContent.Should().Be("depth 1");
        TreeText(view).Should().NotContain("Helsinki");
    }

    [Fact]
    public void Arrow_keys_move_the_roving_tabindex()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });

        var rows = view.FindAll("[role=treeitem]");
        rows[0].GetAttribute("tabindex").Should().Be("-1");
        rows[1].GetAttribute("tabindex").Should().Be("0");

        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "End" });
        var all = view.FindAll("[role=treeitem]");
        all[^1].GetAttribute("tabindex").Should().Be("0");
    }

    [Fact]
    public void The_star_key_opens_a_whole_subtree()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));
        TreeText(view).Should().NotContain("A-1");

        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "*" });

        TreeText(view).Should().Contain("A-1");
    }

    [Fact]
    public void Enter_on_a_focused_row_opens_the_copy_menu_from_the_keyboard()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });
        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        view.Find(".ms-copy-menu-path").TextContent.Should().Be("id");
        context.JSInterop.Invocations["martenStudio.json.openMenu"].Should().ContainSingle();
    }

    [Fact]
    public void Slash_focuses_the_viewers_own_search_and_never_the_browsers()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "/" });

        context.JSInterop.Invocations.Identifiers
            .Should().Contain(id => id.Contains("focus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Typing_a_letter_jumps_to_the_next_member_that_starts_with_it()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));

        view.Find("[role=tree]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "r" });

        var focused = view.FindAll("[role=treeitem]").Single(r => r.GetAttribute("tabindex") == "0");
        focused.TextContent.Should().Contain("retiredOn");
    }

    [Fact]
    public void The_tree_will_not_put_more_rows_in_the_dom_than_it_was_told_to()
    {
        using var context = new JsonTestContext();
        var json = "{\"a\":[" + string.Join(",", Enumerable.Range(0, 50).Select(i => i.ToString(CultureInfo.InvariantCulture))) + "]}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.MaxVisibleRows, 10));

        view.FindAll("[role=treeitem]").Should().HaveCount(10);
        view.Find(".ms-json-row-clamp").GetAttribute("role")
            .Should().Be("presentation", "a sentence about the tree is not a node in it");
        view.Markup.Should().Contain("Showing the first 10 rows");
    }

    [Fact]
    public void A_changed_column_rebuilds_the_expressions_the_copy_menu_offers()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, """{"city":"Helsinki"}""")
            .Add(c => c.Column, "data"));

        view.FindAll(".ms-json-key")[1].Click();
        view.Find(".ms-copy-menu").TextContent.Should().Contain("data ->> 'city'");

        // Same document, different column. Keying the model on the document alone left the viewer
        // offering expressions against a column the page had already stopped using.
        view.Render(p => p
            .Add(c => c.Json, """{"city":"Helsinki"}""")
            .Add(c => c.Column, "body"));

        view.FindAll(".ms-json-key")[1].Click();
        view.Find(".ms-copy-menu").TextContent.Should().Contain("body ->> 'city'");
        view.Find(".ms-copy-menu").TextContent.Should().NotContain("data ->>");
    }

    [Fact]
    public void A_raised_node_limit_builds_the_tree_that_was_refused()
    {
        using var context = new JsonTestContext();
        var json = "{\"blob\":\"" + new string('x', 4_000) + "\"}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.MaxBytesForTree, 1_000));

        view.FindAll("[role=tree]").Should().BeEmpty();

        view.Render(p => p
            .Add(c => c.Json, json)
            .Add(c => c.MaxBytesForTree, 1_000_000)
            .Add(c => c.MaxNodesForTree, 100_000));

        view.FindAll("[role=tree]").Should().ContainSingle("the limits moved, so the model has to be rebuilt");
        view.FindAll("[role=treeitem]").Should().HaveCount(2);
    }

    [Fact]
    public void A_changed_string_limit_re_truncates_the_values_on_screen()
    {
        using var context = new JsonTestContext();
        var json = "{\"note\":\"" + new string('x', 400) + "\"}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.LongString, 120));

        view.Find(".ms-json-string").TextContent.Should().HaveLength(122, "120 characters inside two quotes");

        view.Render(p => p
            .Add(c => c.Json, json)
            .Add(c => c.LongString, 20));

        view.Find(".ms-json-string").TextContent.Should().HaveLength(22);
    }

    [Fact]
    public void A_download_is_offered_for_a_document_small_enough_to_hand_to_the_browser()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, """{"a":1}"""));

        var anchor = view.Find("a[download]");
        anchor.GetAttribute("download").Should().Be("document.json");
        anchor.GetAttribute("href").Should().StartWith("data:application/json;charset=utf-8,");
    }

    [Fact]
    public void A_document_too_large_to_hand_to_the_browser_says_so_instead()
    {
        using var context = new JsonTestContext();
        var json = "{\"blob\":\"" + new string('x', 2_000) + "\"}";

        var view = context.Render<JsonView>(p => p
            .Add(c => c.Json, json)
            .Add(c => c.MaxDownloadBytes, 100));

        view.FindAll("a[download]").Should().BeEmpty();
        view.Find(".ms-btn-disabled").GetAttribute("aria-disabled").Should().Be("true");
    }

    [Fact]
    public void An_empty_document_renders_nothing_and_does_not_fall_over()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, (string?)null));

        view.FindAll("[role=treeitem]").Should().BeEmpty();
        view.FindAll(".ms-code-line").Should().BeEmpty();
    }

    [Fact]
    public void A_new_document_resets_the_expansion_state()
    {
        using var context = new JsonTestContext();

        var view = context.Render<JsonView>(p => p.Add(c => c.Json, Sample));
        ButtonWithText(view, "Expand all").Click();
        TreeText(view).Should().Contain("A-1");

        view.Render(p => p.Add(c => c.Json, """{"b":1}"""));

        view.FindAll("[role=treeitem]").Should().HaveCount(2);
        TreeText(view).Should().NotContain("A-1");
    }

    /// <summary>
    /// The text of the tree, not of the whole component. The toolbar's download anchor carries a
    /// percent-encoded copy of the document in its href, so asserting over the markup would find every
    /// value whether or not a row is showing it.
    /// </summary>
    private static string TreeText(IRenderedComponent<JsonView> view) =>
        view.FindAll("[role=tree]").Count == 0 ? string.Empty : view.Find("[role=tree]").TextContent;

    private static IElement ButtonWithText(IRenderedComponent<JsonView> view, string text) =>
        view.FindAll("button, a").First(e => e.TextContent.Trim().StartsWith(text, StringComparison.Ordinal));

    private sealed class Person
    {
        public string FirstName { get; set; } = string.Empty;

        public Address Address { get; set; } = new();
    }

    private sealed class Address
    {
        public string City { get; set; } = string.Empty;

        public string Street { get; set; } = string.Empty;
    }
}
