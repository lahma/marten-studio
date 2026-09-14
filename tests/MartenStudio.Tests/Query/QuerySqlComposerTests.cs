using JasperFx.MultiTenancy;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The statement behind a Marten <c>where</c> clause - the studio's own statement, not Marten's.
/// </summary>
/// <remarks>
/// <para>
/// Mode A used to hand the clause to <c>session.QueryAsync(type, clause)</c>, and these tests used to
/// assert that the studio <em>imitated</em> what Marten composed for it. That was the bug: Marten's
/// string-query path composes <c>select … from &lt;table&gt; as d &lt;clause&gt;</c> and nothing else - no
/// tenant filter, no soft-delete filter - so a clause of <c>where 1 = 1</c> run by somebody scoped to one
/// tenant returned every tenant's rows and every soft-deleted row. The studio now composes and runs its
/// own statement, so what these tests pin is that the tenant and soft-delete predicates are there, that
/// they come <em>before</em> the visitor's parenthesised predicate, and that the text is the text that
/// runs.
/// </para>
/// </remarks>
public class QuerySqlComposerTests
{
    private const string Qualified = "\"studio\".\"mt_doc_person\"";

    /// <summary>
    /// The visitor's predicate as the composer writes it: parenthesised, indented, and with the closing
    /// bracket on a line of its own so that a clause ending in a <c>--</c> comment does not eat it.
    /// </summary>
    private static string Predicate(string predicate) => "and (\n    " + predicate + "\n  )";

    /// <summary>A document table, tenanted and soft-deleted to taste.</summary>
    private static DocumentTableInfo Table(bool conjoined = false, bool softDeleted = false)
    {
        List<DocumentMetadataColumnInfo> metadata = [];

        if (softDeleted)
        {
            metadata.Add(new DocumentMetadataColumnInfo(DocumentMetadataColumn.IsSoftDeleted, "mt_deleted"));
            metadata.Add(new DocumentMetadataColumnInfo(DocumentMetadataColumn.SoftDeletedAt, "mt_deleted_at"));
        }

        if (conjoined)
        {
            metadata.Add(new DocumentMetadataColumnInfo(DocumentMetadataColumn.TenantId, "tenant_id"));
        }

        return new DocumentTableInfo
        {
            Schema = "studio",
            Table = "mt_doc_person",
            Alias = "person",
            TenancyStyle = conjoined ? TenancyStyle.Conjoined : TenancyStyle.Single,
            SoftDeleteEnabled = softDeleted,
            MetadataColumns = metadata,
        };
    }

