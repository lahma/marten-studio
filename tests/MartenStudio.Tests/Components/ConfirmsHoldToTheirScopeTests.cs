using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Documents;
using MartenStudio.Tests.Events;
using MartenStudio.Tests.Projections;
using MartenStudio.Tests.Schema;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using DetailModel = MartenStudio.Services.Documents.DocumentDetail;
using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using ListPage = MartenStudio.Components.Pages.Documents.Documents;
using ProjectionsPage = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-FINAL-FIX item 7: a confirmation dialog is closed when the scope moves, and what it confirms is the scope it was opened under.
/// Since 1.2 an intercepted Back or Forward to an address naming another scope moves the scope and keeps the page - with its dialog, its
/// pending fields and its selection - so a Skip opened for one database was confirmed against the other. Each owner of a dialog opens
/// it, has the scope moved to the other database, and shows that the dialog is gone and the service was not called.
/// </summary>
public class ConfirmsHoldToTheirScopeTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    /// <summary>Settles the default store on its first database, then lists a second one, as a store with two databases is.</summary>
    private static async Task TwoDatabasesAsync(StudioComponentContext context)
    {
        await context.ReadyAsync();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "localhost.marten", "db-b");
        await context.State.RefreshAsync(Token);
    }

    private static async Task MoveScopeAsync(StudioComponentContext context, IRenderedComponent<IComponent> page)
    {
        await page.InvokeAsync(() => context.State.SetScopeAsync("default", "db-b", null, Token));
        context.State.ActiveScope!.DatabaseId.Should().Be("db-b", "the premise: the scope moved");
    }

    [Fact]
    public async Task Dead_letters_closes_a_Skip_dialog_when_the_scope_moves_and_skips_nothing()
    {
        using StudioComponentContext context = new();
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();
        context.EventData.Shape = context.EventData.Shape with { HasIsSkipped = true };
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(new Guid("22222222-2222-2222-2222-222222222222"), sequence: 77)], false);

        IRenderedComponent<MartenStudio.Components.Pages.Events.DeadLetters> page = context.Render<MartenStudio.Components.Pages.Events.DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());
        await page.FindAll(".ms-dead-letter-actions button").Single(static x => x.TextContent.Contains("Skip event", StringComparison.Ordinal)).ClickAsync(new());
        page.FindAll(".ms-confirm-dialog").Should().ContainSingle("the premise: the dialog is open");

        await MoveScopeAsync(context, page);

        page.WaitForAssertion(() => page.FindAll(".ms-confirm-dialog").Should().BeEmpty("the dialog was opened under the other database"));
        context.EventData.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task Projections_closes_the_Rebuild_dialog_when_the_scope_moves_and_rebuilds_nothing()
    {
        FakeProjectionDataService data = new FakeProjectionDataService().WithProjection("DailySales");
        using StudioComponentContext context = new(services => services.AddSingleton<IProjectionDataService>(data));
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();
        data.HighWaterMark = 5_000;

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();
        page.Find(".ms-projection-rebuild").Click();
        page.WaitForElement(".ms-confirm-dialog");

        await MoveScopeAsync(context, page);

        page.WaitForAssertion(() => page.FindAll(".ms-confirm-dialog").Should().BeEmpty());
        data.Calls.Should().NotContain(static x => x.StartsWith("Rebuild", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Projections_closes_a_pending_Pause_dialog_when_the_scope_moves_and_pauses_nothing()
    {
        FakeProjectionDataService data = new FakeProjectionDataService().WithProjection("DailySales");
        using StudioComponentContext context = new(services => services.AddSingleton<IProjectionDataService>(data));
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();

        IRenderedComponent<ProjectionsPage> page = context.Render<ProjectionsPage>();
        page.Find(".ms-daemon-pause").Click();
        page.WaitForElement(".ms-confirm-dialog");

        await MoveScopeAsync(context, page);

        page.WaitForAssertion(() => page.FindAll(".ms-confirm-dialog").Should().BeEmpty());
        data.Calls.Should().NotContain("PauseDaemon");
    }

    [Fact]
    public async Task The_document_list_closes_the_bulk_delete_dialog_when_the_scope_moves_and_deletes_nothing()
    {
        using DocumentsComponentContext context = new();
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();
        context.Data.Page = FakeDocuments.Page(rows: [.. Enumerable.Range(1, 3).Select(i => FakeDocuments.Row("id-" + i))]);
        context.Data.Rail = FakeDocuments.Rail(documents: [FakeDocuments.Collection("customer", softDeleted: true)]);
        context.Navigate("marten/documents/customer");

        IRenderedComponent<ListPage> page = context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));
        page.Find(".ms-doc-select-all").Change(true);
        page.Find(".ms-doc-bulk-delete").Click();
        page.FindAll(".ms-confirm-dialog").Should().ContainSingle("the premise: the dialog is open");

        await MoveScopeAsync(context, page);

        page.WaitForAssertion(() => page.FindAll(".ms-confirm-dialog").Should().BeEmpty());
        context.Writes.CallsTo("BulkDeleteAsync").Should().BeEmpty();
    }

    [Fact]
    public async Task The_document_page_closes_the_delete_dialog_when_the_scope_moves_and_deletes_nothing()
    {
        using DocumentsComponentContext context = new();
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();
        const string id = "7a3f9b2e-5c1d-4e8a-9f60-2b7d4c1e8a35";
        context.Data.Detail = DocumentDetailResult.Ok(new DetailModel
        {
            Alias = "customer",
            Id = id,
            Json = "{\"Name\":\"Ada\"}",
            TextBytes = 14,
            StoredBytes = 14,
            Columns =
            [
                new PhysicalColumnValue("id", id, PhysicalColumnRole.Identity),
                new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
                new PhysicalColumnValue("mt_version", "00000000-0000-0000-0000-000000000001", PhysicalColumnRole.Metadata),
                new PhysicalColumnValue("mt_deleted", "False", PhysicalColumnRole.Metadata),
            ],
            TableName = "\"studio_sample\".\"mt_doc_customer\"",
            ClrType = typeof(ConfirmsHoldToTheirScopeTests),
            IsRegistered = true,
            IdColumnType = DocumentIdColumnType.Uuid,
        });
        context.Navigate("marten/documents/customer/doc?id=" + id);

        IRenderedComponent<DetailPage> page = context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));
        page.Find(".ms-doc-delete-btn").Click();
        page.FindAll(".ms-confirm-dialog").Should().ContainSingle("the premise: the dialog is open");

        await MoveScopeAsync(context, page);

        page.WaitForAssertion(() => page.FindAll(".ms-confirm-dialog").Should().BeEmpty());
        context.Writes.CallsTo("DeleteAsync").Should().BeEmpty();
    }

    [Fact]
    public async Task The_drift_tab_closes_the_Apply_dialog_when_the_scope_moves_and_applies_nothing()
    {
        FakeSchemaDataService schema = new()
        {
            Preview = new MigrationPreview("CREATE TABLE studio.mt_doc_new (id uuid not null);", 1, [], "Create", null),
        };
        using StudioComponentContext context = new(services => services.AddSingleton<ISchemaDataService>(schema));
        await TwoDatabasesAsync(context);
        context.Options.Capabilities = MartenStudioCapabilities.All();

        IRenderedComponent<MartenStudio.Components.Pages.Schema.DriftTab> tab = context.Render<MartenStudio.Components.Pages.Schema.DriftTab>();
        tab.FindAll("button").First(static x => x.TextContent.Contains("Preview migration", StringComparison.Ordinal)).Click();
        tab.WaitForAssertion(() => tab.Find(".ms-schema-apply").HasAttribute("disabled").Should().BeFalse());
        tab.Find(".ms-schema-apply").Click();
        tab.WaitForAssertion(() => tab.FindAll(".ms-confirm-dialog").Should().ContainSingle());

        await MoveScopeAsync(context, tab);

        tab.WaitForAssertion(() => tab.FindAll(".ms-confirm-dialog").Should().BeEmpty());
        schema.Applies.Should().Be(0);
    }
}
