using Bunit;

using MartenStudio.Tests.Health;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages.Server;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;
using PostgresStudio.Tests.Components;

using static PostgresStudio.Tests.Server.ServerTestFacts;

using ServerPage = PostgresStudio.Components.Pages.Server.Server;

namespace PostgresStudio.Tests.Server;

/// <summary>System › Server (W5-S2): its sections, the empty platform slot, a hidden setting's sentence, and no capability needed.</summary>
public class ServerPageTests
{
    private sealed class FakeServerPage : IServerPageService
    {
        public ServerPageView View { get; set; } = ServerPageView.Refused(ServerRefusal.Unavailable, "unset");

        public List<StudioScope> Requests { get; } = [];

        public Task<ServerPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Requests.Add(scope);
            return Task.FromResult(View);
        }
    }

    private static ServerPageView View()
    {
        ServerSettingsFacts facts = new(
            Now,
            new PostgresServerFacts(Now, 170011, "17.11", SelfHosted),
            ServerRead<PostgresGrants>.Of(Grants("probe")),
            ServerRead<ServerSettingsRead>.Of(Settings(
                Setting("work_mem", "4096", "kB", "configuration file"),
                Setting("max_connections", "100", null, "configuration file", context: "postmaster") with { PendingRestart = true },
                Setting("enable_seqscan", "on", boot: "on"))),
            ServerRead<ServerExtensionsRead>.Of(new ServerExtensionsRead([new("pg_stat_statements", true, "1.11", "1.11", false)], 1)),
            ServerRead<ServerConnectionPosture>.Of(Posture("app", reading: "probe")),
            [new HealthStatement("select 1 from pg_catalog.pg_settings", [])]);

        return ServerPageService.Build(facts, Services.Settings.StudioText.Marten, consoleRoleSet: true);
    }

    private static async Task<(PostgresStudioComponentContext Context, FakeServerPage Data)> ContextAsync(ServerPageView view)
    {
        FakeServerPage data = new() { View = view };
        PostgresStudioComponentContext context = new(services => services.AddSingleton<IServerPageService>(data));
        context.Health.WithFindings(
            FakeHealthService.Finding("S04", HealthSeverity.Info, "The application connects to Postgres as a superuser", HealthArea.Server),
            FakeHealthService.Finding("T01", HealthSeverity.Warning, "A table is read whole", HealthArea.Tables));

        return (await context.ReadyAsync(), data);
    }

    [Fact]
    public async Task It_draws_the_server_the_settings_the_extensions_and_the_security_posture()
    {
        (PostgresStudioComponentContext context, FakeServerPage data) = await ContextAsync(View());
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ServerPage>();

        page.WaitForAssertion(() => page.FindAll("section[aria-label='Settings']").Should().ContainSingle());
        page.Find(".ms-page-title").TextContent.Should().Be("Server");
        page.FindAll("section[aria-label]").Select(static x => x.GetAttribute("aria-label"))
            .Should().Equal("Server", "Settings", "Extensions", "Security posture");

        page.Find(".ms-s2-facts").TextContent.Should().Contain("PostgreSQL 17.11").And.Contain("self-hosted");
        page.Find(".ms-s2-grants").TextContent.Should().Contain("pg_read_all_stats");
        page.Find(".ms-s2-reading").TextContent.Should().Contain("probe (MartenStudioOptions.SqlConsoleRole)");

        page.FindAll(".ms-s2-settings-table").Should().NotBeEmpty();
        page.Markup.Should().Contain("4 MB").And.Contain("pending restart");
        page.FindAll(".ms-s2-value").Should().Contain(x => x.GetAttribute("title") == "Changes in postgresql.conf, followed by a reload.");

        page.FindAll(".ms-s2-chip").Select(static x => x.QuerySelector(".ms-s2-chip-name")!.TextContent)
            .Should().Contain("pg_stat_statements").And.Contain("pgstattuple").And.Contain("track_io_timing");

        // The posture is the advisor's own findings for S04-S10 and K05-K08, and nothing else.
        page.FindAll(".ms-s2-finding").Should().ContainSingle().Which.TextContent.Should().Contain("S04");
        page.Markup.Should().NotContain("A table is read whole");

        page.FindAll("table.ms-table").Should().OnlyContain(static x => x.ParentElement!.ClassList.Contains("ms-table-scroll"), "D25");
        data.Requests.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_platform_slot_is_there_and_renders_nothing()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(View());
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ServerPage>();
        page.WaitForAssertion(() => page.FindAll("section[aria-label='Security posture']").Should().ContainSingle());

        page.FindComponent<ServerPlatformSlot>().Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task A_hidden_setting_says_which_grant_shows_it()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(View());
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ServerPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-s2-setting-hidden").Should().NotBeEmpty());

        page.FindAll(".ms-s2-setting-hidden").Should().Contain(x =>
            x.TextContent.Contains("shared_preload_libraries", StringComparison.Ordinal)
            && x.TextContent.Contains("Hidden from role probe: it needs pg_read_all_settings (pg_monitor includes it).", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_on_the_page_needs_ViewDatabaseActivity()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(View());
        await using PostgresStudioComponentContext owned = context;

        context.Options.Capabilities.ViewDatabaseActivity.Should().BeFalse("every capability is off by default (D4)");

        var page = context.Render<ServerPage>();
        page.WaitForAssertion(() => page.FindAll("section[aria-label]").Should().HaveCount(4));

        page.Markup.Should().NotContain("ViewDatabaseActivity");
        page.FindAll(".ms-s2-refusal").Should().BeEmpty();
        page.FindAll(".ms-s2-unavailable").Should().BeEmpty("every section was read and shown, with no capability on");
        page.Markup.Should().NotContain("10.1.2.3", "the posture's client address is never shown");
    }

    [Fact]
    public async Task A_refusal_is_its_sentence_and_nothing_else()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(ServerPageView.Refused(ServerRefusal.StorePolicy, "Your account may not see this database's server."));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<ServerPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-s2-refusal").Should().ContainSingle());

        page.Find(".ms-s2-refusal").TextContent.Should().Contain("may not see this database's server");
        page.FindAll("section[aria-label]").Should().BeEmpty();
        context.Health.Requests.Should().BeEmpty("a refused visitor's report is not asked for");
    }
}
