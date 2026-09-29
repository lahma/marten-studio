using System.Reflection;
using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Acceptance 6: the database browser's SQL has the shape the plan requires, read off the statements
/// themselves - every one of them, found by reflection so a new statement cannot slip past.
/// </summary>
public partial class DatabaseCatalogQueriesTests
{
    /// <summary>Every <c>const string …Sql</c> on the class, by name.</summary>
    private static readonly Dictionary<string, string> Statements = typeof(DatabaseCatalogQueries)
        .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
        .Where(static x => x.IsLiteral && x.FieldType == typeof(string) && x.Name.EndsWith("Sql", StringComparison.Ordinal))
        .ToDictionary(static x => x.Name, static x => (string) x.GetRawConstantValue()!);

    public static TheoryData<string> EveryStatement() => [.. Statements.Keys.Order(StringComparer.Ordinal)];

    [Fact]
    public void The_reflection_finds_every_statement()
    {
        Statements.Keys.Should().BeEquivalentTo(
        [
            "PinSearchPathSql", "SchemasSql", "RelationsSql", "RoutinesSql", "TriggersSql", "SequencesSql",
            "SequenceValueSql", "TypesSql", "ForeignKeysSql", "ViewDependenciesSql", "ViewReferencesSql",
            "ObjectCountsSql", "ColumnsSql", "ConstraintsSql", "IndexesSql", "ViewDefinitionSql",
            "RoutineDefinitionSql", "TriggerDefinitionSql",
        ]);

        DatabaseCatalogQueries.ScopedStatements.Should().HaveCount(
            Statements.Count - 1 - DatabaseCatalogQueries.ByOidStatements.Count - DatabaseCatalogQueries.SessionStatements.Count,
            "every statement is either the schema list, a session setting, a scoped read or a by-oid read of a " +
            "relation already found in scope");
    }

    /// <summary>
    /// <c>nspname = any(@schemas)</c> in every read but the schema list itself, and the by-oid reads, whose
    /// oid came out of a lookup that was.
    /// </summary>
    [Fact]
    public void Every_scoped_statement_filters_on_the_schema_set()
    {
        foreach (string sql in DatabaseCatalogQueries.ScopedStatements)
        {
            sql.Should().MatchRegex(@"nspname\s*=\s*any\(@schemas\)");
        }

        foreach (string sql in DatabaseCatalogQueries.ByOidStatements)
        {
            sql.Should().Contain("= @oid");
        }

        DatabaseCatalogQueries.SchemasSql.Should().NotContain("@", "the schema list takes nothing at all");

        foreach (string sql in DatabaseCatalogQueries.SessionStatements)
        {
            sql.Should().NotContain("@", "a session setting takes no value from anybody");
        }
    }

    /// <summary>
    /// B1: every deparsed name must come out schema-qualified, so the search path is pinned to
    /// <c>pg_catalog</c> - locally, for the transaction the read-only session rolls back.
    /// </summary>
    [Fact]
    public void The_search_path_is_pinned_to_pg_catalog_for_the_transaction_only()
    {
        DatabaseCatalogQueries.PinSearchPathSql.Should().Contain("pg_catalog.set_config('search_path', 'pg_catalog', true)");
    }

    [Theory]
    [MemberData(nameof(EveryStatement))]
    public void No_statement_carries_DDL_or_DML(string name)
    {
        string sql = Statements[name];

        DdlOrDml().IsMatch(sql).Should().BeFalse(name + " is a catalog read and nothing else");
    }

    /// <summary>
    /// No value is ever interpolated: no <c>{</c> placeholders, no string concatenation built on a
    /// parameter, and every <c>@name</c> is one of the parameters the readers bind.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStatement))]
    public void No_statement_interpolates_a_value(string name)
    {
        string sql = Statements[name];

        sql.Should().NotContain("{").And.NotContain("}");

        string[] parameters = [.. Parameter().Matches(sql).Select(static x => x.Groups[1].Value).Distinct()];

        parameters.Should().BeSubsetOf(
            ["schemas", "q", "exact", "cap", "table", "oid", "arguments", "relation", "relation_schema", "hidden"],
            name + " may only use the parameters its reader binds");
    }

