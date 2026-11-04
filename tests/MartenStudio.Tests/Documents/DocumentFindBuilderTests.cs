using MartenStudio.Internal.Sql;
using MartenStudio.Tests.Sql;

using Npgsql;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// 1.2-SCOPE-3: the statement behind Find's documents. It reads no document, only whether the id is there and, where the table has
/// them, the tenant, the subclass discriminator and the soft-delete flag; every optional column is selected only when it is physical
/// (hard rule 10), and the id and the tenant are parameters.
/// </summary>
public class DocumentFindBuilderTests
{
    [Fact]
    public void A_fully_featured_hierarchy_selects_the_tenant_the_subclass_and_the_deleted_flag_and_never_the_document()
    {
        DocumentQueryBuilder.TryBuildFind(FeaturedHierarchy(), Guid.NewGuid().ToString("D"), null, 0, out NpgsqlCommand? command, out DocumentFindShape shape)
            .Should().BeTrue();

        using (command)
        {
            shape.Should().Be(new DocumentFindShape(Tenant: true, Type: true, Deleted: true));
            command!.CommandText.Should().StartWith("select 1, d.\"tenant_id\", d.\"mt_doc_type\", d.\"mt_deleted\"\n");
            command.CommandText.Should().NotContain("data");
            command.CommandText.Should().Contain("where d.\"id\" = @id");
            command.CommandText.Should().EndWith("limit 21");
        }
    }

    /// <summary>Conjoined, soft-deleted and a hierarchy, which is what puts <c>mt_doc_type</c> on the table.</summary>
    private static DocumentTableInfo FeaturedHierarchy() =>
        DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestCustomer>(options =>
            options.Schema.For<SqlTestCustomer>().AddSubClass<SqlTestVipCustomer>().SoftDeleted().MultiTenanted()));

    [Fact]
    public void A_table_without_subclasses_has_no_discriminator_to_select()
    {
        DocumentQueryBuilder.TryBuildFind(SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), null, 0, out NpgsqlCommand? command, out DocumentFindShape shape)
            .Should().BeTrue();

        using (command)
        {
            shape.Should().Be(new DocumentFindShape(Tenant: true, Type: false, Deleted: true));
            command!.CommandText.Should().StartWith("select 1, d.\"tenant_id\", d.\"mt_deleted\"\n");
        }
    }

    [Fact]
    public void The_limit_is_one_more_than_five_tenants_name()
    {
        DocumentQueryBuilder.FindLimit.Should().Be(21);
    }

    [Fact]
    public void A_bare_table_selects_only_a_constant()
    {
        DocumentQueryBuilder.TryBuildFind(SqlTestTables.MetadataLess(), Guid.NewGuid().ToString("D"), null, 0, out NpgsqlCommand? command, out DocumentFindShape shape)
            .Should().BeTrue();

        using (command)
        {
            shape.Tenant.Should().BeFalse();
            shape.Deleted.Should().BeFalse();
            command!.CommandText.Should().StartWith("select 1");
            command.CommandText.Should().NotContain("tenant_id").And.NotContain("mt_deleted");
        }
    }

    [Fact]
    public void A_selected_tenant_is_a_predicate_on_a_conjoined_table_and_a_parameter()
    {
        DocumentQueryBuilder.TryBuildFind(SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), "acme'; drop table x; --", 0, out NpgsqlCommand? command, out _)
            .Should().BeTrue();

        using (command)
        {
            command!.CommandText.Should().Contain("and d.\"tenant_id\" = @tenant");
            command.CommandText.Should().NotContain("acme").And.NotContain("drop table");
            command.Parameters.Cast<NpgsqlParameter>().Select(static x => x.ParameterName).Should().Contain(["id", "tenant"]);
        }
    }

    [Fact]
    public void The_id_is_always_a_parameter_whatever_the_column_type()
    {
        DocumentQueryBuilder.TryBuildFind(SqlTestTables.StringKeyed(), "ORD-1' or '1'='1", null, 0, out NpgsqlCommand? command, out _)
            .Should().BeTrue();

        using (command)
        {
            command!.CommandText.Should().NotContain("ORD-1").And.NotContain("1'='1");
            command.Parameters["id"].Value.Should().Be("ORD-1' or '1'='1");
        }
    }

    [Fact]
    public void An_id_that_cannot_fit_the_column_is_not_probed_at_all()
    {
        DocumentQueryBuilder.TryBuildFind(SqlTestTables.FullyFeatured(), "not-a-guid", null, 0, out NpgsqlCommand? command, out DocumentFindShape shape)
            .Should().BeFalse();

        command.Should().BeNull();
        shape.Should().Be(default(DocumentFindShape));
    }

    [Fact]
    public void The_command_carries_the_studios_timeout()
    {
        DocumentQueryBuilder.TryBuildFind(SqlTestTables.FullyFeatured(), Guid.NewGuid().ToString("D"), null, 9, out NpgsqlCommand? command, out _)
            .Should().BeTrue();

        using (command)
        {
            command!.CommandTimeout.Should().Be(9);
        }
    }
}
