using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Diagnostics;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Settings;

using static DataStudio.Postgres.Tests.Diagnostics.DiagnosticsTestFacts;

namespace DataStudio.Postgres.Tests.Diagnostics;

/// <summary>The diagnostics API's links into the studio (issue #24): a finding's card and screens, and a page's path.</summary>
public class StudioLinksTests
{
    public static TheoryData<PostgresPage> Pages => [.. Enum.GetValues<PostgresPage>()];

    private static readonly StudioScope Scope = new("main", "db1", null);

    private static PostgresDiagnosticsService Service() => Service(PostgresSettings.Of(Microsoft.Extensions.Options.Options.Create(new PostgresOptions())));

    /// <summary>The service over a container that holds only <paramref name="settings" />, which is all a page's path asks of it.</summary>
    private static PostgresDiagnosticsService Service(IDiagnosticsSettings settings) =>
        new(
            new ServiceCollection().AddSingleton(settings).BuildServiceProvider(),
            null!, null!, null!, null!, null!, NullLogger<PostgresDiagnosticsService>.Instance, PostgresDescriptor.Instance);

    [Fact]
    public void A_mapped_finding_names_its_card_on_the_health_page()
    {
        HealthCheckInfo info = Info("T03", HealthArea.Tables);
        HealthFinding finding = Finding(info, "Index is invalid", "public.orders_idx");

        PostgresFinding mapped = DiagnosticsMapper.Finding(finding, info, Gate(false), NoStudioRuntime.Instance, "store", Scope);

        mapped.StudioPath.Should().Be("postgres/health?store=main&db=db1#" + HealthReportFinding.AnchorFor(finding));
        mapped.StudioPath.Should().Contain("#finding-");
        mapped.StudioLinks.Should().BeEmpty();
    }

