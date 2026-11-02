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

    [Fact]
    public async Task The_summary_prints_the_connection_historys_peak_line_when_the_snapshot_has_one()
    {
        PostgresDiagnosticsSnapshot snapshot = new()
        {
            Database = new PostgresDatabaseRef("default", "app"),
            ConnectionHistory = new PostgresConnectionHistorySection
            {
                Outcome = PostgresReadOutcome.Read,
                Server = "db:5432",
                Samples = 1_440,
                Peak = 83,
                PeakAt = new DateTimeOffset(2026, 11, 2, 3, 0, 0, TimeSpan.Zero),
                ClientLimit = 100,
                ClientLimitNow = 100,
                Since = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero),
                InstanceChanges = 1,
            },
        };

        string text = await DiagnosticsSummaryService.BuildAsync(new Source(snapshot), null, TestContext.Current.CancellationToken);

        text.ReplaceLineEndings("\n").Should().Be(
            "Postgres diagnostics summary\n"
            + "\n"
            + "default / app: OK (0 critical, 0 warning, 0 info)\n"
            + "  Connection history: peak 83 of 100 client connections (83.0%) at 2026-11-02 03:00:00Z, 1,440 samples since 2026-11-01 06:00:00Z, 1 instance changes\n");
    }

    [Fact]
    public async Task The_summary_says_nothing_of_a_history_that_is_not_kept_and_why_of_one_that_has_no_sample_yet()
    {
        PostgresDiagnosticsSnapshot notKept = new()
        {
            Database = new PostgresDatabaseRef("default", "app"),
            ConnectionHistory = new PostgresConnectionHistorySection { Outcome = PostgresReadOutcome.NotApplicable, Reason = "No connection history is kept" },
        };
        PostgresDiagnosticsSnapshot unavailable = notKept with
        {
            ConnectionHistory = new PostgresConnectionHistorySection { Outcome = PostgresReadOutcome.Unavailable, Reason = "no sample yet" },
        };

        (await DiagnosticsSummaryService.BuildAsync(new Source(notKept), null, TestContext.Current.CancellationToken)).Should().NotContain("Connection history");
        (await DiagnosticsSummaryService.BuildAsync(new Source(unavailable), null, TestContext.Current.CancellationToken)).Should()
            .Contain("  Connection history: Unavailable - no sample yet");
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
