using AngleSharp.Dom;

using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Tests.Components;

using Microsoft.AspNetCore.Components.Web;

using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using DetailModel = MartenStudio.Services.Documents.DocumentDetail;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The document detail page: the JSON pane, the "explain this document" pane, the related documents and
/// the copy actions.
/// </summary>
public class DocumentDetailPageTests
{
    private const string Id = "8f1d5a6e-0000-0000-0000-000000000001";

    private static DetailModel Detail(
        string json = """{"Name":"Customer 01","Email":"a@b.c"}""",
        IReadOnlyList<PhysicalColumnValue>? columns = null,
        IReadOnlyList<DuplicatedFieldAgreement>? duplicated = null,
        bool mismatch = false,
        bool deleted = false,
        bool registered = true) => new()
    {
        Alias = "customer",
        Id = Id,
        Json = json,
        TextBytes = 42,
        StoredBytes = 30,
        Columns = columns ??
        [
            new PhysicalColumnValue("id", Id, PhysicalColumnRole.Identity),
            new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
            new PhysicalColumnValue("mt_last_modified", "2026-09-14 12:00:00.000+00:00", PhysicalColumnRole.Metadata),
            new PhysicalColumnValue("email", "a@b.c", PhysicalColumnRole.Duplicated),
        ],
        Duplicated = duplicated ??
        [
            new DuplicatedFieldAgreement("email", "Email", ["Email"], "a@b.c", "a@b.c", AgreementState.Agrees),
        ],
        TableName = "\"studio_sample\".\"mt_doc_customer\"",
        UpsertFunction = "\"studio_sample\".\"mt_upsert_customer\"",
        StoredDotNetType = mismatch ? "Somewhere.Else.Renamed, Old" : null,
        ExpectedDotNetType = "Sample.Customer",
        DotNetTypeMismatch = mismatch,
        IsRegistered = registered,
        IsDeleted = deleted,
        IdColumnType = DocumentIdColumnType.Uuid,
    };

