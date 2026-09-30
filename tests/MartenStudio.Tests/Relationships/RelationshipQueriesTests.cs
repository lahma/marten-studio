using MartenStudio.Internal.Sql;
using MartenStudio.Services.Relationships;
using MartenStudio.Tests.Sql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// The two statements behind the Relationships screen, pinned by shape.
/// </summary>
/// <remarks>
/// Shape pins rather than full-text snapshots: what has to hold is that every identifier is quoted, that
/// the schema list is a parameter rather than an interpolated <c>in (…)</c>, that the inbound count is
/// bounded, and that the tenant and soft-delete predicates appear exactly when the collection has those
/// columns. <c>RelationshipsLiveTests</c> is what proves the statements also run.
/// </remarks>
public class RelationshipQueriesTests
{
    [Fact]
    public void The_catalog_read_interpolates_nothing_and_compares_the_schemas_with_any()
    {
        RelationshipQueries.ForeignKeysSql.Should().Contain("= any(@schemas)");
        RelationshipQueries.ForeignKeysSql.Should().Contain("con.contype = 'f'");
        RelationshipQueries.ForeignKeysSql.Should().NotContain("'\" +", "nothing is concatenated into this");
        RelationshipQueries.ForeignKeysSql.Should().Contain("with ordinality",
            "array_agg over a bare unnest has no defined column order, and a composite key whose columns " +
            "came back the other way round would not match the declared one");
    }

    /// <summary>
    /// Postgres clones a foreign key declared on a partitioned table onto every partition, and onto the
    /// parent once per referenced partition. On a tenant-partitioned store that is one row per tenant per
    /// key, each naming a table (<c>mt_doc_&lt;alias&gt;_&lt;tenant&gt;</c>) the store's mappings do not know — so
    /// they would be listed as keys "on a table no document type maps", printing the tenant list and
    /// walking past <c>IsDocumentTypeVisible</c>, which can only recognise the parent.
    /// </summary>
    [Fact]
    public void The_catalog_read_takes_the_declared_key_and_never_a_partitions_copy_of_it() =>
        RelationshipQueries.ForeignKeysSql.Should().Contain("con.conparentid = 0",
            "a clone carries a non-zero conparentid; a key declared on a partition itself carries zero " +
            "and is still reported");

    [Fact]
    public void The_catalog_read_resolves_both_ends_of_the_constraint()
    {
        RelationshipQueries.ForeignKeysSql.Should().Contain("con.conrelid");
        RelationshipQueries.ForeignKeysSql.Should().Contain("con.confrelid");
        RelationshipQueries.ForeignKeysSql.Should().Contain("con.conkey");
        RelationshipQueries.ForeignKeysSql.Should().Contain("con.confkey");
    }

    [Fact]
    public void The_catalog_read_never_touches_anything_that_builds_schema()
    {
        foreach (string forbidden in new[] { "AllObjects", "DocumentTables", "mt_hilo", "create ", "alter " })
        {
            RelationshipQueries.ForeignKeysSql.Should().NotContain(
                forbidden, "a read path must never apply a migration (AGENTS.md hard rule 14)");
        }
    }

    [Fact]
    public void The_inbound_count_is_bounded_and_quotes_its_identifiers()
    {
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(
            SqlTestStore.DocumentType<RelQueryNote>(static options =>
                options.Schema.For<RelQueryNote>().Duplicate(x => x.CustomerId)));

        string sql = RelationshipQueries.InboundCountSql(table, "customer_id", null);

        sql.Should().StartWith("select count(*) from (select 1 from ");
        sql.Should().Contain("\"studio_sql\".\"mt_doc_relquerynote\"");
        sql.Should().Contain("\"customer_id\" = @id");
        sql.Should().EndWith("limit @cap) as bounded");
        sql.Should().NotContain("tenant_id", "this collection is not conjoined");
    }

    [Fact]
    public void The_inbound_count_filters_by_tenant_when_the_pointing_collection_is_conjoined()
    {
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(
            SqlTestStore.DocumentType<RelQueryNote>(static options =>
                options.Schema.For<RelQueryNote>().MultiTenanted().Duplicate(x => x.CustomerId)));

        string? tenantColumn = RelationshipQueries.TenantColumn(table, "acme");

        tenantColumn.Should().Be("tenant_id");

        string sql = RelationshipQueries.InboundCountSql(table, "customer_id", tenantColumn);

        sql.Should().Contain("\"tenant_id\" = @tenant");
    }

    [Fact]
    public void The_inbound_count_has_no_tenant_predicate_when_no_tenant_is_in_scope()
    {
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(
            SqlTestStore.DocumentType<RelQueryNote>(static options =>
                options.Schema.For<RelQueryNote>().MultiTenanted().Duplicate(x => x.CustomerId)));

        RelationshipQueries.TenantColumn(table, null).Should().BeNull(
            "a scope with no tenant reads every tenant's rows, exactly as the list does");
    }

    [Fact]
    public void The_inbound_count_excludes_soft_deleted_rows()
    {
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(
            SqlTestStore.DocumentType<RelQueryNote>(static options =>
                options.Schema.For<RelQueryNote>().SoftDeleted().Duplicate(x => x.CustomerId)));

        string sql = RelationshipQueries.InboundCountSql(table, "customer_id", null);

        sql.Should().Contain("\"mt_deleted\" = false",
            "the count has to count the same rows the list the row links to would show");
    }

