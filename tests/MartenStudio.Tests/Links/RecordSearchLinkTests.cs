using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Documents;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Settings;

using DetailModel = MartenStudio.Services.Documents.DocumentDetail;
using DocumentPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using StreamPage = MartenStudio.Components.Pages.Events.StreamDetail;
using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Tests.Links;

/// <summary>
/// D-CORR 8.2: the button a document page and a stream page draw for a module's search over the record. It exists only when a
/// module registered the seam and answered with a link; the request holds the page's scope, the record and the window; and with no
/// module, or a module with no answer, the page is the page it was.
/// </summary>
public class RecordSearchLinkTests
{
    private const string DocumentId = "8f1d5a6e-0000-0000-0000-000000000001";

    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void A_document_page_without_the_seam_draws_no_button()
    {
        using DocumentsComponentContext context = new();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        IRenderedComponent<DocumentPage> page = RenderDocument(context);

        page.FindAll(".ms-record-search-link").Should().BeEmpty();
        page.Markup.Should().Contain("Open in Query", "the page itself rendered");
    }

    [Fact]
    public void A_document_page_markup_is_the_same_with_a_seam_that_answers_null_as_without_one()
    {
        string without;
        using (DocumentsComponentContext context = new())
        {
            context.Data.Detail = DocumentDetailResult.Ok(Detail());
            without = Normalized(RenderDocument(context).Markup);
        }

        using DocumentsComponentContext other = new();
        other.Data.Detail = DocumentDetailResult.Ok(Detail());
        other.Services.AddSingleton<IStudioRecordSearchLinks>(new FakeSearchLinks(null));

        Normalized(RenderDocument(other).Markup).Should().Be(without, "a module with nothing to say changes nothing on the page");
    }

    [Fact]
    public void A_document_page_draws_the_modules_label_and_link()
    {
        using DocumentsComponentContext context = new();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        FakeSearchLinks module = new("kafka/related?x=1#v=2", "Find elsewhere");
        context.Services.AddSingleton<IStudioRecordSearchLinks>(module);

        IRenderedComponent<DocumentPage> page = RenderDocument(context);

        AngleSharp.Dom.IElement button = page.Find(".ms-record-search-link");
        button.TextContent.Should().Be("Find elsewhere");
        button.GetAttribute("href").Should().Be("kafka/related?x=1#v=2");
    }

