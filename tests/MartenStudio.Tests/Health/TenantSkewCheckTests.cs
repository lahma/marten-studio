using MartenStudio.Services.Health.Checks;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

using static MartenStudio.Tests.Health.TableCheckBuilders;

namespace MartenStudio.Tests.Health;

/// <summary>
/// T18 (X4): one tenant past half a conjoined table's rows, in both shapes - a table judged by <c>pg_stats</c>, and one Marten
/// partitions by tenant, judged by its partitions' rows - at its edges, with the tenant named only on the finding that carries
/// it (<see cref="HealthFinding.TenantId" />), never in a note.
/// </summary>
public class TenantSkewCheckTests
{
    private static MartenTable Orders => Document<HealthOrderDoc>(static o => o.Schema.For<HealthOrderDoc>().MultiTenanted());

    private static HealthColumnSkewRow Statistics(MartenTable table, double rows, params (string Tenant, double Rows)[] top) => new()
    {
        Schema = table.Schema,
        Table = table.Table,
        Rows = rows,
        Leaves = 1,
        ReadableLeaves = 1,
        RowsCounted = rows,
        Values = 6,
        TopValues = [.. top.Select(static x => x.Tenant)],
        TopRows = [.. top.Select(static x => x.Rows)],
    };

    private static HealthColumnSkewRow Partitions(MartenTable table, double rows, double defaultRows, params (string Tenant, double Rows)[] top) => new()
    {
        Schema = table.Schema,
        Table = table.Table,
        ByPartition = true,
        Rows = rows,
        Leaves = 40,
        RowsCounted = rows - defaultRows,
        Values = 39,
        TopValues = [.. top.Select(static x => "FOR VALUES IN ('" + x.Tenant.Replace("'", "''", StringComparison.Ordinal) + "')")],
        TopRows = [.. top.Select(static x => x.Rows)],
        DefaultRows = defaultRows,
    };

    private static HealthVerdict Judge(params TenantSkewTable[] tables) =>
        new TenantSkewCheck().Evaluate(new TenantSkewFacts(tables), Evaluation());

    [Fact]
    public void One_tenant_past_half_of_a_conjoined_table_is_info_naming_it_on_the_finding_alone()
    {
        MartenTable orders = Orders;
        HealthFinding finding = Judge(new TenantSkewTable(orders, Statistics(orders, 120_000, ("acme", 84_000), ("globex", 7_200)))).Findings.Single();

        finding.Code.Should().Be("T18");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Subject.Should().Be(orders.Key);
        finding.TenantId.Should().Be("acme", "a finding about one tenant is shown only past the activity gate and that tenant's store policy");
        finding.Title.Should().Be("One tenant holds 70% of the HealthOrderDoc documents").And.NotContain("acme");
        finding.DocumentTypes.Should().Equal(typeof(HealthOrderDoc));
        finding.Evidence.Should().Contain(new HealthEvidence("Tenant", "acme"))
            .And.Contain(new HealthEvidence("Its rows", "~84,000 of ~120,000 (70%)"))
            .And.Contain(new HealthEvidence("The next largest tenant", "6.0%"))
            .And.Contain(new HealthEvidence("Tenants", "~6 (estimated)"));
        finding.Caveat.Should().Contain("ANALYZE").And.Contain("shown only to a visitor who may see that tenant");
        finding.Remedy!.Code.Should().Contain("MultiTenantedWithPartitioning").And.Contain("PartitionMultiTenantedDocumentsUsingMartenManagement")
            .And.Contain("MultiTenantedDatabases");
        finding.Remedy.Explanation.Should().Contain("is not additive");
    }

    [Fact]
    public void Half_or_less_too_few_rows_or_too_few_tenants_is_quiet_and_the_note_names_no_tenant()
    {
        MartenTable orders = Orders;

        HealthVerdict half = Judge(new TenantSkewTable(orders, Statistics(orders, 120_000, ("acme", 60_000))));
        half.Findings.Should().BeEmpty("exactly half is not more than half");
        half.Note.Should().Contain("The largest tenant holds 50%").And.NotContain("acme");

        Judge(new TenantSkewTable(orders, Statistics(orders, 99_000, ("acme", 90_000)))).Findings.Should().BeEmpty("under 100,000 rows");
        Judge(new TenantSkewTable(orders, Statistics(orders, 99_000, ("acme", 90_000))))
            .Note.Should().Be("No conjoined table holds 100,000 rows yet.");
        Judge(new TenantSkewTable(orders, Statistics(orders, 120_000, ("acme", 84_000)) with { Values = 4 }))
            .Findings.Should().BeEmpty("with four tenants one being large is the shape of the data");
        Judge(new TenantSkewTable(orders, null)).Findings.Should().BeEmpty("the table does not exist yet");
        new TenantSkewCheck().Evaluate(new TenantSkewFacts([]), Evaluation()).Note.Should().Be("No table this store shows is conjoined-tenanted.");
    }

