using MartenStudio.Internal.Sql;
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
