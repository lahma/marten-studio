using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The document list and single-document reads, asserted as SQL text plus parameters.
/// </summary>
/// <remarks>
/// These tests are the reason the builders take a <see cref="DocumentTableInfo"/> rather than a live
/// store: the whole of AGENTS.md hard rule 4 — quoted identifiers, parameterised values, never
/// <c>select *</c> — is checkable without a database, in milliseconds, on every keystroke.
/// </remarks>
public class DocumentQueryBuilderTests
{
    [Fact]
    public void The_list_selects_id_capped_json_size_metadata_and_duplicated_columns()
    {
        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery());

        command.CommandText.Should().Contain("select d.\"id\",");
        command.CommandText.Should().Contain(
            "case when octet_length(d.\"data\"::text) <= @maxInline then d.\"data\"::text end as data");
        command.CommandText.Should().Contain("octet_length(d.\"data\"::text) as data_bytes");
        command.CommandText.Should().Contain("d.\"mt_last_modified\"");
        command.CommandText.Should().Contain("d.\"email\"");
        command.CommandText.Should().Contain("d.\"address_city\"");
        command.CommandText.Should().Contain("from \"studio_sql\".\"mt_doc_sqltestcustomer\" as d");
        command.CommandText.Should().NotContain("select *");

        Parameter(command, "maxInline").Value.Should().Be(DocumentListQuery.DefaultMaxInlineDocumentBytes);
    }

    [Fact]
    public void The_list_against_a_table_with_no_metadata_selects_only_id_and_the_json()
    {
        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.MetadataLess(), new DocumentListQuery());

        command.CommandText.Should().Be(
            """
            select d."id",
                   case when octet_length(d."data"::text) <= @maxInline then d."data"::text end as data,
                   octet_length(d."data"::text) as data_bytes
            from "studio_sql"."mt_doc_sqltestnote" as d
            where 1 = 1
            order by d."id"
            limit @limit offset @offset
            """.ReplaceLineEndings("\n"),
            "a store with DisableInformationalFields() has no mt_last_modified to select");
    }

    [Fact]
    public void Soft_deleted_documents_are_excluded_by_default_and_the_tri_state_says_otherwise()
    {
        var table = SqlTestTables.FullyFeatured();

        using var excluded = DocumentQueryBuilder.BuildList(table, new DocumentListQuery());
        using var included = DocumentQueryBuilder.BuildList(
            table, new DocumentListQuery { IncludeDeleted = DeletedFilter.Include });
        using var only = DocumentQueryBuilder.BuildList(
            table, new DocumentListQuery { IncludeDeleted = DeletedFilter.Only });

        excluded.CommandText.Should().Contain("and d.\"mt_deleted\" = false");
        included.CommandText.Should().NotContain("and d.\"mt_deleted\" =");
        only.CommandText.Should().Contain("and d.\"mt_deleted\" = true");
    }

    /// <summary>
    /// The tri-state and <c>is:deleted</c> are two ways of saying the same thing, and a query that emitted
    /// both would read <c>mt_deleted = false and mt_deleted = true</c> — a page that is always empty and
    /// never says why. The typed term is the more specific of the two, so it wins.
    /// </summary>
    [Fact]
    public void An_explicit_deleted_term_replaces_the_tri_state_rather_than_contradicting_it()
    {
        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery
        {
            Predicates = SearchGrammar.Parse("is:deleted").Predicates,
            IncludeDeleted = DeletedFilter.Exclude,
        });

        command.CommandText.Should().NotContain("and d.\"mt_deleted\" = false");
        command.CommandText.Should().Contain("and d.\"mt_deleted\" = @p0");
        Parameter(command, "p0").Value.Should().Be(true);
    }

    [Fact]
    public void A_collection_without_soft_delete_has_no_deleted_filter_at_all()
    {
        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.MetadataLess(), new DocumentListQuery());

        command.CommandText.Should().NotContain("mt_deleted");
    }

    [Fact]
    public void Asking_for_only_deleted_documents_of_a_collection_that_has_none_is_refused()
    {
        var act = () => DocumentQueryBuilder.BuildList(
            SqlTestTables.MetadataLess(), new DocumentListQuery { IncludeDeleted = DeletedFilter.Only });

        act.Should().Throw<ArgumentException>().WithMessage("*not soft-deleted*");
    }

    [Fact]
    public void The_tenant_filter_appears_only_on_a_conjoined_collection()
    {
        using var conjoined = DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { TenantId = "acme" });

        conjoined.CommandText.Should().Contain("and d.\"tenant_id\" = @tenant");
        Parameter(conjoined, "tenant").Value.Should().Be("acme");

        // A single-tenant collection is genuinely shared; filtering it by the scope's tenant would hide it.
        using var single = DocumentQueryBuilder.BuildList(
            SqlTestTables.MetadataLess(), new DocumentListQuery { TenantId = "acme" });

        single.CommandText.Should().NotContain("tenant_id");
    }

    [Fact]
    public void A_json_path_travels_as_a_text_array_parameter_and_never_as_SQL()
    {
        var nasty = new[] { "'); drop table mt_doc_customer; --" };

        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery
        {
            Columns = [new DocumentColumn.JsonPath(nasty)],
        });

        command.CommandText.Should().Contain("(d.\"data\" #>> @p0) as json_0");
        command.CommandText.Should().NotContain("drop table");

        var parameter = Parameter(command, "p0");

        parameter.NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Text);
        parameter.Value.Should().BeEquivalentTo(nasty);
    }

    [Fact]
    public void A_filter_on_a_duplicated_field_uses_the_column_and_its_own_type()
    {
        var predicates = SearchGrammar.Parse("email = bob@example.com orders.count >= 3").Predicates;

        using var command = DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { Predicates = predicates });

        command.CommandText.Should().Contain("and d.\"email\" = @p0");
        Parameter(command, "p0").NpgsqlDbType.Should().Be(NpgsqlDbType.Text);
    }

    [Fact]
    public void A_numeric_filter_on_a_duplicated_int_column_binds_an_integer()
    {
        var predicates = SearchGrammar.Parse("ordercount >= 3").Predicates;

        using var command = DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { Predicates = predicates });

        command.CommandText.Should().Contain("and d.\"order_count\" >= @p0");
        Parameter(command, "p0").Value.Should().Be(3);
    }

    [Fact]
    public void A_literal_the_duplicated_column_cannot_hold_falls_back_to_the_json_path()
    {
        // An int4 column and the word "many" do not meet. Comparing through the JSON is wrong-ish but it
        // returns an empty page; binding "many" to an int4 parameter would be an exception on a page render.
        var predicates = SearchGrammar.Parse("ordercount > many").Predicates;

        using var command = DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { Predicates = predicates });

        command.CommandText.Should().Contain("(d.\"data\" #>> @p0) > @p1");
        command.CommandText.Should().NotContain("and d.\"order_count\"");
    }

    [Fact]
    public void A_filter_that_is_only_in_the_json_is_cast_for_its_literal()
    {
        var predicates = SearchGrammar.Parse(
            "total > 99.5 opened >= 2026-01-01 flagged = true note = hello missing = null").Predicates;

        using var command = DocumentQueryBuilder.BuildList(
            SqlTestTables.MetadataLess(), new DocumentListQuery { Predicates = predicates });

        command.CommandText.Should().Contain("(d.\"data\" #>> @p0)::numeric > @p1");
        command.CommandText.Should().Contain("(d.\"data\" #>> @p2)::timestamptz >= @p3");
        command.CommandText.Should().Contain("(d.\"data\" #>> @p4)::boolean = @p5");
        command.CommandText.Should().Contain("(d.\"data\" #>> @p6) = @p7");
        command.CommandText.Should().Contain("(d.\"data\" #>> @p8) is null");

        Parameter(command, "p1").NpgsqlDbType.Should().Be(NpgsqlDbType.Numeric);
        Parameter(command, "p3").NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz);
        Parameter(command, "p5").NpgsqlDbType.Should().Be(NpgsqlDbType.Boolean);
    }

    [Fact]
    public void Containment_free_text_and_contains_match_each_have_their_own_shape()
    {
        var predicates = SearchGrammar.Parse("""@> {"status":"open"} address.city ~ helsin urgent""").Predicates;

        using var command = DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { Predicates = predicates });

        command.CommandText.Should().Contain("d.\"data\" @> @p0::jsonb");
        command.CommandText.Should().Contain("d.\"address_city\" ilike @p1");
        command.CommandText.Should().Contain("d.\"data\"::text ilike @p2");

        Parameter(command, "p0").Value.Should().Be("""{"status":"open"}""");
        Parameter(command, "p1").Value.Should().Be("%helsin%");
        Parameter(command, "p2").Value.Should().Be("%urgent%");
    }

    [Theory]
    [InlineData("50%", "%50\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("back\\slash", "%back\\\\slash%")]
    public void A_contains_match_escapes_the_LIKE_metacharacters(string text, string expected) =>
        DocumentQueryBuilder.LikePattern(text).Should().Be(expected);

    [Fact]
    public void An_id_filter_is_typed_from_the_id_column()
    {
        var id = Guid.NewGuid();

        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery
        {
            Predicates = SearchGrammar.Parse("id:" + id.ToString("D")).Predicates,
        });

        command.CommandText.Should().Contain("and d.\"id\" = @p0");

        var parameter = Parameter(command, "p0");

        parameter.NpgsqlDbType.Should().Be(NpgsqlDbType.Uuid);
        parameter.Value.Should().Be(id);
    }

    [Fact]
    public void An_id_filter_that_cannot_be_parsed_is_refused_rather_than_sent()
    {
        var act = () => DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery
        {
            Predicates = SearchGrammar.Parse("id:not-a-guid").Predicates,
        });

        act.Should().Throw<ArgumentException>().WithMessage("*not a GUID*");
    }

    [Fact]
    public void Sorting_always_ends_in_the_id_tiebreaker()
    {
        var table = SqlTestTables.FullyFeatured();

        using var byMetadata = DocumentQueryBuilder.BuildList(table, new DocumentListQuery
        {
            Sort = new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified),
            Direction = SortDirection.Descending,
        });

        byMetadata.CommandText.Should().Contain("order by d.\"mt_last_modified\" desc nulls last, d.\"id\"");

        using var byDuplicated = DocumentQueryBuilder.BuildList(
            table, new DocumentListQuery { Sort = new DocumentColumn.Duplicated("email") });

        byDuplicated.CommandText.Should().Contain("order by d.\"email\" nulls last, d.\"id\"");

        using var byJson = DocumentQueryBuilder.BuildList(
            table, new DocumentListQuery { Sort = new DocumentColumn.JsonPath(["Address", "City"]) });

        byJson.CommandText.Should().Contain("order by (d.\"data\" #>> @p0) nulls last, d.\"id\"");

        using var byId = DocumentQueryBuilder.BuildList(table, new DocumentListQuery());

        byId.CommandText.Should().Contain("order by d.\"id\"\n", "id is already the total order");
    }

    // ------------------------------------------------------------------------------------------------
    // The default order: the primary key (P2-perf deliverable 1)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The order a collection opens in, pinned as SQL: the primary key descending, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the shape of the query whose cost stopped depending on the size of the collection. The
    /// default used to be <c>order by mt_last_modified desc nulls last, id</c> — a column Marten declares
    /// no index on, so every collection opened with a sequential scan and a top-N sort: 88 ms at 100 000
    /// documents and 784 ms at 1.2 M, against a 250 ms budget.
    /// </para>
    /// <para>
    /// Three things are being pinned, and each of them is what makes the plan an index scan. There is no
    /// second sort term, because <c>id</c> is already a total order and a tiebreaker after it would be
    /// dead weight in the index condition. There is no <c>nulls last</c>, because a primary key has none.
    /// And the direction is a modifier on the indexed column itself rather than an expression, which is
    /// what lets a btree walk backwards instead of sorting.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_default_order_is_the_primary_key_and_carries_no_tiebreaker()
    {
        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery
        {
            Sort = DocumentColumn.ById,
            Direction = SortDirection.Descending,
        });

        command.CommandText.Should().Contain("order by d.\"id\" desc\n");
        command.CommandText.Should().NotContain("nulls last");
        command.CommandText.Should().NotContain("mt_last_modified\" desc");
    }

    /// <summary>
    /// The same order pages by cursor and by offset, so the two agree about which row is first.
    /// </summary>
    /// <remarks>
    /// A keyset page on the primary key is a single index condition — <c>id &lt; @i</c> — with no null
    /// branch and no composite comparison, which is the other half of why the page cost stopped growing.
    /// An offset page keeps the same <c>order by</c>, which is what makes "page 3 by offset" and "three
    /// pages of cursor" the same rows in the same order.
    /// </remarks>
    [Fact]
    public void The_default_order_pages_the_same_way_by_cursor_and_by_offset()
    {
        var table = SqlTestTables.FullyFeatured();
        var id = Guid.NewGuid();

        using var keyset = DocumentQueryBuilder.BuildList(table, new DocumentListQuery
        {
            Sort = DocumentColumn.ById,
            Direction = SortDirection.Descending,
            Cursor = new DocumentKeysetCursor(null, id.ToString()),
        });

        keyset.CommandText.Should().Contain("and d.\"id\" < @i\n");
        keyset.CommandText.Should().Contain("order by d.\"id\" desc\n");
        keyset.CommandText.Should().NotContain("offset");
        Parameter(keyset, "i").Value.Should().Be(id);

        using var offset = DocumentQueryBuilder.BuildList(table, new DocumentListQuery
        {
            Sort = DocumentColumn.ById,
            Direction = SortDirection.Descending,
            Offset = 500,
        });

        offset.CommandText.Should().Contain("order by d.\"id\" desc\n");
        offset.CommandText.Should().Contain("limit @limit offset @offset");
        Parameter(offset, "offset").Value.Should().Be(500);
    }

    /// <summary>
    /// The statement timeout is a <c>set_config</c> with the value as a parameter, local to a transaction.
    /// </summary>
    /// <remarks>
    /// <c>SET</c> takes a literal and not a parameter, so it cannot be used here: the one rule this file
    /// has is that values are parameters. The third argument is what makes the setting local to the
    /// transaction rather than to the pooled connection, which would leak it to whoever got the connection
    /// next.
    /// </remarks>
    [Fact]
    public void The_statement_timeout_parameterises_its_value_and_is_local_to_the_transaction()
    {
        using var command = DocumentQueryBuilder.BuildStatementTimeout(TimeSpan.FromSeconds(30));

        command.CommandText.Should().Be("select set_config('statement_timeout', @statementTimeout, true)");
        Parameter(command, "statementTimeout").Value.Should().Be("30000");

        // 0ms is "disabled" to Postgres, so the shortest thing anybody can ask for is one millisecond -
        // a caller who asked for less meant "immediately", not "never".
        using var tiny = DocumentQueryBuilder.BuildStatementTimeout(TimeSpan.Zero);

        Parameter(tiny, "statementTimeout").Value.Should().Be("1");
    }

    [Fact]
    public void A_sort_key_the_table_has_no_column_for_is_refused()
    {
        var bare = SqlTestTables.MetadataLess();

        var byDisabledMetadata = () => DocumentQueryBuilder.BuildList(
            bare, new DocumentListQuery { Sort = new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified) });

        byDisabledMetadata.Should().Throw<ArgumentException>().WithMessage("*disabled*");

        var byUnknownColumn = () => DocumentQueryBuilder.BuildList(
            bare, new DocumentListQuery { Sort = new DocumentColumn.Duplicated("whatever") });

        byUnknownColumn.Should().Throw<ArgumentException>().WithMessage("*no duplicated column*");
    }

    [Fact]
    public void An_ascending_keyset_page_starts_after_the_cursor_and_keeps_the_nulls_at_the_end()
    {
        var lastId = Guid.NewGuid();

        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery
        {
            Sort = new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified),
            Cursor = new DocumentKeysetCursor("2026-01-01T10:00:00Z", lastId.ToString("D")),
        });

        command.CommandText.Should().Contain(
            "and (d.\"mt_last_modified\" > @k or d.\"mt_last_modified\" is null or " +
            "(d.\"mt_last_modified\" = @k and d.\"id\" > @i))");
        command.CommandText.Should().NotContain("offset", "a keyset page has no offset");

        Parameter(command, "k").NpgsqlDbType.Should().Be(NpgsqlDbType.TimestampTz);
        Parameter(command, "i").Value.Should().Be(lastId);
    }

    [Fact]
    public void A_descending_keyset_page_walks_the_other_way()
    {
        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery
        {
            Sort = new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified),
            Direction = SortDirection.Descending,
            Cursor = new DocumentKeysetCursor("2026-01-01T10:00:00Z", Guid.NewGuid().ToString("D")),
        });

        command.CommandText.Should().Contain("and (d.\"mt_last_modified\" < @k or");
    }

    [Fact]
    public void A_keyset_page_whose_cursor_sort_value_was_null_walks_the_nulls_at_the_end()
    {
        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery
        {
            Sort = new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified),
            Cursor = new DocumentKeysetCursor(null, Guid.NewGuid().ToString("D")),
        });

        command.CommandText.Should().Contain("and (d.\"mt_last_modified\" is null and d.\"id\" > @i)");
        command.Parameters.Contains("k").Should().BeFalse();
    }

    [Fact]
    public void A_keyset_page_on_id_alone_needs_only_the_id()
    {
        using var command = DocumentQueryBuilder.BuildList(SqlTestTables.FullyFeatured(), new DocumentListQuery
        {
            Cursor = new DocumentKeysetCursor(null, Guid.NewGuid().ToString("D")),
        });

        command.CommandText.Should().Contain("and d.\"id\" > @i");
    }

    [Fact]
    public void An_offset_page_is_capped_at_ten_thousand()
    {
        using var allowed = DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { Offset = DocumentQueryBuilder.MaxOffset });

        Parameter(allowed, "offset").Value.Should().Be(10_000);

        var tooFar = () => DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { Offset = DocumentQueryBuilder.MaxOffset + 1 });

        tooFar.Should().Throw<ArgumentException>().WithMessage("*keyset cursor*");

        var negative = () => DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { Offset = -1 });

        negative.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_page_size_is_clamped_on_the_server()
    {
        using var huge = DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { PageSize = 100_000 });

        Parameter(huge, "limit").Value.Should().Be(DocumentListQuery.MaxPageSize);

        using var zero = DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { PageSize = 0 });

        Parameter(zero, "limit").Value.Should().Be(1);
    }

    [Fact]
    public void The_single_document_read_selects_the_whole_json_and_every_metadata_column()
    {
        var id = Guid.NewGuid();

        DocumentQueryBuilder.TryBuildSingle(SqlTestTables.FullyFeatured(), id.ToString("D"), "acme", out var command, out var error)
            .Should().BeTrue();

        using (command)
        {
            error.Should().BeNull();
            command!.CommandText.Should().Contain("select d.\"data\"::text as data");
            command.CommandText.Should().Contain("octet_length(d.\"data\"::text) as data_bytes");
            command.CommandText.Should().Contain("d.\"mt_version\"");
            command.CommandText.Should().Contain("d.\"email\"", "the detail page compares the duplicated columns with the JSON");
            command.CommandText.Should().Contain("where d.\"id\" = @id");
            command.CommandText.Should().Contain("and d.\"tenant_id\" = @tenant");
            Parameter(command, "id").Value.Should().Be(id);
        }
    }

    [Fact]
    public void The_single_document_read_of_a_bare_table_is_two_columns()
    {
        DocumentQueryBuilder.TryBuildSingle(SqlTestTables.MetadataLess(), Guid.NewGuid().ToString("D"), null, out var command, out _)
            .Should().BeTrue();

        using (command)
        {
            command!.CommandText.Trim().Should().Be(
                """
                select d."data"::text as data,
                       octet_length(d."data"::text) as data_bytes
                from "studio_sql"."mt_doc_sqltestnote" as d
                where d."id" = @id
                """.ReplaceLineEndings("\n"));
        }
    }

    [Theory]
    [InlineData("not-a-guid", "*not a GUID*")]
    [InlineData("", "*id is required*")]
    [InlineData("   ", "*id is required*")]
    public void A_malformed_id_comes_back_as_a_message_and_never_as_an_exception(string rawId, string expected)
    {
        var built = DocumentQueryBuilder.TryBuildSingle(SqlTestTables.FullyFeatured(), rawId, null, out var command, out var error);

        built.Should().BeFalse();
        command.Should().BeNull();
        error.Should().Match(expected);
    }

    [Fact]
    public void An_id_is_parsed_against_the_column_type_and_not_the_CLR_type()
    {
        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Uuid, "  " + Guid.Empty.ToString("D") + "  ")
            .Value.Should().Be(Guid.Empty);
        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Int4, "42").Value.Should().Be(42);
        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Int8, "9000000000").Value.Should().Be(9_000_000_000L);
        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Text, "anything").Value.Should().Be("anything");
        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Varchar, "anything").Value.Should().Be("anything");

        // A strong-typed id the studio could not identify: send the text and let Postgres decide.
        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Unknown, "ORD-17").Value.Should().Be("ORD-17");

        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Int4, "9000000000").Success.Should().BeFalse();
        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Int4, "x").Error.Should().Contain("int4");
        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Int8, "x").Error.Should().Contain("int8");
    }

    [Fact]
    public void A_string_keyed_collection_binds_its_id_as_text()
    {
        DocumentQueryBuilder.TryBuildSingle(SqlTestTables.StringKeyed(), "ORD-17", null, out var command, out _)
            .Should().BeTrue();

        using (command)
        {
            Parameter(command!, "id").NpgsqlDbType.Should().Be(NpgsqlDbType.Varchar);
            Parameter(command!, "id").Value.Should().Be("ORD-17");
        }
    }

    [Fact]
    public void An_unidentified_id_column_is_sent_as_an_untyped_literal() =>
        DocumentIdColumnTypes.DbType(DocumentIdColumnType.Unknown).Should().Be(NpgsqlDbType.Unknown);

    /// <summary>
    /// A request that does not fit the collection comes back as a message, and the message is fit to
    /// render.
    /// </summary>
    /// <remarks>
    /// The <c>Try</c> form exists so a page never has to catch an exception to find out that
    /// <c>is:deleted</c> means nothing here. What made that an actual bug rather than a style point is
    /// where the old exception surfaced: after the verdict strip had been composed, so the page drew a
    /// green badge and an <c>ArgumentException.Message</c> underneath it — parameter name included.
    /// </remarks>
    [Theory]
    [InlineData("is:deleted", "*not soft-deleted*")]
    [InlineData("tenant:acme", "*not conjoined-tenanted*")]
    [InlineData("id:not-a-guid", "*not a GUID*")]
    public void A_predicate_the_collection_cannot_answer_is_refused_with_a_message(string search, string expected)
    {
        var built = DocumentQueryBuilder.TryBuildList(
            SqlTestTables.MetadataLess(),
            new DocumentListQuery { Predicates = SearchGrammar.Parse(search).Predicates },
            out var command,
            out DocumentQueryRefusal refusal);

        built.Should().BeFalse();
        command.Should().BeNull();
        refusal.Message.Should().Match(expected);
        refusal.Message.Should().NotContain("Parameter", "the message is rendered to whoever typed the search");
        refusal.IsFilter.Should().BeTrue("this belongs in the parse strip, beside the terms that did parse");
    }

    /// <summary>
    /// A paging refusal is a refusal too, but it does not belong in the search box's verdict.
    /// </summary>
    [Fact]
    public void An_offset_past_the_cap_is_refused_as_paging_rather_than_as_a_filter()
    {
        DocumentQueryBuilder.TryBuildList(
                SqlTestTables.FullyFeatured(),
                new DocumentListQuery { Offset = DocumentQueryBuilder.MaxOffset + 1 },
                out _,
                out DocumentQueryRefusal refusal)
            .Should().BeFalse();

        refusal.Message.Should().Contain("keyset cursor");
        refusal.Message.Should().NotContain("Parameter");
        refusal.IsFilter.Should().BeFalse("nobody typed an offset into the search box");
    }

    [Fact]
    public void A_request_that_does_fit_comes_back_through_the_same_door()
    {
        DocumentQueryBuilder.TryBuildList(
                SqlTestTables.FullyFeatured(), new DocumentListQuery(), out var command, out DocumentQueryRefusal refusal)
            .Should().BeTrue();

        using (command)
        {
            refusal.Message.Should().BeNull();
            command!.CommandText.Should().Contain("from \"studio_sql\".\"mt_doc_sqltestcustomer\"");
        }
    }

    /// <summary>
    /// A genuine programming error is still an exception: <c>TryBuildList</c> catches refusals, not bugs.
    /// </summary>
    [Fact]
    public void A_request_the_builder_could_never_have_assembled_is_still_a_throw()
    {
        // An empty JSON path is not something a user can type: the grammar cannot produce one and the
        // column keys reject it. Reaching the builder with one is a bug in the studio, and a bug must not
        // be rendered to a visitor as though they had mistyped something.
        var act = () => DocumentQueryBuilder.TryBuildList(
            SqlTestTables.FullyFeatured(),
            new DocumentListQuery { Sort = new DocumentColumn.JsonPath([]) },
            out _,
            out _);

        act.Should().Throw<ArgumentException>().Which.Should().NotBeOfType<DocumentQueryRefusedException>();
    }

    [Fact]
    public void Every_built_read_carries_the_studios_command_timeout_when_one_is_given()
    {
        using var list = DocumentQueryBuilder.BuildList(
            SqlTestTables.FullyFeatured(), new DocumentListQuery { CommandTimeoutSeconds = 7 });

        list.CommandTimeout.Should().Be(7);

        DocumentQueryBuilder.TryBuildSingle(
                SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), "acme", DocumentRowLock.None, 7,
                out var single, out _)
            .Should().BeTrue();

        using (single)
        {
            single!.CommandTimeout.Should().Be(7);
        }

        DocumentQueryBuilder.TryBuildExists(
                SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), "acme", 7, out var exists)
            .Should().BeTrue();

        using (exists)
        {
            exists!.CommandTimeout.Should().Be(7);
        }
    }

    /// <summary>
    /// The existence probe reads no column of the row.
    /// </summary>
    /// <remarks>
    /// It used to be a one-row list, whose select list carries <c>octet_length(data::text)</c> — which
    /// detoasts and decompresses the document. The id probe does this once per collection and the related
    /// strip once per foreign key, so the difference is megabytes to answer a yes/no.
    /// </remarks>
    [Fact]
    public void The_existence_probe_selects_nothing_and_stops_at_the_first_row()
    {
        DocumentQueryBuilder.TryBuildExists(
                SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), "acme", 0, out var command)
            .Should().BeTrue();

        using (command)
        {
            command!.CommandText.Should().StartWith("select 1\n");
            command.CommandText.Should().NotContain("data");
            command.CommandText.Should().Contain("where d.\"id\" = @id");
            command.CommandText.Should().Contain("and d.\"tenant_id\" = @tenant");
            command.CommandText.Should().EndWith("limit 1");
        }
    }

    [Fact]
    public void An_id_that_cannot_fit_the_column_is_not_probed_at_all() =>
        DocumentQueryBuilder.TryBuildExists(SqlTestTables.FullyFeatured(), "not-a-guid", null, 0, out var command)
            .Should().BeFalse(command?.CommandText);

    /// <summary>
    /// A conjoined collection read with no tenant in scope reads a named row, not an arbitrary one.
    /// </summary>
    /// <remarks>
    /// The same id exists once per tenant on such a table, so <c>where id = @id</c> alone matched several
    /// rows and the reader took whichever came back first — a link that shows a different tenant's
    /// document on different days, and, under <c>for update</c>, a lock on a row nobody named.
    /// </remarks>
    [Fact]
    public void A_conjoined_read_with_no_tenant_is_ordered_and_limited_to_one_row()
    {
        DocumentQueryBuilder.TryBuildSingle(
                SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), null, out var command, out _)
            .Should().BeTrue();

        using (command)
        {
            command!.CommandText.Should().Contain("order by d.\"tenant_id\"");
            command.CommandText.Should().Contain("limit 1");
            command.CommandText.Should().Contain("-- conjoined", "the Show SQL disclosure has to say why");
        }

        // With a tenant there is exactly one row by construction, so nothing is added.
        DocumentQueryBuilder.TryBuildSingle(
                SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), "acme", out var scoped, out _)
            .Should().BeTrue();

        using (scoped)
        {
            scoped!.CommandText.Should().NotContain("limit 1");
        }
    }

    [Fact]
    public void A_locked_conjoined_read_puts_the_lock_after_the_ordering()
    {
        DocumentQueryBuilder.TryBuildSingle(
                SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), null, DocumentRowLock.ForUpdate,
                out var command, out _)
            .Should().BeTrue();

        using (command)
        {
            command!.CommandText.IndexOf("order by", StringComparison.Ordinal).Should()
                .BeLessThan(command.CommandText.IndexOf("for update", StringComparison.Ordinal));
        }
    }

    private static NpgsqlParameter Parameter(NpgsqlCommand command, string name) => command.Parameters[name];
}