    [Fact]
    public void A_table_partitioned_by_tenant_is_judged_by_its_partitions_with_the_default_partition_in_the_whole()
    {
        MartenTable orders = Orders;
        HealthFinding finding = Judge(new TenantSkewTable(orders, Partitions(orders, 200_000, 20_000, ("o'brien", 130_000), ("acme", 10_000)))).Findings.Single();

        finding.TenantId.Should().Be("o'brien", "the partition's one value, unquoted");
        finding.Evidence.Should().Contain(new HealthEvidence("Its rows", "~130,000 of ~200,000 (65%)"))
            .And.Contain(new HealthEvidence("Tenants", "39 tenant partitions, and a DEFAULT partition holding others"));
        finding.WhyItMatters.Should().Contain("partitioned by tenant").And.Contain("T19");
        finding.Remedy!.Code.Should().Contain("already partitioned by tenant").And.Contain("MultiTenantedDatabases").And.NotContain("MultiTenantedWithPartitioning");

        // A partition that holds several tenants is no one tenant's skew.
        HealthColumnSkewRow grouped = Partitions(orders, 200_000, 0, ("acme", 130_000)) with { TopValues = ["FOR VALUES IN ('acme', 'globex')"] };
        Judge(new TenantSkewTable(orders, grouped)).Findings.Should().BeEmpty();
    }

    [Fact]
    public void The_event_store_is_given_a_database_or_tenant_partitioned_events_in_words()
    {
        MartenTable events = EventTable();
        HealthFinding finding = Judge(new TenantSkewTable(events, Statistics(events, 500_000, ("acme", 400_000)))).Findings.Single();

        finding.Title.Should().Be("One tenant holds 80% of the rows of " + events.Key);
        finding.Remedy!.Code.Should().Contain("UseTenantPartitionedEvents").And.Contain("MultiTenantedDatabases");
        finding.Remedy.Explanation.Should().Contain("not a setting to flip");
    }

    [Fact]
    public void Statistics_never_analysed_or_hidden_from_the_role_are_said_in_the_note()
    {
        MartenTable orders = Orders;

        HealthVerdict unanalysed = Judge(new TenantSkewTable(orders, Statistics(orders, 120_000) with { RowsCounted = 0 }));
        unanalysed.Findings.Should().BeEmpty();
        unanalysed.Note.Should().Contain("never analysed");

        MartenTable events = EventTable();
        HealthVerdict partly = new TenantSkewCheck().Evaluate(
            new TenantSkewFacts(
                [
                    new TenantSkewTable(orders, Statistics(orders, 120_000) with { ReadableLeaves = 0, RowsCounted = 0 }),
                    new TenantSkewTable(events, Statistics(events, 500_000, ("acme", 100_000))),
                ])
            {
                ReadsAs = "console_role",
                Platform = PostgresStudio.Services.Health.PostgresPlatform.SelfHosted,
            },
            Evaluation());

        partly.Findings.Should().BeEmpty();
        partly.Note.Should().Contain("The largest tenant holds 20%")
            .And.Contain("Not checked: this needs SELECT on column tenant_id of " + orders.Key + " for role console_role.");
    }

    [Fact]
    public void Only_conjoined_tables_are_judged()
    {
        TenantSkewCheck.IsConjoined(Orders, conjoinedEvents: false, "studio_sql_events").Should().BeTrue();
        TenantSkewCheck.IsConjoined(Document<HealthOrderDoc>(), conjoinedEvents: false, "studio_sql_events").Should().BeFalse("single-tenanted");
        TenantSkewCheck.IsConjoined(EventTable(), conjoinedEvents: true, "studio_sql_events").Should().BeTrue();
        TenantSkewCheck.IsConjoined(EventTable("mt_streams"), conjoinedEvents: true, "studio_sql_events").Should().BeTrue();
        TenantSkewCheck.IsConjoined(EventTable(), conjoinedEvents: false, "studio_sql_events").Should().BeFalse();
        TenantSkewCheck.IsConjoined(EventTable("mt_event_progression"), conjoinedEvents: true, "studio_sql_events").Should().BeFalse();
        TenantSkewCheck.IsConjoined(EventTable(), conjoinedEvents: true, "another_schema").Should().BeFalse("another store's event store");
    }
}
