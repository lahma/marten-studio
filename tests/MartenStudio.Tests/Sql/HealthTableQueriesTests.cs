using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;

using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The shape of the tables-and-indexes health statements: a list locks nothing it lists, names travel as
/// values, and nothing creates anything.
/// </summary>
/// <remarks>
/// No database: these are built statements, and what is asserted is their text and their parameters. The
/// live sweep tests run them against a tenant-partitioned table with a second connection watching
/// <c>pg_locks</c>.
/// </remarks>
public partial class HealthTableQueriesTests
{
    private static readonly HealthTableTarget[] Targets =
    [
        new("studio", "mt_doc_order"),
        new("studio_events", "mt_events"),
    ];

    private static readonly string[] TargetSchemas = ["studio", "studio_events"];

    private static readonly string[] TargetTables = ["mt_doc_order", "mt_events"];

    private static readonly string[] SequenceNames = ["Invoice", "Order"];

    /// <summary>Every function that locks what it is handed, or that applies DDL, or that reads a sequence's state.</summary>
    [GeneratedRegex(
        @"\b(pg_partition_tree|pg_partition_ancestors|pg_partition_root|pg_relation_size|pg_total_relation_size|pg_table_size|pg_indexes_size|pg_sequence_last_value|pg_get_expr)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LockingFunction();

    /// <summary>Anything that writes, or reads the catalog the way that hides what the role cannot touch.</summary>
    [GeneratedRegex(
        @"\b(insert|update|delete|create|alter|drop|truncate|grant|vacuum|analyze|information_schema|nextval|setval)\b|select\s+\*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WritesOrHides();

    public static TheoryData<string> ListStatements() => [.. HealthTableQueries.ListStatements];

    [Theory]
    [MemberData(nameof(ListStatements))]
    public void A_list_statement_calls_no_function_that_locks_what_it_lists(string sql)
    {
        LockingFunction().IsMatch(sql).Should().BeFalse(
            "a size function opens the relation and queues behind a migration, and pg_partition_tree holds a lock on every partition (D27)");
    }

    [Theory]
    [MemberData(nameof(ListStatements))]
    public void A_list_statement_writes_nothing_and_selects_nothing_by_star(string sql)
    {
        WritesOrHides().IsMatch(sql).Should().BeFalse();
    }

    /// <summary>The anti-vacuity half: the scan sees a statement that does lock what it lists.</summary>
    [Fact]
    public void The_scan_catches_the_Schema_tabs_own_size_functions()
    {
        LockingFunction().IsMatch(SchemaStatsQueries.TableStatsSql).Should().BeTrue(
            "the Schema tab sizes its listed tables with pg_total_relation_size, which a health list may not");
        LockingFunction().IsMatch("select pg_catalog.pg_partition_tree('x'::regclass)").Should().BeTrue();
        WritesOrHides().IsMatch("select * from pg_catalog.pg_class").Should().BeTrue();
    }

    [Fact]
    public void The_tables_and_their_schemas_travel_as_two_text_arrays_and_no_name_is_in_the_text()
    {
        foreach (HealthQuery query in new[] { HealthTableQueries.Tables(Targets), HealthTableQueries.Indexes(Targets), HealthTableQueries.SoftDeleteShares(Targets) })
        {
            // The table read binds T08's line too (W5-V1 review), so each partition is judged against it in the statement.
            query.ParameterNames.Take(2).Should().Equal("@schemas", "@tables");
            query.Parameters[0].NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Text);
            query.Parameters[0].Value.Should().BeEquivalentTo(TargetSchemas);
            query.Parameters[1].Value.Should().BeEquivalentTo(TargetTables);
            query.Sql.Should().NotContain("mt_doc_order", "a hidden type's table name must never be in a statement Show SQL prints")
                .And.NotContain("studio_events");
        }
    }

    [Fact]
    public void Every_call_builds_a_fresh_query_with_parameters_of_its_own()
    {
        HealthQuery first = HealthTableQueries.Tables(Targets);
        HealthQuery second = HealthTableQueries.Tables(Targets);

        first.Parameters[0].Should().NotBeSameAs(second.Parameters[0], "an NpgsqlParameter belongs to one command");
    }

    [Fact]
    public void Every_list_statement_names_its_catalog_relations_with_their_schema()
    {
        Regex.Matches("select 1 from pg_class c join pg_catalog.pg_index x on true", @"\b(?:from|join)\s+(pg_[a-z_]+)\b(?!\.)", RegexOptions.IgnoreCase)
            .Should().ContainSingle("the scan finds an unqualified catalog relation and passes a qualified one");

        foreach (string sql in HealthTableQueries.ListStatements)
        {
            Regex.Matches(sql, @"\b(?:from|join)\s+(pg_[a-z_]+)\b(?!\.)", RegexOptions.IgnoreCase)
                .Should().BeEmpty("search_path is pinned to pg_catalog, and a statement says so itself:\n" + sql);
        }
    }

    [Fact]
    public void A_partitioned_parents_relfrozenxid_is_never_aged()
    {
        HealthTableQueries.TablesSql.Should().Contain("case when c.relkind = 'r' then pg_catalog.age(c.relfrozenxid) end",
            "age() of a partitioned parent's zero relfrozenxid is int.MaxValue, which would be a Critical T12 on every partitioned store");
        HealthTableQueries.TablesSql.Should().Contain("case when d.relkind = 'r' then pg_catalog.age(d.relfrozenxid) end");
    }

    /// <summary>
    /// T03's guard against calling a running build a failed one: both list statements read the progress view,
    /// which is shared memory and takes no lock, for this database's builds only, and count the ones whose table
    /// the role may not see.
    /// </summary>
    [Fact]
    public void Both_list_statements_read_the_index_builds_running_in_this_database()
    {
        foreach (string sql in new[] { HealthTableQueries.TablesSql, HealthTableQueries.IndexesSql })
        {
            sql.Should().Contain("from pg_catalog.pg_stat_progress_create_index p")
                .And.Contain("where p.datid = (select db.oid from pg_catalog.pg_database db where db.datname = pg_catalog.current_database())")
                .And.Contain("where b.relid is null", "a build whose table this role may not see is counted, never ignored");
        }

        HealthTableQueries.IndexesSql.Should().Contain("where b.index_relid = i.oid",
            "CREATE INDEX CONCURRENTLY reports the index it builds")
            .And.Contain("or (b.command = 'REINDEX CONCURRENTLY'")
            .And.Contain("and i.index_name ~ '_cc(new|old)[0-9]*$'",
                "REINDEX CONCURRENTLY's first phase reports the index it copies, not its _ccnew copy")
            .And.Contain("or (i.relkind = 'I'")
            .And.Contain("and b.command not like 'REINDEX%'",
                "only a partitioned index is matched by a build anywhere in its tree, and only by a CREATE INDEX (spelt so the "
                + "statement never contains the word the no-DDL scan looks for)");
    }

    /// <summary>
    /// The H4 re-review's case, pinned in the text: a build of another index on the same table - a nightly rolling
    /// REINDEX - is no reason an index is invalid, so the own-build match never matches "any build on the table".
    /// That broad match is read too, but only as the "let it finish first" caveat.
    /// </summary>
    [Fact]
    public void A_build_of_another_index_on_the_table_is_never_this_index_s_build()
    {
        string ownBuild = HealthTableQueries.IndexesSql[
            HealthTableQueries.IndexesSql.IndexOf("where b.index_relid = i.oid", StringComparison.Ordinal)..
            HealthTableQueries.IndexesSql.IndexOf("(select pg_catalog.count(*) from builds b where b.relid is null)", StringComparison.Ordinal)];

        ownBuild.Should().NotContain("or b.relid in", "a build anywhere on an ordinary table hid an older failed index for as long as it ran");

        string sql = HealthTableQueries.TablesSql;
        string partitions = sql[sql.IndexOf("tree_invalid as (", StringComparison.Ordinal)..sql.IndexOf("invalid_rolled as (", StringComparison.Ordinal)];
        partitions.Should().Contain("where b.index_relid = x.indexrelid")
            .And.Contain("and b.relid = d.oid")
            .And.NotContain("or b.relid in", "the same narrow match decides a partition's invalid index");
    }

    /// <summary>
    /// Postgres keeps no storage parameters on a partitioned table, so its are the leaves' agreement, in both the
    /// health read and the Schema tab's - and a disagreement travels by parameter name alone.
    /// </summary>
    [Fact]
    public void A_partitioned_table_s_storage_parameters_are_rolled_up_from_its_leaves()
    {
        foreach (string sql in new[] { HealthTableQueries.TablesSql, SchemaStatsQueries.TableStatsSql })
        {
            sql.Should().Contain("cross join lateral pg_catalog.unnest(d.reloptions) as o(opt)")
                .And.Contain("pg_catalog.count(distinct o.value) = 1 and pg_catalog.count(*) = r.leaves as agreed")
                .And.Contain("when l.relkind = 'p' then coalesce(orl.agreed, '{}'::text[])")
                .And.Contain("pg_catalog.array_agg(a.name order by a.name) filter (where not a.agreed) as differing");
        }
    }

    [Fact]
    public void The_hilo_existence_check_reads_the_catalog_and_binds_the_schema_and_the_table()
    {
        HealthQuery query = HealthTableQueries.HiloExists("studio");

        query.Sql.Should().Contain("pg_catalog.pg_class").And.NotContain("studio");
        query.ParameterNames.Should().Equal("@schema", "@table");
        query.Parameters[1].Value.Should().Be("mt_hilo");
    }

    [Fact]
    public void The_hilo_read_quotes_its_table_and_binds_the_sequence_names()
    {
        HealthQuery query = HealthTableQueries.HiloValues("studio", SequenceNames);

        query.Sql.Should().Contain("from \"studio\".\"mt_hilo\" as h").And.Contain("= any(@names)");
        query.Parameters.Single().Value.Should().BeEquivalentTo(SequenceNames);
        WritesOrHides().IsMatch(query.Sql).Should().BeFalse("reading mt_hilo never creates it - hard rule 14's own trap");
    }

    [Fact]
    public void A_name_with_a_quote_never_reaches_a_statement()
    {
        Action read = () => HealthTableQueries.HiloValues("stu\"dio", ["x"]);
        Action sample = () => HealthTableQueries.DocumentSizeSample("studio", "mt_doc_\"x", 1f, 10);

        read.Should().Throw<ArgumentException>();
        sample.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_deeper_sample_is_bounded_in_pages_and_rows_and_binds_both_bounds()
    {
        HealthQuery query = HealthTableQueries.DocumentSizeSample("studio", "mt_doc_order", 2.5f, 2_000);

        query.Sql.Should().Contain("from \"studio\".\"mt_doc_order\" as t tablesample system (@percent) limit @limit")
            .And.Contain("pg_catalog.pg_column_size(d.data)");
        query.ParameterNames.Should().Equal("@percent", "@limit");

        NpgsqlParameter percent = query.Parameters[0];
        percent.NpgsqlDbType.Should().Be(NpgsqlDbType.Real, "TABLESAMPLE SYSTEM takes a float4 and does not coerce a float8");
        percent.Value.Should().Be(2.5f);
        query.Parameters[1].Value.Should().Be(2_000);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(100.5f)]
    public void A_sample_outside_zero_to_a_hundred_percent_is_refused(float percent)
    {
        Action build = () => HealthTableQueries.DocumentSizeSample("studio", "mt_doc_order", percent, 10);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// G1M: MartenStudio's list of the tables beside the store is Postgres Studio's statement and two predicates more -
    /// the same admission the database browser makes, the same estimates and cap - so the two cannot drift apart.
    /// </summary>
    [Fact]
    public void The_undeclared_tables_statement_is_the_relational_one_with_two_exclusions_and_nothing_else()
    {
        const string Exclusions =
            "      and not (@skip_documents and pg_catalog.starts_with(pg_catalog.lower(c.relname::text), @document_prefix))\n"
            + "      and not exists (\n"
            + "          select 1\n"
            + "          from rows from (pg_catalog.unnest(@declared_schemas), pg_catalog.unnest(@declared_tables)) as x(schema_name, table_name)\n"
            + "          where pg_catalog.lower(x.schema_name) = pg_catalog.lower(a.nspname::text)\n"
            + "            and pg_catalog.lower(x.table_name) = pg_catalog.lower(c.relname::text))\n";

        HealthTableQueries.UndeclaredTablesSql.Should().Contain(Exclusions);
        HealthTableQueries.UndeclaredTablesSql.Replace(Exclusions, string.Empty, StringComparison.Ordinal)
            .Should().Be(HealthTableQueries.RelationalTablesSql);
        HealthTableQueries.DocumentTablePrefix.Should().Be(DatabaseObjectClassifier.DocumentTablePrefix);
    }

    [Fact]
    public void The_undeclared_tables_statement_binds_every_name_and_names_none()
    {
        HealthQuery query = HealthTableQueries.UndeclaredTables(
            ["studio", "quartz"], everything: false, Targets, skipDocumentTables: true, 1_990);

        query.Sql.Should().NotContain("studio").And.NotContain("quartz").And.NotContain("mt_doc_order").And.Contain("limit @cap");
        query.Parameters.Select(static x => x.ParameterName).Should().BeEquivalentTo(
            ["names", "everything", "cap", "system", "skip_documents", "document_prefix", "declared_schemas", "declared_tables"]);
        query.Parameters.Single(static x => x.ParameterName == "declared_schemas").Value.Should().BeEquivalentTo(TargetSchemas);
        query.Parameters.Single(static x => x.ParameterName == "declared_tables").Value.Should().BeEquivalentTo(TargetTables);
        query.Parameters.Single(static x => x.ParameterName == "skip_documents").Value.Should().Be(true);
        query.Parameters.Single(static x => x.ParameterName == "cap").Value.Should().Be(1_990);

        Action none = () => HealthTableQueries.UndeclaredTables(["studio"], false, [], false, 0);
        none.Should().Throw<ArgumentOutOfRangeException>("a sweep whose declared tables fill the cap reads no list at all");
    }
}
