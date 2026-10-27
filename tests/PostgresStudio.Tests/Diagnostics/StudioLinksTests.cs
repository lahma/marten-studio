using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Services;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>The diagnostics API's links into the studio (issue #24): a finding's card and screens, and a page's path.</summary>
public class StudioLinksTests
{
    public static TheoryData<PostgresStudioPage> Pages => [.. Enum.GetValues<PostgresStudioPage>()];

    private static readonly StudioScope Scope = new("main", "db1", null);

    private static PostgresDiagnosticsService Service() =>
        new(null!, null!, null!, null!, null!, null!, NullLogger<PostgresDiagnosticsService>.Instance, PostgresStudioDescriptor.Instance);

    [Fact]
    public void A_mapped_finding_names_its_card_on_the_health_page()
    {
        HealthCheckInfo info = Info("T03", HealthArea.Tables);
        HealthFinding finding = Finding(info, "Index is invalid", "public.orders_idx");

        PostgresFinding mapped = DiagnosticsMapper.Finding(finding, info, Gate(false), NoStudioRuntime.Instance, "store", Scope);

        mapped.StudioPath.Should().Be("health?store=main&db=db1#" + HealthReportFinding.AnchorFor(finding));
        mapped.StudioPath.Should().Contain("#finding-");
        mapped.StudioLinks.Should().BeEmpty();
    }

    [Fact]
    public void A_findings_link_becomes_a_studio_link_with_its_own_query_and_the_scope()
    {
        HealthCheckInfo info = Info("T03", HealthArea.Tables);
        HealthFinding finding = Finding(info, "Index is invalid") with
        {
            Link = new HealthLink("Open the index advisor", "statements", [new KeyValuePair<string, string?>("key", "value")]),
        };

        PostgresFinding mapped = DiagnosticsMapper.Finding(finding, info, Gate(false), NoStudioRuntime.Instance, "store", Scope);

        mapped.StudioLinks.Should().Equal(new PostgresStudioLink { Label = "Open the index advisor", Path = "statements?key=value&store=main&db=db1" });
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void Every_page_has_a_relative_path_carrying_the_scope(PostgresStudioPage page)
    {
        string path = Service().GetStudioPath(new PostgresDatabaseRef("main", "db1"), page);

        path.Should().NotBeEmpty().And.NotStartWith("/").And.Contain("store=main").And.Contain("db=db1");
    }

    /// <summary>Every member's exact path: the route its page's own link helper names, the tab's view parameter where it has one, then the scope.</summary>
    [Theory]
    [InlineData("Health", "health?store=main&db=db1")]
    [InlineData("Sessions", "database/sessions?store=main&db=db1")]
    [InlineData("Blocking", "database/sessions?view=blocking&store=main&db=db1")]
    [InlineData("Statements", "statements?store=main&db=db1")]
    [InlineData("Maintenance", "maintenance?store=main&db=db1")]
    [InlineData("Storage", "storage?store=main&db=db1")]
    [InlineData("Server", "server?store=main&db=db1")]
    [InlineData("Plan", "plan?store=main&db=db1")]
    [InlineData("Holders", "database/sessions?view=holders&store=main&db=db1")]
    [InlineData("Replication", "replication?store=main&db=db1")]
    [InlineData("Overview", "?store=main&db=db1")]
    public void Every_page_has_its_exact_path_and_the_table_names_every_member(string page, string path)
    {
        Service().GetStudioPath(new PostgresDatabaseRef("main", "db1"), Enum.Parse<PostgresStudioPage>(page)).Should().Be(path);
    }

    [Fact]
    public void The_enums_members_are_the_ones_the_exact_paths_name_with_their_numbers()
    {
        Enum.GetValues<PostgresStudioPage>().Select(static x => ((int) x, x.ToString())).Should().Equal(
            (0, "Health"), (1, "Sessions"), (2, "Blocking"), (3, "Statements"), (4, "Maintenance"), (5, "Storage"), (6, "Server"),
            (7, "Plan"), (8, "Holders"), (9, "Replication"), (10, "Overview"));
    }

    [Fact]
    public void The_overview_is_the_studios_front_page_so_its_path_is_the_query_alone_and_still_carries_the_scope()
    {
        string path = Service().GetStudioPath(new PostgresDatabaseRef("main", "db1"), PostgresStudioPage.Overview);

        path.Should().StartWith("?").And.Contain("store=main").And.Contain("db=db1");
    }

    [Fact]
    public void The_blocking_page_carries_the_view_parameter() =>
        Service().GetStudioPath(new PostgresDatabaseRef("main", "db1"), PostgresStudioPage.Blocking).Should().Contain("view=blocking");

    [Fact]
    public void A_name_with_a_slash_or_a_space_is_percent_encoded() =>
        Service().GetStudioPath(new PostgresDatabaseRef("a/b c", "d e"), PostgresStudioPage.Health)
            .Should().Be("health?store=a%2Fb%20c&db=d%20e");

    [Fact]
    public void An_unknown_page_and_a_null_database_throw()
    {
        PostgresDiagnosticsService service = Service();

        ((Action)(() => service.GetStudioPath(new PostgresDatabaseRef("a", "b"), (PostgresStudioPage)99))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => service.GetStudioPath(null!, PostgresStudioPage.Health))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_hosts_own_double_of_the_three_members_compiles_and_has_no_path()
    {
        IPostgresDiagnostics stub = new Stub();

        stub.GetStudioPath(new PostgresDatabaseRef("a", "b"), PostgresStudioPage.Health).Should().BeEmpty();
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
