using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.EventHealthFacts;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// E09's judgement over stated probe rows: when it is quiet, what it counts and names, and the one rule it copies from E03 -
/// a stream id on a conjoined store is shown only through its tenant.
/// </summary>
public class EmptyCompactedSnapshotCheckTests
{
    private const string Stream1 = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    private const string Stream2 = "11111111-1111-1111-1111-111111111111";

    private static EmptySnapshotRow Summary(long candidates, long damaged) =>
        new(HealthEventQueries.SummaryRow, null, null, candidates, damaged);

    private static EmptySnapshotRow Damaged(string stream, long version, long inTenant, string? tenant = null) =>
        new(HealthEventQueries.StreamRow, stream, tenant, version, inTenant);

    private static HealthVerdict Evaluate(IReadOnlyList<EmptySnapshotRow> rows, bool conjoined = false) =>
        new EmptyCompactedSnapshotCheck().Evaluate(EmptyCompactedSnapshotCheck.From(rows, conjoined), At());

    [Fact]
    public void A_store_with_no_compacted_stream_is_quiet()
    {
        HealthVerdict verdict = Evaluate([Summary(0, 0)]);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("No stream has a compaction watermark").And.Contain("since 9.32")
            .And.NotContain("has been compacted", "a stream compacted before Marten recorded a watermark is not seen, so the note does not claim none was");
    }

    [Fact]
    public void Compacted_streams_that_hold_a_snapshot_are_quiet_and_the_note_says_how_many_were_looked_at()
    {
        HealthVerdict verdict = Evaluate([Summary(1_234, 0)]);

        verdict.Findings.Should().BeEmpty("a healthy compacted stream is not a finding");
        verdict.Note.Should().Contain("1,234 compacted streams").And.Contain("holds a snapshot");
    }

    [Fact]
    public void One_damaged_stream_is_one_warning_that_names_it_at_the_checks_own_gate()
    {
        HealthFinding finding = Evaluate([Summary(40, 1), Damaged(Stream1, 12, 1)])
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("E09");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("A stream was compacted into an empty snapshot").And.NotContain(Stream1, "a title names nothing a gate would hide");
        finding.Subject.Should().BeNull();
        finding.TenantId.Should().BeNull("a store that is not tenanted per event has no tenant to name");
        finding.Evidence.Should().Contain(new HealthEvidence("Streams compacted into an empty snapshot", "1"));
        finding.Evidence.Should().Contain(new HealthEvidence("Compacted streams probed", "40"));
        finding.Evidence.Should().Contain(new HealthEvidence("Stream", Stream1 + " (version 12)"));
        finding.Evidence.Should().OnlyContain(static x => x.Gate == HealthGate.None, "the ids are shown at the finding's own gate");
        finding.Remedy.Should().BeNull("there is no StoreOptions line that brings a deleted history back");
        finding.Key.Should().Be("E09");
    }

    [Fact]
    public void The_remedy_is_to_restore_and_says_that_marten_cannot_rebuild_them()
    {
        HealthFinding finding = Evaluate([Summary(5, 1), Damaged(Stream1, 3, 1)]).Findings.Single();

        finding.WhyItMatters.Should().Contain("Restore those streams from a backup")
            .And.Contain("archiver")
            .And.Contain("cannot be got back from Marten")
            .And.Contain("9.57");
    }

    [Fact]
    public void More_damaged_streams_than_are_named_are_counted_and_the_cap_is_said()
    {
        List<EmptySnapshotRow> rows = [Summary(10_001, 8)];
        rows.AddRange(Enumerable.Range(1, 5).Select(static i => Damaged("s" + i, i, 8)));

        HealthFinding finding = Evaluate(rows).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Be("8 streams were compacted into an empty snapshot");
        finding.Evidence.Should().Contain(new HealthEvidence("Compacted streams probed", "10,000 - there are more, and they were not probed"));
        finding.Evidence.Single(static x => x.Label.StartsWith("Streams (up to 5)", StringComparison.Ordinal))
            .Value.Should().EndWith(" and 3 more").And.Contain("s1 (version 1)").And.Contain("s5 (version 5)");
        finding.Caveat.Should().Contain("at most 10,000");
    }

