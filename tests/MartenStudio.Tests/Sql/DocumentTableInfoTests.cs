using JasperFx.MultiTenancy;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The adapter from Marten's own mapping to the description the SQL builders read, tested against real
/// mappings out of a real <c>DocumentStore</c> (see <see cref="SqlTestStore"/>) rather than a fake.
/// </summary>
/// <remarks>
/// The load-bearing assertions here are the <em>absences</em>. A metadata column that the studio believes
/// in and the table does not have is a select list that fails at the database on every page render, and
/// <c>Metadata.X.Enabled</c> on its own does not answer the question — see
/// <see cref="DocumentTableInfo.FromDocumentType"/>.
/// </remarks>
public class DocumentTableInfoTests
{
    [Fact]
    public void A_fully_configured_type_carries_its_table_alias_tenancy_and_soft_delete()
    {
        var info = SqlTestTables.FullyFeatured();

        info.Schema.Should().Be(SqlTestStore.Schema);
        info.Table.Should().Be("mt_doc_sqltestcustomer");
        info.Alias.Should().Be("sqltestcustomer");
        info.DocumentTypeName.Should().Be(nameof(SqlTestCustomer));
        info.IdColumnType.Should().Be(DocumentIdColumnType.Uuid);
        info.TenancyStyle.Should().Be(TenancyStyle.Conjoined);
        info.SoftDeleteEnabled.Should().BeTrue();
        info.IsRegistered.Should().BeTrue();
        info.QualifiedName.Should().Be("\"studio_sql\".\"mt_doc_sqltestcustomer\"");
    }

    [Fact]
    public void Every_enabled_metadata_column_is_named_the_way_Marten_names_it()
    {
        var info = SqlTestTables.FullyFeatured();

        info.MetadataColumns.Select(x => x.ColumnName).Should().BeEquivalentTo(
        [
            "mt_deleted", "mt_deleted_at", "mt_version", "mt_last_modified", "mt_created_at", "tenant_id",
            "mt_dotnet_type", "causation_id", "correlation_id", "last_modified_by", "headers",
        ]);
    }

    /// <summary>
    /// The correction to the plan's Appendix B. A document with no configuration at all reports
    /// <c>IsSoftDeleted</c>, <c>SoftDeletedAt</c>, <c>TenantId</c> and <c>DocumentType</c> as
    /// <c>Enabled</c>, and its table has none of those columns.
    /// </summary>
    [Fact]
    public void A_plain_type_has_no_soft_delete_no_tenant_and_no_doc_type_column_whatever_Enabled_says()
    {
        var documentType = SqlTestStore.DocumentType<SqlTestNote>();

        documentType.Metadata.IsSoftDeleted.Enabled.Should().BeTrue(
            "Marten reports Enabled = true here, which is exactly why the adapter cannot use it alone");
        documentType.Metadata.TenantId.Enabled.Should().BeTrue();
        documentType.Metadata.DocumentType.Enabled.Should().BeTrue();

        var info = DocumentTableInfo.FromDocumentType(documentType);

        info.SoftDeleteEnabled.Should().BeFalse();
        info.TenancyStyle.Should().Be(TenancyStyle.Single);
        info.MetadataColumns.Select(x => x.ColumnName).Should().BeEquivalentTo(
            ["mt_last_modified", "mt_version", "mt_dotnet_type"],
            "those three are what Marten actually puts on an unconfigured document table");
    }

    [Fact]
    public void DisableInformationalFields_leaves_a_table_of_id_and_data()
    {
        var info = SqlTestTables.MetadataLess();

        info.MetadataColumns.Should().BeEmpty();
        info.DuplicatedColumns.Should().BeEmpty();
        info.SoftDeleteEnabled.Should().BeFalse();
        info.TenancyStyle.Should().Be(TenancyStyle.Single);
    }

    [Fact]
    public void A_hierarchy_gains_the_doc_type_column()
    {
        var info = DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestCustomer>(options =>
            options.Schema.For<SqlTestCustomer>().AddSubClass<SqlTestVipCustomer>()));