    [Fact]
    public void A_findings_link_becomes_a_studio_link_with_its_own_query_and_the_scope()
    {
        HealthCheckInfo info = Info("T03", HealthArea.Tables);
        HealthFinding finding = Finding(info, "Index is invalid") with
        {
            Link = new HealthLink("Open the index advisor", "postgres/statements", [new KeyValuePair<string, string?>("key", "value")]),
        };

        PostgresFinding mapped = DiagnosticsMapper.Finding(finding, info, Gate(false), NoStudioRuntime.Instance, "store", Scope);

        mapped.StudioLinks.Should().Equal(new PostgresPageLink { Label = "Open the index advisor", Path = "postgres/statements?key=value&store=main&db=db1" });
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Every_page_has_a_relative_path_carrying_the_scope(PostgresPage page)
    {
        string path = Service().GetStudioPath(new PostgresDatabaseRef("main", "db1"), page);

        path.Should().NotBeEmpty().And.NotStartWith("/").And.Contain("store=main").And.Contain("db=db1");
    }

    /// <summary>Every member's exact path: the route its page's own link helper names, the tab's view parameter where it has one, then the scope.</summary>
    [Theory]
    [InlineData("Health", "postgres/health?store=main&db=db1")]
    [InlineData("Sessions", "postgres/database/sessions?store=main&db=db1")]
    [InlineData("Blocking", "postgres/database/sessions?view=blocking&store=main&db=db1")]
    [InlineData("Statements", "postgres/statements?store=main&db=db1")]
    [InlineData("Maintenance", "postgres/maintenance?store=main&db=db1")]
    [InlineData("Storage", "postgres/storage?store=main&db=db1")]
    [InlineData("Server", "postgres/server?store=main&db=db1")]
    [InlineData("Plan", "postgres/plan?store=main&db=db1")]
    [InlineData("Holders", "postgres/database/sessions?view=holders&store=main&db=db1")]
    [InlineData("Replication", "postgres/replication?store=main&db=db1")]
    [InlineData("Overview", "postgres?store=main&db=db1")]
    [InlineData("Connection", "postgres/database/connection?store=main&db=db1")]
    public void Every_page_has_its_exact_path_and_the_table_names_every_member(string page, string path)
    {
        Service().GetStudioPath(new PostgresDatabaseRef("main", "db1"), Enum.Parse<PostgresPage>(page)).Should().Be(path);
    }

    [Fact]
    public void The_enums_members_are_the_ones_the_exact_paths_name_with_their_numbers()
    {
        Enum.GetValues<PostgresPage>().Select(static x => ((int) x, x.ToString())).Should().Equal(
            (0, "Health"), (1, "Sessions"), (2, "Blocking"), (3, "Statements"), (4, "Maintenance"), (5, "Storage"), (6, "Server"),
            (7, "Plan"), (8, "Holders"), (9, "Replication"), (10, "Overview"), (11, "Connection"));
    }

    [Fact]
    public void The_overview_is_the_postgres_modules_home_so_its_path_is_the_segment_and_still_carries_the_scope()
    {
        string path = Service().GetStudioPath(new PostgresDatabaseRef("main", "db1"), PostgresPage.Overview);

        path.Should().StartWith("postgres?").And.Contain("store=main").And.Contain("db=db1");
    }

    /// <summary>
    /// Issue #84: the Overview is the home of the module that owns the database, which the settings name - so a module whose home
    /// is another address is handed out as that address, and every other page keeps its own path.
    /// </summary>
    [Fact]
    public void The_overview_is_the_home_the_settings_name_and_no_other_page_moves()
    {
        PostgresDiagnosticsService service = Service(new OtherHome());
        PostgresDatabaseRef database = new("main", "db1");

        service.GetStudioPath(database, PostgresPage.Overview).Should().Be("elsewhere?store=main&db=db1");
        service.GetStudioPath(database, PostgresPage.Health).Should().Be("postgres/health?store=main&db=db1");
        service.GetStudioPath(database, PostgresPage.Connection).Should().Be("postgres/database/connection?store=main&db=db1");
    }

    private sealed class OtherHome : IDiagnosticsSettings
    {
        private readonly PostgresSettings inner =PostgresSettings.Of(Microsoft.Extensions.Options.Options.Create(new PostgresOptions()));

        public string DatabaseHomeRoute => "elsewhere";

        public string OptionsTypeName => inner.OptionsTypeName;

        public StudioText Text => inner.Text;

        public TimeSpan QueryTimeout => inner.QueryTimeout;

        public TimeSpan EffectiveHealthInterval => inner.EffectiveHealthInterval;

        public string? SqlConsoleRole => inner.SqlConsoleRole;

        public IReadOnlyList<string> BrowsableSchemas => inner.BrowsableSchemas;

        public Func<Type, bool>? IsDocumentTypeVisible => inner.IsDocumentTypeVisible;
    }

    [Fact]
    public void The_connection_pages_path_is_the_route_both_studios_mount_and_carries_the_scope()
    {
        string path = Service().GetStudioPath(new PostgresDatabaseRef("main", "db1"), PostgresPage.Connection);

        // Issue #55: the one spelling the page's own link (ConnectionLinks) and the diagnostics API share, so a host's link and a
        // visitor's cannot drift; both studios mount it (the Marten module's navigation names the same route).
        path.Should().StartWith(DataStudio.Postgres.Services.Connection.ConnectionLinks.Route + "?").And.Contain("store=main").And.Contain("db=db1");
        PostgresDescriptor.Instance.Navigation(PostgresSettings.Of(Microsoft.Extensions.Options.Options.Create(new PostgresOptions()))).SelectMany(static x => x.Items).Should().Contain(x => x.Route == "postgres/database/connection");
    }

    [Fact]
    public void The_blocking_page_carries_the_view_parameter() =>
        Service().GetStudioPath(new PostgresDatabaseRef("main", "db1"), PostgresPage.Blocking).Should().Contain("view=blocking");

    [Fact]
    public void A_name_with_a_slash_or_a_space_is_percent_encoded() =>
        Service().GetStudioPath(new PostgresDatabaseRef("a/b c", "d e"), PostgresPage.Health)
            .Should().Be("postgres/health?store=a%2Fb%20c&db=d%20e");

    [Fact]
    public void An_unknown_page_and_a_null_database_throw()
    {
        PostgresDiagnosticsService service = Service();

        ((Action)(() => service.GetStudioPath(new PostgresDatabaseRef("a", "b"), (PostgresPage)99))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => service.GetStudioPath(null!, PostgresPage.Health))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_hosts_own_double_of_the_three_members_compiles_and_has_no_path()
    {
        IPostgresDiagnostics stub = new Stub();

        stub.GetStudioPath(new PostgresDatabaseRef("a", "b"), PostgresPage.Health).Should().BeEmpty();
    }

    private sealed class Stub : IPostgresDiagnostics
    {
        public IReadOnlyList<PostgresCheck> Checks => [];

        public async IAsyncEnumerable<PostgresDatabaseRef> ListDatabasesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<PostgresDiagnosticsSnapshot> GetSnapshotAsync(PostgresDatabaseRef database, PostgresDiagnosticsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PostgresDiagnosticsSnapshot());
    }
}