    [Theory]
    [MemberData(nameof(EveryStatement))]
    public void No_statement_reads_information_schema(string name) =>
        Statements[name].Should().NotContain("information_schema.",
            "information_schema hides what the role has no privilege on, which is the one thing the browser must show");

    /// <summary>A size function stats every file and takes a lock per relation; a list on "*" must not.</summary>
    [Fact]
    public void No_list_statement_calls_a_size_function()
    {
        foreach (string sql in DatabaseCatalogQueries.LockFreeStatements)
        {
            SizeFunction().IsMatch(sql).Should().BeFalse(sql);
        }

        DatabaseCatalogQueries.RelationsSql.Should().Contain("relpages", "the size is the planner's own figure");
    }

    /// <summary>
    /// B1: a list takes no lock on anything it lists. <c>pg_partition_tree</c> (and its two siblings) lock
    /// every partition through <c>find_all_inheritors</c>, and <c>pg_sequence_last_value</c> takes a
    /// <c>RowExclusiveLock</c> per sequence through <c>init_sequence</c> - all held until the snapshot's one
    /// transaction ends. <c>DatabaseCatalogLockLiveTests</c> measures the same thing in <c>pg_locks</c>.
    /// </summary>
    [Fact]
    public void No_list_or_count_statement_calls_a_function_that_locks_what_it_describes()
    {
        DatabaseCatalogQueries.LockFreeStatements.Should().Contain(DatabaseCatalogQueries.ObjectCountsSql)
            .And.Contain(DatabaseCatalogQueries.ListStatements);

        foreach (string sql in DatabaseCatalogQueries.LockFreeStatements)
        {
            LockingFunction().IsMatch(sql).Should().BeFalse(
                "a list reads every part in one transaction, so a lock per object piles up until it ends: " + sql);
        }

        // The anti-vacuity half: the pattern still recognises every one of the four spellings.
        foreach (string call in new[]
                 {
                     "pg_catalog.pg_partition_tree(c.oid)", "pg_partition_ancestors(x)", "pg_catalog.pg_partition_root(x)",
                     "pg_catalog.pg_sequence_last_value(c.oid::pg_catalog.regclass)",
                 })
        {
            LockingFunction().IsMatch(call).Should().BeTrue(call);
        }
    }

    [Fact]
    public void A_partitioned_tables_totals_are_walked_through_pg_inherits()
    {
        DatabaseCatalogQueries.RelationsSql.Should().Contain("with recursive tree")
            .And.Contain("join pg_catalog.pg_inherits i on i.inhparent = tree.relid");
    }

    [Fact]
    public void Every_list_is_bounded_and_filtered_by_name()
    {
        foreach (string sql in DatabaseCatalogQueries.ListStatements)
        {
            sql.Should().Contain("limit @cap");
        }

        foreach (string sql in new[]
                 {
                     DatabaseCatalogQueries.RelationsSql, DatabaseCatalogQueries.RoutinesSql,
                     DatabaseCatalogQueries.TriggersSql, DatabaseCatalogQueries.SequencesSql,
                     DatabaseCatalogQueries.TypesSql,
                 })
        {
            sql.Should().Contain("pg_catalog.strpos(pg_catalog.lower(").And.Contain("pg_catalog.lower(@q)) > 0");
        }
    }

    [Fact]
    public void Triggers_are_the_ones_a_person_wrote_and_never_a_partitions_clone()
    {
        DatabaseCatalogQueries.TriggersSql.Should().Contain("t.tgparentid = 0").And.Contain("not t.tgisinternal");
        DatabaseCatalogQueries.TriggerDefinitionSql.Should().Contain("t.tgparentid = 0");
    }

    [Fact]
    public void Index_key_columns_stop_at_indnkeyatts_so_an_INCLUDE_column_is_never_a_key()
    {
        DatabaseCatalogQueries.IndexesSql.Should().Contain("u.ord <= i.indnkeyatts");
    }

