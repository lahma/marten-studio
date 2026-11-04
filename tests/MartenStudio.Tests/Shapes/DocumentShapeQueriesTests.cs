using JasperFx.MultiTenancy;

using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Tests.Shapes;

/// <summary>The shape check's statements (D-SHAPE S1, S3): values are parameters, identifiers are quoted, and no sample is random.</summary>
public class DocumentShapeQueriesTests
{
    private const string Schema = "My Schema";
    private const string Nasty = "acme'; drop table x; --";

    private static DocumentTableInfo Table(
        DocumentIdColumnType id = DocumentIdColumnType.Uuid,
        bool conjoined = false,
        bool softDeleted = false,
        bool hierarchy = false)
    {
        List<DocumentMetadataColumnInfo> columns = [];

        if (conjoined)
        {
            columns.Add(new DocumentMetadataColumnInfo(DocumentMetadataColumn.TenantId, "tenant_id"));
        }

        if (softDeleted)
        {
            columns.Add(new DocumentMetadataColumnInfo(DocumentMetadataColumn.IsSoftDeleted, "mt_deleted"));
        }

        if (hierarchy)
        {
            columns.Add(new DocumentMetadataColumnInfo(DocumentMetadataColumn.DocumentType, "mt_doc_type"));
        }

        return new DocumentTableInfo
        {
            Schema = Schema,
            Table = "mt_doc_order",
            Alias = "order",
            IdColumnType = id,
            MetadataColumns = columns,
            TenancyStyle = conjoined ? TenancyStyle.Conjoined : TenancyStyle.Single,
            SoftDeleteEnabled = softDeleted,
        };
    }

    private static string[] All(DocumentTableInfo table, string? tenant, string? subclass)
    {
        using NpgsqlCommand probe = DocumentShapeQueries.BuildSizeProbe(table, tenant, subclass, 201, 10);
        using NpgsqlCommand complete = DocumentShapeQueries.BuildCompleteKeys(table, tenant, subclass, 200, 10);
        using NpgsqlCommand sampled = DocumentShapeQueries.BuildSampledKeys(table, tenant, subclass, 25, 150, 10);
        using NpgsqlCommand rows = DocumentShapeQueries.BuildRows(table, tenant, subclass, [Guid.NewGuid()], [tenant], 262144, 10);

        return [probe.CommandText, complete.CommandText, sampled.CommandText, rows.CommandText];
    }

    [Fact]
    public void No_statement_is_random_or_a_block_sample_and_every_identifier_is_quoted()
    {
        foreach (string sql in All(Table(conjoined: true, softDeleted: true, hierarchy: true), "acme", "car"))
        {
            sql.Should().NotContainEquivalentOf("random(");
            sql.Should().NotContainEquivalentOf("tablesample");
            sql.Should().Contain("\"My Schema\".\"mt_doc_order\"");
        }
    }

