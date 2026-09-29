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
            "SchemasSql", "RelationsSql", "RoutinesSql", "TriggersSql", "SequencesSql", "TypesSql",
            "ForeignKeysSql", "ViewDependenciesSql", "ColumnsSql", "ConstraintsSql", "IndexesSql",
            "ViewDefinitionSql", "RoutineDefinitionSql", "TriggerDefinitionSql",
        ]);

        DatabaseCatalogQueries.ScopedStatements.Should().HaveCount(Statements.Count - 1 - DatabaseCatalogQueries.ByOidStatements.Count,
            "every statement is either the schema list, a scoped read or a by-oid read of a relation already found in scope");
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
            ["schemas", "q", "exact", "cap", "table", "oid", "arguments", "relation", "relation_schema"],
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
        foreach (string sql in DatabaseCatalogQueries.ListStatements)
        {
            SizeFunction().IsMatch(sql).Should().BeFalse(sql);
        }

        DatabaseCatalogQueries.RelationsSql.Should().Contain("relpages", "the size is the planner's own figure");
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

    [Fact]
    public void A_sequences_value_is_read_only_where_the_role_may_read_it()
    {
        DatabaseCatalogQueries.SequencesSql.Should().MatchRegex(
            @"when pg_catalog\.has_sequence_privilege\(c\.oid, 'SELECT,USAGE'\)\s+then pg_catalog\.pg_sequence_last_value");
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
        "any", "exists", "in", "coalesce", "greatest", "case", "deps", "u", "ordinality", "select", "and", "or",
        "not", "when", "then", "else", "is", "on", "as",
    ];

    [GeneratedRegex(@"\b(create|alter|drop|insert|update|delete|truncate|grant|revoke|comment\s+on|set\s+role|vacuum|analyze|refresh|lock)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DdlOrDml();

    [GeneratedRegex(@"@([a-z_]+)")]
    private static partial Regex Parameter();

    [GeneratedRegex(@"pg_(total_relation|relation|table|indexes|database)_size")]
    private static partial Regex SizeFunction();

    /// <summary>An identifier followed by an opening bracket, not preceded by a dot or a quote.</summary>
    [GeneratedRegex(@"(?<![.\w""])([a-z_]+)\s*\(")]
    private static partial Regex UnqualifiedCall();
}
