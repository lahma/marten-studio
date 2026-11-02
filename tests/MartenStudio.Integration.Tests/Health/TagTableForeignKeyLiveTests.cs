using Marten.Events;

using MartenStudio.Services.Configuration;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// T15 on a DCB tag table. Its foreign key on <c>seq_id</c> sits behind a primary key that leads with <c>value</c>; since
/// Marten 9.51 the table declares <c>idx_mt_event_tag_&lt;suffix&gt;_seq_id</c>, the apply builds it, and the key is
/// covered (marten#5694).
/// </summary>
/// <remarks>
/// Not a <c>[HealthFires]</c>: T15's own proof is <c>IndexLintLiveTests</c> in the Postgres Studio suite. This is the
/// Marten half of its wording.
/// </remarks>
public class TagTableForeignKeyLiveTests(PostgresFixture postgres)
{
    private const string Table = "mt_event_tag_health_tag";

    [PostgresFact]
    public async Task A_tag_tables_key_is_covered_by_the_seq_id_index_Marten_declares()
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

        long seqIdIndexes = await EventHealthData.ScalarAsync(
            postgres,
            $"select count(*) from pg_catalog.pg_indexes where schemaname = '{host.EventSchema}' and tablename = '{Table}' and indexname = 'idx_{Table}_seq_id'");
        seqIdIndexes.Should().Be(1, "marten#5694 declares the index since 9.51.0, and this run resolved {0}", MartenRuntime.Current.Describe(RuntimePackage.Marten));

        findings.Should().BeEmpty("idx_mt_event_tag_<suffix>_seq_id leads with the key's column");
    }
}
