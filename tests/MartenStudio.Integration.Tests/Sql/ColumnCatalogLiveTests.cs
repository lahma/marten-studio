using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Integration.Tests.Sql;

/// <summary>
/// The catalog reader, against a hand-made table. What matters is that the <c>udt_name</c> — not the
/// friendly <c>data_type</c> — is what the studio branches on, because <c>timestamp with time zone</c> and
/// <c>character varying</c> are not names any code should be comparing against.
/// </summary>
public class ColumnCatalogLiveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, $"""
            create table "{Schema}".shapes (
                id uuid not null primary key,
                data jsonb not null,
                label varchar(100),
                counted bigint not null,
                seen timestamp with time zone,
                payload bytea
            );
            """);
    }

    [PostgresFact]
    public async Task Every_column_comes_back_in_ordinal_order_with_its_types_and_nullability()
    {
        var catalog = new ColumnCatalog();

        await using var connection = await OpenAsync();

        var columns = await catalog.GetAsync(connection, Schema, "shapes");

        columns.Exists.Should().BeTrue();
        columns.Columns.Select(x => x.Name).Should().Equal("id", "data", "label", "counted", "seen", "payload");

        columns.Find("id")!.UdtName.Should().Be("uuid");
        columns.Find("id")!.IsNullable.Should().BeFalse();
        columns.Find("label")!.UdtName.Should().Be("varchar");
        columns.Find("label")!.DataType.Should().Be("character varying");
        columns.Find("label")!.IsNullable.Should().BeTrue();
        columns.Find("counted")!.UdtName.Should().Be("int8");
        columns.Find("seen")!.UdtName.Should().Be("timestamptz");
        columns.Find("payload")!.UdtName.Should().Be("bytea");
        columns.Find("nothing").Should().BeNull();
        columns.Has("data").Should().BeTrue();
    }

    [PostgresFact]
    public async Task A_table_that_is_not_there_reads_as_not_existing_rather_than_throwing()
    {
        var catalog = new ColumnCatalog();

        await using var connection = await OpenAsync();

        var columns = await catalog.GetAsync(connection, Schema, "no_such_table");

        columns.Exists.Should().BeFalse();
        columns.Columns.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task The_answer_is_cached_until_it_is_invalidated()
    {
        var catalog = new ColumnCatalog();

        await using var connection = await OpenAsync();

        var first = await catalog.GetAsync(connection, Schema, "shapes");

        await ExecuteAsync(connection, $"alter table \"{Schema}\".shapes add column added int");

        var cached = await catalog.GetAsync(connection, Schema, "shapes");

        cached.Should().BeSameAs(first, "a column set does not change without a migration, so it is cached");

        catalog.Invalidate(connection, Schema, "shapes");

        var reread = await catalog.GetAsync(connection, Schema, "shapes");

        reread.Has("added").Should().BeTrue();
    }

    [PostgresFact]
    public async Task A_discovered_table_can_be_described_entirely_from_the_catalog()
    {
        var catalog = new ColumnCatalog();

        await using var connection = await OpenAsync();

        await ExecuteAsync(connection, $"""
            create table "{Schema}".mt_doc_legacy (
                id uuid not null primary key,
                data jsonb not null,
                mt_last_modified timestamptz,
                tenant_id varchar not null default '*DEFAULT*',
                mt_deleted boolean not null default false,
                legacy_code int
            );
            """);

        var columns = await catalog.GetAsync(connection, Schema, "mt_doc_legacy");
        var table = DocumentTableInfo.FromDiscoveredTable(Schema, "mt_doc_legacy", columns.Columns);

        table.Alias.Should().Be("legacy");
        table.IdColumnType.Should().Be(DocumentIdColumnType.Uuid);
        table.SoftDeleteEnabled.Should().BeTrue();
        table.DuplicatedColumns.Single().ColumnName.Should().Be("legacy_code");

        // And the description is good enough to build a query the database accepts.
        using var command = DocumentQueryBuilder.BuildList(table, new MartenStudio.Services.Query.DocumentListQuery());

        command.Connection = connection;

        await using var reader = await command.ExecuteReaderAsync();

        reader.HasRows.Should().BeFalse("the table is empty, but the statement is valid SQL against it");
    }
}
