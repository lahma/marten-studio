using System.Text.RegularExpressions;

using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Tests.Tables;

/// <summary>
/// W5-B1's statements hold D27 in their text: a list locks nothing it lists - no size function, no <c>pg_partition_tree</c>,
/// no sequence value, no deparse that opens what it describes - and every built-in is <c>pg_catalog</c>-qualified. The live
/// half is <c>LintMatrixLiveTests</c>, behind a held <c>ACCESS EXCLUSIVE</c> on 14 and 18, and the version matrix's
/// <c>MatrixReads</c> on every leg; <c>IndexLintLiveTests</c> runs the sweep under the probe that sees any lock.
/// </summary>
public partial class LintQueriesTests
{
    public static TheoryData<string> Statements =>
    [
        .. IndexLintQueries.ListStatements.Select((_, i) => "IndexLintQueries[" + i + "]"),
        .. PartitionQueries.ListStatements.Select((_, i) => "PartitionQueries[" + i + "]"),
    ];

    private static string Sql(string name)
    {
        int index = int.Parse(name[(name.IndexOf('[', StringComparison.Ordinal) + 1)..^1], System.Globalization.CultureInfo.InvariantCulture);
        return name.StartsWith("IndexLint", StringComparison.Ordinal) ? IndexLintQueries.ListStatements[index] : PartitionQueries.ListStatements[index];
    }

    [Theory]
    [MemberData(nameof(Statements))]
    public void A_list_locks_nothing_it_lists(string name)
    {
        string sql = Sql(name);

        Locking().IsMatch(sql).Should().BeFalse(name + ": a size function queues behind a migration, pg_partition_tree locks every partition, and a sequence's value is read only under a lock (D27, B0 §3)");
        OpeningDeparse().IsMatch(sql).Should().BeFalse(name + ": these deparse functions open what they describe and queue behind ACCESS EXCLUSIVE (14-18, measured 2026-10-26)");
        ExpressionWithRelation().IsMatch(sql).Should().BeFalse(name + ": pg_get_expr with a relation opens it; with 0 it opens nothing");
        sql.Should().NotContain("pg_sequences", "the view locks every sequence it lists on 18 (B0 §3)");
    }

    [Theory]
    [MemberData(nameof(Statements))]
    public void Every_built_in_is_pg_catalog_qualified(string name)
    {
        foreach (Match call in UnqualifiedCall().Matches(Sql(name)))
        {
            Keywords.Should().Contain(call.Groups[1].Value, name + " calls " + call.Groups[1].Value + "() without pg_catalog.");
        }
    }

    [Fact]
    public void The_partition_read_deparses_a_bound_with_no_relation_and_the_index_read_never_deparses_for_b1()
    {
        PartitionQueries.PartitionsSql.Should().Contain("pg_catalog.pg_get_expr(m.relpartbound, 0)");
        PartitionQueries.PartitionsSql.Should().Contain("pg_catalog.pg_inherits").And.NotContain("pg_get_partkeydef");

        // The columns W5-B1 adds to the shared index read are catalog values: the deparse stays behind the pg_locks guard.
        string added = HealthTableQueries.IndexesSql[HealthTableQueries.IndexesSql.IndexOf("i.table_oid in (select lk.oid from locked lk),", StringComparison.Ordinal)..];
        added.Should().NotContain("pg_get_indexdef").And.Contain("last_idx_scan").And.Contain("avg_width");

        // T25's widths are every stored column's, INCLUDE ones too; the coverage keys stay the key columns (W5-B1 review F6).
        added.Should().Contain("a.attnum between 1 and i.indnatts").And.Contain("[1:i.indnatts]").And.Contain("::pg_catalog.int2[])[1:i.indnkeyatts],");
        IndexLintQueries.IntegerKeysSql.Should().Contain("q.seqmax < 9223372036854775807", "a bigint sequence capped by its own MAXVALUE is judged (F8)");
        PartitionQueries.PartitionsSql.Should().Contain("pg_catalog.current_setting('TimeZone')", "months are looked for in the session's zone (F10)");
    }

    [Fact]
    public void The_counters_age_read_names_the_last_scan_columns_only_from_16()
    {
        IndexLintQueries.CountersAge([], lastScanColumns: false).Sql.Should().NotContain("last_seq_scan").And.NotContain("last_idx_scan");
        IndexLintQueries.CountersAge([], lastScanColumns: true).Sql.Should().Contain("s.last_seq_scan, s.last_idx_scan").And.Contain("pg_catalog.pg_stat_all_indexes");
        IndexLintQueries.CountersAgeSql.Should().Contain("not c.relisshared", "a shared catalog's statistics are the cluster's, not this database's");
    }

    [Fact]
    public void The_integer_key_read_follows_pg_depend_and_never_matches_nextval_in_text()
    {
        IndexLintQueries.IntegerKeysSql.Should().Contain("dep.deptype in ('a', 'i')").And.Contain("dep.deptype = 'n'")
            .And.NotContain("nextval").And.NotContain("adbin");
    }

    [Theory]
    [InlineData("select pg_catalog.pg_get_expr(c.relpartbound, c.oid) from pg_catalog.pg_class c", true)]
    [InlineData("select pg_catalog.pg_get_expr(c.relpartbound,c.oid)", true)]
    [InlineData("select pg_catalog.pg_get_expr(c.relpartbound, 0) from pg_catalog.pg_class c", false)]
    [InlineData("select pg_catalog.pg_get_expr(c.relpartbound,0)", false)]
    public void The_screens_still_see_an_offender(string sql, bool offends)
    {
        // Anti-vacuity: the screens above are only as good as their patterns.
        ExpressionWithRelation().IsMatch(sql).Should().Be(offends);
        Locking().IsMatch("select pg_catalog.pg_total_relation_size(c.oid)").Should().BeTrue();
        Locking().IsMatch("select pg_catalog.pg_sequence_last_value(s.oid)").Should().BeTrue();
        OpeningDeparse().IsMatch("select pg_catalog.pg_get_partkeydef(c.oid)").Should().BeTrue();
        UnqualifiedCall().Matches("select format_type(a.atttypid, a.atttypmod), pg_catalog.lower(x)").Select(static x => x.Groups[1].Value)
            .Should().Equal("format_type");
    }

    private static readonly string[] Keywords =
    [
        "any", "all", "exists", "in", "coalesce", "greatest", "least", "case", "u", "ordinality", "select", "and", "or", "not",
        "when", "then", "else", "is", "on", "as", "filter", "from", "extract", "rows", "lateral", "union", "where", "join",
        "listed", "tree", "keys", "referenced", "leaves", "fed", "members", "keyed", "bounds", "valued", "read_back", "settings",
        "judged", "sized", "candidates", "h", "x", "d", "b", "n", "m",
    ];

    [GeneratedRegex(@"pg_(?:total_)?relation_size|pg_indexes_size|pg_table_size|pg_database_size|pg_partition_(?:tree|ancestors|root)|pg_sequence_last_value", RegexOptions.IgnoreCase)]
    private static partial Regex Locking();

    [GeneratedRegex(@"pg_get_(?:indexdef|viewdef|constraintdef|triggerdef|partkeydef|ruledef)", RegexOptions.IgnoreCase)]
    private static partial Regex OpeningDeparse();

    [GeneratedRegex(@"pg_get_expr\s*\([^,()]+,(?>\s*)(?!0\s*\))", RegexOptions.IgnoreCase)]
    private static partial Regex ExpressionWithRelation();

    [GeneratedRegex(@"(?<![.\w""])([a-z_]+)\s*\(")]
    private static partial Regex UnqualifiedCall();
}
