using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Documents;
using MartenStudio.Tests.Events;
using MartenStudio.Tests.Schema;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using DetailModel = MartenStudio.Services.Documents.DocumentDetail;
using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-FINAL-FIX-3 (F2): the captured-scope refusal, not only the close. <c>StudioState</c> sets <c>ActiveScope</c> and then awaits each
/// <c>ScopeChanged</c> handler in turn, so a click can land while the dialog is still open - before the page's own handler has closed it.
/// Each test registers, before the page renders, a handler that waits on a gate; starts the move to the other database without awaiting
/// it; clicks Confirm; shows that the service was not called; and only then lets the move finish.
/// </summary>
public class ConfirmsRefuseDuringScopeChangeTests
{
    private const string ConfirmButton = ".ms-confirm-actions button.ms-button-danger";

    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    /// <summary>A scope change that is announced to its handlers one at a time, the first of which (the test's) waits.</summary>
    private sealed class Gate
    {
        private readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool armed;

        public Gate(StudioComponentContext context) => context.State.ScopeChanged += () => armed ? open.Task : Task.CompletedTask;

        /// <summary>Starts the move to the other database and returns once <c>ActiveScope</c> has changed, with the handlers still waiting.</summary>
        public async Task<Task> BeginMoveAsync(StudioComponentContext context, IRenderedComponent<IComponent> page)
        {
            armed = true;
            Task move = page.InvokeAsync(() => context.State.SetScopeAsync("default", "db-b", null, Token));
            context.State.ActiveScope!.DatabaseId.Should().Be("db-b", "the premise: the scope has moved");
            move.IsCompleted.Should().BeFalse("the premise: the handlers are still being awaited");
            await Task.Yield();
            return move;
        }

        public Task FinishAsync(Task move)
        {
            open.SetResult();
            return move;
        }
    }