    [Fact]
    public void An_identifier_with_a_quote_in_it_is_refused_and_never_escaped()
    {
        DocumentTableInfo table = Table() with { Schema = "we\"ird" };

        FluentActions.Invoking(() => DocumentShapeQueries.BuildSizeProbe(table, null, null, 201, 10))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Every_value_is_a_parameter_and_none_is_in_the_text()
    {
        foreach (string sql in All(Table(conjoined: true, hierarchy: true), Nasty, Nasty))
        {
            sql.Should().NotContain("drop table").And.NotContain("acme");
            sql.Should().Contain("@tenant").And.Contain("@subclass");
        }
    }

    [Fact]
    public void Each_scope_predicate_is_present_if_and_only_if_its_fact_is()
    {
        string plain = All(Table(), "acme", null)[0];
        plain.Should().NotContain("tenant_id").And.NotContain("mt_deleted").And.NotContain("mt_doc_type");

        // The subclass alias is only a predicate on a table that has the discriminator; asking for one without it is refused.
        FluentActions.Invoking(() => DocumentShapeQueries.BuildSizeProbe(Table(), null, "car", 201, 10))
            .Should().Throw<InvalidOperationException>();

        string noTenantInScope = All(Table(conjoined: true), null, null)[0];
        noTenantInScope.Should().NotContain("@tenant", "no tenant in scope reads every tenant of the database");

        string full = All(Table(conjoined: true, softDeleted: true, hierarchy: true), "acme", "Car")[0];
        full.Should().Contain("\"tenant_id\" = @tenant").And.Contain("\"mt_deleted\" = false").And.Contain("\"mt_doc_type\" = @subclass");

        using NpgsqlCommand command = DocumentShapeQueries.BuildSizeProbe(Table(hierarchy: true), null, "Car", 201, 10);
        command.Parameters["subclass"].Value.Should().Be("car", "Marten writes and compares the discriminator lower-cased");
    }

    [Fact]
    public void A_conjoined_table_with_a_tenant_but_no_tenant_column_is_refused_rather_than_read_across_tenants()
    {
        DocumentTableInfo table = Table() with { TenancyStyle = TenancyStyle.Conjoined };

        FluentActions.Invoking(() => DocumentShapeQueries.BuildSizeProbe(table, "acme", null, 201, 10))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_size_probe_is_bounded_and_the_keys_carry_the_stored_size()
    {
        using NpgsqlCommand probe = DocumentShapeQueries.BuildSizeProbe(Table(), null, null, 201, 10);
        using NpgsqlCommand complete = DocumentShapeQueries.BuildCompleteKeys(Table(), null, null, 200, 10);

        probe.CommandText.Should().Contain("limit @probe");
        probe.Parameters["probe"].Value.Should().Be(201);
        complete.CommandText.Should().Contain("pg_column_size").And.Contain("order by d.\"id\"").And.Contain("limit @sample");
        complete.CommandText.Should().NotContain("data::text", "the keys phase never reads a document");
    }

    [Fact]
    public void The_sampled_keys_are_three_bounded_branches_and_the_histogram_is_read_inside_the_statement()
    {
        using NpgsqlCommand sampled = DocumentShapeQueries.BuildSampledKeys(Table(), null, null, 25, 150, 10);

        sampled.CommandText.Should().Contain("order by d.\"id\" asc").And.Contain("order by d.\"id\" desc");
        sampled.CommandText.Split("limit @edge").Length.Should().Be(3, "both edges carry the same bound");
        sampled.CommandText.Should().Contain("pg_catalog.pg_stats").And.Contain("histogram_bounds::text::text[]")
            .And.Contain("::uuid").And.Contain("limit 1");
        sampled.Parameters["edge"].Value.Should().Be(25);
        sampled.Parameters["spread"].Value.Should().Be(150);
        sampled.Parameters["schema"].Value.Should().Be(Schema, "the schema and table are bound to pg_stats, not pasted into it");
        sampled.Parameters["table"].Value.Should().Be("mt_doc_order");
    }

    [Theory]
    [InlineData("Uuid", "::uuid")]
    [InlineData("Int4", "::integer")]
    [InlineData("Int8", "::bigint")]
    [InlineData("Text", "::text")]
    [InlineData("Varchar", "::varchar")]
    public void The_bound_is_cast_to_a_type_from_a_closed_table(string idName, string cast)
    {
        DocumentIdColumnType id = Enum.Parse<DocumentIdColumnType>(idName);

        using NpgsqlCommand sampled = DocumentShapeQueries.BuildSampledKeys(Table(id), null, null, 25, 150, 10);

        DocumentShapeQueries.CanSpread(Table(id)).Should().BeTrue();
        sampled.CommandText.Should().Contain("])" + cast, "the cast is the one the id column's type chooses");
    }

    [Fact]
    public void An_id_column_of_any_other_type_gets_no_spread_branch()
    {
        DocumentTableInfo table = Table(DocumentIdColumnType.Unknown);
        using NpgsqlCommand sampled = DocumentShapeQueries.BuildSampledKeys(table, null, null, 25, 150, 10);

        DocumentShapeQueries.CanSpread(table).Should().BeFalse();
        sampled.CommandText.Should().NotContain("pg_stats").And.NotContain("histogram");
        sampled.CommandText.Should().Contain("\"id\"::text", "an unknown id is read as text");
    }

    [Fact]
    public void The_rows_are_read_by_key_with_the_size_guard_and_a_typed_array()
    {
        using NpgsqlCommand rows = DocumentShapeQueries.BuildRows(Table(), null, null, [Guid.NewGuid(), Guid.NewGuid()], [null, null], 262144, 10);

        rows.CommandText.Should().Contain("= any(@ids)").And.Contain("octet_length(t.j) <= @maxBytes").And.Contain("order by d.\"id\"");
        rows.Parameters["ids"].Value.Should().BeOfType<Guid[]>();
        rows.Parameters["maxBytes"].Value.Should().Be(262144);
    }

    [Fact]
    public void Under_conjoined_tenancy_with_no_tenant_in_scope_a_key_is_tenant_and_id_together()
    {
        using NpgsqlCommand all = DocumentShapeQueries.BuildRows(
            Table(DocumentIdColumnType.Int4, conjoined: true), null, null, [1, 2], ["a", "b"], 262144, 10);
        using NpgsqlCommand one = DocumentShapeQueries.BuildRows(
            Table(DocumentIdColumnType.Int4, conjoined: true), "a", null, [1, 2], ["a", "a"], 262144, 10);

        all.CommandText.Should().Contain("in (select * from unnest(@tenants, @ids))");
        all.Parameters["ids"].Value.Should().BeOfType<int[]>();
        all.Parameters["tenants"].Value.Should().BeOfType<string[]>();
        one.CommandText.Should().Contain("= any(@ids)").And.NotContain("unnest").And.Contain("\"tenant_id\" = @tenant");
    }

    [Fact]
    public void Parameter_names_are_listed_without_values()
    {
        using NpgsqlCommand probe = DocumentShapeQueries.BuildSizeProbe(Table(conjoined: true), "acme", null, 201, 10);

        DocumentShapeQueries.ParameterNames(probe).Should().BeEquivalentTo("@tenant", "@probe");
    }
}
