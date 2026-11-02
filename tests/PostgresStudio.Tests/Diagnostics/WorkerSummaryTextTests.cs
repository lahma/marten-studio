using System.Runtime.CompilerServices;

using PostgresStudio.Worker;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The diagnostics worker sample's summary text (#42): a line per check, taken from
/// <see cref="PostgresDiagnosticsSnapshot.FindingsByCheck" />, with the group's link.
/// </summary>
public class WorkerSummaryTextTests
{
    private static PostgresFinding Finding(string code, string subject, PostgresSeverity severity, string title, bool accepted = false) => new()
    {
        Code = code,
        Subject = subject,
        Severity = severity,
        Title = title,
        StudioPath = "health?store=default&db=app#" + code + "-" + subject,
        Accepted = accepted,
    };

    [Fact]
    public async Task The_summary_has_a_line_per_check_with_its_link_and_leaves_out_the_accepted()
    {
        PostgresDiagnosticsSnapshot snapshot = new()
        {
            Database = new PostgresDatabaseRef("default", "app"),
            Checks = [new PostgresCheck { Code = "T03", Title = "Unused indexes" }, new PostgresCheck { Code = "T07", Title = "Vacuum falling behind" }],
            Findings =
            [
                Finding("T03", "public.a", PostgresSeverity.Warning, "Index public.a is unused"),
                Finding("T03", "public.b", PostgresSeverity.Warning, "Index public.b is unused"),
                Finding("T07", "public.orders", PostgresSeverity.Critical, "Vacuum is behind on public.orders"),
                Finding("T09", "public.old", PostgresSeverity.Info, "Accepted thing", accepted: true),
            ],
        };

        string text = await DiagnosticsSummaryService.BuildAsync(new Source(snapshot), "https://studio.example.com/postgres/", TestContext.Current.CancellationToken);

        text.ReplaceLineEndings("\n").Should().Be(
            "Postgres diagnostics summary\n"
            + "\n"
            + "default / app: CRITICAL (1 critical, 2 warning, 0 info)\n"
            + "  Critical  Vacuum is behind on public.orders: public.orders - https://studio.example.com/postgres/health?store=default&db=app#T07-public.orders\n"
            + "  Warning  Unused indexes (2) - https://studio.example.com/postgres/health?store=default&db=app\n");
    }

    [Fact]
    public async Task Without_a_studio_base_no_link_is_printed()
    {
        PostgresDiagnosticsSnapshot snapshot = new()
        {
            Database = new PostgresDatabaseRef("default", "app"),
            Findings = [Finding("T03", "public.a", PostgresSeverity.Warning, "Index public.a is unused")],
        };

        string text = await DiagnosticsSummaryService.BuildAsync(new Source(snapshot), null, TestContext.Current.CancellationToken);

        text.Should().Contain("  Warning  Index public.a is unused: public.a").And.NotContain(" - ");
    }

    private sealed class Source(PostgresDiagnosticsSnapshot snapshot) : IPostgresDiagnostics
    {
        public IReadOnlyList<PostgresCheck> Checks => [];

        public async IAsyncEnumerable<PostgresDatabaseRef> ListDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return snapshot.Database;
            await Task.CompletedTask;
        }

        public Task<PostgresDiagnosticsSnapshot> GetSnapshotAsync(
            PostgresDatabaseRef database,
            PostgresDiagnosticsRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