    private static async Task TwoDatabasesAsync(StudioComponentContext context)
    {
        await context.ReadyAsync();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "localhost.marten", "db-b");
        await context.State.RefreshAsync(Token);
    }

    [Fact]
    public async Task Dead_letters_does_not_skip_when_Confirm_lands_during_the_scope_change()
    {
        using StudioComponentContext context = new();
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();
        context.EventData.Shape = context.EventData.Shape with { HasIsSkipped = true };
        context.EventData.DeadLetters = new DeadLetterPage([FakeEventDataService.DeadLetter(new Guid("22222222-2222-2222-2222-222222222222"), sequence: 77)], false);
        Gate gate = new(context);

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find("tbody tr td.ms-table-cell-actions button").ClickAsync(new());
        await page.FindAll(".ms-dead-letter-actions button").Single(static x => x.TextContent.Contains("Skip event", StringComparison.Ordinal)).ClickAsync(new());
        page.FindAll(".ms-confirm-dialog").Should().ContainSingle("the premise: the dialog is open");

        Task move = await gate.BeginMoveAsync(context, page);
        page.FindAll(".ms-confirm-dialog").Should().ContainSingle("the page's own handler has not run yet");
        await page.Find(ConfirmButton).ClickAsync(new());

        context.EventData.Skipped.Should().BeEmpty("the dialog was opened under the other database");
        await gate.FinishAsync(move);
    }

    [Fact]
    public async Task Dead_letter_groups_does_not_skip_when_Confirm_lands_during_the_scope_change()
    {
        using StudioComponentContext context = new();
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();
        context.EventData.Shape = context.EventData.Shape with { HasIsSkipped = true };
        context.EventData.Groups = FakeEventDataService.GroupList(FakeEventDataService.Group(events: 40, skipped: 3));
        context.EventData.SkipPlan = new DeadLetterGroupSkipPlan(37, 37, 3, null);
        Gate gate = new(context);
        context.Navigate("marten/events/dead-letters?view=grouped");

        IRenderedComponent<DeadLetters> page = context.Render<DeadLetters>();
        await page.Find(".ms-dl-group-skip").ClickAsync(new());
        page.FindAll(".ms-confirm-dialog").Should().ContainSingle("the premise: the dialog is open");
        await page.Find("#ms-confirm-input").InputAsync(new() { Value = "37" });

        Task move = await gate.BeginMoveAsync(context, page);
        await page.Find(ConfirmButton).ClickAsync(new());

        context.EventData.GroupSkips.Should().BeEmpty();
        await gate.FinishAsync(move);
    }

    [Fact]
    public async Task The_drift_tab_does_not_apply_when_Confirm_lands_during_the_scope_change()
    {
        FakeSchemaDataService schema = new()
        {
            Preview = new MigrationPreview("CREATE TABLE studio.mt_doc_new (id uuid not null);", 1, [], "Create", null),
        };
        using StudioComponentContext context = new(services => services.AddSingleton<ISchemaDataService>(schema));
        await TwoDatabasesAsync(context);
        context.Options.Capabilities = MartenStudioCapabilities.All();
        Gate gate = new(context);

        IRenderedComponent<MartenStudio.Components.Pages.Schema.DriftTab> tab = context.Render<MartenStudio.Components.Pages.Schema.DriftTab>();
        tab.FindAll("button").First(static x => x.TextContent.Contains("Preview migration", StringComparison.Ordinal)).Click();
        tab.WaitForAssertion(() => tab.Find(".ms-schema-apply").HasAttribute("disabled").Should().BeFalse());
        tab.Find(".ms-schema-apply").Click();
        tab.WaitForAssertion(() => tab.FindAll(".ms-confirm-dialog").Should().ContainSingle());
        await tab.Find("#ms-confirm-input").InputAsync(new() { Value = schema.DatabaseIdentity });

        Task move = await gate.BeginMoveAsync(context, tab);
        await tab.Find(ConfirmButton).ClickAsync(new());

        schema.Applies.Should().Be(0);
        await gate.FinishAsync(move);
    }

    private static StreamState Stream() =>
        new(
            StreamId,
            Exists: true,
            "Order",
            3,
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            false,
            null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stream_detail_does_not_archive_when_Confirm_lands_during_the_scope_change(bool moving)
    {
        using StudioComponentContext context = new();
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();
        context.EventData.StreamStates[StreamId] = Stream();
        Gate gate = new(context);
        context.Navigate("marten/events/streams/s?id=" + StreamId);

        IRenderedComponent<StreamDetail> page = context.Render<StreamDetail>();
        await page.Find("button.ms-button-danger").ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = StreamId });

        Task? move = moving ? await gate.BeginMoveAsync(context, page) : null;
        await page.Find(ConfirmButton).ClickAsync(new());

        // The control (not moving): the same clicks do archive, so the empty list below is the refusal and nothing else.
        context.EventData.Archived.Should().HaveCount(moving ? 0 : 1);
        if (move is not null)
        {
            await gate.FinishAsync(move);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stream_detail_does_not_compact_when_Confirm_lands_during_the_scope_change(bool moving)
    {
        using StudioComponentContext context = new();
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();
        context.EventData.StreamStates[StreamId] = Stream();
        context.EventData.CompactionPlans[StreamId] = FakeEventDataService.Offered(StreamId, version: 3);
        Gate gate = new(context);
        context.Navigate("marten/events/streams/s?id=" + StreamId);

        IRenderedComponent<StreamDetail> page = context.Render<StreamDetail>();
        await page.Find(".ms-stream-compact-button").ClickAsync(new());
        await page.Find(".ms-confirm-dialog input").InputAsync(new() { Value = StreamId });

        Task? move = moving ? await gate.BeginMoveAsync(context, page) : null;
        await page.Find(ConfirmButton).ClickAsync(new());

        context.EventData.Compactions.Should().HaveCount(moving ? 0 : 1);
        if (move is not null)
        {
            await gate.FinishAsync(move);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_document_save_preview_does_not_save_when_Confirm_lands_during_the_scope_change(bool moving)
    {
        const string id = "7a3f9b2e-5c1d-4e8a-9f60-2b7d4c1e8a35";
        const string stored = """{"Name":"Customer 01"}""";
        const string edited = """{"Name":"Customer 02"}""";

        using DocumentsComponentContext context = new();
        await TwoDatabasesAsync(context);
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(new DetailModel
        {
            Alias = "customer",
            Id = id,
            Json = stored,
            TextBytes = 22,
            StoredBytes = 22,
            Columns =
            [
                new PhysicalColumnValue("id", id, PhysicalColumnRole.Identity),
                new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
                new PhysicalColumnValue("mt_version", "00000000-0000-0000-0000-000000000001", PhysicalColumnRole.Metadata),
                new PhysicalColumnValue("mt_deleted", "False", PhysicalColumnRole.Metadata),
            ],
            TableName = "\"studio_sample\".\"mt_doc_customer\"",
            ClrType = typeof(ConfirmsRefuseDuringScopeChangeTests),
            IsRegistered = true,
            IdColumnType = DocumentIdColumnType.Uuid,
        });
        context.Writes.Preview = FakeDocumentWriteService.Ready(stored, edited, "legacyNote");
        context.JSInterop.Setup<string?>("martenStudio.json.readValue", _ => true).SetResult(edited);
        Gate gate = new(context);
        context.Navigate("marten/documents/customer/doc?id=" + id);

        IRenderedComponent<DetailPage> page = context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));
        page.Find(".ms-doc-edit-btn").Click();
        page.Find(".ms-editor-save").Click();
        page.FindAll(".ms-write-preview").Should().ContainSingle("the premise: the save-preview dialog is open");

        Task? move = moving ? await gate.BeginMoveAsync(context, page) : null;
        page.FindAll(".ms-write-preview-actions button")[1].Click();

        context.Writes.CallsTo("SaveAsync").Should().HaveCount(moving ? 0 : 1, "the preview was built under the other database when moving");
        if (move is not null)
        {
            await gate.FinishAsync(move);
        }
    }
}