    [Fact]
    public void Partitions_and_extension_members_are_never_listed()
    {
        DatabaseCatalogQueries.RelationsSql.Should().Contain("not c.relispartition");
        DatabaseCatalogQueries.ForeignKeysSql.Should().Contain("con.conparentid = 0")
            .And.Contain("not cl.relispartition").And.Contain("not fcl.relispartition");

        foreach (string sql in new[]
                 {
                     DatabaseCatalogQueries.RelationsSql, DatabaseCatalogQueries.RoutinesSql,
                     DatabaseCatalogQueries.TriggersSql, DatabaseCatalogQueries.SequencesSql,
                     DatabaseCatalogQueries.TypesSql, DatabaseCatalogQueries.ViewDefinitionSql,
                     DatabaseCatalogQueries.RoutineDefinitionSql, DatabaseCatalogQueries.TriggerDefinitionSql,
                 })
        {
            sql.Should().MatchRegex(@"d\.deptype (= 'e'|in \('e', 'i'\))",
                "a definition exists only for what a list can show, and neither shows an extension's objects");
        }

        DatabaseCatalogQueries.RoutinesSql.Should().Contain("d.deptype in ('e', 'i')",
            "a range type's constructors are Postgres' internal dependents, not functions anybody wrote");
    }

    /// <summary>
    /// B1: the value is read for one sequence, by exact name, and only where the role may read it - never
    /// by the list, which says only whether it could be asked for.
    /// </summary>
    [Fact]
    public void A_sequences_value_is_read_on_demand_for_one_sequence_and_only_where_the_role_may_read_it()
    {
        DatabaseCatalogQueries.SequenceValueSql.Should().MatchRegex(
            @"when pg_catalog\.has_sequence_privilege\(c\.oid, 'SELECT,USAGE'\)\s+then pg_catalog\.pg_sequence_last_value");
        DatabaseCatalogQueries.SequenceValueSql.Should().Contain("c.relname::text = @exact")
            .And.NotContain("strpos", "a value is read by exact name and never by a filter")
            .And.NotContain("limit", "one name in one schema is one sequence");

        DatabaseCatalogQueries.SequencesSql.Should().NotContain("pg_sequence_last_value");
        DatabaseCatalogQueries.ListStatements.Should().NotContain(DatabaseCatalogQueries.SequenceValueSql);
    }

    /// <summary>
    /// F3: what a view calls is followed as well as what it reads - directly, through an operator, and
    /// through other views - and Postgres' own functions are never counted.
    /// </summary>
    [Fact]
    public void View_references_follow_functions_operators_sequences_and_types_through_other_views()
    {
        string sql = DatabaseCatalogQueries.ViewReferencesSql;

        sql.Should().Contain("with recursive walk")
            .And.Contain("'pg_catalog.pg_proc'::pg_catalog.regclass")
            .And.Contain("join pg_catalog.pg_proc p on p.oid = o.oprcode", "an operator is recorded as the operator, not its function")
            .And.Contain("'pg_catalog.pg_type'::pg_catalog.regclass")
            .And.Contain("s.relkind = 'S'")
            .And.Contain("n.nspname <> 'pg_catalog'")
            .And.Contain("n.nspname <> 'information_schema'")
            .And.Contain("e.deptype = 'e'", "an extension's function is not code somebody wrote here");
    }

    /// <summary>
    /// F6: the overview's counts are one grouped query, filtered the way each list is, with the hidden
    /// tables and the per-tenant event sequences left out as a list leaves them out.
    /// </summary>
    [Fact]
    public void The_counts_are_grouped_by_schema_and_filtered_like_the_lists()
    {
        string sql = DatabaseCatalogQueries.ObjectCountsSql;

        foreach (string kind in new[] { "'tables'", "'views'", "'functions'", "'triggers'", "'sequences'", "'types'" })
        {
            sql.Should().Contain(kind);
        }

        Regex.Matches(sql, "group by n.nspname").Should().HaveCount(6, "one count per kind, per schema");
        sql.Should().Contain("not c.relispartition")
            .And.Contain("t.tgparentid = 0")
            .And.Contain("d.deptype in ('e', 'i')")
            .And.Contain("<> all(@hidden)")
            .And.Contain("= any(@hidden)", "a sequence one of a hidden table's columns owns is not counted")
            .And.Contain("'mt_events_sequence_'", "the per-tenant event sequences roll up and are not counted");
        sql.Should().NotContain("limit", "the answer is as small as the schema set");
    }

    /// <summary>F1: a trigger's definition comes with its function's schema, so a withheld one can be refused.</summary>
    [Fact]
    public void A_triggers_definition_is_read_with_its_functions_schema()
    {
        DatabaseCatalogQueries.TriggerDefinitionSql.Should().Contain("fn.nspname::text")
            .And.Contain("join pg_catalog.pg_proc p on p.oid = t.tgfoid");
    }

