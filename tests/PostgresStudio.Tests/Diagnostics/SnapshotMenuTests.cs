using Bunit;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Components.Pages.Health;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Statements;
using PostgresStudio.Tests.Components;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The Health page's Snapshot menu (API design §4.3, §5.3): Download hands <c>martenStudio.download.text</c> the visitor's JSON
/// under §4.3's file name, its <c>withheld</c> map names the page's gate, View opens it in the JSON viewer, and the builder
/// asks each page's service as this visitor, starting no statement sweep.
/// </summary>
public class SnapshotMenuTests
{
    private const string StatementsRefusal = "Statements need Capabilities.ViewDatabaseActivity.";

    private sealed class FakeStatements : IStatementsService
    {
        public int Reads { get; private set; }

        public int Latest { get; private set; }

        public Task<StatementsView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(StatementsView.Refused(DatabaseRefusal.CapabilityOff, StatementsRefusal));
        }

        public Task<StatementsView> GetLatestAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Latest++;
            return Task.FromResult(StatementsView.Refused(DatabaseRefusal.CapabilityOff, StatementsRefusal));
        }

        public Task<StatementPlanView> PlanAsync(StudioScope scope, long queryId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Snapshot menu never plans a statement.");

        public Task<PostgresStudio.Services.Query.WhatIfResult> WhatIfAsync(
            StudioScope scope, long queryId, PostgresStudio.Services.Query.HypotheticalIndex index, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Snapshot menu never imagines an index.");
    }

    private sealed class FakeServer : IServerPageService
    {
        public Task<ServerPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerPageView { MeasuredAt = DateTimeOffset.UnixEpoch });
    }

    /// <summary>The Replication page's service: the numbers the download carries (Q36), never names.</summary>
    private sealed class FakeReplication : IReplicationService
    {
        public static readonly WalStatistics Counters = new(1000, 250, 64e6, 5, 12_345, 678, null, null, null);

        public Task<ReplicationView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Snapshot menu asks for the numbers only.");

        public Task<ReplicationView> GetNumbersAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReplicationView
            {
                MeasuredAt = DateTimeOffset.UnixEpoch,
                Wal = ReplicationSectionView<WalView>.Of(new WalView(Counters, null, null, null)),
            });

        public Task<TablespaceSize> MeasureTablespaceAsync(StudioScope scope, string tablespace, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The Snapshot menu never measures a tablespace.");
    }

    private static async Task<(PostgresStudioComponentContext Context, FakeStatements Statements)> ContextAsync(Action<IServiceCollection>? more = null)
    {
        FakeStatements statements = new();

        PostgresStudioComponentContext context = new(services =>
        {
            services.AddSingleton<IStatementsService>(statements);
            services.AddSingleton<IServerPageService>(new FakeServer());
            services.AddSingleton<IReplicationService>(new FakeReplication());
            services.AddSingleton(new HealthCheckRegistry(PostgresStudioDescriptor.Instance));
            services.AddScoped<VisitorSnapshotBuilder>();
            more?.Invoke(services);
        });

        context.Health.WithFindings(MartenStudio.Tests.Health.FakeHealthService.Finding("T01", HealthSeverity.Warning, "A table is read whole", HealthArea.Tables));

        return (await context.ReadyAsync(), statements);
    }

    private static async Task SelectAsync(IRenderedComponent<SnapshotMenu> menu, string label)
    {
        AngleSharp.Dom.IElement item = menu.FindAll("button.ms-menu-item").Single(x => x.TextContent.Contains(label, StringComparison.Ordinal));
        await item.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
    }

    /// <summary>
    /// The markup the stylesheet places the menu by (1.0-UIFIX): the button is the anchor of the popover that sits in the
    /// same wrapper, and the popover carries the shared menu's class. Without an anchor the browser puts a popover in the
    /// middle of the viewport, which is where this menu opened over a finding's SQL.
    /// </summary>
    [Fact]
    public async Task The_menu_is_anchored_to_its_button_in_one_wrapper()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync();
        await using PostgresStudioComponentContext owned = context;

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();

        AngleSharp.Dom.IElement button = menu.Find("button.ms-snapshot-menu-button");
        AngleSharp.Dom.IElement popover = menu.Find(".ms-snapshot-menu-popover");

        popover.ClassList.Should().Contain("ms-menu-popover");
        popover.ParentElement.Should().BeSameAs(button.ParentElement);
        popover.ParentElement!.ClassList.Should().Contain(["ms-menu-button", "ms-snapshot-menu"]);

        string? anchor = button.GetAttribute("style");
        anchor.Should().StartWith("anchor-name: --").And.EndWith("-button");
        popover.GetAttribute("style").Should().Be("position-anchor: " + anchor!["anchor-name: ".Length..]);
    }

    [Fact]
    public async Task Download_hands_the_browser_the_visitors_json_under_the_designed_file_name()
    {
        (PostgresStudioComponentContext context, FakeStatements statements) = await ContextAsync();
        await using PostgresStudioComponentContext owned = context;

        context.JSInterop.Setup<bool>("martenStudio.download.text", static _ => true).SetResult(true);

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();
        await SelectAsync(menu, "Download JSON");

        JSRuntimeInvocation download = context.JSInterop.Invocations.Single(static x => x.Identifier == "martenStudio.download.text");
        string fileName = download.Arguments[0].Should().BeOfType<string>().Subject;
        string json = download.Arguments[2].Should().BeOfType<string>().Subject;

        fileName.Should().MatchRegex(@"^postgres-snapshot-default-default-[0-9]{8}T[0-9]{6}Z\.json$");
        download.Arguments[1].Should().Be("application/json");

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        document.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("withheld").GetProperty("statements").GetString()
            .Should().Be(StatementsRefusal, "the Statements page's own sentence names its gate");
        document.RootElement.GetProperty("findings").GetProperty("items")[0].GetProperty("code").GetString().Should().Be("T01");

        System.Text.Json.JsonElement wal = document.RootElement.GetProperty("wal");
        wal.GetProperty("outcome").GetString().Should().Be("read", "the Replication page's WAL read is in the download (issue #36)");
        wal.GetProperty("records").GetInt64().Should().Be(FakeReplication.Counters.Records);
        wal.GetProperty("syncs").GetInt64().Should().Be(FakeReplication.Counters.Syncs);

        statements.Reads.Should().Be(0, "a snapshot never starts a statement sweep the visitor did not start");
        statements.Latest.Should().Be(1);
        menu.Find(".ms-snapshot-message").TextContent.Should().Be("Saved " + fileName + ".");
    }

    /// <summary>W5-API-fix F4: a download the browser refused is never reported as saved.</summary>
    [Fact]
    public async Task A_download_the_browser_did_not_save_is_not_called_saved()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync();
        await using PostgresStudioComponentContext owned = context;

        context.JSInterop.Setup<bool>("martenStudio.download.text", static _ => true).SetResult(false);

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();
        await SelectAsync(menu, "Download JSON");

        menu.Find(".ms-snapshot-message").TextContent.Should().Be("The browser did not save the file.");
    }

    /// <summary>W5-API-fix F4: a builder that throws is logged at Warning, once, and the visitor is told only that it failed.</summary>
    [Fact]
    public async Task A_snapshot_that_cannot_be_built_is_logged_and_says_so_without_the_exceptions_words()
    {
        CapturingLoggerProvider logs = new();
        (PostgresStudioComponentContext context, _) = await ContextAsync(services =>
        {
            services.AddSingleton<IServerPageService>(new ThrowingServer());
            services.AddLogging(builder => builder.AddProvider(logs));
        });
        await using PostgresStudioComponentContext owned = context;

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();
        await SelectAsync(menu, "View JSON");

        menu.Find(".ms-snapshot-message").TextContent.Should().Be("The snapshot could not be built; the server log has why.")
            .And.NotContain(ThrowingServer.Words);
        menu.FindAll(".ms-snapshot-view").Should().BeEmpty();
        logs.Entries.Should().ContainSingle(static x => x.Level == LogLevel.Warning && x.Message.Contains("could not be built", StringComparison.Ordinal));
    }

    private sealed class ThrowingServer : IServerPageService
    {
        public const string Words = "SENTINEL-EXCEPTION-WORDS";

        public Task<ServerPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(Words);
    }

    [Fact]
    public async Task View_opens_the_json_in_the_viewer_and_Close_closes_it()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync();
        await using PostgresStudioComponentContext owned = context;

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();
        menu.FindAll(".ms-snapshot-view").Should().BeEmpty();

        await SelectAsync(menu, "View JSON");

        menu.WaitForAssertion(() => menu.FindAll("section.ms-snapshot-view[aria-label='Snapshot JSON']").Should().ContainSingle());
        menu.Find(".ms-snapshot-view-name").TextContent.Should().StartWith("postgres-snapshot-default-default-");

        await menu.Find(".ms-snapshot-view-close").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        menu.FindAll(".ms-snapshot-view").Should().BeEmpty();
    }

    [Fact]
    public async Task Copy_puts_the_json_on_the_clipboard()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync();
        await using PostgresStudioComponentContext owned = context;

        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", static _ => true).SetResult(true);

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();
        await SelectAsync(menu, "Copy JSON");

        JSRuntimeInvocation copy = context.JSInterop.Invocations.Single(static x => x.Identifier == "martenStudio.clipboard.copyText");
        copy.Arguments[0].Should().BeOfType<string>().Which.Should().Contain("\"schemaVersion\": 1");
        menu.Find(".ms-snapshot-message").TextContent.Should().Be("Copied the snapshot.");
    }

    /// <summary>1.2-H-B: the Markdown report is written from the very view the JSON is, and saved as <c>.md</c>.</summary>
    [Fact]
    public async Task Download_Markdown_hands_the_browser_the_report_as_markdown_under_the_json_files_name()
    {
        (PostgresStudioComponentContext context, FakeStatements statements) = await ContextAsync();
        await using PostgresStudioComponentContext owned = context;

        context.JSInterop.Setup<bool>("martenStudio.download.text", static _ => true).SetResult(true);

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();
        await SelectAsync(menu, "Download Markdown");

        JSRuntimeInvocation download = context.JSInterop.Invocations.Single(static x => x.Identifier == "martenStudio.download.text");
        string fileName = download.Arguments[0].Should().BeOfType<string>().Subject;
        string markdown = download.Arguments[2].Should().BeOfType<string>().Subject;

        fileName.Should().MatchRegex(@"^postgres-snapshot-default-default-[0-9]{8}T[0-9]{6}Z\.md$");
        download.Arguments[1].Should().Be("text/markdown");
        markdown.Should().StartWith("# Health report: `default` / `default`")
            .And.Contain("A table is read whole")
            .And.Contain("- `statements`: Statements need Capabilities\\.ViewDatabaseActivity\\.");
        statements.Reads.Should().Be(0, "a report never starts a statement sweep the visitor did not start");
        menu.Find(".ms-snapshot-message").TextContent.Should().Be("Saved " + fileName + ".");
    }

    [Fact]
    public async Task Copy_Markdown_puts_the_report_on_the_clipboard()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync();
        await using PostgresStudioComponentContext owned = context;

        context.JSInterop.Setup<bool>("martenStudio.clipboard.copyText", static _ => true).SetResult(true);

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();
        await SelectAsync(menu, "Copy Markdown");

        JSRuntimeInvocation copy = context.JSInterop.Invocations.Single(static x => x.Identifier == "martenStudio.clipboard.copyText");
        copy.Arguments[0].Should().BeOfType<string>().Which.Should().StartWith("# Health report: ");
        menu.Find(".ms-snapshot-message").TextContent.Should().Be("Copied the report.");
    }

    [Fact]
    public async Task A_markdown_report_the_browser_did_not_save_is_not_called_saved()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync();
        await using PostgresStudioComponentContext owned = context;

        context.JSInterop.Setup<bool>("martenStudio.download.text", static _ => true).SetResult(false);

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();
        await SelectAsync(menu, "Download Markdown");

        menu.Find(".ms-snapshot-message").TextContent.Should().Be("The browser did not save the file.");
    }

    [Fact]
    public async Task A_report_that_cannot_be_built_is_logged_and_says_so_without_the_exceptions_words()
    {
        CapturingLoggerProvider logs = new();
        (PostgresStudioComponentContext context, _) = await ContextAsync(services =>
        {
            services.AddSingleton<IServerPageService>(new ThrowingServer());
            services.AddLogging(builder => builder.AddProvider(logs));
        });
        await using PostgresStudioComponentContext owned = context;

        IRenderedComponent<SnapshotMenu> menu = context.Render<SnapshotMenu>();
        await SelectAsync(menu, "Copy Markdown");

        menu.Find(".ms-snapshot-message").TextContent.Should().Be("The snapshot could not be built; the server log has why.")
            .And.NotContain(ThrowingServer.Words);
        logs.Entries.Should().ContainSingle(static x => x.Level == LogLevel.Warning && x.Message.Contains("could not be built", StringComparison.Ordinal));
    }
}
