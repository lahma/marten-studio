using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services.Documents;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// <c>IsDocumentTypeVisible</c> as a gate on the <em>read</em> path, not as a filter on the rail.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2-fix B3.</b> The collections rail built its "tables Marten already knows about" set out of the
/// <em>visible</em> mappings, and then offered every other <c>mt_doc_*</c> table it found as "Discovered
/// (unregistered)". A hidden type's table is exactly such a table: it came straight back in the discovered
/// band, with a row count, a list with every column <c>information_schema</c> could describe, and a detail
/// page — which is the whole of the data the host hid, under a different heading.
/// </para>
/// <para>
/// The store below is the demo domain with <c>Customer</c> hidden. Marten still creates
/// <c>mt_doc_customer</c> and the seeder still writes twenty-five customers into it, so there is real data
/// to leak; the assertions are that none of it can be reached, by any of the four ways in.
/// </para>
/// </remarks>
public class DocumentVisibilityLiveTests(DocumentVisibilityLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentVisibilityLiveTests.Fixture>
{
    /// <summary>The demo store, with one document type hidden from the studio.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <inheritdoc />
        protected override void ConfigureStudio(MartenStudioOptions options) =>
            options.IsDocumentTypeVisible = static type => type != typeof(Customer);
    }

    [PostgresFact]
    public async Task A_hidden_type_is_in_neither_band_of_the_rail()
    {
        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, TestContext.Current.CancellationToken);

        rail.Error.Should().BeNull();

        // The anti-vacuity half: the rest of the demo is there, so "customer is absent" is not "the rail
        // is empty".
        rail.Groups.SelectMany(x => x.Collections).Select(x => x.Alias).Should().Contain("order");

        rail.Find("customer").Should().BeNull();
        rail.Groups.SelectMany(x => x.Collections).Should().NotContain(
            x => x.Alias.Contains("customer", StringComparison.OrdinalIgnoreCase),
            "not as a registered collection, and not as a discovered table either");
    }

    [PostgresFact]
    public async Task A_hidden_collection_cannot_be_listed_by_name()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest(), TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.Failed);
        page.Rows.Should().BeEmpty();
        page.Error.Should().Contain("customer");
    }

    [PostgresFact]
    public async Task A_document_of_a_hidden_collection_cannot_be_opened_by_id()
    {
        // The id is taken from the database directly, so this is a real row and the refusal is the only
        // reason the read comes back empty.
        var id = await FirstCustomerIdAsync();

        using var documents = Documents();

        DocumentDetailResult detail = await documents.Service.GetDocumentAsync(
            Scope, "customer", id, TestContext.Current.CancellationToken);

        detail.Found.Should().BeFalse();
        detail.NotFound!.Message.Should().Contain("customer");
    }

    [PostgresFact]
    public async Task The_id_probe_does_not_report_a_hidden_collection()
    {
        var id = await FirstCustomerIdAsync();

        using var documents = Documents();

        IReadOnlyList<DocumentIdProbe> probes = await documents.Service.ProbeIdAsync(
            Scope, id, TestContext.Current.CancellationToken);

        // Something answered, so an empty list is not why customer is missing.
        probes.Should().NotBeEmpty();
        probes.Should().NotContain(x => x.Alias == "customer");
    }

    /// <summary>
    /// P2-fix H4: a declared foreign key is not a way round the gate either.
    /// </summary>
    /// <remarks>
    /// <c>Order</c> has a <c>ForeignKey&lt;Customer&gt;</c>, and the related-documents strip walked
    /// <c>AllKnownDocumentTypes()</c> to name its target — so it labelled the link "customer", coloured it,
    /// linked to it and said whether the row was there.
    /// </remarks>
    [PostgresFact]
    public async Task A_foreign_key_to_a_hidden_collection_is_not_offered_as_a_related_document()
    {
        using var documents = Documents();

        DocumentPage orders = await documents.Service.ListAsync(
            Scope, "order", new DocumentListRequest(), TestContext.Current.CancellationToken);

        orders.Rows.Should().NotBeEmpty("the order collection is visible and seeded");

        RelatedDocuments related = await documents.Service.GetRelatedAsync(
            Scope, "order", orders.Rows[0].Id, TestContext.Current.CancellationToken);

        related.Error.Should().BeNull();
        related.Links.Should().NotContain(x => x.TargetAlias == "customer");
    }

    private async Task<string> FirstCustomerIdAsync()
    {
        await using Npgsql.NpgsqlConnection connection = await Postgres.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new Npgsql.NpgsqlCommand(
            $"select id::text from \"{Schema}\".\"mt_doc_customer\" order by id limit 1", connection);

        var id = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

        return id.Should().BeOfType<string>().Subject;
    }
}