    // ------------------------------------------------------------------------------------------------
    // The shape of the composed statement
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_where_clause_becomes_a_parenthesised_predicate_on_the_studios_own_select()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), "where data ->> 'Name' = 'Alice'", 50);

        composed.Statement.Should().Be(
            """
            select d."id", d."data"::text
            from "studio"."mt_doc_person" as d
            where 1 = 1
              and (
                data ->> 'Name' = 'Alice'
              )
            limit @limit
            """.ReplaceLineEndings("\n"));

        composed.Predicate.Should().Be("data ->> 'Name' = 'Alice'", "the leading `where` is normalised away");
        composed.Limit.Should().Be(50);
        composed.LimitApplied.Should().BeTrue();
    }

    [Fact]
    public void A_bare_predicate_needs_no_where_to_be_normalised()
    {
        QuerySqlComposer.Compose(Table(), "d.data ->> 'Name' = 'Alice'", 50).Predicate
            .Should().Be("d.data ->> 'Name' = 'Alice'");
    }

    [Fact]
    public void An_empty_clause_composes_a_statement_with_no_predicate_term_at_all()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), null, 10);

        composed.Statement.Should().Be(
            """
            select d."id", d."data"::text
            from "studio"."mt_doc_person" as d
            where 1 = 1
            limit @limit
            """.ReplaceLineEndings("\n"));

        composed.Predicate.Should().BeEmpty();
    }

    /// <summary>
    /// The one that was live-proven broken. A conjoined, soft-deleted collection read as one tenant has to
    /// carry both predicates, and they have to come <em>before</em> the visitor's - a parenthesised
    /// predicate cannot reach around an <c>and</c> that precedes it, however much <c>or</c> is inside it.
    /// </summary>
    [Fact]
    public void A_conjoined_soft_deleted_table_carries_both_predicates_in_front_of_the_visitors()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(
            Table(conjoined: true, softDeleted: true), "where 1 = 1 or true", 50, "acme");

        composed.Statement.Should().Be(
            """
            select d."id", d."data"::text, d."tenant_id", d."mt_deleted"
            from "studio"."mt_doc_person" as d
            where 1 = 1
              and d."tenant_id" = @tenant
              and d."mt_deleted" = false
              and (
                1 = 1 or true
              )
            limit @limit
            """.ReplaceLineEndings("\n"));

        composed.TenantId.Should().Be("acme");
        composed.CrossTenant.Should().BeFalse();
        composed.Deleted.Should().Be(DeletedFilter.Exclude);
        composed.TenantOrdinal.Should().Be(2);
        composed.DeletedOrdinal.Should().Be(3);
    }

    /// <summary>
    /// Even a clause naming another tenant cannot widen the read: the studio's own predicate is still
    /// <c>and</c>-ed in front of it, so the two together match nothing.
    /// </summary>
    [Fact]
    public void A_clause_naming_another_tenant_is_anded_with_the_scopes_own_tenant_and_not_instead_of_it()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(
            Table(conjoined: true), "where d.tenant_id = 'globex'", 50, "acme");

        composed.Statement.Should()
            .Contain("and d.\"tenant_id\" = @tenant\n")
            .And.Contain(Predicate("d.tenant_id = 'globex'"));

        composed.Parameters.Should().ContainSingle(x => x.Name == "tenant")
            .Which.Value.Should().Be("acme");
    }

    [Fact]
    public void A_conjoined_table_read_with_no_tenant_in_scope_says_so_rather_than_filtering()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(conjoined: true), null, 50, tenantId: null);

        composed.Statement.Should().NotContain("@tenant");
        composed.CrossTenant.Should().BeTrue("the results header has to say the rows come from every tenant");
        composed.TenantId.Should().BeNull();
    }

    [Fact]
    public void A_single_tenant_table_is_never_filtered_by_the_scopes_tenant()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), null, 50, "acme");

        composed.Statement.Should().NotContain("@tenant", "there is no tenant_id column to filter on");
        composed.CrossTenant.Should().BeFalse();
    }

    [Theory]
    [InlineData(nameof(DeletedFilter.Exclude), "and d.\"mt_deleted\" = false")]
    [InlineData(nameof(DeletedFilter.Only), "and d.\"mt_deleted\" = true")]
    public void The_soft_delete_predicate_follows_the_tri_state(string filter, string expected)
    {
        var parsed = Enum.Parse<DeletedFilter>(filter);

        QuerySqlComposer.Compose(Table(softDeleted: true), null, 50, null, parsed).Statement
            .Should().Contain(expected);
    }

    [Fact]
    public void Including_deleted_emits_no_predicate_but_still_selects_the_column()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(
            Table(softDeleted: true), null, 50, null, DeletedFilter.Include);

        composed.Statement.Should().NotContain("mt_deleted\" =");
        composed.Statement.Should().Contain("d.\"mt_deleted\"\n", "every row still says whether it is deleted");
        composed.DeletedOrdinal.Should().Be(2, "there is no tenant_id column in front of it here");
    }

    [Fact]
    public void A_table_that_is_not_soft_deleted_ignores_the_tri_state_entirely()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), null, 50, null, DeletedFilter.Only);

        composed.Statement.Should().NotContain("mt_deleted");
        composed.Deleted.Should().Be(DeletedFilter.Exclude);
        composed.DeletedOrdinal.Should().Be(-1);
    }

    /// <summary>
    /// Fail closed. A table the mapping calls conjoined but that has no <c>tenant_id</c> column cannot be
    /// read honestly, and composing without the predicate is precisely the leak this file exists to stop.
    /// </summary>
    [Fact]
    public void A_conjoined_table_with_no_tenant_column_refuses_to_compose()
    {
        var broken = new DocumentTableInfo
        {
            Schema = "studio",
            Table = "mt_doc_person",
            Alias = "person",
            TenancyStyle = TenancyStyle.Conjoined,
        };

        Action compose = () => QuerySqlComposer.Compose(broken, null, 50, "acme");

        compose.Should().Throw<ArgumentException>().WithMessage("*tenant*");
    }

    [Fact]
    public void The_table_is_quoted_and_the_clause_never_is()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), "where a = 'x'", 5);

        composed.Statement.Should().Contain(Qualified);
        composed.Statement.Should().Contain(Predicate("a = 'x'"));
    }

    // ------------------------------------------------------------------------------------------------
    // order by / limit / offset - split off deterministically rather than left in the predicate
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_trailing_order_by_is_lifted_out_of_the_predicate_and_placed_on_the_statement()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), "where age > 30 order by d.id desc", 25);

        composed.Statement.Should().Be(
            """
            select d."id", d."data"::text
            from "studio"."mt_doc_person" as d
            where 1 = 1
              and (
                age > 30
              )
            order by d.id desc
            limit @limit
            """.ReplaceLineEndings("\n"));

        composed.OrderBy.Should().Be("d.id desc");
    }

    [Fact]
    public void A_clause_that_is_only_an_order_by_has_no_predicate_term()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), "order by mt_last_modified desc", 25);

        composed.Predicate.Should().BeEmpty();
        composed.OrderBy.Should().Be("mt_last_modified desc");
        composed.Statement.Should().Contain("order by mt_last_modified desc\nlimit @limit");
    }

    /// <summary>
    /// A <c>limit</c> the visitor wrote is honoured, and clamped <em>down</em> to the studio's cap. Never
    /// up: the Rows selector is a server-side clamp (plan §4.8), and a clause is not a way past it.
    /// </summary>
    [Theory]
    [InlineData("where age > 30 limit 3", 3, false)]
    [InlineData("where age > 30 limit 10000", 25, false)]
    [InlineData("where age > 30", 25, true)]
    public void A_limit_in_the_clause_is_clamped_down_and_never_up(string clause, int expected, bool applied)
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), clause, 25);

        composed.Limit.Should().Be(expected);
        composed.LimitApplied.Should().Be(applied);
        composed.Parameters.Should().ContainSingle(x => x.Name == "limit").Which.Value.Should().Be(expected);
    }

    [Fact]
    public void An_offset_is_bound_as_a_parameter_too()
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), "where a = 1 order by b limit 5 offset 10", 50);

        composed.Statement.Should().EndWith("order by b\nlimit @limit\noffset @offset");
        composed.Offset.Should().Be(10);
        composed.Parameters.Select(x => x.Name).Should().Equal("limit", "offset");
    }

    /// <summary>
    /// The split is at parenthesis depth zero, which is what keeps a window function, an ordered aggregate
    /// and a subquery's own <c>limit</c> in the predicate where they belong.
    /// </summary>
    [Theory]
    [InlineData("where rank() over (order by d.id) = 1")]
    [InlineData("where d.id in (select id from t limit 5)")]
    [InlineData("where (data ->> 'x') = (select x from t offset 1)")]
    public void A_nested_order_by_or_limit_is_not_a_split_point(string clause)
    {
        ComposedQuery composed = QuerySqlComposer.Compose(Table(), clause, 50);

        composed.OrderBy.Should().BeNull();
        composed.Statement.Should().Contain(Predicate(clause["where ".Length..]));
    }

    [Fact]
    public void A_column_called_order_is_not_a_split_point_either()
    {
        QuerySqlComposer.TryPartition("where \"order\" > 5", out ClauseParts parts, out _).Should().BeTrue();

        parts.Predicate.Should().Be("\"order\" > 5");
        parts.OrderBy.Should().BeNull();
    }

    [Theory]
    [InlineData("where a = 1 limit 5 limit 6", "limit")]
    [InlineData("where a = 1 limit all", "limit")]
    [InlineData("where a = 1 limit 5 order by b", "order by")]
    [InlineData("where a = 1 offset -1", "offset")]
    [InlineData("where a = 1 order by", "order by")]
    public void A_tail_the_composer_cannot_place_is_refused_by_name(string clause, string token)
    {
        QuerySqlComposer.TryPartition(clause, out _, out SqlGuardResult rejection).Should().BeFalse(clause);

        rejection.Allowed.Should().BeFalse();
        rejection.Token.Should().Be(token);
        rejection.Position.Should().BeGreaterThanOrEqualTo(0);

        QuerySqlComposer.CheckClause(clause, allowNestedReads: false).Allowed.Should().BeFalse(
            "the guard asks the same question, so the page refuses before anything is sent");
        QuerySqlComposer.CheckClause(clause, allowNestedReads: true).Allowed.Should().BeFalse(
            "RunSql lifts the nested-read rules, not the rule that the tail has to be placeable");
    }

    // ------------------------------------------------------------------------------------------------
    // The clause guard. Mode A needs no capability, so this is the thing that keeps it from being one.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The clause runs outside the console's read-only transaction, so a second statement in it would
    /// execute. <c>1 = 1; drop table x</c> is one Npgsql command holding two statements, and Postgres runs
    /// both.
    /// </summary>
    [Theory]
    [InlineData("where a = 1; drop table x")]
    [InlineData("a = 1;")]
    [InlineData("where a = 1 /* */; delete from x")]
    [InlineData("where a = 'unterminated")]
    public void A_clause_carrying_a_second_statement_is_refused(string clause)
    {
        SqlGuardResult refused = QuerySqlComposer.CheckClause(clause, allowNestedReads: false);

        refused.Allowed.Should().BeFalse();
        refused.Message.Should().Contain("MartenStudioOptions.Capabilities.RunSql");
        refused.Position.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// The two shape rules hold even for somebody who may run SQL: Mode A does not run inside the console's
    /// read-only transaction, so a second statement here would really write - which is more than
    /// <c>RunSql</c> grants anywhere else.
    /// </summary>
    [Theory]
    [InlineData("where a = 1; drop table x")]
    [InlineData("select * from pg_authid")]
    public void The_shape_rules_hold_even_with_RunSql(string clause)
    {
        QuerySqlComposer.CheckClause(clause, allowNestedReads: true).Allowed.Should().BeFalse(clause);
    }

    [Theory]
    [InlineData("select * from pg_authid")]
    [InlineData("  SELECT 1")]
    [InlineData("with x as (select 1) select * from x")]
    [InlineData("delete from mt_doc_person")]
    [InlineData("set role postgres")]
    [InlineData("copy x from '/etc/passwd'")]
    public void A_clause_that_is_a_statement_of_its_own_is_refused(string clause)
    {
        QuerySqlComposer.CheckClause(clause, allowNestedReads: false).Allowed.Should().BeFalse(clause);
    }

    // ------------------------------------------------------------------------------------------------
    // Reading outside the collection. Refused without RunSql; left alone with it.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a filter on a collection looks like: the table's own columns, its JSON, and ordinary SQL over
    /// both. None of it reads anything the visitor could not already see by opening the collection.
    /// </summary>
    [Theory]
    [InlineData("where data ->> 'Name' = 'x'")]
    [InlineData("where d.data @> '{\"a\":1}'")]
    [InlineData("where mt_last_modified > now() - interval '1 day'")]
    [InlineData("where (data ->> 'Age')::int > 3 and d.id is not null")]
    [InlineData("where lower(data ->> 'Name') like 'a%'")]
    [InlineData("where name = 'it''s; fine'")]
    [InlineData("order by mt_last_modified desc")]
    [InlineData("age > 30 and name like 'A%'")]
    [InlineData("where age > 30 order by age desc limit 5 offset 2")]
    [InlineData(null)]
    [InlineData("")]
    public void A_clause_that_reads_only_its_own_collection_is_allowed_without_RunSql(string? clause)
    {
        QuerySqlComposer.CheckClause(clause, allowNestedReads: false).Allowed.Should().BeTrue(clause ?? "(null)");
    }

    [Theory]
    [InlineData("where id in (select id from other.t)")]
    [InlineData("where exists (select 1)")]
    [InlineData("where pg_read_file('x') = ''")]
    [InlineData("where 1 = 1 union select id, data from other.t")]
    [InlineData("where pg_sleep(10) is null")]
    [InlineData("where d.id = (with x as (select 1) select 1)")]
    [InlineData("where d.id in (select id from t join u on true)")]
    [InlineData("where current_setting('is_superuser') = 'on'")]
    [InlineData("where 'mt_doc_person'::regclass is not null")]
    [InlineData("where set_config('statement_timeout', '0', false) is not null")]
    public void A_clause_that_reads_outside_its_collection_is_refused_without_RunSql(string clause)
    {
        SqlGuardResult refused = QuerySqlComposer.CheckClause(clause, allowNestedReads: false);

        refused.Allowed.Should().BeFalse(clause);
        refused.Message.Should().Contain("MartenStudioOptions.Capabilities.RunSql");
        refused.Token.Should().NotBeNullOrEmpty();
        refused.Position.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// With <c>RunSql</c> the same clauses run: a subquery is the point of the mode for somebody who could
    /// have typed the whole thing into the console anyway. What it does <em>not</em> lift is the tenant and
    /// soft-delete predicates, which are composed in either way - see
    /// <see cref="A_conjoined_soft_deleted_table_carries_both_predicates_in_front_of_the_visitors" />.
    /// </summary>
    [Theory]
    [InlineData("where id in (select id from other.t)")]
    [InlineData("where exists (select 1)")]
    [InlineData("where 1 = 1 union select id, data from other.t")]
    [InlineData("where pg_sleep(10) is null")]
    public void The_same_clause_is_allowed_with_RunSql(string clause)
    {
        QuerySqlComposer.CheckClause(clause, allowNestedReads: true).Allowed.Should().BeTrue(clause);
    }

    /// <summary>
    /// A keyword inside a value is a value. The scanner skips strings, quoted identifiers, dollar-quoted
    /// bodies and comments, which is the same discipline the statement guard uses.
    /// </summary>
    [Theory]
    [InlineData("where data ->> 'Name' = 'select from union'")]
    [InlineData("where \"join\" = 1")]
    [InlineData("where data ->> 'Name' = $$ select 1 $$")]
    [InlineData("where a = 1 -- select from t")]
    [InlineData("where a = 1 /* select from t */")]
    public void A_keyword_inside_a_literal_is_not_a_refusal(string clause)
    {
        QuerySqlComposer.CheckClause(clause, allowNestedReads: false).Allowed.Should().BeTrue(clause);
    }

    /// <summary>
    /// A <c>limit</c> in a string is not a <c>limit</c>, which the partition has to agree with or it would
    /// cut somebody's predicate in half.
    /// </summary>
    [Theory]
    [InlineData("where name = 'a limit b'")]
    [InlineData("where \"limit\" = 1")]
    [InlineData("where x = 1 -- limit 5")]
    [InlineData("where x = $tag$ limit $tag$")]
    public void A_limit_inside_a_literal_is_not_a_split_point(string clause)
    {
        QuerySqlComposer.TryPartition(clause, out ClauseParts parts, out _).Should().BeTrue(clause);

        parts.Limit.Should().BeNull(clause);
        parts.Predicate.Should().Be(clause["where ".Length..].Trim());
    }

    /// <summary>
    /// The composer spells the option out rather than reaching up into the services layer, so this is what
    /// keeps the two spellings from drifting apart.
    /// </summary>
    [Fact]
    public void The_option_the_refusals_name_is_the_one_the_capability_guard_names()
    {
        QuerySqlComposer.RunSqlOption.Should().Be(StudioCapabilityGuard.OptionName(StudioCapability.RunSql));
    }

    [Theory]
    [InlineData("where name = 'limit'", false)]
    [InlineData("where name = 'a limit b'", false)]
    [InlineData("where name = \"limit\"", false)]
    [InlineData("where x = 1 -- limit 5", false)]
    [InlineData("where x = 1 /* limit 5 */", false)]
    [InlineData("where x = $tag$ limit $tag$", false)]
    [InlineData("where x = 1 limit 5", true)]
    [InlineData("WHERE x = 1 LIMIT 5", true)]
    [InlineData("where x = 1\nlimit 5", true)]
    public void A_limit_is_only_a_limit_outside_strings_and_comments(string clause, bool expected)
    {
        QuerySqlComposer.ContainsKeyword(clause, "limit").Should().Be(expected);
    }

    /// <summary>
    /// A clause with an unterminated string counts as "already has one", because the safe failure is to
    /// add nothing: the statement guard and Postgres both refuse it a moment later anyway.
    /// </summary>
    [Fact]
    public void An_unterminated_string_makes_the_scanner_answer_conservatively()
    {
        QuerySqlComposer.ContainsKeyword("where name = 'unterminated", "limit").Should().BeTrue();
        QuerySqlComposer.ContainsKeyword("where x = 1 /* never closed", "limit").Should().BeTrue();
    }

    /// <summary>
    /// EXPLAIN, never EXPLAIN ANALYZE: ANALYZE <em>runs</em> the statement, and the plan is offered for
    /// clauses somebody is still writing.
    /// </summary>
    [Fact]
    public void The_explain_wrapper_never_analyzes()
    {
        var explained = QuerySqlComposer.Explain("select 1", json: true);

        explained.Should().Be("explain (format json) select 1");
        explained.Should().NotContain("analyze");
    }

    /// <summary>
    /// The two denylists are one list. The console's set is the base and the composer adds only what a
    /// <c>where</c> clause needs on top of it, so a function added to <c>ReadOnlySqlGuard</c> reaches the
    /// ungated Mode A for free and the two can never answer the same question differently.
    /// </summary>
    [Fact]
    public void Every_function_the_console_refuses_is_refused_in_a_where_clause_too()
    {
        QuerySqlComposer.DisallowedFunctions.Should().Contain(ReadOnlySqlGuard.DisallowedFunctions.Keys);

        QuerySqlComposer.DisallowedFunctions.Should().HaveCount(
            ReadOnlySqlGuard.DisallowedFunctions.Count + QuerySqlComposer.AdditionalDisallowedFunctions.Length,
            "the composer's own entries are the difference between the two lists, not a second copy");

        QuerySqlComposer.AdditionalDisallowedFunctions.Should().NotIntersectWith(
            ReadOnlySqlGuard.DisallowedFunctions.Keys,
            "an entry restated on both sides is the drift this test exists to stop");
    }

    /// <summary>
    /// The console allows <c>pg_sleep</c> because a server-side <c>statement_timeout</c> bounds it inside
    /// the read-only transaction. A Mode A read carries only a client-side <c>CommandTimeout</c>, so it
    /// refuses it - the one deliberate disagreement between the two lists, and therefore worth pinning.
    /// </summary>
    [Fact]
    public void Pg_sleep_is_the_one_the_console_allows_and_a_clause_does_not()
    {
        ReadOnlySqlGuard.DisallowedFunctions.Should().NotContainKey("pg_sleep");
        QuerySqlComposer.DisallowedFunctions.Should().Contain("pg_sleep");
    }
}
