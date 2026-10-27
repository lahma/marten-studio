using JasperFx.Events.Projections;

using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using PostgresStudio.Services.Health;

using static MartenStudio.Tests.Health.HighWaterPinnedCheckTests;

namespace MartenStudio.Tests.Health;

/// <summary>
/// D10's judgement over stated facts: one Info finding per projection a row names, a tenant's row counted and never
/// named, a row the daemon still reads said so, and a projection the store still registers carrying its document types.
/// </summary>
public class OrphanedProgressionCheckTests
{
    private static readonly ProgressionRowClassifier Classifier = new(
        [new RegisteredShard("Trips:V2:All", "Trips", GatesSideEffectsBehindPriorVersion: true), new RegisteredShard("Orders:All", "Orders")],
        [new RegisteredSource("Trips", ProjectionLifecycle.Async, 2), new RegisteredSource("Orders", ProjectionLifecycle.Async, 1)],
        new Dictionary<string, string>(),
        tenantPartitionedEvents: true);

    private static OrphanedRowFact Row(string name, long sequence = 40) => new(name, sequence, Now, Classifier.Classify(name));

    private static HealthVerdict Evaluate(DaemonCheckConfiguration? configuration = null, bool truncated = false, params OrphanedRowFact[] rows) =>
        new OrphanedProgressionCheck().Evaluate(
            new OrphanedProgressionFacts(configuration ?? DaemonConfiguration(), rows, truncated),
            HealthEvaluation.At(Now));

    [Fact]
    public void A_store_whose_every_row_is_registered_is_quiet_and_says_what_it_compared()
    {
        HealthVerdict verdict = Evaluate();

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("a tenant's row of one under per-tenant partitioning");
    }

    /// <summary>
    /// A rename leaves one row per shard and tenant: one finding for the projection, its store-global rows named, its
    /// tenants' rows counted and never named - a finding that named a tenant would need the activity gate (D29).
    /// </summary>
    [Fact]
    public void A_renamed_projection_is_one_Info_finding_with_its_tenants_rows_counted_and_not_named()
    {
        HealthVerdict verdict = Evaluate(rows: [Row("TripsBeforeRename:All", 12), Row("TripsBeforeRename:All:acme"), Row("TripsBeforeRename:All:globex")]);

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("D10");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Subject.Should().Be("TripsBeforeRename");
        finding.Title.Should().Be("3 progression rows of TripsBeforeRename that nothing in this store advances");
        finding.WhyItMatters.Should().StartWith("No projection or subscription named TripsBeforeRename is registered: it was renamed or removed.");
        finding.Evidence.Should().Contain(new HealthEvidence("Rows", "3, 2 of them tenants' rows, which are counted and not named"));
        finding.Evidence.Should().Contain(new HealthEvidence("Rows by name", "TripsBeforeRename:All at 12"));
        string.Join(" ", finding.Evidence.Select(static x => x.Value)).Should().NotContain("acme").And.NotContain("globex");
        finding.Title.Should().NotContain("acme");
        finding.Link!.Route.Should().Be("projections");
        finding.Link.Label.Should().Be("Open the projections screen");
        finding.Caveat.Should().StartWith("Marten Studio never removes one: whether to is the host's decision.");
        finding.DocumentTypes.Should().BeEmpty("a projection no longer registered has no published types to read");

        // The remedy is Marten's own removal by name, for the host's code: the store-global row by name, the tenants' in words.
        HealthRemedy remedy = finding.Remedy!;
        remedy.Kind.Should().Be(HealthRemedyKind.Configuration);
        remedy.Code.Should().Be(
            "// Marten's own removal, by each row's exact name, against this database's IMartenDatabase:\n"
            + "await ((IEventDatabase) database).DeleteProjectionProgressByShardNameAsync(\"TripsBeforeRename:All\");\n"
            + "// and the same for each of the 2 tenants' rows, Name:ShardKey:tenant, listed on the projections screen.");
        remedy.Code.Should().NotContain("acme").And.NotContain("globex");
        remedy.Explanation.Should().Contain("DeleteProjectionProgressAsync, which takes a projection's name, refuses one that is no longer registered");
    }

    /// <summary>A row name that reads like a statement the remedy rules refuse costs the finding its lines, never the check.</summary>
    [Fact]
    public void A_row_name_the_remedy_rules_refuse_leaves_the_finding_without_lines()
    {
        HealthFinding finding = Evaluate(rows: [Row("Gone\"; drop table x;:All")]).Findings.Should().ContainSingle().Subject;

        finding.Remedy.Should().BeNull();
    }

    [Fact]
    public void Each_projection_is_its_own_finding_and_a_version_is_part_of_its_subject()
    {
        HealthVerdict verdict = Evaluate(rows: [Row("Gone:All"), Row("Trips:All"), Row("Trips:V3:All")]);

        verdict.Findings.Select(static x => x.Subject).Should().Equal("Gone", "Trips", "Trips:V3");
        verdict.Findings.Should().OnlyContain(static x => x.Severity == HealthSeverity.Info);
    }

    /// <summary>A prior version's row of a projection that gates its side effects behind it is still read, and the finding says so.</summary>
    [Fact]
    public void A_prior_versions_row_the_daemon_still_reads_is_said_so()
    {
        HealthFinding finding = Evaluate(rows: [Row("Trips:All")]).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Be("A progression row of Trips that nothing in this store advances");
        finding.WhyItMatters.Should().Contain("Trips is registered at version 2, and this row is version 1's")
            .And.Contain("The daemon still reads it");
        finding.Remedy.Should().BeNull("the only row is one the daemon reads, and nothing offers to remove it");
    }

    /// <summary>A projection the store still registers carries its published types, so one of a hidden type is never named.</summary>
    [Fact]
    public void A_finding_about_a_registered_projection_carries_its_document_types()
    {
        DaemonCheckConfiguration configuration = DaemonConfiguration() with
        {
            PublishedTypes = new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase) { ["Trips"] = [typeof(Uri)] },
        };

        Evaluate(configuration, rows: [Row("Trips:All")]).Findings.Should().ContainSingle().Which.DocumentTypes.Should().Equal(typeof(Uri));
    }

    [Fact]
    public void A_read_that_stopped_at_its_limit_says_there_may_be_more()
    {
        Evaluate(truncated: true, rows: [Row("Gone:All")]).Findings.Should().ContainSingle()
            .Which.Caveat.Should().EndWith("The read stopped at 1,000 rows, so there may be more.");
    }

    [Theory]
    [InlineData("Trips:All", "Trips")]
    [InlineData("Trips:V2:All:acme", "Trips:V2")]
    [InlineData("a:b:c:d:e", "a:b:c:d:e")]
    public void The_subject_is_the_projection_and_its_version(string name, string subject)
    {
        OrphanedProgressionCheck.SubjectOf(name).Should().Be(subject);
    }
}