    [Fact]
    public void The_inbound_count_sends_the_id_the_tenant_and_the_cap_as_parameters()
    {
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(
            SqlTestStore.DocumentType<RelQueryNote>(static options =>
                options.Schema.For<RelQueryNote>().MultiTenanted().Duplicate(x => x.CustomerId)));

        Guid id = Guid.NewGuid();

        using var command = RelationshipQueries.BuildInboundCount(
            table, "customer_id", NpgsqlDbType.Uuid, id, "acme", 1001, 30);

        command.Parameters.Should().HaveCount(3);
        command.Parameters["id"].Value.Should().Be(id);
        command.Parameters["id"].NpgsqlDbType.Should().Be(NpgsqlDbType.Uuid);
        command.Parameters["tenant"].Value.Should().Be("acme");
        command.Parameters["cap"].Value.Should().Be(1001);
        command.CommandTimeout.Should().Be(30);
    }

    [Fact]
    public void The_cap_is_one_more_than_the_number_the_screen_reports()
    {
        RelationshipQueries.DefaultInboundCap.Should().Be(1001,
            "the panel says '1000+', which needs one row beyond the thousand to know there is a beyond");
    }

    /// <summary>
    /// The read the graph shares between visitors takes a key if <em>either</em> end is in the schema set -
    /// a key from <c>legacy</c> into the document schema is exactly the one that must be counted for a
    /// visitor who may not see <c>legacy</c> - and it is bounded, and never has a partition at either end.
    /// </summary>
    [Fact]
    public void The_shared_read_takes_a_key_by_either_end_bounded_and_never_a_partition()
    {
        string sql = RelationshipQueries.ForeignKeysTouchingSql;

        sql.Should().Contain("(ns.nspname = any(@schemas) or fns.nspname = any(@schemas))");
        sql.Should().Contain("con.conparentid = 0");
        sql.Should().Contain("not cl.relispartition");
        sql.Should().Contain("not fcl.relispartition");
        sql.Should().Contain("limit @cap");
        sql.Should().Contain("with ordinality");

        foreach (string forbidden in new[] { "AllObjects", "DocumentTables", "mt_hilo", "create ", "alter ", "information_schema" })
        {
            sql.Should().NotContain(forbidden);
        }
    }

    /// <summary>
    /// Both reads carry what the graph needs beyond the names: whether Postgres validated the key, and
    /// both ends' primary keys - the key columns only, never an <c>INCLUDE</c> column.
    /// </summary>
    [Fact]
    public void Both_reads_carry_validation_and_both_primary_keys()
    {
        foreach (string sql in new[] { RelationshipQueries.ForeignKeysSql, RelationshipQueries.ForeignKeysTouchingSql })
        {
            sql.Should().Contain("con.convalidated");
            sql.Should().Contain("i.indisprimary");
            sql.Should().Contain("u.ord <= i.indnkeyatts");
            sql.Should().Contain("i.indrelid = con.conrelid");
            sql.Should().Contain("i.indrelid = con.confrelid");
        }

        RelationshipQueries.ForeignKeysSql.Should().NotContain("fns.nspname = any(@schemas)",
            "the store-schema read DB-3's inbound counts use keeps its meaning: the pointing end only");
    }

    /// <summary>
    /// DB-6 review F5: a pointing table's rows are counted with the database browser's own statement - the
    /// one row detail's "referenced by" runs - rather than a second builder of the relationships screen's
    /// that said <c>count(*)</c> unqualified.
    /// </summary>
    [Fact]
    public void The_table_count_is_the_row_browsers_own_statement_capped_where_every_inbound_count_is()
    {
        string[] columns = ["customer_id", "tenant_id"];
        string[] values = ["6f9619ff-8b86-d011-b42d-00cf4fc964ff", "acme"];

        TableRowStatement statement = RelationshipDataService.TableInboundCount("legacy", "Customer Credit", columns, values);
        TableRowStatement browsers = TableRowQueryBuilder.BuildInboundCount("legacy", "Customer Credit", columns, values);

        statement.Should().BeEquivalentTo(browsers, "one statement, so the two screens can never count differently");

        statement.Sql.Should().Contain("pg_catalog.count(*)", "a count of somebody else's on the search path cannot answer");
        statement.Sql.Should().Contain("\"legacy\".\"Customer Credit\"").And.Contain("\"customer_id\"").And.Contain("\"tenant_id\"");
        statement.Sql.Should().NotContain("6f9619ff").And.NotContain("acme", "values are parameters, never text");

        statement.Parameters.Where(static x => x.Name != "cap").Should().OnlyContain(static x => x.Type == NpgsqlDbType.Unknown,
            "Postgres applies the column's own input function, so no type name is ever written into the SQL");
        statement.Parameters.Should().ContainSingle(static x => x.Name == "cap")
            .Which.Value.Should().Be(RelationshipQueries.DefaultInboundCap);
    }

    [Fact]
    public void The_table_count_refuses_a_column_without_a_value()
    {
        Action build = () => RelationshipDataService.TableInboundCount("legacy", "t", ["a", "b"], ["1"]);

        build.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("a", "NoAction")]
    [InlineData("r", "Restrict")]
    [InlineData("c", "Cascade")]
    [InlineData("n", "SetNull")]
    [InlineData("d", "SetDefault")]
    [InlineData(null, "NoAction")]
    [InlineData("", "NoAction")]
    public void A_referential_action_reads_the_way_CascadeAction_spells_it(string? code, string expected)
    {
        RelationshipQueries.CascadeAction(code).Should().Be(expected);
    }
}

/// <summary>A document with a duplicated column a foreign key could sit on.</summary>
public class RelQueryNote
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }

    /// <summary>The customer, duplicated into <c>customer_id</c>.</summary>
    public Guid CustomerId { get; set; }
}
