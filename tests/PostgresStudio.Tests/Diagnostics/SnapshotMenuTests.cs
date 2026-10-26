using Bunit;

using Microsoft.Extensions.DependencyInjection;

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

    private static async Task<(PostgresStudioComponentContext Context, FakeStatements Statements)> ContextAsync()
    {
        FakeStatements statements = new();

        PostgresStudioComponentContext context = new(services =>
        {
            services.AddSingleton<IStatementsService>(statements);
            services.AddSingleton<IServerPageService>(new FakeServer());
            services.AddSingleton(new HealthCheckRegistry(PostgresStudioDescriptor.Instance));
            services.AddScoped<VisitorSnapshotBuilder>();
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
        menu.Find(".ms-snapshot-message").TextContent.Should().Contain(fileName);
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
