using Marten;
using Marten.Schema;

using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services.Documents;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// The related-documents strip against the id shape that used to break it.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2-fix H4.</b> The strip described its target with <c>DocumentTableInfo.FromDocumentType(target)</c>
/// and nothing else. That is the offline view: <c>IdColumnType</c> is guessed from the CLR
/// <c>IdType</c>, and a strong-typed id — a record struct wrapping a Guid, which Marten stores in a
/// <c>uuid</c> column — guesses <see cref="MartenStudio.Internal.Sql.DocumentIdColumnType.Unknown"/>. The
/// probe then bound the id as an untyped literal, which cannot use the primary key and is one Postgres
/// type-resolution rule away from failing outright.
/// </para>
/// <para>
/// The demo domain has a strong-typed id (<c>OrderId</c>) but nothing pointing <em>at</em> it, so this
/// class adds a document type of its own with a foreign key to <c>Order</c>. The sample's own
/// <c>Order → Customer</c> key — a plain Guid — is asserted alongside it, because the fix must not change
/// the case that already worked.
/// </para>
/// </remarks>
public class DocumentRelatedLiveTests(DocumentRelatedLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentRelatedLiveTests.Fixture>
{
    /// <summary>A note about an order: a foreign key whose target has a strong-typed id.</summary>
    [DocumentAlias("ordernote")]
    public class OrderNote
    {
        /// <summary>A plain Guid id.</summary>
        public Guid Id { get; set; }

        /// <summary>The order this note is about, duplicated into a <c>uuid</c> column by the foreign key.</summary>
        public Guid OrderId { get; set; }

        /// <summary>The note.</summary>
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>The demo store plus one extra document type.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>The id of the order the seeded note points at.</summary>
        public Guid LinkedOrderId { get; private set; }

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options) =>
            options.Schema.For<OrderNote>().ForeignKey<Order>(x => x.OrderId);

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            await using IQuerySession query = Marten.Store.QuerySession();

            Order order = await query.Query<Order>().OrderBy(x => x.Reference).FirstAsync();

            LinkedOrderId = order.Id.Value;

            await using IDocumentSession session = Marten.Store.LightweightSession();

            session.Store(new OrderNote
            {
                Id = Guid.NewGuid(),
                OrderId = LinkedOrderId,
                Text = "Chased the courier.",
            });

            await session.SaveChangesAsync();
        }
    }

    [PostgresFact]
    public async Task A_foreign_key_to_a_strong_typed_id_resolves_against_the_real_column_type()
    {
        using var documents = Documents();

        DocumentPage notes = await documents.Service.ListAsync(
            Scope, "ordernote", new DocumentListRequest { PageSize = 100 }, TestContext.Current.CancellationToken);

        notes.State.Should().Be(DocumentListState.Loaded, notes.Error);

        var id = notes.Rows.Should().ContainSingle().Subject.Id;

        RelatedDocumentLink link = await LinkAsync(documents, id);

        link.TargetAlias.Should().Be("order");
        link.ColumnName.Should().Be("order_id");
        link.TargetId.Should().Be(fixture.LinkedOrderId.ToString("D"));
        link.Exists.Should().BeTrue();

        // The other answer, so "exists" is not a constant a broken probe would also produce. The database
        // constraint has to go first - it is the reason a dangling value cannot simply be seeded - and it
        // is not what the studio reads: the link comes from the ForeignKey declared in StoreOptions.
        await DropForeignKeyAsync();
        await SetOrderColumnAsync(id, Guid.NewGuid());

        try
        {
            (await LinkAsync(documents, id)).Exists.Should().BeFalse("no order has that id");
        }
        finally
        {
            await SetOrderColumnAsync(id, fixture.LinkedOrderId);
        }
    }

    private async Task<RelatedDocumentLink> LinkAsync(
        MartenFixture.ScopedService<IDocumentDataService> documents,
        string id)
    {
        RelatedDocuments related = await documents.Service.GetRelatedAsync(
            Scope, "ordernote", id, TestContext.Current.CancellationToken);

        related.Error.Should().BeNull();

        return related.Links.Should().ContainSingle().Subject;
    }

    private async Task DropForeignKeyAsync()
    {
        await using Npgsql.NpgsqlConnection connection = await Postgres.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new Npgsql.NpgsqlCommand(
            $"alter table \"{Schema}\".\"mt_doc_ordernote\" " +
            "drop constraint if exists mt_doc_ordernote_order_id_fkey",
            connection);

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task SetOrderColumnAsync(string id, Guid orderId)
    {
        await using Npgsql.NpgsqlConnection connection = await Postgres.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new Npgsql.NpgsqlCommand(
            $"update \"{Schema}\".\"mt_doc_ordernote\" set order_id = @order where id = @id", connection);

        command.Parameters.AddWithValue("order", orderId);
        command.Parameters.AddWithValue("id", Guid.Parse(id));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The overload that takes the loaded row answers exactly as the one that reloads it.</summary>
    /// <remarks>
    /// The detail page has the document in its hand; asking for the related documents by id read the same
    /// row a second time, with a second scope resolution, a second connection and a second size query
    /// (P2-fix follow-up 5).
    /// </remarks>
    [PostgresFact]
    public async Task The_overload_that_takes_the_loaded_document_answers_the_same()
    {
        using var documents = Documents();

        DocumentPage notes = await documents.Service.ListAsync(
            Scope, "ordernote", new DocumentListRequest { PageSize = 100 }, TestContext.Current.CancellationToken);

        var id = notes.Rows[0].Id;

        DocumentDetail detail = (await documents.Service.GetDocumentAsync(
            Scope, "ordernote", id, TestContext.Current.CancellationToken)).Detail!;

        RelatedDocuments byId = await documents.Service.GetRelatedAsync(
            Scope, "ordernote", id, TestContext.Current.CancellationToken);

        RelatedDocuments byDetail = await documents.Service.GetRelatedAsync(
            Scope, "ordernote", detail, TestContext.Current.CancellationToken);

        byDetail.Links.Should().BeEquivalentTo(byId.Links);
    }

    /// <summary>The sample's own plain-Guid foreign key still resolves.</summary>
    [PostgresFact]
    public async Task The_plain_guid_foreign_key_of_the_demo_domain_still_works()
    {
        using var documents = Documents();

        DocumentPage orders = await documents.Service.ListAsync(
            Scope, "order", new DocumentListRequest(), TestContext.Current.CancellationToken);

        RelatedDocuments related = await documents.Service.GetRelatedAsync(
            Scope, "order", orders.Rows[0].Id, TestContext.Current.CancellationToken);

        RelatedDocumentLink link = related.Links.Should().ContainSingle().Subject;

        link.TargetAlias.Should().Be("customer");
        link.Exists.Should().BeTrue();
    }
}
