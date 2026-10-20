using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.StatementBuilders;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// T22 with no Postgres: Marten's guarded writes recognised by their shape - as Marten 9.39.0 and 9.50.2 both write
/// them, and as pg_stat_statements normalises them - and the conflict rate judged per table.
/// </summary>
public class ConcurrencyConflictCheckTests
{
    private static readonly ConcurrencyConflictCheck Check = new();

    private static string Table => Doc.Schema + "." + Doc.Table;

    /// <summary>DocumentStorageDescriptorBuilder's optimistic upsert, normalised: every constant a placeholder.</summary>
    private static string OptimisticUpsert =>
        "insert into " + Table + " (id, data, \"mt_dotnet_type\", \"mt_version\", \"mt_last_modified\") values ($1, $2, $3, $4, transaction_timestamp()) "
        + "on conflict (id) do update set data = excluded.data, \"mt_dotnet_type\" = excluded.\"mt_dotnet_type\", \"mt_version\" = excluded.\"mt_version\", "
        + "\"mt_last_modified\" = excluded.\"mt_last_modified\" where " + Table + ".mt_version = $5 returning mt_version";

    /// <summary>Its numeric-revision twin: the <c>? = 0</c> of the auto revision is normalised too.</summary>
    private static string NumericUpsert =>
        "insert into " + Table + " (id, data, \"mt_version\") values ($1, $2, CASE WHEN $3 = $4 THEN $5 ELSE $6 END) "
        + "on conflict (id) do update set data = excluded.data, mt_version = CASE WHEN $7 = $8 THEN " + Table + ".mt_version + $9 ELSE $10 END "
        + "where $11 = $12 or " + Table + ".mt_version < $13 returning mt_version";

    private static string OptimisticUpdate =>
        "update " + Table + " set data = $1, \"mt_dotnet_type\" = $2, \"mt_last_modified\" = transaction_timestamp() where id = $3 and mt_version = $4 returning mt_version";

    private static string StreamVersion =>
        "update " + Streams.Schema + ".mt_streams set version = $1 where id = $2 and version = $3 returning version";

