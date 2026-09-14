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
/// they surround the visitor's parenthesised predicate, and that the text is the text that runs.
/// </para>
/// <para>
/// The second round of tests is about the parentheses themselves. Composing the studio's terms in front of
/// a parenthesised predicate is only a guarantee while the predicate stays inside the brackets the composer
/// wrote, and it did not: <c>1 = 1) or (1 = 1</c> closed the composer's bracket and put its second half
/// outside every predicate, which was measured returning four rows on a scope narrowed to one tenant. So
/// there is a balance rule, the terms are repeated after the predicate as well as before it, the
/// <c>order by</c> body is held to being a sort list, and the function denylist applies at every
/// capability level rather than evaporating for a <c>RunSql</c> holder.
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
    /// carry both predicates, and they have to surround the visitor's - a parenthesised predicate cannot
    /// reach around an <c>and</c> that precedes it, however much <c>or</c> is inside it, and the copy
    /// <em>after</em> it is the half that still holds if the parentheses are ever escaped.
    /// </summary>
    [Fact]
    public void A_conjoined_soft_deleted_table_carries_both_predicates_on_both_sides_of_the_visitors()
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
              and d."tenant_id" = @tenant
              and d."mt_deleted" = false
            limit @limit
            """.ReplaceLineEndings("\n"));

        composed.TenantId.Should().Be("acme");
        composed.CrossTenant.Should().BeFalse();
        composed.Deleted.Should().Be(DeletedFilter.Exclude);
        composed.TenantOrdinal.Should().Be(2);
        composed.DeletedOrdinal.Should().Be(3);

        composed.Parameters.Should().ContainSingle(x => x.Name == "tenant",
            "the tenant is bound once and named twice; an Npgsql parameter may be referenced as often as it likes");
    }

    /// <summary>
    /// Defence in depth, stated as a property rather than as a string: whatever the clause is, the studio's
    /// own terms appear on <em>both</em> sides of it. <c>a and (X) and a</c> distributes over any <c>or</c>
    /// the predicate turns out to contain, so an <c>or</c> disjunct that somehow escaped the parentheses is
    /// still tenant-filtered and still soft-delete-filtered.
    /// </summary>
    [Theory]
    [InlineData("where 1 = 1")]
    [InlineData("where subject = 'x' or true")]
    [InlineData("where (a = 1) and (b = 2)")]
    [InlineData("where a = 1 order by d.id limit 5")]
    public void The_studios_own_terms_bracket_the_visitors_predicate_on_both_sides(string clause)
    {
        ComposedQuery composed = QuerySqlComposer.Compose(
            Table(conjoined: true, softDeleted: true), clause, 50, "acme");

        int openBracket = composed.Statement.IndexOf("  and (\n", StringComparison.Ordinal);
        int closeBracket = composed.Statement.IndexOf("\n  )\n", StringComparison.Ordinal);

        openBracket.Should().BeGreaterThan(0, clause);
        closeBracket.Should().BeGreaterThan(openBracket, clause);

        string before = composed.Statement[..openBracket];
        string after = composed.Statement[closeBracket..];

        before.Should().Contain("and d.\"tenant_id\" = @tenant").And.Contain("and d.\"mt_deleted\" = false");
        after.Should().Contain("and d.\"tenant_id\" = @tenant").And.Contain("and d.\"mt_deleted\" = false");
    }

    /// <summary>
    /// The repeat is only worth having where there is something to repeat: a clause with no predicate has
    /// no parentheses to escape from, and a second copy of the terms would be noise in the SQL tab.
    /// </summary>
    [Fact]
    public void An_empty_predicate_gets_the_studios_terms_once_and_not_twice()
    {
        QuerySqlComposer.Compose(Table(conjoined: true, softDeleted: true), null, 50, "acme").Statement
            .Should().Be(
                """
                select d."id", d."data"::text, d."tenant_id", d."mt_deleted"
                from "studio"."mt_doc_person" as d
                where 1 = 1
                  and d."tenant_id" = @tenant
                  and d."mt_deleted" = false
                limit @limit
                """.ReplaceLineEndings("\n"));
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
    /// A clause needs no capability, so a second statement in it would be an ungated SQL console.
    /// <c>1 = 1; drop table x</c> is one Npgsql command holding two statements, and Postgres runs both -
    /// and the read-only transaction Mode A now runs in would not stop the second one from being
    /// <c>select pg_advisory_lock(42)</c>.
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
    /// The two shape rules hold even for somebody who may run SQL. <c>RunSql</c> lifts
    /// <see cref="QuerySqlComposer.NestedReadKeywords" /> and nothing else, and a second statement here is
    /// one Npgsql command the studio never showed anybody - which is more than <c>RunSql</c> grants
    /// anywhere else.
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
    // Balanced parentheses. The clause is spliced between a `(` and a `)` the composer wrote, so a clause
    // that closes one it never opened closes the studio's - and everything after it lands outside the
    // tenant and soft-delete predicates. Measured returning four rows where the answer was one.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every spelling the adversarial review found, by name. The first is the one it measured: on a
    /// conjoined, soft-deleted collection scoped to <c>acme</c>, <c>1 = 1) or (1 = 1</c> returned acme
    /// live, acme deleted, globex live and globex deleted, because <c>and</c> binds tighter than <c>or</c>
    /// and the second disjunct had no predicate in front of it at all. The second needs no unbalanced
    /// <c>(</c> whatsoever: the composer puts its closing bracket on a line of its own, so a clause ending
    /// in a <c>--</c> comment can supply the <c>)</c> from the free line that follows.
    /// </summary>
    [Theory]
    [InlineData("1 = 1) or (1 = 1", ")")]
    [InlineData("where 1 = 1) or (1 = 1", ")")]
    [InlineData("a = 1 --\n) or (true", ")")]
    [InlineData("data is not null -- \n) or (true", ")")]
    [InlineData("data is not null --\r) or (d.id in (select id from t)) or (true", ")")]
    [InlineData("data is not null --\r) or (pg_advisory_lock(1) is not null) or (true", ")")]
    [InlineData("a = 1 --\r\n) or (true", ")")]
    [InlineData("x = 1) or (1=1 limit 5", ")")]
    [InlineData("1=1) or (1=1 order by d.tenant_id limit 500", ")")]
    [InlineData("a = 1)", ")")]
    [InlineData("')' = ')' ) or (true", ")")]
    [InlineData("a = 1 /* ( */ )", ")")]
    [InlineData("(a = 1", "(")]
    [InlineData("where (a = 1 and (b = 2)", "(")]
    [InlineData("a = 1 and (", "(")]
    public void A_clause_whose_brackets_do_not_balance_is_refused_at_both_capability_levels(
        string clause,
        string token)
    {
        foreach (bool mayRunSql in new[] { false, true })
        {
            SqlGuardResult refused = QuerySqlComposer.CheckClause(clause, mayRunSql);

            refused.Allowed.Should().BeFalse($"{clause} (RunSql: {mayRunSql})");
            refused.Token.Should().Be(token, clause);
            refused.Position.Should().BeGreaterThanOrEqualTo(0, clause);
            refused.Message.Should().Contain("Balance the brackets", clause);
            refused.Message.Should().Contain("no capability lifts this one", clause);
        }
    }

    /// <summary>
    /// The anti-vacuity half. A bracket inside a string, a quoted identifier, a dollar body or a comment is
    /// not a bracket, so the balance rule has to leave these alone - a rule that refuses everything is not
    /// a rule, it is an outage.
    /// </summary>
    [Theory]
    [InlineData("where ')' = ')'")]
    [InlineData("where data ->> 'Name' = '((('")]
    [InlineData("where name = 'it''s )'")]
    [InlineData("where \")\" = 1")]
    [InlineData("where x = $tag$ ) ) ( $tag$")]
    [InlineData("where a = 1 -- ) ) (")]
    [InlineData("where a = 1 -- ) ) (\r\n and b = 2")]
    [InlineData("where a = 1 -- ) ) (\r and b = 2")]
    [InlineData("where a = 1 /* ) ) ( */")]
    [InlineData("where (a = 1 or b = 2) and (c = 3)")]
    [InlineData("where lower(data ->> 'Name') like 'a%'")]
    public void Brackets_inside_a_literal_or_a_comment_are_not_brackets(string clause)
    {
        QuerySqlComposer.CheckClause(clause, allowNestedReads: false).Allowed.Should().BeTrue(clause);
    }

    /// <summary>
    /// Postgres block comments <b>nest</b>, unlike C's: in <c>/* /* */ ) */</c> the <c>)</c> is still
    /// inside the comment, and a scanner that stopped at the first <c>*/</c> would refuse a clause Postgres
    /// is perfectly happy with. An unterminated nest is the opposite answer - "I cannot tell what this
    /// would run" is a refusal, not a pass.
    /// </summary>
    [Fact]
    public void Block_comments_nest_the_way_postgres_nests_them()
    {
        QuerySqlComposer.CheckClause("where a = 1 /* /* */ ) */", allowNestedReads: false)
            .Allowed.Should().BeTrue("the ')' never leaves the comment");

        QuerySqlComposer.CheckClause("where a = 1 /* /* */", allowNestedReads: false)
            .Allowed.Should().BeFalse("the outer comment is never closed");
    }

    /// <summary>
    /// <c>standard_conforming_strings</c> is on by default, so a backslash escapes <em>only</em> inside an
    /// <c>E'…'</c> literal. Both halves matter, and they pull in opposite directions: in <c>E'it\'s ('</c>
    /// the bracket is inside the string, while in <c>'x\'; drop table t --'</c> the quote after the
    /// backslash <b>closes</b> the string and the <c>;</c> that follows is a second statement. A scanner
    /// that treats backslashes as escapes everywhere gets the second one wrong, which is the more
    /// expensive direction.
    /// </summary>
    [Fact]
    public void A_backslash_escapes_in_an_E_string_and_nowhere_else()
    {
        QuerySqlComposer.CheckClause("where name = E'it\\'s ('", allowNestedReads: false)
            .Allowed.Should().BeTrue("the '(' is inside the escaped literal");

        SqlGuardResult refused = QuerySqlComposer.CheckClause(
            "where name = 'x\\'; drop table t --'", allowNestedReads: false);

        refused.Allowed.Should().BeFalse("the quote after the backslash closes the string");
        refused.Reason.Should().Be(SqlRejectionReason.MultipleStatements);
    }

    /// <summary>
    /// Postgres ends a <c>--</c> comment at a carriage return as well as at a line feed (its lexer's
    /// <c>non_newline</c> is <c>[^\n\r]</c>). A scanner that read on to the line feed hid everything after
    /// a lone CR from every rule while Postgres ran it - a second statement, a denylisted function, an
    /// unbalanced bracket - and a <c>%0D</c> in the page's <c>?where=</c> deep link delivers one.
    /// </summary>
    [Theory]
    [InlineData("a = 1 --\r; select 1")]
    [InlineData("data is not null --\r); select pg_advisory_lock(1) from t d where (1=1")]
    public void A_line_comment_ends_at_a_carriage_return_as_it_does_to_postgres(string clause)
    {
        SqlGuardResult refused = QuerySqlComposer.CheckClause(clause, allowNestedReads: true);

        refused.Allowed.Should().BeFalse(clause);
        refused.Reason.Should().Be(SqlRejectionReason.MultipleStatements);
        refused.Token.Should().Be(";");
    }

    /// <summary>
    /// A dollar-quote tag follows the rules of an unquoted identifier, so it cannot begin with a digit:
    /// <c>$1$</c> is a parameter placeholder. Reading it as a tag made the <c>;</c> scanner skip the span
    /// between two of them, which is a scanner disagreeing with Postgres about where a string is - the one
    /// thing it may never do, whether or not today's spelling happens to be exploitable.
    /// </summary>
    [Theory]
    [InlineData("1 = 1 and 'x' = $1$;drop table x;--$1$")]
    [InlineData("a = $2$;select 1;$2$")]
    public void A_dollar_tag_that_starts_with_a_digit_is_a_parameter_and_hides_nothing(string clause)
    {
        SqlGuardResult refused = QuerySqlComposer.CheckClause(clause, allowNestedReads: false);

        refused.Allowed.Should().BeFalse(clause);
        refused.Reason.Should().Be(SqlRejectionReason.MultipleStatements);
        refused.Token.Should().Be(";");

        QuerySqlComposer.IndexOfStatementSeparator(clause).Should().BeGreaterThanOrEqualTo(0,
            "the ';' scanner has to see it too, because that is the scanner that was blind");
    }

    /// <summary>
    /// A real tag still works, so the rule above did not simply delete dollar quoting.
    /// </summary>
    [Theory]
    [InlineData("where x = $$ ; $$")]
    [InlineData("where x = $tag$ ; $tag$")]
    [InlineData("where x = $_t1$ ; $_t1$")]
    public void A_tag_that_starts_with_a_letter_or_an_underscore_is_still_a_body(string clause)
    {
        QuerySqlComposer.CheckClause(clause, allowNestedReads: false).Allowed.Should().BeTrue(clause);
        QuerySqlComposer.IndexOfStatementSeparator(clause).Should().Be(-1, clause);
    }

    // ------------------------------------------------------------------------------------------------
    // The `order by` body. It is spliced onto the statement verbatim, and Postgres 17 accepts a locking
    // clause between `order by` and `limit` - which is how `for update` reached the server from the mode
    // that needs no capability.
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("where a = 1 order by 1 for update", "for")]
    [InlineData("where a = 1 order by 1 --\rfor update", "for")]
    [InlineData("order by 1 for update", "for")]
    [InlineData("where a = 1 order by d.id for no key update", "for")]
    [InlineData("where a = 1 order by 1 fetch first 1 rows only", "fetch")]
    [InlineData("where a = 1 order by 1 union select 1", "union")]
    [InlineData("where a = 1 order by 1 intersect select 1", "intersect")]
    [InlineData("where a = 1 order by 1 except select 1", "except")]
    [InlineData("where a = 1 order by 1 into other_table", "into")]
    public void A_sort_list_that_is_not_a_sort_list_is_refused_at_both_capability_levels(
        string clause,
        string token)
    {
        foreach (bool mayRunSql in new[] { false, true })
        {
            SqlGuardResult refused = QuerySqlComposer.CheckClause(clause, mayRunSql);

            refused.Allowed.Should().BeFalse($"{clause} (RunSql: {mayRunSql})");
            refused.Token.Should().Be(token, clause);
            refused.Message.Should().Contain("not part of a sort list", clause);
        }

        QuerySqlComposer.TryPartition(clause, out _, out SqlGuardResult rejection).Should().BeFalse(clause);
        rejection.Token.Should().Be(token);
    }

    /// <summary>
    /// The anti-vacuity half again. <c>substring(x from 2 for 3)</c> and <c>overlay(…)</c> carry a
    /// <c>for</c> of their own, inside the function's parentheses - the check is at depth zero for exactly
    /// that reason.
    /// </summary>
    [Theory]
    [InlineData("where a = 1 order by substring(data ->> 'Name' from 2 for 3)")]
    [InlineData("where a = 1 order by d.id desc nulls last")]
    [InlineData("where a = 1 order by lower(data ->> 'Name') collate \"C\"")]
    [InlineData("where a = 1 order by (select 1 from t limit 1)")]
    public void A_sort_list_that_is_one_survives_the_check(string clause)
    {
        QuerySqlComposer.TryPartition(clause, out ClauseParts parts, out SqlGuardResult rejection)
            .Should().BeTrue(rejection.Message);

        parts.OrderBy.Should().NotBeNullOrWhiteSpace(clause);

        // `select`, `from` and `limit` inside that last one still need RunSql; that is a different rule.
        QuerySqlComposer.CheckClause(clause, allowNestedReads: true).Allowed.Should().BeTrue(clause);
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
    /// With <c>RunSql</c> the <em>nested-read</em> clauses run: a subquery is the point of the mode for
    /// somebody who could have typed the whole thing into the console anyway. What it does <em>not</em>
    /// lift is the tenant and soft-delete predicates, which are composed in either way - see
    /// <see cref="A_conjoined_soft_deleted_table_carries_both_predicates_on_both_sides_of_the_visitors" /> -
    /// nor <see cref="QuerySqlComposer.DisallowedFunctions" />, which is the next test.
    /// </summary>
    [Theory]
    [InlineData("where id in (select id from other.t)")]
    [InlineData("where exists (select 1)")]
    [InlineData("where 1 = 1 union select id, data from other.t")]
    [InlineData("where d.id in (select id from t join u on true)")]
    public void The_same_clause_is_allowed_with_RunSql(string clause)
    {
        QuerySqlComposer.CheckClause(clause, allowNestedReads: true).Allowed.Should().BeTrue(clause);
    }

    /// <summary>
    /// <b>The denylist never lifts.</b> <c>RunSql</c> used to turn <see cref="QuerySqlComposer.CheckClause" />
    /// into "no <c>;</c>, no leading statement word, a placeable tail" and nothing else, so
    /// <c>where pg_advisory_lock(42) is not null</c> was refused without the capability and <b>allowed with
    /// it</b> - a session-level lock, taken from a <c>where</c> box, on a pooled connection that outlives
    /// the request. A read-only transaction does not refuse an advisory lock; only the list does. So the
    /// list applies to everybody, and only <see cref="QuerySqlComposer.NestedReadKeywords" /> answers to
    /// the capability.
    /// </summary>
    [Theory]
    [InlineData("where pg_advisory_lock(42) is not null")]
    [InlineData("where pg_advisory_lock_shared(1, 2) is not null")]
    [InlineData("where setval('s', 1) > 0")]
    [InlineData("where nextval('s') > 0")]
    [InlineData("where pg_terminate_backend(pg_backend_pid())")]
    [InlineData("where pg_read_file('x') = ''")]
    [InlineData("where set_config('statement_timeout', '0', false) is not null")]
    [InlineData("where current_setting('is_superuser') = 'on'")]
    [InlineData("where pg_sleep(10) is null")]
    [InlineData("where pg_notify('c', 'p') is null")]
    [InlineData("where 'mt_doc_person'::regclass is not null")]
    [InlineData("where 'now'::regproc is not null")]
    [InlineData("where 1 = 1 --\r and pg_advisory_lock(42) is not null")]
    public void A_function_or_cast_the_denylist_names_is_refused_at_both_capability_levels(string clause)
    {
        foreach (bool mayRunSql in new[] { false, true })
        {
            SqlGuardResult refused = QuerySqlComposer.CheckClause(clause, mayRunSql);

            refused.Allowed.Should().BeFalse($"{clause} (RunSql: {mayRunSql})");
            refused.Token.Should().NotBeNullOrEmpty();
            refused.Position.Should().BeGreaterThanOrEqualTo(0);
            refused.Message.Should().Contain("whatever capabilities you hold");
        }
    }

    /// <summary>
    /// The mirror image, so the test above cannot pass by refusing everything: the words <c>RunSql</c>
    /// really does lift stay lifted.
    /// </summary>
    [Fact]
    public void RunSql_lifts_the_nested_read_words_and_only_those()
    {
        QuerySqlComposer.CheckClause("where id in (select id from other.t)", allowNestedReads: false)
            .Allowed.Should().BeFalse();
        QuerySqlComposer.CheckClause("where id in (select id from other.t)", allowNestedReads: true)
            .Allowed.Should().BeTrue("a nested read is what the capability is for");

        QuerySqlComposer.NestedReadKeywords.Should().NotIntersectWith(
            QuerySqlComposer.DisallowedFunctions,
            "a word in both lists would be lifted by one rule and refused by the other");
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
    /// the read-only transaction. Mode A runs inside that same transaction now, so the timeout is no longer
    /// the difference - the capability is: the console is gated on <c>RunSql</c> and a <c>where</c> clause
    /// is gated on nothing, and parking a connection for the whole statement timeout with no capability is
    /// cheaper than the studio should make it. The one deliberate disagreement between the two lists, and
    /// therefore worth pinning.
    /// </summary>
    [Fact]
    public void Pg_sleep_is_the_one_the_console_allows_and_a_clause_does_not()
    {
        ReadOnlySqlGuard.DisallowedFunctions.Should().NotContainKey("pg_sleep");
        QuerySqlComposer.DisallowedFunctions.Should().Contain("pg_sleep");
    }
}