    [Fact]
    public void A_document_page_asks_with_its_scope_its_names_and_a_window_of_an_hour_either_side()
    {
        using DocumentsComponentContext context = new();
        context.Data.Detail = DocumentDetailResult.Ok(Detail() with { ClrType = typeof(RecordSearchLinkTests) });
        FakeSearchLinks module = new("kafka/related");
        context.Services.AddSingleton<IStudioRecordSearchLinks>(module);

        RenderDocument(context);

        StudioRecordSearchRequest request = module.Requests.Should().ContainSingle("the page asks once, not on every render").Which;
        request.StoreKey.Should().Be("default");
        request.TenantId.Should().BeNull();
        request.Reference.Kind.Should().Be(StudioRecordKind.Document);
        request.Reference.Id.Should().Be(DocumentId);
        request.Reference.TypeNames.Should().Equal("customer", typeof(RecordSearchLinkTests).FullName, nameof(RecordSearchLinkTests));
        request.From.Should().Be(new DateTimeOffset(2026, 9, 14, 11, 0, 0, TimeSpan.Zero));
        request.To.Should().Be(new DateTimeOffset(2026, 9, 14, 13, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_window_is_null_when_the_table_keeps_no_last_modified_column()
    {
        using DocumentsComponentContext context = new();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(withLastModified: false));
        FakeSearchLinks module = new("kafka/related");
        context.Services.AddSingleton<IStudioRecordSearchLinks>(module);

        RenderDocument(context);

        StudioRecordSearchRequest request = module.Requests.Should().ContainSingle().Which;
        request.From.Should().BeNull();
        request.To.Should().BeNull();
    }

    [Fact]
    public void A_module_that_throws_leaves_the_document_page_whole()
    {
        using DocumentsComponentContext context = new();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());
        context.Services.AddSingleton<IStudioRecordSearchLinks>(new FakeSearchLinks("x", throws: true));

        IRenderedComponent<DocumentPage> page = RenderDocument(context);

        page.FindAll(".ms-record-search-link").Should().BeEmpty();
        page.Markup.Should().Contain("Open in Query");
    }

    [Fact]
    public async Task A_stream_page_without_the_seam_draws_no_button()
    {
        using StudioComponentContext context = await NewContextAsync();
        context.EventData.StreamStates[StreamId] = Stream();

        IRenderedComponent<StreamPage> page = RenderStream(context);

        page.FindAll(".ms-record-search-link").Should().BeEmpty();
        page.Markup.Should().Contain("Open in feed", "the page itself rendered");
    }

    [Fact]
    public async Task A_stream_page_asks_the_module_for_the_stream_in_its_scope_with_five_minutes_either_side()
    {
        FakeSearchLinks module = new("kafka/related?s=1", "Find elsewhere");
        using StudioComponentContext context = await NewContextAsync(services => services.AddSingleton<IStudioRecordSearchLinks>(module));
        context.EventData.StreamStates[StreamId] = Stream(tenantId: "acme");

        IRenderedComponent<StreamPage> page = RenderStream(context);

        page.Find(".ms-record-search-link").TextContent.Should().Be("Find elsewhere");
        page.Find(".ms-record-search-link").GetAttribute("href").Should().Be("kafka/related?s=1");

        StudioRecordSearchRequest request = module.Requests.Should().ContainSingle("the page asks once, not on every render").Which;
        request.Reference.Kind.Should().Be(StudioRecordKind.Stream);
        request.Reference.TypeNames.Should().BeEmpty();
        request.Reference.Id.Should().Be(StreamId);
        request.StoreKey.Should().Be("default");
        request.From.Should().Be(new DateTimeOffset(2026, 9, 1, 7, 55, 0, TimeSpan.Zero));
        request.To.Should().Be(new DateTimeOffset(2026, 9, 14, 9, 5, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_stream_page_markup_is_the_same_with_a_seam_that_answers_null_as_without_one()
    {
        string without;
        using (StudioComponentContext bare = await NewContextAsync())
        {
            bare.EventData.StreamStates[StreamId] = Stream();
            without = Normalized(RenderStream(bare).Markup);
        }

        using StudioComponentContext context = await NewContextAsync(services => services.AddSingleton<IStudioRecordSearchLinks>(new FakeSearchLinks(null)));
        context.EventData.StreamStates[StreamId] = Stream();

        Normalized(RenderStream(context).Markup).Should().Be(without);
    }

    private static DetailModel Detail(bool withLastModified = true) => new()
    {
        Alias = "customer",
        Id = DocumentId,
        Json = """{"Name":"Customer 01"}""",
        TextBytes = 42,
        StoredBytes = 30,
        Columns =
        [
            new PhysicalColumnValue("id", DocumentId, PhysicalColumnRole.Identity),
            new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
            .. withLastModified
                ? (PhysicalColumnValue[])[new PhysicalColumnValue("mt_last_modified", "2026-09-14 12:00:00.000+00:00", PhysicalColumnRole.Metadata)]
                : [],
        ],
        TableName = "\"studio_sample\".\"mt_doc_customer\"",
        UpsertFunction = "\"studio_sample\".\"mt_upsert_customer\"",
        ExpectedDotNetType = "Sample.Customer",
        IdColumnType = DocumentIdColumnType.Uuid,
    };

    private static StreamState Stream(string? tenantId = null) =>
        new(
            StreamId,
            Exists: true,
            "Order",
            3,
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            false,
            tenantId);

    private static IRenderedComponent<DocumentPage> RenderDocument(DocumentsComponentContext context)
    {
        context.Navigate($"marten/documents/customer/doc?id={DocumentId}");
        return context.Render<DocumentPage>(parameters => parameters.Add(x => x.Alias, "customer"));
    }

    private static IRenderedComponent<StreamPage> RenderStream(StudioComponentContext context)
    {
        context.Navigate("marten/events/streams/s?id=" + StreamId);
        return context.Render<StreamPage>();
    }

    private static async Task<StudioComponentContext> NewContextAsync(Action<IServiceCollection>? configure = null)
    {
        StudioComponentContext context = new(configure);
        await context.ReadyAsync();
        return context;
    }

    /// <summary>The markup with the random ids a page draws for itself made the same, so that two renders compare.</summary>
    private static string Normalized(string markup) =>
        System.Text.RegularExpressions.Regex.Replace(
            markup,
            "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|(?<=menu-)[0-9a-f]{8}",
            static match => match.Value == DocumentId || match.Value == StreamId ? match.Value : "<guid>",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

    /// <summary>A module's seam: what it answers, and every request it was asked.</summary>
    private sealed class FakeSearchLinks : IStudioRecordSearchLinks
    {
        private readonly string? answer;
        private readonly bool throws;

        public FakeSearchLinks(string? answer, string label = "Find it", bool throws = false)
        {
            this.answer = answer;
            this.throws = throws;
            Label = label;
        }

        public string Label { get; }

        public List<StudioRecordSearchRequest> Requests { get; } = [];

        public string? ToSearch(StudioRecordSearchRequest request)
        {
            Requests.Add(request);

            return throws ? throw new InvalidOperationException("the module failed") : answer;
        }
    }
}