        info.HasMetadata(DocumentMetadataColumn.DocumentType).Should().BeTrue();
        info.MetadataColumnName(DocumentMetadataColumn.DocumentType).Should().Be("mt_doc_type");
    }

    [Fact]
    public void A_duplicated_field_carries_its_column_name_and_its_Npgsql_type()
    {
        var info = SqlTestTables.FullyFeatured();

        var email = info.DuplicatedColumns.Single(x => x.ColumnName == "email");

        email.MemberPath.Should().Be("Email");
        email.DbType.Should().Be(NpgsqlTypes.NpgsqlDbType.Text);
        email.PgType.Should().Be("varchar");
    }

    /// <summary>
    /// Marten runs a nested member path together: <c>Duplicate(x =&gt; x.Address.City)</c> is reported as
    /// <c>AddressCity</c>, not <c>Address.City</c>. A search for <c>address.city</c> still has to find it.
    /// </summary>
    [Fact]
    public void A_nested_duplicated_field_is_found_by_its_dotted_path()
    {
        var info = SqlTestTables.FullyFeatured();

        info.DuplicatedColumns.Select(x => x.MemberPath).Should().Contain("AddressCity");

        info.FindDuplicated(["Address", "City"])!.ColumnName.Should().Be("address_city");
        info.FindDuplicated(["address", "city"])!.ColumnName.Should().Be("address_city");
        info.FindDuplicated(["AddressCity"])!.ColumnName.Should().Be("address_city");
        info.FindDuplicated(["address_city"])!.ColumnName.Should().Be("address_city");
        info.FindDuplicated(["Address", "Street"]).Should().BeNull();
        info.FindDuplicated([]).Should().BeNull();
    }

    [Theory]
    [InlineData("uuid", "Uuid")]
    [InlineData("int4", "Int4")]
    [InlineData("int8", "Int8")]
    [InlineData("text", "Text")]
    [InlineData("varchar", "Varchar")]
    [InlineData("citext", "Unknown")]
    [InlineData("", "Unknown")]
    public void The_id_column_type_is_read_from_the_udt_name(string udtName, string expected) =>
        DocumentIdColumnTypes.FromUdtName(udtName).ToString().Should().Be(expected);

    [Fact]
    public void The_id_column_type_falls_back_to_the_CLR_id_type_before_a_connection_exists()
    {
        DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestTicket>())
            .IdColumnType.Should().Be(DocumentIdColumnType.Varchar);

        DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestLedgerEntry>())
            .IdColumnType.Should().Be(DocumentIdColumnType.Int8);
    }

    [Fact]
    public void The_physical_columns_win_over_the_configuration()
    {
        // Schema drift: the store believes in a duplicated column and a soft-delete column that the table
        // does not have, and its id column is a domain type no CLR type could have predicted.
        var drifted = SqlTestTables.FullyFeatured().WithPhysicalColumns(new TableColumns(
            SqlTestStore.Schema,
            "mt_doc_sqltestcustomer",
            [
                new PostgresColumn("id", "bigint", "int8", false),
                new PostgresColumn("data", "jsonb", "jsonb", false),
                new PostgresColumn("mt_last_modified", "timestamp with time zone", "timestamptz", true),
                new PostgresColumn("email", "character varying", "varchar", true),
            ]));

        drifted.IdColumnType.Should().Be(DocumentIdColumnType.Int8);
        drifted.SoftDeleteEnabled.Should().BeFalse();
        drifted.TenancyStyle.Should().Be(TenancyStyle.Single);
        drifted.MetadataColumns.Select(x => x.ColumnName).Should().BeEquivalentTo(["mt_last_modified"]);
        drifted.DuplicatedColumns.Select(x => x.ColumnName).Should().BeEquivalentTo(["email"]);
    }

    /// <summary>
    /// Drift the other way: the <em>table</em> has <c>tenant_id</c>, <c>mt_deleted</c> and
    /// <c>mt_deleted_at</c> and the mapping does not. Reading the tenancy style off the physical column
    /// while leaving the metadata list alone left the builders knowing they had to filter by tenant and
    /// having no column name to filter with, which surfaced as an exception about a metadata column being
    /// disabled on a table that plainly has it.
    /// </summary>
    [Fact]
    public void A_physical_tenant_or_soft_delete_column_the_configuration_lacks_is_reconciled_in()
    {
        var plain = DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestNote>());

        plain.TenancyStyle.Should().Be(TenancyStyle.Single);
        plain.HasMetadata(DocumentMetadataColumn.TenantId).Should().BeFalse();

        var reconciled = plain.WithPhysicalColumns(new TableColumns(
            SqlTestStore.Schema,
            "mt_doc_sqltestnote",
            [
                new PostgresColumn("id", "uuid", "uuid", false),
                new PostgresColumn("data", "jsonb", "jsonb", false),
                new PostgresColumn("mt_last_modified", "timestamp with time zone", "timestamptz", true),
                new PostgresColumn("tenant_id", "character varying", "varchar", false),
                new PostgresColumn("mt_deleted", "boolean", "bool", false),
                new PostgresColumn("mt_deleted_at", "timestamp with time zone", "timestamptz", true),
            ]));

        reconciled.TenancyStyle.Should().Be(TenancyStyle.Conjoined);
        reconciled.SoftDeleteEnabled.Should().BeTrue();

        reconciled.MetadataColumnName(DocumentMetadataColumn.TenantId).Should().Be("tenant_id");
        reconciled.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted).Should().Be("mt_deleted");
        reconciled.MetadataColumnName(DocumentMetadataColumn.SoftDeletedAt).Should().Be("mt_deleted_at");
    }

    [Fact]
    public void A_reconciled_column_is_added_once_and_never_over_a_configured_one()
    {
        var configured = SqlTestTables.FullyFeatured();

        var reconciled = configured.WithPhysicalColumns(new TableColumns(
            SqlTestStore.Schema,
            "mt_doc_sqltestcustomer",
            [
                new PostgresColumn("id", "uuid", "uuid", false),
                new PostgresColumn("data", "jsonb", "jsonb", false),
                new PostgresColumn("tenant_id", "character varying", "varchar", false),
                new PostgresColumn("mt_deleted", "boolean", "bool", false),
                new PostgresColumn("mt_deleted_at", "timestamp with time zone", "timestamptz", true),
            ]));

        reconciled.MetadataColumns.Select(x => x.Column).Should().OnlyHaveUniqueItems();
        reconciled.MetadataColumns.Select(x => x.ColumnName).Should().BeEquivalentTo(
            ["tenant_id", "mt_deleted", "mt_deleted_at"]);
    }

    /// <summary>
    /// Marten's default <c>RevisionColumn</c> is <c>bigint</c>; the <c>integer</c> one is used only for a
    /// document implementing <c>IRevisioned</c>. The static guess says so, and once the catalog has been
    /// read the physical type wins over the guess either way.
    /// </summary>
    [Fact]
    public void The_revision_column_is_a_bigint_unless_the_catalog_says_otherwise()
    {
        var revisioned = DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestNote>(options =>
            options.Schema.For<SqlTestNote>().UseNumericRevisions(true)));

        var revision = revisioned.MetadataColumns.Single(x => x.Column == DocumentMetadataColumn.Revision);

        revision.ColumnName.Should().Be("mt_version", "Marten keeps the revision in the version column");
        revision.PhysicalDbType.Should().BeNull("no catalog has been consulted yet");
        revision.DbType.Should().Be(NpgsqlTypes.NpgsqlDbType.Bigint);

        var read = revisioned.WithPhysicalColumns(new TableColumns(
            SqlTestStore.Schema,
            "mt_doc_sqltestnote",
            [
                new PostgresColumn("id", "uuid", "uuid", false),
                new PostgresColumn("data", "jsonb", "jsonb", false),
                new PostgresColumn("mt_version", "integer", "int4", false),
            ]));

        read.MetadataColumns.Single(x => x.Column == DocumentMetadataColumn.Revision)
            .DbType.Should().Be(NpgsqlTypes.NpgsqlDbType.Integer, "an IRevisioned document gets int4");
    }

    [Fact]
    public void A_column_type_the_studio_does_not_recognise_falls_back_to_the_static_guess()
    {
        var read = SqlTestTables.FullyFeatured().WithPhysicalColumns(new TableColumns(
            SqlTestStore.Schema,
            "mt_doc_sqltestcustomer",
            [
                new PostgresColumn("id", "uuid", "uuid", false),
                new PostgresColumn("data", "jsonb", "jsonb", false),
                new PostgresColumn("tenant_id", "USER-DEFINED", "citext", false),
            ]));

        read.MetadataColumns.Single(x => x.Column == DocumentMetadataColumn.TenantId)
            .DbType.Should().Be(NpgsqlTypes.NpgsqlDbType.Varchar, "Unknown is a worse answer than the guess");
    }

    [Fact]
    public void A_catalog_read_that_saw_nothing_changes_nothing()
    {
        var configured = SqlTestTables.FullyFeatured();

        var unchanged = configured.WithPhysicalColumns(
            new TableColumns(SqlTestStore.Schema, "mt_doc_sqltestcustomer", []));

        unchanged.Should().Be(configured, "an empty catalog read means the table could not be seen");
    }

    [Fact]
    public void An_unregistered_table_is_described_entirely_from_its_columns()
    {
        var info = DocumentTableInfo.FromDiscoveredTable(
            "other",
            "mt_doc_legacything",
            [
                new PostgresColumn("id", "uuid", "uuid", false),
                new PostgresColumn("data", "jsonb", "jsonb", false),
                new PostgresColumn("mt_last_modified", "timestamp with time zone", "timestamptz", true),
                new PostgresColumn("tenant_id", "character varying", "varchar", false),
                new PostgresColumn("mt_deleted", "boolean", "bool", false),
                new PostgresColumn("legacy_code", "integer", "int4", true),
            ]);

        info.IsRegistered.Should().BeFalse();
        info.Alias.Should().Be("legacything");
        info.IdColumnType.Should().Be(DocumentIdColumnType.Uuid);
        info.TenancyStyle.Should().Be(TenancyStyle.Conjoined);
        info.SoftDeleteEnabled.Should().BeTrue();
        info.MetadataColumns.Select(x => x.Column).Should().BeEquivalentTo(
        [
            DocumentMetadataColumn.LastModified,
            DocumentMetadataColumn.TenantId,
            DocumentMetadataColumn.IsSoftDeleted,
        ]);

        // Anything left over on an mt_doc_ table is a duplicated field by construction.
        var legacy = info.DuplicatedColumns.Single();

        legacy.ColumnName.Should().Be("legacy_code");
        legacy.DbType.Should().Be(NpgsqlTypes.NpgsqlDbType.Integer);
    }
}
