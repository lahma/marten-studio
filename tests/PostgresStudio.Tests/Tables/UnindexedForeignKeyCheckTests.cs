using static PostgresStudio.Tests.Tables.LintBuilders;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Tables;

/// <summary>T15 over plain Postgres tables: the size line, the rule, a library's exemption, a locked table, and the remedy's quoting.</summary>
public class UnindexedForeignKeyCheckTests
{
    private const long Large = 10L * 1024 * 1024;

    private static HealthForeignKeyRow Key(StudioTable table, string name, short[] attributes, string[] columns, string referencedSchema = "app", string referencedTable = "parent", short[]? partitionKey = null, long? deletes = 1_234) =>
        new(table.Schema, table.Table, name, attributes, columns, referencedSchema, referencedTable, ["id"], partitionKey ?? [], deletes, true);

    private static UnindexedForeignKeyFacts Facts(StudioTable table, long heapBytes, IReadOnlyList<HealthForeignKeyRow> keys, params IndexFacts[] indexes) =>
        new([Table(table, heapBytes)], [table, Host("parent")], keys, indexes);

    [Fact]
    public void An_uncovered_key_on_a_table_of_8_MiB_or_more_is_a_warning_with_a_concurrent_index_for_the_hosts_migrations()
    {
        StudioTable child = Host("child");
        HealthVerdict verdict = new UnindexedForeignKeyCheck().Evaluate(
            Facts(child, Large, [Key(child, "child_parent_id_fkey", [2], ["parent_id"])], Index(child, IndexRow(child, "child_pkey", 1))),
            Evaluation());

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("T15");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be("app.child.child_parent_id_fkey");
        finding.Title.Should().Contain("child_parent_id_fkey").And.Contain("(parent_id)").And.Contain("no index that covers it");
        finding.Evidence.Should().Contain(new HealthEvidence("References", "app.parent (id)"));
        finding.Evidence.Should().Contain(new HealthEvidence("Deletes from the referenced table", "1,234 since the counters began - each one ran this lookup"));
        finding.WhyItMatters.Should().Contain("10.0 MiB").And.Contain("never a foreign key");
        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Migration);
        finding.Remedy.Code.Should().Be("CREATE INDEX CONCURRENTLY ON \"app\".\"child\" (\"parent_id\");");
        finding.GatedSchemas.Should().BeEmpty("both ends are in the source's own schema");
    }

    [Fact]
    public void Below_8_MiB_a_covered_key_and_a_librarys_table_say_nothing()
    {
        StudioTable child = Host("child");
        new UnindexedForeignKeyCheck().Evaluate(Facts(child, UnindexedForeignKeyCheck.MinTableBytes - 1, [Key(child, "fk", [2], ["parent_id"])]), Evaluation())
            .Findings.Should().BeEmpty("a small table is read whole cheaply");

        new UnindexedForeignKeyCheck().Evaluate(
                Facts(child, Large, [Key(child, "fk", [2], ["parent_id"])], Index(child, IndexRow(child, "by_parent", 2))),
                Evaluation())
            .Findings.Should().BeEmpty();

        StudioTable triggers = LibraryTable("qrtz_triggers");
        new UnindexedForeignKeyCheck().Evaluate(Facts(triggers, Large, [Key(triggers, "fk", [2], ["job_name"])]), Evaluation())
            .Findings.Should().BeEmpty("a library's foreign keys are the library's to index (issue #8 G1)");
    }

    [Fact]
    public void A_locked_table_is_never_read_as_one_with_no_covering_index_and_is_counted()
    {
        StudioTable child = Host("child");
        HealthVerdict verdict = new UnindexedForeignKeyCheck().Evaluate(
            Facts(child, Large, [Key(child, "fk", [2], ["parent_id"])]) with { LockedTables = new HashSet<string>([child.Key], StringComparer.Ordinal) },
            Evaluation());

        verdict.Findings.Should().BeEmpty("its indexes were not read this sweep, so 'none covers it' is unknown, not true");
        verdict.Note.Should().Be(HealthTables.LockedNote(new HashSet<string>([child.Key], StringComparer.Ordinal)));
    }

    [Fact]
    public void A_partitioned_table_gets_a_plain_create_index_because_concurrently_is_not_allowed_there()
    {
        StudioTable events = Host("events");
        UnindexedForeignKeyFacts facts = new(
            [Table(events, Large, static row => row with { Kind = "p" })],
            [events, Host("parent")],
            [Key(events, "events_parent_fk", [2], ["parent_id"], partitionKey: [3])],
            []);

        HealthFinding finding = new UnindexedForeignKeyCheck().Evaluate(facts, Evaluation()).Findings.Single();
        finding.Remedy!.Code.Should().Be("CREATE INDEX ON \"app\".\"events\" (\"parent_id\");");
        finding.Remedy.Explanation.Should().Contain("ON ONLY").And.Contain("ATTACH PARTITION");
    }

    [Fact]
    public void A_referenced_document_table_the_set_does_not_hold_is_never_named_and_another_schemas_table_is_gated()
    {
        StudioTable child = Host("child");
        Dictionary<string, StudioTable> judged = new(StringComparer.Ordinal) { [child.Key] = child };
        HashSet<string> own = new(["app"], StringComparer.Ordinal);

        UnindexedForeignKeyCheck.Referenced(Key(child, "fk", [2], ["doc_id"], "app", "mt_doc_secret"), child, judged, own).Name
            .Should().Be(UnindexedForeignKeyCheck.UnshownDocumentTable, "it may be a hidden document type's table");

        var elsewhere = UnindexedForeignKeyCheck.Referenced(Key(child, "fk", [2], ["code"], "hr", "codes"), child, judged, own);
        elsewhere.Name.Should().Be("hr.codes");
        elsewhere.GatedSchemas.Should().Equal("hr");

        StudioTable codes = new("hr", "codes", StudioTableKind.Relational, StudioTableOwnership.Other) { GatedSchemas = ["hr"] };
        judged[codes.Key] = codes;
        UnindexedForeignKeyCheck.Referenced(Key(child, "fk", [2], ["code"], "hr", "codes"), child, judged, own).GatedSchemas
            .Should().ContainSingle().Which.Should().Be("hr", "a judged table brings the set's own gating");
    }

    [Fact]
    public void A_hostile_name_is_quoted_and_one_that_cannot_be_printed_gets_no_statement()
    {
        StudioTable odd = Host("Order Lines; drop table x", "Sales");
        HealthFinding finding = new UnindexedForeignKeyCheck().Evaluate(
            new UnindexedForeignKeyFacts([Table(odd, Large)], [odd], [Key(odd, "fk", [2], ["Parent \"Id\""], "Sales", "parent")], []),
            Evaluation()).Findings.Single();

        finding.Remedy.Should().BeNull("a name with a double quote in it gets no statement rather than an escaped one");

        HealthFinding quoted = new UnindexedForeignKeyCheck().Evaluate(
            new UnindexedForeignKeyFacts([Table(odd, Large)], [odd], [Key(odd, "fk", [2], ["Parent Id"], "Sales", "parent")], []),
            Evaluation()).Findings.Single();

        quoted.Remedy!.Code.Should().Be("CREATE INDEX CONCURRENTLY ON \"Sales\".\"Order Lines; drop table x\" (\"Parent Id\");");
        MigrationSqlText.CreateIndexConcurrently("app", "t", ["a‮b"]).Should().BeNull("a format character makes the text say something else");
        MigrationSqlText.CreateIndexConcurrently("app", "t", []).Should().BeNull();
    }
}