    /// <summary>Each text and the kind it is, by name: the theories pass names, because the kind is internal.</summary>
    public static TheoryData<string, string?> Shapes => new()
    {
        { OptimisticUpsert, nameof(GuardedWriteKind.Upsert) },
        { NumericUpsert, nameof(GuardedWriteKind.Upsert) },
        { OptimisticUpdate, nameof(GuardedWriteKind.Update) },
        { StreamVersion, nameof(GuardedWriteKind.StreamVersion) },
        { "update " + Streams.Schema + ".mt_streams set version = $1 where id = $2 and version = $3 and tenant_id = $4 returning version", nameof(GuardedWriteKind.StreamVersion) },
        // Unguarded: Overwrite, Insert, an update with concurrency off, another mt_streams update, a read.
        { "insert into " + Table + " (id, data) values ($1, $2) on conflict (id) do update set data = excluded.data returning mt_version", null },
        { "insert into " + Table + " (id, data) values ($1, $2) on conflict (id) do nothing returning mt_version", null },
        { "update " + Table + " set data = $1 where id = $2 returning id", null },
        { "update " + Streams.Schema + ".mt_streams set is_archived = TRUE where id = $1", null },
        { SelectOnDoc, null },
        // A stream-version subquery's own where inside the set list is not a guard.
        {
            "insert into " + Table + " (id, data, \"mt_version\") values ($1, $2, $3) on conflict (id) do update set data = excluded.data, "
            + "mt_version = COALESCE((select version from s.mt_streams where s.mt_streams.id = " + Table + ".id), " + Table + ".mt_version + $4) "
            + "returning mt_version",
            null
        },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Marten_s_guarded_writes_are_recognised_by_their_shape_and_nothing_else_is(string text, string? kind)
    {
        GuardedWriteKind? recognised = ConcurrencyConflictCheck.Classify(text)?.Kind;

        recognised?.ToString().Should().Be(kind);
        (recognised is null).Should().Be(kind is null);
    }

    [Fact]
    public void The_target_is_the_table_the_write_names_and_must_be_one_of_the_stores_visible_tables()
    {
        ConcurrencyConflictCheck.Classify(OptimisticUpsert)!.Value.Target.Should().Be(Table);

        StatementRead read = Read(
            Statement(1, OptimisticUpsert, [Doc], 2000, 100, rows: 1900, walBytes: 1),
            // A hidden type's table is never among a statement's tables, so its writes are never counted.
            Statement(2, OptimisticUpsert.Replace(Doc.Table, "mt_doc_payroll", StringComparison.Ordinal), [], 2000, 100, rows: 1000, walBytes: 1));

        ConcurrencyConflictCheck.Recognise(read).Should().ContainSingle().Which.Statement.QueryId.Should().Be(1);
    }

    [Theory]
    [InlineData(2000, 1960, nameof(HealthSeverity.Info), "2.0%")]
    [InlineData(2000, 1800, nameof(HealthSeverity.Warning), "10%")]
    public void A_conflict_rate_is_info_below_5_percent_and_a_warning_from_it(long calls, long rows, string severity, string rate)
    {
        StatementRead read = Read(Statement(1, OptimisticUpsert, [Doc], calls, 100, rows: rows, walBytes: 1));

        HealthFinding finding = Check.Evaluate(new ConcurrencyFacts(read, ConcurrencyConflictCheck.Recognise(read)), TableCheckBuilders.Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.ToString().Should().Be(severity);
        finding.Subject.Should().Be(Doc.Key);
        finding.Title.Should().Be(rate + " of the StatementDoc collection's guarded writes found a newer version");
        finding.DocumentTypes.Should().Equal(typeof(StatementDoc));
        finding.Evidence.Should().Contain(new HealthEvidence("Conflicts", TableText.Count(calls - rows)));
        finding.Caveat.Should().Contain("quick append's conflict is a unique violation").And.Contain("E02").And.Contain("C08");
        finding.Remedy!.Code.Should().Contain("FetchForExclusiveWriting");
    }

    [Fact]
    public void Every_guarded_statement_of_one_table_is_summed()
    {
        StatementRead read = Read(
            Statement(1, OptimisticUpsert, [Doc], 600, 100, rows: 540, walBytes: 1),
            Statement(2, OptimisticUpdate, [Doc], 600, 100, rows: 600, walBytes: 1));

        HealthFinding finding = Check.Evaluate(new ConcurrencyFacts(read, ConcurrencyConflictCheck.Recognise(read)), TableCheckBuilders.Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Evidence.Should().Contain(new HealthEvidence("Guarded writes", "1,200"));
        finding.Evidence.Should().Contain(new HealthEvidence("Conflict rate", "5.0%"));
        finding.Evidence.Single(static x => x.Label == "Statements").Value.Should()
            .Be("guarded upsert 600 calls, 60 conflicts; guarded update 600 calls, 0 conflicts");
    }

    [Fact]
    public void Fewer_than_a_thousand_guarded_writes_or_none_that_conflicted_is_quiet_and_says_which()
    {
        StatementRead few = Read(Statement(1, OptimisticUpsert, [Doc], 500, 100, rows: 250, walBytes: 1));
        StatementRead clean = Read(Statement(1, OptimisticUpsert, [Doc], 5000, 100, rows: 5000, walBytes: 1));

        HealthVerdict quietFew = Check.Evaluate(new ConcurrencyFacts(few, ConcurrencyConflictCheck.Recognise(few)), TableCheckBuilders.Evaluation());
        HealthVerdict quietClean = Check.Evaluate(new ConcurrencyFacts(clean, ConcurrencyConflictCheck.Recognise(clean)), TableCheckBuilders.Evaluation());

        quietFew.Findings.Should().BeEmpty();
        quietFew.Note.Should().Contain("500 guarded writes, fewer than the 1,000");
        quietClean.Findings.Should().BeEmpty();
        quietClean.Note.Should().Contain("5,000 guarded writes, none conflicted");
    }

    [Fact]
    public void A_rich_appends_expected_version_check_is_the_event_stores_finding()
    {
        StatementRead read = Read(Statement(1, StreamVersion, [Streams], 3000, 100, rows: 2700, walBytes: 1));

        HealthFinding finding = Check.Evaluate(new ConcurrencyFacts(read, ConcurrencyConflictCheck.Recognise(read)), TableCheckBuilders.Evaluation())
            .Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Be("10% of the event store's expected-version appends found the stream had moved on");
        finding.DocumentTypes.Should().BeEmpty();
        finding.Remedy!.Code.Should().Contain("FetchForExclusiveWriting");
    }

    [Fact]
    public void The_window_between_two_statement_checks_is_what_is_judged_once_it_exists()
    {
        // Clean over its whole life, 20% conflicts in the last ten minutes.
        StatementRead read = Read(Statement(1, OptimisticUpsert, [Doc], 101_000, 100, rows: 100_800, walBytes: 1));
        HealthHistoryView history = History(
            (TimeSpan.FromMinutes(10), [(ConcurrencyConflictCheck.CallsSeries + "1", 100_000), (ConcurrencyConflictCheck.RowsSeries + "1", 100_000)]),
            (TimeSpan.Zero, [(ConcurrencyConflictCheck.CallsSeries + "1", 101_000), (ConcurrencyConflictCheck.RowsSeries + "1", 100_800)]));

        HealthFinding finding = Check.Evaluate(new ConcurrencyFacts(read, ConcurrencyConflictCheck.Recognise(read)), TableCheckBuilders.Evaluation(history))
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Guarded writes", "1,000"));
        finding.Evidence.Single(static x => x.Label == "Window").Value.Should().StartWith("over the last 10 min");
    }

    [Fact]
    public void Each_guarded_statement_is_sampled_and_nothing_else()
    {
        StatementRead read = Read(Statement(1, OptimisticUpsert, [Doc], 2000, 100, rows: 1900, walBytes: 1), Statement(2, SelectOnDoc, [Doc], 10, 1));

        Check.Sample(new ConcurrencyFacts(read, ConcurrencyConflictCheck.Recognise(read))).Select(static x => x.Series)
            .Should().BeEquivalentTo([ConcurrencyConflictCheck.CallsSeries + "1", ConcurrencyConflictCheck.RowsSeries + "1"]);
    }

    [Fact]
    public void A_truncated_text_is_never_classified()
    {
        StatementRead read = Read(Statement(1, OptimisticUpsert, [Doc], 2000, 100, rows: 1900, walBytes: 1) with { TextTruncated = true });

        ConcurrencyConflictCheck.Recognise(read).Should().BeEmpty();
    }
}
