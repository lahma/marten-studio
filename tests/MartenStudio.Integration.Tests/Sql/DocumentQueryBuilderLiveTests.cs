using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Integration.Tests.Sql;

/// <summary>
/// The document reads against real <c>mt_doc_</c>-shaped tables — one carrying every metadata column
/// Marten can add, one carrying none of them.
/// </summary>
/// <remarks>
/// The metadata-less table is the half that catches real bugs. A store configured with
/// <c>Policies.DisableInformationalFields()</c> has a document table of <c>id</c> and <c>data</c>, and any
/// select list that assumed <c>mt_last_modified</c> would fail on every page render against it — a failure
/// no amount of testing against the default configuration would ever show.
/// </remarks>
public class DocumentQueryBuilderLiveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly Guid FirstId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ThirdId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DeletedId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherTenantId = new("55555555-5555-5555-5555-555555555555");

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        // The full table is written the way Marten writes one: tenant_id first, the informational columns,
        // the duplicated fields, then the soft-delete pair.
        await ExecuteAsync(connection, $$"""
            create table "{{Schema}}".mt_doc_customer (
                tenant_id varchar not null default '*DEFAULT*',
                id uuid not null,
                data jsonb not null,
                mt_last_modified timestamptz not null default transaction_timestamp(),
                mt_version uuid not null default gen_random_uuid(),
                mt_dotnet_type varchar,
                mt_created_at timestamptz,
                correlation_id varchar,
                causation_id varchar,
                last_modified_by varchar,
                headers jsonb,
                email varchar,
                address_city varchar,
                order_count int,
                mt_deleted boolean not null default false,
                mt_deleted_at timestamptz,
                primary key (tenant_id, id)
            );

            insert into "{{Schema}}".mt_doc_customer
                (tenant_id, id, data, mt_last_modified, email, address_city, order_count, mt_deleted)
            values
                ('acme', '{{FirstId}}', '{"Name":"Ada","Status":"open","Total":120.5,"Address":{"City":"Helsinki"} }',
                 '2026-01-01T10:00:00Z', 'ada@example.com', 'Helsinki', 3, false),
                ('acme', '{{SecondId}}', '{"Name":"Bob","Status":"closed","Total":80,"Address":{"City":"Tampere"} }',
                 '2026-01-02T10:00:00Z', 'bob@example.com', 'Tampere', 1, false),
                ('acme', '{{ThirdId}}', '{"Name":"Cid","Status":"open","Total":900,"Address":{"City":"Helsinki"} }',
                 '2026-01-03T10:00:00Z', 'cid@example.com', 'Helsinki', 12, false),
                ('acme', '{{DeletedId}}', '{"Name":"Dot","Status":"open"}',
                 '2026-01-04T10:00:00Z', 'dot@example.com', null, 0, true),
                ('other', '{{OtherTenantId}}', '{"Name":"Eve","Status":"open"}',
                 '2026-01-05T10:00:00Z', 'eve@example.com', null, 7, false);

            create table "{{Schema}}".mt_doc_note (
                id uuid not null primary key,
                data jsonb not null
            );

            insert into "{{Schema}}".mt_doc_note values
                ('{{FirstId}}', '{"Text":"first"}'),
                ('{{SecondId}}', '{"Text":"second"}');
            """);
    }

    [PostgresFact]
    public async Task The_list_of_a_fully_featured_collection_runs_and_brings_back_every_column()
    {
        var table = await FullTableAsync();

        var rows = await ReadAsync(table, new DocumentListQuery { TenantId = "acme" });

        rows.Should().HaveCount(3, "the soft-deleted one is excluded and the other tenant's is filtered out");
        rows[0].Should().ContainKey("data");
        rows[0].Should().ContainKey("data_bytes");
        rows[0].Should().ContainKey("mt_last_modified");
        rows[0].Should().ContainKey("email");
        rows[0]["data"].Should().NotBeNull("the documents are small enough to inline");
    }

    [PostgresFact]
    public async Task The_list_of_a_collection_with_no_metadata_at_all_runs()
    {
        var table = await BareTableAsync();

        var rows = await ReadAsync(table, new DocumentListQuery());

        rows.Should().HaveCount(2);
        rows[0].Keys.Should().BeEquivalentTo(["id", "data", "data_bytes"]);
    }

    [PostgresFact]
    public async Task A_document_bigger_than_the_inline_budget_comes_back_as_a_size_and_not_as_text()
    {
        var table = await BareTableAsync();

        var rows = await ReadAsync(table, new DocumentListQuery { MaxInlineDocumentBytes = 5 });

        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(row =>
        {
            row["data"].Should().BeNull("the document is past the inline budget");
            Convert.ToInt32(row["data_bytes"], System.Globalization.CultureInfo.InvariantCulture)
                .Should().BeGreaterThan(5);
        });
    }

    [PostgresFact]
    public async Task The_deleted_tri_state_answers_all_three_ways()
    {
        var table = await FullTableAsync();

        (await ReadAsync(table, new DocumentListQuery { TenantId = "acme" })).Should().HaveCount(3);

        (await ReadAsync(table, new DocumentListQuery { TenantId = "acme", IncludeDeleted = DeletedFilter.Include }))
            .Should().HaveCount(4);

        var onlyDeleted = await ReadAsync(
            table, new DocumentListQuery { TenantId = "acme", IncludeDeleted = DeletedFilter.Only });

        onlyDeleted.Should().ContainSingle();
        onlyDeleted.Single()["id"].Should().Be(DeletedId);
    }

    [PostgresFact]
    public async Task The_tenant_filter_keeps_one_tenants_documents_out_of_anothers_page()
    {
        var table = await FullTableAsync();

        var acme = await ReadAsync(table, new DocumentListQuery { TenantId = "acme" });
        var other = await ReadAsync(table, new DocumentListQuery { TenantId = "other" });

        acme.Select(x => x["id"]).Should().NotContain(OtherTenantId);
        other.Should().ContainSingle();
    }

    [PostgresFact]
    public async Task A_filter_on_a_duplicated_column_finds_the_right_documents()
    {
        var table = await FullTableAsync();

        var rows = await ReadAsync(table, new DocumentListQuery
        {
            TenantId = "acme",
            Predicates = SearchGrammar.Parse("address_city = Helsinki order_count > 5").Predicates,
        });

        rows.Should().ContainSingle();
        rows.Single()["id"].Should().Be(ThirdId);
    }

    [PostgresFact]
    public async Task A_filter_through_the_json_path_finds_the_right_documents()
    {
        var table = await BareTableAsync();

        var rows = await ReadAsync(table, new DocumentListQuery
        {
            Predicates = SearchGrammar.Parse("Text = second").Predicates,
        });

        rows.Should().ContainSingle();
        rows.Single()["id"].Should().Be(SecondId);
    }

    [PostgresFact]
    public async Task A_numeric_comparison_through_the_json_casts_and_compares_as_a_number()
    {
        var table = await FullTableAsync();

        var rows = await ReadAsync(table, new DocumentListQuery
        {
            TenantId = "acme",
            Predicates = SearchGrammar.Parse("Total > 100").Predicates,
        });

        // 900 and 120.5 are greater than 100; "80" as text would have sorted above "100" and is not here.
        rows.Select(x => x["id"]).Should().BeEquivalentTo([FirstId, ThirdId]);
    }

    [PostgresFact]
    public async Task Containment_free_text_and_a_contains_match_all_run()
    {
        var table = await FullTableAsync();

        var containment = await ReadAsync(table, new DocumentListQuery
        {
            TenantId = "acme",
            Predicates = SearchGrammar.Parse("""@> {"Status":"closed"}""").Predicates,
        });

        containment.Should().ContainSingle();

        var freeText = await ReadAsync(table, new DocumentListQuery
        {
            TenantId = "acme",
            Predicates = SearchGrammar.Parse("Tampere").Predicates,
        });

        freeText.Should().ContainSingle();

        var like = await ReadAsync(table, new DocumentListQuery
        {
            TenantId = "acme",
            Predicates = SearchGrammar.Parse("address.city ~ helsin").Predicates,
        });

        like.Should().HaveCount(2, "ilike is case insensitive");
    }

    [PostgresFact]
    public async Task A_json_path_column_is_selected_by_its_path_parameter()
    {
        var table = await BareTableAsync();

        var rows = await ReadAsync(table, new DocumentListQuery
        {
            Columns = [new DocumentColumn.JsonPath(["Text"])],
        });

        rows.Select(x => x["json_0"]).Should().BeEquivalentTo(["first", "second"]);
    }

    [PostgresFact]
    public async Task Sorting_by_a_metadata_column_orders_the_page_and_ends_in_the_id()
    {
        var table = await FullTableAsync();

        var rows = await ReadAsync(table, new DocumentListQuery
        {
            TenantId = "acme",
            Sort = new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified),
            Direction = SortDirection.Descending,
        });

        rows.Select(x => x["id"]).Should().Equal(ThirdId, SecondId, FirstId);
    }

    [PostgresFact]
    public async Task A_keyset_walk_visits_every_document_exactly_once()
    {
        var table = await FullTableAsync();

        List<object?> seen = [];
        DocumentKeysetCursor? cursor = null;

        for (var page = 0; page < 5; page++)
        {
            var rows = await ReadAsync(table, new DocumentListQuery
            {
                TenantId = "acme",
                Sort = new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified),
                PageSize = 2,
                Cursor = cursor,
            });

            if (rows.Count == 0)
            {
                break;
            }

            seen.AddRange(rows.Select(x => x["id"]));

            var last = rows[^1];

            cursor = new DocumentKeysetCursor(
                ((DateTime)last["mt_last_modified"]!).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ((Guid)last["id"]!).ToString("D"));
        }

        seen.Should().Equal(FirstId, SecondId, ThirdId);
        seen.Should().OnlyHaveUniqueItems();
    }

    [PostgresFact]
    public async Task An_offset_page_walks_the_same_order()
    {
        var table = await FullTableAsync();

        var second = await ReadAsync(table, new DocumentListQuery
        {
            TenantId = "acme",
            Sort = new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified),
            PageSize = 1,
            Offset = 1,
        });

        second.Single()["id"].Should().Be(SecondId);
    }

    [PostgresFact]
    public async Task The_single_document_read_brings_back_the_whole_json_and_its_metadata()
    {
        var table = await FullTableAsync();

        DocumentQueryBuilder.TryBuildSingle(table, FirstId.ToString("D"), "acme", out var command, out _)
            .Should().BeTrue();

        await using var connection = await OpenAsync();

        using (command)
        {
            command!.Connection = connection;

            await using var reader = await command.ExecuteReaderAsync();

            (await reader.ReadAsync()).Should().BeTrue();

            reader.GetString(reader.GetOrdinal("data")).Should().Contain("Ada");
            reader.GetInt32(reader.GetOrdinal("data_bytes")).Should().BeGreaterThan(0);
            reader.GetString(reader.GetOrdinal("email")).Should().Be("ada@example.com");
        }
    }

    [PostgresFact]
    public async Task The_single_document_read_of_a_bare_collection_runs()
    {
        var table = await BareTableAsync();

        DocumentQueryBuilder.TryBuildSingle(table, SecondId.ToString("D"), null, out var command, out _)
            .Should().BeTrue();

        await using var connection = await OpenAsync();

        using (command)
        {
            command!.Connection = connection;

            await using var reader = await command.ExecuteReaderAsync();

            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetString(0).Should().Contain("second");
        }
    }

    [PostgresFact]
    public async Task A_property_name_that_looks_like_SQL_is_a_property_name()
    {
        var table = await BareTableAsync();

        var rows = await ReadAsync(table, new DocumentListQuery
        {
            Columns = [new DocumentColumn.JsonPath(["'); drop table mt_doc_note; --"])],
        });

        rows.Should().HaveCount(2);
        rows.Should().AllSatisfy(row => row["json_0"].Should().BeNull());

        await using var connection = await OpenAsync();

        var stillThere = await ScalarAsync(
            connection,
            $"select count(*) from information_schema.tables where table_schema = '{Schema}' and table_name = 'mt_doc_note'");

        stillThere.Should().Be(1L);
    }

    private async Task<DocumentTableInfo> FullTableAsync() => await DescribeAsync("mt_doc_customer");

    private async Task<DocumentTableInfo> BareTableAsync() => await DescribeAsync("mt_doc_note");

    private async Task<DocumentTableInfo> DescribeAsync(string table)
    {
        await using var connection = await OpenAsync();

        var columns = await new ColumnCatalog().GetAsync(connection, Schema, table);

        return DocumentTableInfo.FromDiscoveredTable(Schema, table, columns.Columns);
    }

    private async Task<List<Dictionary<string, object?>>> ReadAsync(DocumentTableInfo table, DocumentListQuery query)
    {
        using var command = DocumentQueryBuilder.BuildList(table, query);

        await using var connection = await OpenAsync();

        command.Connection = connection;

        await using var reader = await command.ExecuteReaderAsync();

        List<Dictionary<string, object?>> rows = [];

        while (await reader.ReadAsync())
        {
            Dictionary<string, object?> row = new(StringComparer.Ordinal);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }
}
