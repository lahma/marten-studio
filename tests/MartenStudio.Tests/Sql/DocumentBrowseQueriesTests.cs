using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The browse reads that are checkable without a database: the document-table listing's SQL, the
/// <c>_recent</c> union, and the two sizes.
/// </summary>
public class DocumentBrowseQueriesTests
{
    /// <summary>
    /// The listing that replaced <c>IMartenDatabase.DocumentTables()</c>.
    /// </summary>
    /// <remarks>
    /// <c>DocumentTables()</c> is <c>AllObjects()</c>, which builds Marten's feature schemas and applies
    /// the HiLo migration on the way through — DDL, from a read path, on a connection the studio did not
    /// open and cannot time out. The replacement is a catalog read: schemas as a parameter, the prefix as
    /// an escaped LIKE, and a command timeout.
    /// </remarks>
    [Fact]
    public void The_document_table_listing_is_a_parameterised_catalog_read()
    {
        DocumentBrowseQueries.DocumentTablesSql.Should().Contain("information_schema.tables");
        DocumentBrowseQueries.DocumentTablesSql.Should().Contain("table_schema = any(@schemas)");
        DocumentBrowseQueries.DocumentTablesSql.Should().Contain("table_type = 'BASE TABLE'");

        // The underscores are escaped, so the pattern means mt_doc_ and not mt?doc?.
        DocumentBrowseQueries.DocumentTablesSql.Should().Contain(@"like 'mt\_doc\_%'");
    }

    [Theory]
    [InlineData("mt_doc_customer", "customer")]
    [InlineData("MT_DOC_Customer", "Customer")]
    [InlineData("something_else", "something_else")]
    public void A_listed_table_reports_the_alias_the_browser_shows(string table, string expected) =>
        new DocumentBrowseQueries.DocumentTableName("public", table).Alias.Should().Be(expected);

    [Fact]
    public void The_sizes_read_carries_the_command_timeout_it_is_given()
    {
        DocumentBrowseQueries.TryBuildSizes(
                SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), "acme", 9, out var command, out var error)
            .Should().BeTrue();

        using (command)
        {
            error.Should().BeNull();
            command!.CommandTimeout.Should().Be(9);
            command.CommandText.Should().Contain("octet_length(\"data\"::text)");
            command.CommandText.Should().Contain("pg_column_size(\"data\")");
            command.CommandText.Should().Contain("\"tenant_id\" = @tenant");
        }
    }

    /// <summary>
    /// On a conjoined collection with no tenant in scope, the sizes read breaks the tie the same way the
    /// document read does.
    /// </summary>
    /// <remarks>
    /// The same id exists once per tenant, so <c>where id = @id</c> alone matches several rows and the
    /// reader takes whichever came back first. <c>DocumentQueryBuilder.TryBuildSingle</c> settles that by
    /// ordering on <c>tenant_id</c> and taking one row; this read did not, and the metadata pane draws its
    /// two byte counts underneath the JSON that read returned — so one tenant's sizes could appear under
    /// another tenant's document, with nothing on screen to suggest it.
    /// </remarks>
    [Fact]
    public void A_conjoined_sizes_read_with_no_tenant_in_scope_takes_the_same_row_the_document_read_did()
    {
        DocumentTableInfo table = SqlTestTables.FullyFeatured();
        var id = Guid.NewGuid().ToString("D");

        DocumentBrowseQueries.TryBuildSizes(table, id, null, 9, out NpgsqlCommand? sizes, out _)
            .Should().BeTrue();

        DocumentQueryBuilder.TryBuildSingle(table, id, null, out NpgsqlCommand? single, out _)
            .Should().BeTrue();

        using (sizes)
        using (single)
        {
            sizes!.CommandText.Should().Contain("order by \"tenant_id\"").And.Contain("limit 1");
            single!.CommandText.Should().Contain("\"tenant_id\"").And.Contain("limit 1");
        }
    }

    /// <summary>With a tenant in scope there is no ambiguity, so there is no tie to break.</summary>
    [Fact]
    public void A_tenant_scoped_sizes_read_filters_rather_than_ordering()
    {
        DocumentBrowseQueries.TryBuildSizes(
                SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), "acme", 9,
                out NpgsqlCommand? command, out _)
            .Should().BeTrue();

        using (command)
        {
            command!.CommandText.Should().Contain("\"tenant_id\" = @tenant");
            command.CommandText.Should().NotContain("order by");
            command.CommandText.Should().NotContain("limit 1");
        }
    }

    /// <summary>A single-tenanted collection has no tenant column to order by, and gets neither.</summary>
    [Fact]
    public void A_single_tenanted_sizes_read_has_neither_a_filter_nor_a_tie_break()
    {
        DocumentBrowseQueries.TryBuildSizes(
                SqlTestTables.MetadataLess(), Guid.NewGuid().ToString("D"), "acme", 9,
                out NpgsqlCommand? command, out _)
            .Should().BeTrue();

        using (command)
        {
            command!.CommandText.Should().NotContain("tenant");
            command.CommandText.Should().NotContain("order by");
        }
    }

    [Fact]
    public void A_malformed_id_never_becomes_a_sizes_query()
    {
        DocumentBrowseQueries.TryBuildSizes(
                SqlTestTables.FullyFeatured(), "not-a-guid", null, 9, out var command, out var error)
            .Should().BeFalse();

        command.Should().BeNull();
        error.Should().Contain("not a GUID");
    }

    [Fact]
    public void The_recent_union_carries_the_command_timeout_it_is_given()
    {
        using NpgsqlCommand? command = DocumentBrowseQueries.BuildRecent(
            [SqlTestTables.FullyFeatured()], "acme", 10, 20, 9);

        command.Should().NotBeNull();
        command!.CommandTimeout.Should().Be(9);
        command.CommandText.Should().Contain("order by last_modified desc");
    }

    /// <summary>
    /// A collection with no <c>mt_last_modified</c> contributes no branch, and a union of no branches is
    /// no query at all.
    /// </summary>
    [Fact]
    public void A_collection_that_keeps_no_last_modified_is_simply_absent() =>
        DocumentBrowseQueries.BuildRecent([SqlTestTables.MetadataLess()], null, 10, 20).Should().BeNull();
}
