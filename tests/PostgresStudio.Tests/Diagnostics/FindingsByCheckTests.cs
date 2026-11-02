using System.Runtime.CompilerServices;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The diagnostics API's line per check (#42): <see cref="PostgresDiagnosticsSnapshot.FindingsByCheck" /> over hand-built
/// snapshots, and the single-database <see cref="IPostgresDiagnostics.GetSnapshotAsync(PostgresDiagnosticsRequest, CancellationToken)" />.
/// </summary>
public class FindingsByCheckTests
{
    private const string HealthPath = "health?store=default&db=app";

    private static PostgresFinding Finding(
        string code,
        string subject,
        PostgresSeverity severity = PostgresSeverity.Warning,
        bool accepted = false,
        string? title = null) => new()
    {
        Code = code,
        Subject = subject,
        Severity = severity,
        Category = PostgresFindingCategory.Tables,
        Title = title ?? code + " on " + subject,
        Detail = "Why " + code + " on " + subject,
        StudioPath = HealthPath + "#" + code + "-" + subject,
        Accepted = accepted,
    };

    private static PostgresCheck Check(string code, string title) => new() { Code = code, Title = title };

    private static PostgresDiagnosticsSnapshot Snapshot(
        IEnumerable<PostgresFinding> findings,
        params PostgresCheck[] checks) => new()
    {
        SchemaVersion = 1,
        Producer = "PostgresStudio test",
        GeneratedAt = At,
        Database = new PostgresDatabaseRef("default", "app"),
        Findings = [.. findings],
        Checks = checks,
    };

    [Fact]
    public void One_finding_is_a_group_that_carries_its_own_words_and_card()
    {
        PostgresFinding finding = Finding("T03", "public.orders_idx", title: "Index public.orders_idx is unused");

        PostgresFindingGroup group = Snapshot([finding], Check("T03", "Unused indexes")).FindingsByCheck().Should().ContainSingle().Subject;

        group.Count.Should().Be(1);
        group.Code.Should().Be("T03");
        group.Category.Should().Be(PostgresFindingCategory.Tables);
        group.Severity.Should().Be(PostgresSeverity.Warning);
        group.Title.Should().Be("Index public.orders_idx is unused");
        group.Subject.Should().Be("public.orders_idx");
        group.Detail.Should().Be(finding.Detail);
        group.StudioPath.Should().Be(finding.StudioPath).And.EndWith("#T03-public.orders_idx");
        group.Findings.Should().Equal(finding);
    }

    [Fact]
    public void Several_findings_are_one_group_with_the_checks_title_and_the_Health_page()
    {
        PostgresFinding[] findings = [Finding("T03", "public.a"), Finding("T03", "public.b"), Finding("T03", "public.c")];

        PostgresFindingGroup group = Snapshot(findings, Check("T03", "Unused indexes")).FindingsByCheck().Should().ContainSingle().Subject;

        group.Count.Should().Be(3);
        group.Title.Should().Be("Unused indexes");
        group.Subject.Should().BeNull();
        group.Detail.Should().BeNull();
        group.StudioPath.Should().Be(HealthPath, "the database's Health page, with no finding's card");
        group.Findings.Should().Equal(findings, "in the snapshot's order");
    }

    [Fact]
    public void A_group_of_several_with_a_code_the_checks_do_not_list_is_titled_by_its_first_finding()
    {
        PostgresFindingGroup group = Snapshot([Finding("X99", "one", title: "First"), Finding("X99", "two", title: "Second")])
            .FindingsByCheck().Should().ContainSingle().Subject;

        group.Title.Should().Be("First");
    }

    [Fact]
    public void A_groups_severity_is_the_worst_among_its_findings()
    {
        PostgresFinding[] findings =
        [
            Finding("T03", "public.a", PostgresSeverity.Info),
            Finding("T03", "public.b", PostgresSeverity.Critical),
            Finding("T03", "public.c", PostgresSeverity.Warning),
        ];

        Snapshot(findings, Check("T03", "Unused indexes")).FindingsByCheck().Single().Severity.Should().Be(PostgresSeverity.Critical);
    }

    [Fact]
    public void Groups_are_ordered_by_severity_then_check_order_and_a_code_not_among_the_checks_comes_last_by_code()
    {
        PostgresFinding[] findings =
        [
            Finding("Z98", "z", PostgresSeverity.Warning),
            Finding("C01", "c", PostgresSeverity.Warning),
            Finding("T03", "t", PostgresSeverity.Info),
            Finding("Y97", "y", PostgresSeverity.Warning),
            Finding("T07", "t", PostgresSeverity.Warning),
            Finding("S05", "s", PostgresSeverity.Critical),
        ];

        // The checks' order is T07, C01, S05, T03: not alphabetical, so the order is the checks' and not the codes'.
        PostgresDiagnosticsSnapshot snapshot = Snapshot(
            findings,
            Check("T07", "t7"), Check("C01", "c1"), Check("S05", "s5"), Check("T03", "t3"));

        snapshot.FindingsByCheck().Select(static x => x.Code).Should().Equal("S05", "T07", "C01", "Y97", "Z98", "T03");
    }