    /// <summary>
    /// A privilege function answers NULL for an object dropped mid-read; a column read as a boolean must
    /// never be that NULL.
    /// </summary>
    [Fact]
    public void Every_privilege_answer_read_as_a_column_is_coalesced_for_an_object_dropped_mid_read()
    {
        DatabaseCatalogQueries.SchemasSql.Should().Contain("coalesce(pg_catalog.has_schema_privilege(n.oid, 'USAGE'), false)");
        DatabaseCatalogQueries.RelationsSql.Should().MatchRegex(
            @"coalesce\(\s+pg_catalog\.has_schema_privilege\(n\.oid, 'USAGE'\)\s+and pg_catalog\.has_any_column_privilege\(c\.oid, 'SELECT'\),\s+false\)");
        DatabaseCatalogQueries.SequencesSql.Should().Contain(
            "coalesce(pg_catalog.has_sequence_privilege(c.oid, 'SELECT,USAGE'), false)");
    }

    [Fact]
    public void Types_are_enums_domains_ranges_and_standalone_composites()
    {
        DatabaseCatalogQueries.TypesSql.Should().Contain("t.typtype in ('e', 'd', 'r', 'c')")
            .And.Contain("tc.relkind = 'c'")
            .And.Contain("order by e.enumsortorder");
    }

    [Fact]
    public void An_aggregates_body_is_never_asked_for()
    {
        DatabaseCatalogQueries.RoutineDefinitionSql.Should().Contain(
            "case when p.prokind = 'a' then null else pg_catalog.pg_get_functiondef(p.oid) end");
    }

    [Fact]
    public void View_dependencies_are_followed_through_pg_rewrite_and_pg_depend_recursively()
    {
        DatabaseCatalogQueries.ViewDependenciesSql.Should().Contain("with recursive")
            .And.Contain("pg_catalog.pg_rewrite").And.Contain("pg_catalog.pg_depend");
    }

    /// <summary>Every call to a built-in function is qualified, so a role's search_path cannot stand in one.</summary>
    [Theory]
    [MemberData(nameof(EveryStatement))]
    public void Built_in_functions_are_pg_catalog_qualified(string name)
    {
        string sql = Statements[name];

        foreach (Match call in UnqualifiedCall().Matches(sql))
        {
            string function = call.Groups[1].Value;

            KeywordsThatLookLikeCalls.Should().Contain(function,
                name + " calls " + function + "() without pg_catalog.");
        }
    }

    [Theory]
    [InlineData("quartz", true)]
    [InlineData("Legacy Schema", true)]
    [InlineData("bad\"name", false)]
    [InlineData("nul\0name", false)]
    [InlineData("   ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Quotable_is_exactly_what_SqlIdentifier_Quote_accepts(string? name, bool quotable) =>
        DatabaseCatalogQueries.IsQuotable(name).Should().Be(quotable);

    /// <summary>The words a regex for "name(" catches that are SQL syntax rather than functions.</summary>
    private static readonly string[] KeywordsThatLookLikeCalls =
    [
        "any", "all", "exists", "in", "coalesce", "greatest", "case", "deps", "u", "ordinality", "select", "and",
        "or", "not", "when", "then", "else", "is", "on", "as", "filter", "from",

        // Common table expressions, named with their columns: tree (root, relid) as (...).
        "tree", "partitioned", "walk", "refs", "named",
    ];

    [GeneratedRegex(@"\b(create|alter|drop|insert|update|delete|truncate|grant|revoke|comment\s+on|set\s+role|vacuum|analyze|refresh|lock)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DdlOrDml();

    [GeneratedRegex(@"@([a-z_]+)")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"pg_(total_relation|relation|table|indexes|database)_size")]
    private static partial Regex SizeFunction();

    [GeneratedRegex(@"pg_partition_(tree|ancestors|root)|pg_sequence_last_value")]
    private static partial Regex LockingFunction();

    /// <summary>An identifier followed by an opening bracket, not preceded by a dot or a quote.</summary>
    [GeneratedRegex(@"(?<![.\w""])([a-z_]+)\s*\(")]
    private static partial Regex UnqualifiedCall();
}