    [Fact]
    public void On_a_conjoined_store_each_tenant_with_damage_is_a_finding_of_its_own_that_names_the_tenant()
    {
        HealthVerdict verdict = Evaluate(
            [
                Summary(50, 3),
                Damaged(Stream1, 12, 2, "acme"),
                Damaged(Stream2, 4, 2, "acme"),
                Damaged(Stream1, 9, 1, "globex"),
            ],
            conjoined: true);

        IReadOnlyList<HealthFinding> findings = verdict.Findings;
        findings.Should().HaveCount(2);
        findings.Select(static x => x.Key).Should().Equal("E09|acme", "E09|globex");

        HealthFinding acme = findings[0];
        acme.TenantId.Should().Be("acme", "so only a visitor through the activity gate who may see acme is shown it");
        acme.Subject.Should().Be("acme");
        acme.Title.Should().Be("2 streams were compacted into an empty snapshot").And.NotContain("acme");
        acme.Evidence.Should().Contain(new HealthEvidence("Tenant", "acme"));
        acme.Evidence.Should().Contain(new HealthEvidence("Streams compacted into an empty snapshot", "2 in this tenant"));
        acme.Evidence.Single(static x => x.Label.StartsWith("Streams", StringComparison.Ordinal) && x.Value.Contains(Stream1, StringComparison.Ordinal))
            .Value.Should().Contain(Stream1 + " (version 12)").And.Contain(Stream2 + " (version 4)");

        HealthFinding globex = findings[1];
        globex.TenantId.Should().Be("globex");
        globex.Title.Should().Be("A stream was compacted into an empty snapshot");
        string others = globex.Title + globex.WhyItMatters + globex.Caveat + string.Join("|", globex.Evidence.Select(static x => x.Label + x.Value));
        others.Should().NotContain("acme", "one tenant's finding names nothing of another's").And.NotContain(Stream2);
    }

    [Fact]
    public void A_conjoined_store_with_more_damaged_tenants_than_it_lists_says_the_tenant_cap_was_hit()
    {
        List<EmptySnapshotRow> rows = [Summary(500, 14)];
        rows.AddRange(Enumerable.Range(0, HealthEventQueries.MaxEmptySnapshotTenants)
            .Select(static i => Damaged("s" + i, 3, 1, "tenant" + i)));

        IReadOnlyList<HealthFinding> findings = Evaluate(rows, conjoined: true).Findings;

        findings.Should().HaveCount(HealthEventQueries.MaxEmptySnapshotTenants);
        findings.Should().OnlyContain(static x => x.Caveat!.Contains("tenant cap was hit", StringComparison.Ordinal));

        rows[0] = Summary(500, HealthEventQueries.MaxEmptySnapshotTenants);
        Evaluate(rows, conjoined: true).Findings.Should().OnlyContain(
            static x => !x.Caveat!.Contains("tenant cap", StringComparison.Ordinal), "every damaged tenant is listed");
    }

    [Fact]
    public void The_caveat_says_streams_compacted_before_Marten_9_32_are_not_seen()
    {
        HealthFinding finding = Evaluate([Summary(40, 1), Damaged(Stream1, 12, 1)]).Findings.Should().ContainSingle().Subject;

        finding.Caveat.Should().Contain("9.32").And.Contain("not seen");
    }

    [Fact]
    public void A_conjoined_store_whose_damage_names_no_tenant_is_one_finding_that_names_no_stream()
    {
        HealthVerdict verdict = Evaluate([Summary(5, 2)], conjoined: true);

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;

        finding.TenantId.Should().BeNull();
        finding.Title.Should().Be("2 streams were compacted into an empty snapshot");
        finding.Evidence.Select(static x => x.Label).Should().NotContain("Stream").And.NotContain(static x => x.StartsWith("Streams (", StringComparison.Ordinal));
    }

    [Fact]
    public void Folding_the_rows_caps_the_probed_count_and_flags_that_there_were_more()
    {
        EmptySnapshotFacts facts = EmptyCompactedSnapshotCheck.From([Summary(HealthEventQueries.MaxCompactedProbes + 1, 0)], conjoined: false);

        facts.Probed.Should().Be(HealthEventQueries.MaxCompactedProbes);
        facts.Capped.Should().BeTrue();

        EmptyCompactedSnapshotCheck.From([Summary(HealthEventQueries.MaxCompactedProbes, 0)], conjoined: false).Capped.Should().BeFalse();
    }
}
