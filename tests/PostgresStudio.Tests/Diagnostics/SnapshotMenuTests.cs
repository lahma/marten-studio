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
    }

    private sealed class FakeServer : IServerPageService
    {
        public Task<ServerPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerPageView { MeasuredAt = DateTimeOffset.UnixEpoch });
    }

    private static async Task<(PostgresStudioComponentContext Context, FakeStatements Statements)> ContextAsync(Action<IServiceCollection>? more = null)
    {
        FakeStatements statements = new();

        PostgresStudioComponentContext context = new(services =>
        {
            services.AddSingleton<IStatementsService>(statements);
            services.AddSingleton<IServerPageService>(new FakeServer());
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
}