    private static IRenderedComponent<DetailPage> Render(DocumentsComponentContext context)
    {
        context.Navigate($"marten/documents/customer/doc?id={Id}");

        // The id is a [SupplyParameterFromQuery] parameter, and bUnit insists it arrive the way the
        // browser delivers it: through the navigation manager, not as a component parameter.
        return context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));
    }

    [Fact]
    public void Both_panes_are_rendered_the_json_on_the_left_and_the_columns_on_the_right()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        page.Find(".ms-doc-detail-json").TextContent.Should().Contain("Customer 01");
        page.Find(".ms-doc-meta").TextContent.Should().Contain("mt_last_modified");
    }

    [Fact]
    public void The_metadata_pane_names_the_table_the_upsert_function_and_both_sizes()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        page.KeyValue("Table").Should().Be("\"studio_sample\".\"mt_doc_customer\"");
        page.KeyValue("Upsert function").Should().Be("\"studio_sample\".\"mt_upsert_customer\"");
        page.KeyValue("JSON size").Should().Be("42 B");
        page.KeyValue("Stored size").Should().Be("30 B");
        page.KeyValue("id column").Should().Be("uuid");
    }

    [Fact]
    public void Every_physical_column_present_is_listed_including_the_ones_that_are_null()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(columns:
        [
            new PhysicalColumnValue("id", Id, PhysicalColumnRole.Identity),
            new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
            new PhysicalColumnValue("tenant_id", null, PhysicalColumnRole.Metadata),
        ]));

        var page = Render(context);

        page.TextOfAll(".ms-doc-meta-table tbody th").Should().Contain(["id", "data", "tenant_id"]);
        page.FindAll(".ms-json-null").Should().NotBeEmpty("a null column is drawn as null, not as blank");
    }

    /// <summary>
    /// A column name repeated in the Columns table renders, rather than ending the session. Issue #1.
    /// </summary>
    /// <remarks>
    /// The repeat itself is fixed where it was made — <c>MartenDuplicatedFields</c> — so this is the other
    /// half: the pane keys its rows on the ordinal, because a duplicate <c>@key</c> throws inside Blazor's
    /// diff builder, which is not a place a component can catch anything. The exception escapes the render
    /// and terminates the circuit, so the page does not show an error, it stops existing. Every repeat
    /// still to be found now costs a duplicated row instead of a 500.
    /// </remarks>
    [Fact]
    public void A_repeated_column_name_renders_twice_rather_than_taking_the_circuit_down()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(
            columns:
            [
                new PhysicalColumnValue("id", Id, PhysicalColumnRole.Identity),
                new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
                new PhysicalColumnValue("mt_version", "00000000-0000-0000-0000-000000000001", PhysicalColumnRole.Metadata),
                new PhysicalColumnValue("mt_version", "00000000-0000-0000-0000-000000000001", PhysicalColumnRole.Duplicated),
            ],
            duplicated:
            [
                new DuplicatedFieldAgreement("mt_version", "Version", ["Version"], "1", "1", AgreementState.Agrees),
                new DuplicatedFieldAgreement("mt_version", "Version", ["Version"], "1", "1", AgreementState.Agrees),
            ]));

        var page = Render(context);

        // Measured rather than reasoned about, because it is easy to get backwards: with the fix reverted
        // this pane's *first* render draws all six rows and throws nothing, and the render after it is the
        // one that throws. So the second render is not decoration - without it the test passes either way
        // and proves nothing. In the browser it is the interactive render arriving over the circuit.
        page.Render();

        page.TextOfAll(".ms-doc-meta-table tbody th").Where(x => x == "mt_version").Should().HaveCount(4,
            "twice in the Columns table and twice in the Duplicated fields table - and no exception");
    }

    [Fact]
    public void A_duplicated_field_that_has_drifted_from_its_json_gets_a_red_dot_and_says_why()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(duplicated:
        [
            new DuplicatedFieldAgreement("email", "Email", ["Email"], "old@b.c", "a@b.c", AgreementState.Differs),
        ]));

        var page = Render(context);

        var dot = page.Find(".ms-agree-dot");
        dot.ClassList.Should().Contain("ms-agree-differs");
        dot.GetAttribute("aria-label").Should().Contain("without going through Marten's upsert function");
    }

    [Fact]
    public void A_duplicated_field_the_studio_could_not_resolve_is_unknown_rather_than_wrong()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(duplicated:
        [
            new DuplicatedFieldAgreement("address_city", "AddressCity", null, "Helsinki", null, AgreementState.Unknown),
        ]));

        Render(context).Find(".ms-agree-dot").ClassList.Should().Contain("ms-agree-unknown");
    }

    [Fact]
    public void A_dotnet_type_mismatch_is_a_warning_that_says_what_both_sides_think()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(mismatch: true));

        var alert = Render(context).Find(".ms-doc-meta .ms-alert-warning");

        alert.TextContent.Should().Contain("Somewhere.Else.Renamed");
        alert.TextContent.Should().Contain("Sample.Customer");
    }

    [Fact]
    public void An_unregistered_collection_says_it_is_read_only()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(registered: false));

        Render(context).Find(".ms-doc-meta .ms-alert-info").TextContent.Should().Contain("read-only");
    }

    /// <summary>
    /// Both metadata tables scroll, and their value cells may break anywhere.
    /// </summary>
    /// <remarks>
    /// A 40-character e-mail in a value cell has no break point of its own, so the cell asked for its own
    /// width and the panel grew past its column - 1235px inside 1200px on a desktop and 534px inside
    /// 334px on a phone, which gave the whole detail page a horizontal overflow it could not scroll.
    /// </remarks>
    [Fact]
    public void The_metadata_tables_scroll_and_their_value_cells_may_break_anywhere()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        page.FindAll(".ms-doc-meta-table").Should().HaveCount(2, "the columns and the duplicated fields");
        page.ShouldPutEveryTableInALabelledScrollRegion();
        page.FindAll(".ms-doc-meta-table .ms-doc-meta-value").Should().NotBeEmpty(
            "the value cells are the ones that carry an e-mail or an id and the ones allowed to wrap");
    }

    [Fact]
    public void A_soft_deleted_document_says_so_without_hiding_itself()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(deleted: true));

        Render(context).Find(".ms-doc-detail-page > .ms-alert-warning").TextContent
            .Should().Contain("soft-deleted");
    }

    /// <summary>
    /// The same assertion as before the three copy buttons became one menu: the menu item is the button,
    /// and it reaches the same handler.
    /// </summary>
    [Fact]
    public async Task Copy_as_C_sharp_record_puts_a_record_for_this_document_on_the_clipboard()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        await CopyMenuItem(page, "Copy as C# record").ClickAsync(new MouseEventArgs());

        var invocation = context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Single();

        var code = invocation.Arguments[0]!.ToString()!;
        code.Should().Contain("public sealed record");
        code.Should().Contain("Name");
        code.Should().Contain("Email");
    }

    [Fact]
    public async Task Copy_json_and_copy_id_go_through_the_same_clipboard_helper()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        await CopyMenuItem(page, "Copy id").ClickAsync(new MouseEventArgs());

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Single()
            .Arguments[0].Should().Be(Id);
    }

    /// <summary>Copy JSON is the third item, and it copies the document rather than the id.</summary>
    [Fact]
    public async Task Copy_json_from_the_menu_copies_the_document_as_it_is_stored()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        await CopyMenuItem(page, "Copy JSON").ClickAsync(new MouseEventArgs());

        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Single()
            .Arguments[0].Should().Be("""{"Name":"Customer 01","Email":"a@b.c"}""");
    }

    /// <summary>
    /// The menu carries exactly the three copies the header used to spell out, and nothing else moved
    /// into it.
    /// </summary>
    [Fact]
    public void The_copy_menu_offers_the_three_copies_the_header_used_to_spell_out()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        page.TextOfAll(".ms-doc-copy-popover .ms-menu-item .ms-menu-item-label")
            .Should().Equal("Copy JSON", "Copy id", "Copy as C# record");

        IElement menu = page.Find(".ms-doc-copy-popover");
        menu.GetAttribute("role").Should().Be("menu");
        menu.GetAttribute("popover").Should().Be(
            "auto",
            "light dismiss and Escape are the platform's, not a click-outside handler of ours");

        page.Find(".ms-doc-copy-menu-button").GetAttribute("popovertarget")
            .Should().Be(menu.GetAttribute("id"));
    }

    /// <summary>
    /// A menu is one tab stop with a roving tabindex inside it, so the arrow keys have to move which
    /// item is focusable. Keeping <c>role="menu"</c> and leaving three tab stops behind would tell a
    /// screen-reader user this is a menu and then behave like a row of buttons.
    /// </summary>
    [Fact]
    public async Task The_arrow_keys_move_the_roving_tabindex_between_the_copy_menu_items()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        Tabindexes(page).Should().Equal("0", "-1", "-1");

        await page.Find(".ms-doc-copy-popover").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        Tabindexes(page).Should().Equal("-1", "0", "-1");

        await page.Find(".ms-doc-copy-popover").KeyDownAsync(new KeyboardEventArgs { Key = "End" });
        Tabindexes(page).Should().Equal("-1", "-1", "0");

        await page.Find(".ms-doc-copy-popover").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        Tabindexes(page).Should().Equal(["0", "-1", "-1"], "the end wraps round to the top");

        context.JSInterop.Invocations["martenStudio.json.focusElement"].Should().NotBeEmpty(
            "moving the tabindex without moving DOM focus would leave the keyboard where it was");
    }

    /// <summary>
    /// The button says whether the menu is open. The browser opens and closes a popover on its own -
    /// Escape, a click anywhere else - so the flag is driven by the toggle event rather than by the
    /// click that happened to open it.
    /// </summary>
    [Fact]
    public async Task The_copy_button_reports_whether_the_menu_is_open()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        page.Find(".ms-doc-copy-menu-button").GetAttribute("aria-expanded").Should().Be("false");

        await page.Find(".ms-doc-copy-popover").TriggerEventAsync("ontoggle", EventArgs.Empty);
        page.Find(".ms-doc-copy-menu-button").GetAttribute("aria-expanded").Should().Be("true");

        await page.Find(".ms-doc-copy-popover").TriggerEventAsync("ontoggle", EventArgs.Empty);
        page.Find(".ms-doc-copy-menu-button").GetAttribute("aria-expanded").Should().Be("false");
    }

    /// <summary>
    /// Nine equal-weight buttons, three of them copies and two of them repeated in the viewer's own
    /// toolbar sixty pixels below, is not a header anybody reads. Six controls: Open in Query, Download,
    /// the copy menu, the previous/next pair, Edit and Delete.
    /// </summary>
    [Fact]
    public void The_header_carries_six_controls_and_a_stream_link_is_the_seventh()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        Controls(Render(context)).Should().Equal(
            "Open in Query", "Download", "Copy", "◂▸", "Edit", "Delete");

        using var withStream = new DocumentsComponentContext();
        withStream.Data.Detail = DocumentDetailResult.Ok(Detail());
        withStream.Data.StreamExists = true;

        Controls(Render(withStream)).Should().Equal(
            "Open in Query", "View stream", "Download", "Copy", "◂▸", "Edit", "Delete");
    }

    /// <summary>
    /// The previous/next pair is two icon buttons, so each carries its own accessible name. The old
    /// glyphs were text - a screen reader read "black left-pointing triangle" - and they inherited the
    /// button's font, which rendered them at a different weight in every browser.
    /// </summary>
    [Fact]
    public void The_previous_and_next_buttons_are_named_rather_than_drawn_as_text()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        List<IElement> nav = [.. page.FindAll(".ms-doc-detail-nav a")];

        nav.Select(x => x.GetAttribute("aria-label"))
            .Should().Equal("Previous document", "Next document");
        nav.Select(x => x.GetAttribute("title")).Should().Equal(
            "The previous document on the list you came from",
            "The next document on the list you came from");

        nav.Should().OnlyContain(x => x.QuerySelector("svg") != null, "the glyph is inline SVG");
        nav.Should().OnlyContain(
            x => x.TextContent.Trim().Length == 0,
            "and nothing but the SVG, so there is no ◀ for a screen reader to read out");

        // Nothing came back from a list, so both ends are disabled - exactly as they were before.
        nav.Should().OnlyContain(x => x.GetAttribute("aria-disabled") == "true");
        nav.Should().OnlyContain(x => x.ClassList.Contains("ms-btn-disabled"));
    }

    private static IElement CopyMenuItem<T>(IRenderedComponent<T> page, string label)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        page.FindAll(".ms-doc-copy-popover .ms-menu-item")
            .First(x => x.QuerySelector(".ms-menu-item-label")!.TextContent.Trim() == label);

    private static List<string> Tabindexes<T>(IRenderedComponent<T> page)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        [.. page.FindAll(".ms-doc-copy-popover .ms-menu-item").Select(x => x.GetAttribute("tabindex") ?? string.Empty)];

    /// <summary>
    /// The header's controls, in order, with the previous/next pair counted as the one control it reads
    /// as. Everything inside <c>.ms-doc-detail-nav</c> is that pair; everything else with
    /// <c>.ms-btn</c> stands on its own.
    /// </summary>
    private static List<string> Controls<T>(IRenderedComponent<T> page)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        List<string> controls = [];

        foreach (IElement element in page.FindAll(".ms-doc-detail-actions .ms-btn, .ms-doc-detail-actions .ms-doc-detail-nav"))
        {
            if (element.ClassList.Contains("ms-doc-detail-nav"))
            {
                controls.Add("◂▸");
            }
            else if (element.Closest(".ms-doc-detail-nav") is null)
            {
                controls.Add(element.TextContent.Trim());
            }
        }

        return controls;
    }

    [Fact]
    public void The_view_stream_link_appears_only_when_a_stream_with_this_id_exists()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        Render(context).FindAll(".ms-doc-detail-actions a")
            .Should().NotContain(x => x.TextContent.Trim() == "View stream");

        using var withStream = new DocumentsComponentContext();
        withStream.Data.Detail = DocumentDetailResult.Ok(Detail());
        withStream.Data.StreamExists = true;

        Render(withStream).FindAll(".ms-doc-detail-actions a")
            .Should().Contain(x => x.TextContent.Trim() == "View stream");
    }

    [Fact]
    public void Related_documents_are_listed_and_a_dangling_reference_is_marked_rather_than_hidden()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Data.Related = new RelatedDocuments(
        [
            new RelatedDocumentLink("customer_id", "customer", "Customer", "c-1", Exists: true, Hue: 200),
            new RelatedDocumentLink("owner_id", "person", "Person", "p-9", Exists: false, Hue: 100),
        ]);

        var page = Render(context);

        page.TextOfAll(".ms-related-item .ms-related-column").Should().Equal("customer_id", "owner_id");
        page.Find(".ms-related-missing").TextContent.Trim().Should().Be("p-9");
        page.Find(".ms-related-item .ms-badge-warning").TextContent.Trim().Should().Be("missing");
    }

    [Fact]
    public void A_malformed_id_gets_a_friendly_not_found_with_the_shape_of_the_query_that_was_tried()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Missing(
            "'oops' is not a GUID, and this collection's id column is uuid.",
            "select \"data\"::text from \"studio_sample\".\"mt_doc_customer\" where \"id\" = @id",
            malformed: true);

        var page = Render(context);

        page.Find(".ms-empty-title").TextContent.Should().Be("No such document");
        page.Find(".ms-empty-description").TextContent.Should().Contain("is not a GUID");
        page.Find(".ms-code").TextContent.Should().Contain("mt_doc_customer");
        page.Find(".ms-doc-probe .ms-btn").TextContent.Should().Contain("Search other collections");
    }

    [Fact]
    public async Task Probing_the_other_collections_says_which_of_them_holds_the_id()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Missing("not there", null, malformed: true);
        context.Data.Probes =
        [
            new DocumentIdProbe("order", 100, Found: true),
            new DocumentIdProbe("invoice", 200, Found: false),
        ];

        var page = Render(context);

        await page.Find(".ms-doc-probe .ms-btn").ClickAsync(new MouseEventArgs());

        page.TextOfAll(".ms-doc-probe-list .ms-collection-alias").Should().Equal("order", "invoice");
        page.Find(".ms-doc-probe-list .ms-doc-id").TextContent.Trim().Should().Be("found");
    }

    [Fact]
    public void Prev_and_next_come_from_the_list_the_document_was_opened_from()
    {
        using var context = new DocumentsComponentContext();
        context.Browser.RememberPage("customer", ["a", Id, "z"], "marten/documents/customer");
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = Render(context);

        var arrows = page.FindAll(".ms-doc-detail-nav a");
        arrows.Should().HaveCount(2);
        arrows[0].GetAttribute("href").Should().Contain("id=a");
        arrows[1].GetAttribute("href").Should().Contain("id=z");
    }

    [Fact]
    public void Without_a_remembered_list_the_arrows_are_disabled_rather_than_absent()
    {
        using var context = new DocumentsComponentContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var arrows = Render(context).FindAll(".ms-doc-detail-nav a");

        arrows.Should().OnlyContain(x => x.GetAttribute("aria-disabled") == "true");
    }
}
