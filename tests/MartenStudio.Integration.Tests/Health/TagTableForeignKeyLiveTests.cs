using Marten.Events;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// T15 on a DCB tag table, at whichever Marten built this run. Its foreign key on <c>seq_id</c> sits behind a primary key
/// that leads with <c>value</c>, so below 9.51 nothing covers it, and T15 says so with no index offered - Marten's next
/// apply would drop one nobody declared - and marten#5694's sentence. From 9.51 the table declares
/// <c>idx_mt_event_tag_&lt;suffix&gt;_seq_id</c>, the apply builds it, and the key is covered.
/// </summary>
/// <remarks>
/// Not a <c>[HealthFires]</c>: T15's own proof is <c>IndexLintLiveTests</c> in the Postgres Studio suite. This is the
/// Marten half of its wording, which the floor (9.39.0) and the latest tested (9.54.1) answer differently.
/// </remarks>
public class TagTableForeignKeyLiveTests(PostgresFixture postgres)
{
    private const string Table = "mt_event_tag_health_tag";

    [PostgresFact]
    public async Task A_tag_tables_key_is_T15_with_the_5694_note_below_9_51_and_covered_from_it()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(
            postgres,
            "health_t15_tags",
            store: static options => ((EventGraph) options.Events).RegisterTagType<HealthTag>());

        await EventHealthData.AppendToAsync(host.Store, Guid.NewGuid(), 1);

        // Two hundred thousand tag values on one event - the primary key is (value, seq_id) - so the table is past T15's
        // 8 MiB with a single row in mt_events for the key to point at.
        string tags = SqlIdentifier.Qualify(host.EventSchema, Table);
        string events = SqlIdentifier.Qualify(host.EventSchema, "mt_events");
        await EventHealthData.ExecuteAsync(
            postgres,
            $"insert into {tags} (value, seq_id) select 'customer-' || g, (select pg_catalog.min(seq_id) from {events}) "
            + "from pg_catalog.generate_series(1, 200000) g; "
            + $"analyze {tags};");

        HealthReport report = await host.ReportAsync();
        string key = SchemaKey.For(host.EventSchema, Table);
        IReadOnlyList<HealthReportFinding> findings = [.. report.Findings.Where(x => x.Code == "T15" && x.Finding.Subject?.StartsWith(key + ".", StringComparison.Ordinal) == true)];

        bool declared = RuntimeFeature.TaggedStreamCompaction.IsAvailable(MartenRuntime.Current);
        long seqIdIndexes = await EventHealthData.ScalarAsync(
            postgres,
            $"select count(*) from pg_catalog.pg_indexes where schemaname = '{host.EventSchema}' and tablename = '{Table}' and indexname = 'idx_{Table}_seq_id'");
        seqIdIndexes.Should().Be(declared ? 1 : 0, "marten#5694 declares the index from 9.51.0, and this run resolved {0}", MartenRuntime.Current.Describe(RuntimePackage.Marten));

        if (declared)
        {
            findings.Should().BeEmpty("idx_mt_event_tag_<suffix>_seq_id leads with the key's column");
            return;
        }

        HealthReportFinding finding = findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Remedy.Should().BeNull("Marten creates the table, and its next apply drops an index its configuration does not declare");
        finding.Finding.WhyItMatters.Should().EndWith(MartenHealthRemedyProvider.TagTableNote);
        finding.Evidence.Should().Contain(new HealthEvidence("References", SchemaKey.For(host.EventSchema, "mt_events") + " (seq_id)"));
    }
}