    [Fact]
    public void Accepted_findings_are_left_out_and_a_group_of_only_accepted_ones_is_absent()
    {
        PostgresFinding[] findings =
        [
            Finding("T03", "public.a", accepted: true),
            Finding("T03", "public.b"),
            Finding("T07", "public.c", accepted: true),
        ];

        IReadOnlyList<PostgresFindingGroup> groups = Snapshot(findings, Check("T03", "Unused indexes"), Check("T07", "Vacuum")).FindingsByCheck();

        PostgresFindingGroup group = groups.Should().ContainSingle().Subject;
        group.Code.Should().Be("T03");
        group.Count.Should().Be(1, "the accepted one is not counted, so this is the one-finding form");
        group.Subject.Should().Be("public.b");
        group.Findings.Should().OnlyContain(static x => !x.Accepted);
    }

    [Fact]
    public void An_empty_snapshot_has_no_groups()
    {
        Snapshot([]).FindingsByCheck().Should().BeEmpty();
    }

    [Theory]
    [InlineData(PostgresReadOutcome.NotFound)]
    [InlineData(PostgresReadOutcome.Failed)]
    public void A_snapshot_that_was_not_read_has_no_groups(PostgresReadOutcome outcome)
    {
        (Snapshot([Finding("T03", "public.a")]) with { Outcome = outcome, Reason = "gone" }).FindingsByCheck().Should().BeEmpty();
    }

    [Fact]
    public void Asking_for_the_groups_does_not_change_the_snapshots_json()
    {
        PostgresDiagnosticsSnapshot snapshot = Snapshot(
            [Finding("T03", "public.a"), Finding("T03", "public.b", accepted: true), Finding("T07", "public.c", PostgresSeverity.Critical)],
            Check("T03", "Unused indexes"),
            Check("T07", "Vacuum"));

        string before = snapshot.ToJson();
        _ = snapshot.FindingsByCheck();
        string after = snapshot.ToJson();

        after.Should().Be(before);
        after.Should().NotContain("FindingsByCheck", "the groups are a method, never a key").And.NotContain("\"groups\"");
    }

    [Fact]
    public async Task One_listed_database_is_the_one_whose_snapshot_comes_back()
    {
        FakeDiagnostics diagnostics = new(new PostgresDatabaseRef("default", "app"));
        IPostgresDiagnostics api = diagnostics;

        PostgresDiagnosticsSnapshot snapshot = await api.GetSnapshotAsync(PostgresDiagnosticsRequest.FindingsOnly, TestContext.Current.CancellationToken);

        snapshot.Database.Should().Be(new PostgresDatabaseRef("default", "app"));
        diagnostics.Requested.Should().Equal(new PostgresDatabaseRef("default", "app"));
    }

    [Fact]
    public async Task No_listed_database_throws_and_says_so()
    {
        IPostgresDiagnostics api = new FakeDiagnostics();

        Func<Task> act = () => api.GetSnapshotAsync(PostgresDiagnosticsRequest.FindingsOnly, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("0 databases").And.Contain("ListDatabasesAsync").And.Contain("GetSnapshotAsync(database, request");
    }

    [Fact]
    public async Task More_than_one_listed_database_throws_having_read_no_further_than_two()
    {
        FakeDiagnostics diagnostics = new(
            new PostgresDatabaseRef("default", "a"), new PostgresDatabaseRef("default", "b"), new PostgresDatabaseRef("default", "c"));
        IPostgresDiagnostics api = diagnostics;

        Func<Task> act = () => api.GetSnapshotAsync(PostgresDiagnosticsRequest.FindingsOnly, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("2 or more databases").And.Contain("ListDatabasesAsync").And.Contain("GetSnapshotAsync(database, request");
        diagnostics.Listed.Should().Be(2, "two are enough to know it is not one");
        diagnostics.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task A_null_request_is_an_argument_error()
    {
        IPostgresDiagnostics api = new FakeDiagnostics(new PostgresDatabaseRef("default", "app"));

        Func<Task> act = () => api.GetSnapshotAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task The_two_and_three_argument_forms_still_bind_to_the_database_overload()
    {
        FakeDiagnostics diagnostics = new(new PostgresDatabaseRef("default", "a"), new PostgresDatabaseRef("default", "b"));
        IPostgresDiagnostics api = diagnostics;
        PostgresDatabaseRef database = new("default", "b");

        PostgresDiagnosticsSnapshot two = await api.GetSnapshotAsync(database, PostgresDiagnosticsRequest.FindingsOnly, TestContext.Current.CancellationToken);
        PostgresDiagnosticsSnapshot three = await api.GetSnapshotAsync(database, PostgresDiagnosticsRequest.FindingsOnly, CancellationToken.None);

        two.Database.Should().Be(database);
        three.Database.Should().Be(database);
    }

    /// <summary>
    /// A host's own test double: it implements only the members without a default, so the one-argument snapshot is the
    /// interface's own default member.
    /// </summary>
    private sealed class FakeDiagnostics(params PostgresDatabaseRef[] databases) : IPostgresDiagnostics
    {
        public List<PostgresDatabaseRef> Requested { get; } = [];

        public int Listed { get; private set; }

        public IReadOnlyList<PostgresCheck> Checks => [];

        public async IAsyncEnumerable<PostgresDatabaseRef> ListDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (PostgresDatabaseRef database in databases)
            {
                Listed++;
                yield return database;
                await Task.CompletedTask;
            }
        }

        public Task<PostgresDiagnosticsSnapshot> GetSnapshotAsync(
            PostgresDatabaseRef database,
            PostgresDiagnosticsRequest request,
            CancellationToken cancellationToken = default)
        {
            Requested.Add(database);
            return Task.FromResult(new PostgresDiagnosticsSnapshot { Database = database });
        }
    }
}
