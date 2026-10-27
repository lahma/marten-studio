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
