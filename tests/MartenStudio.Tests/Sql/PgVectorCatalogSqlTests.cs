using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// PGV, read off the statements: pgvector columns and indexes are known from <c>pg_catalog</c> alone - the
/// extension that owns a type or an access method, a type modifier, <c>pg_opclass</c> and <c>reloptions</c> - with
/// no value read, and the schema filter every catalog read carries. The new vector-column read calls no function that
/// locks; the index reads' <c>pg_get_indexdef</c> briefly locks the indexed table, as it always did, and
/// <c>PgVectorLiveTests</c> measures what that costs behind an <c>ACCESS EXCLUSIVE</c> lock.
/// </summary>
public partial class PgVectorCatalogSqlTests
{
    public static TheoryData<string, string> CatalogStatements() => new()
    {
        { nameof(SchemaStatsQueries.VectorColumnsSql), SchemaStatsQueries.VectorColumnsSql },
        { nameof(SchemaStatsQueries.IndexStatsSql), SchemaStatsQueries.IndexStatsSql },
        { "DatabaseCatalogQueries." + nameof(DatabaseCatalogQueries.ColumnsSql), DatabaseCatalogQueries.ColumnsSql },
        { "DatabaseCatalogQueries." + nameof(DatabaseCatalogQueries.IndexesSql), DatabaseCatalogQueries.IndexesSql },
    };

    [Theory]
    [MemberData(nameof(CatalogStatements))]
    public void Every_relation_the_statement_reads_is_a_system_catalog(string name, string sql)
    {
        string[] targets =
        [
            .. FromOrJoin().Matches(sql)
                .Select(static x => x.Groups[1].Value)
                .Where(static x => x is not "lateral" and not "listed" and not "rolled" and not "tree" && !x.StartsWith('(')),
        ];

        targets.Should().NotBeEmpty();
        targets.Should().OnlyContain(static x => x.StartsWith("pg_catalog.", StringComparison.Ordinal),
            name + " reads the catalog and nothing else - never a value of the table it describes");
    }

    /// <summary>
    /// The new statement locks nothing it lists. (The index read sizes each index it lists and the column read
    /// deparses defaults, as they always have; their own tests hold them to their own rules.)
    /// </summary>
    [Fact]
    public void The_vector_columns_read_calls_no_function_that_locks_or_sizes_what_it_lists()
    {
        LockingOrSizing().IsMatch(SchemaStatsQueries.VectorColumnsSql).Should().BeFalse();

        // The anti-vacuity half: the pattern still recognises what it is for.
        LockingOrSizing().IsMatch("pg_catalog.pg_relation_size(c.oid)").Should().BeTrue();
        LockingOrSizing().IsMatch("pg_catalog.pg_partition_tree(c.oid)").Should().BeTrue();
    }

    [Fact]
    public void The_vector_columns_read_is_scoped_to_the_stores_schemas_and_never_lists_a_partition()
    {
        string sql = SchemaStatsQueries.VectorColumnsSql;

        sql.Should().MatchRegex(@"nspname\s*=\s*any\(@schemas\)");
        sql.Should().Contain("not c.relispartition", "a partition is named after its tenant");
        sql.Should().Contain("c.relkind in ('r', 'p')");
        sql.Should().Contain("not a.attisdropped");
        SchemaStatsQueries.AllStatements.Should().Contain(sql);
    }

    [Fact]
    public void A_vector_type_is_the_vector_extensions_by_pg_depend_and_its_dimensions_are_the_type_modifier()
    {
        foreach (string sql in new[] { SchemaStatsQueries.VectorColumnsSql, DatabaseCatalogQueries.ColumnsSql })
        {
            sql.Should().Contain("d.classid = 'pg_catalog.pg_type'::pg_catalog.regclass");
            sql.Should().Contain("d.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass");
            sql.Should().Contain("d.deptype = 'e'");
            sql.Should().Contain("case when t.typtype = 'd' then t.typtypmod else a.atttypmod end",
                "a column of a domain over vector(3) has no type modifier of its own");
        }

        SchemaStatsQueries.VectorColumnsSql.Should().Contain("e.extname = 'vector'");
        DatabaseCatalogQueries.ColumnsSql.Should().Contain("e.extname::text", "the browser reads the owning extension for every column");
    }

    [Fact]
    public void An_index_read_names_its_access_method_and_whether_pgvector_owns_it()
    {
        foreach (string sql in new[] { SchemaStatsQueries.IndexStatsSql, DatabaseCatalogQueries.IndexesSql })
        {
            sql.Should().Contain("join pg_catalog.pg_am am on am.oid = ");
            sql.Should().Contain("d.classid = 'pg_catalog.pg_am'::pg_catalog.regclass");
            sql.Should().Contain("e.extname = 'vector'");
            sql.Should().Contain("in ('hnsw', 'ivfflat')", "operator classes and parameters are read for pgvector's two access methods only");
            sql.Should().Contain("oc.opcname::text");
            sql.Should().MatchRegex(@"case when [a-z]+\.amname in \('hnsw', 'ivfflat'\) then [a-z]+\.reloptions end",
                "an index's reloptions are read for pgvector's two access methods only");
            sql.Should().MatchRegex(@"pg_get_indexdef\([a-z.]+, k, true\)");
            sql.Should().MatchRegex(@"u\.ord <= [a-z]\.indnkeyatts", "an INCLUDE column has no operator class to show");
        }
    }

    [GeneratedRegex(@"\b(?:from|join)\s+([^\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FromOrJoin();

    [GeneratedRegex(@"pg_(?:total_)?relation_size|pg_indexes_size|pg_table_size|pg_partition_(?:tree|ancestors|root)|pg_sequence_last_value|pg_get_viewdef|pg_get_expr|pg_get_triggerdef", RegexOptions.IgnoreCase)]
    private static partial Regex LockingOrSizing();
}
