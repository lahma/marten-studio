using MartenStudio.Services.Documents;
using MartenStudio.Services.Find;
using MartenStudio.Tests.Support;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// 1.2-SCOPE-3: Find's documents. The probe itself (the visibility gate, the partition skip, the tenant filter) is the data service's
/// and is proven against Postgres; what the contributor adds is how a row becomes a hit - the link with that row's tenant, the
/// subclass, the deleted mark, the tenants beyond five - and which notes it says.
/// </summary>
public class DocumentFindContributorTests
{
    private static readonly StudioScope Asked = new("default", string.Empty, null);
    private static readonly StudioScope Matched = new("default", "primary", null);

    private static DocumentFindContributor Contributor(FakeDocumentDataService documents, FakeFindScopes? scopes = null) =>
        new(documents, scopes ?? new FakeFindScopes(FakeFindScopes.Context(Matched)), Microsoft.Extensions.Options.Options.Create(new MartenStudioOptions()));

    private static DocumentFindHit Hit(string alias, string id, params DocumentFindRow[] rows) =>
        new(alias, 120, id, rows, 0, false);

    [Fact]
    public async Task A_row_is_a_hit_that_opens_the_document_in_the_scope_the_resolver_matched()
    {
        FakeDocumentDataService documents = new() { FindResult = new([Hit("customer", "c-1", new DocumentFindRow(null, null, false))], 4, 0, false, Matched) };

        FindContribution found = await Contributor(documents).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        FindHit hit = found.Hits.Should().ContainSingle().Subject;
        hit.Kind.Should().Be("Document");
        hit.Title.Should().Be("customer c-1");
        hit.Url.Should().Contain("documents/customer/doc").And.Contain("id=c-1").And.Contain("db=primary");
        hit.Scope.Should().Be(Matched);
        hit.TenantChip.Should().BeNull();
        hit.Mark.Should().BeNull();
        documents.Finds.Should().ContainSingle().Which.Should().Be((Asked, "c-1"));
    }

    [Fact]
    public async Task One_hit_per_tenant_each_linking_to_its_own_tenant_with_the_subclass_and_the_deleted_mark()
    {
        FakeDocumentDataService documents = new()
        {
            FindResult = new(
                [Hit("customer", "c-1", new DocumentFindRow("acme", null, false), new DocumentFindRow("zeta", "VipCustomer", true))],
                4, 0, false, Matched),
        };

        FindContribution found = await Contributor(documents).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        found.Hits.Should().HaveCount(2);
        found.Hits[0].TenantChip.Should().Be("tenant acme");
        found.Hits[0].Url.Should().Contain("tenant=acme");
        found.Hits[0].Scope.TenantId.Should().Be("acme");
        found.Hits[1].TenantChip.Should().Be("tenant zeta");
        found.Hits[1].Url.Should().Contain("tenant=zeta");
        found.Hits[1].Mark.Should().Be("deleted");
        found.Hits[1].Detail.Should().Be("as VipCustomer");
        found.Hits.Select(static x => x.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_row_in_no_tenant_keeps_the_tenant_the_scope_selected()
    {
        StudioScope inTenant = Matched with { TenantId = "acme" };
        FakeDocumentDataService documents = new() { FindResult = new([Hit("customer", "c-1", new DocumentFindRow(null, null, false))], 1, 0, false, inTenant) };

        FindContribution found = await Contributor(documents).FindAsync(inTenant, FindQuery.Parse("c-1"), CancellationToken.None);

        found.Hits.Single().Scope.Should().Be(inTenant);
        found.Hits.Single().Url.Should().Contain("tenant=acme");
    }

    [Fact]
    public async Task Tenants_beyond_the_five_shown_are_counted_on_the_last_hit_and_only_from_what_the_visitor_may_see()
    {
        DocumentFindRow[] rows = [.. Enumerable.Range(1, 5).Select(i => new DocumentFindRow("t" + i, null, false))];
        FakeDocumentDataService documents = new() { FindResult = new([new DocumentFindHit("customer", 120, "c-1", rows, 3, true)], 1, 0, false, Matched) };

        FindContribution found = await Contributor(documents).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        found.Hits.Should().HaveCount(5);
        found.Hits[3].Detail.Should().BeNull();
        found.Hits[4].Detail.Should().Be("and 3 more tenants you may see (or more)");
    }

    [Fact]
    public async Task A_hit_with_rows_the_visitor_may_not_see_says_nothing_of_them()
    {
        DocumentFindRow[] rows = [new("acme", null, false)];
        FakeDocumentDataService documents = new() { FindResult = new([new DocumentFindHit("customer", 120, "c-1", rows, 0, true)], 1, 0, false, Matched) };

        FindContribution found = await Contributor(documents).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        found.Hits.Single().Detail.Should().BeNull("a full read that the policy trimmed to one tenant does not say that more exist");
    }

    [Fact]
    public async Task The_phrase_counts_the_visible_types_and_the_notes_say_what_was_skipped()
    {
        FakeDocumentDataService documents = new() { FindResult = new([], 47, 2, true, Matched) };

        FindContribution found = await Contributor(documents).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        found.Searched.Should().Be("47 document types");
        found.Notes.Should().Equal(
            "2 document types partitioned by tenant were not searched: select a tenant.",
            "Only the first 25 document types were searched.");
    }

    [Fact]
    public async Task One_partitioned_type_and_one_visible_type_are_worded_in_the_singular()
    {
        FakeDocumentDataService documents = new() { FindResult = new([], 1, 1, false, Matched) };

        FindContribution found = await Contributor(documents).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        found.Searched.Should().Be("1 document type");
        found.Notes.Should().Equal("1 document type partitioned by tenant was not searched: select a tenant.");
    }

    [Fact]
    public async Task A_store_with_no_visible_type_has_nothing_to_say_it_searched()
    {
        FakeDocumentDataService documents = new() { FindResult = new([], 0, 0, false, Matched) };

        FindContribution found = await Contributor(documents).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        found.Searched.Should().BeNull();
        found.Hits.Should().BeEmpty();
    }

    [Theory]
    [InlineData("#12")]
    [InlineData("")]
    public async Task A_sequence_or_nothing_is_not_a_document_id_and_asks_nothing(string typed)
    {
        FakeDocumentDataService documents = new();

        FindContribution found = await Contributor(documents).FindAsync(Asked, FindQuery.Parse(typed), CancellationToken.None);

        found.Should().Be(FindContribution.NotApplicable);
        documents.Finds.Should().BeEmpty();
    }

    [Fact]
    public async Task The_tenants_the_visitor_may_address_are_handed_to_the_probe()
    {
        FindTenants tenants = FindTenants.OnlyThese(["acme", "globex"]);
        FakeDocumentDataService documents = new() { FindResult = new([], 3, 0, false, Matched) };

        await Contributor(documents, new FakeFindScopes(FakeFindScopes.Context(Matched, tenants: tenants))).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        documents.FindTenantsAsked.Should().ContainSingle().Which.Should().BeSameAs(tenants);
    }

    [Fact]
    public async Task Tenanted_collections_left_unread_because_the_visitor_must_select_a_tenant_say_so()
    {
        FakeDocumentDataService documents = new() { FindResult = new([], 3, 0, false, Matched, TenantedSkipped: 2) };

        FindContribution found = await Contributor(documents).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        found.Notes.Should().Equal(FindTenants.SelectionNeededNote);
        found.Notes[0].Should().Contain("select a tenant to search documents and streams");
    }

    [Fact]
    public async Task A_scope_that_could_not_be_read_is_a_note_and_the_probe_is_not_made()
    {
        FakeDocumentDataService documents = new();

        FindContribution found = await Contributor(documents, new FakeFindScopes(null)).FindAsync(Asked, FindQuery.Parse("c-1"), CancellationToken.None);

        found.Notes.Should().Equal("Documents: could not be searched.");
        documents.Finds.Should().BeEmpty();
    }
}
