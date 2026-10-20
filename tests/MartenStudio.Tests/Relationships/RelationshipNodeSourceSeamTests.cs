using Microsoft.Extensions.Logging.Abstractions;
using PostgresStudio.Services;
using PostgresStudio.Services.Relationships;

namespace MartenStudio.Tests.Relationships;

/// <summary>
/// PS-1d: "referenced by" is a source's own nodes' question, so the shared service hands it to the source's node
/// source - and a studio with none has nothing that can be referenced.
/// </summary>
public class RelationshipNodeSourceSeamTests
{
    [Fact]
    public async Task Without_a_node_source_nothing_is_referenced_and_nothing_is_resolved()
    {
        // No resolver and no reads: the answer must not need either, because there is nothing to count.
        var service = new RelationshipDataService(null!, null!, NullLogger<RelationshipDataService>.Instance, nodes: null);

        ReferencedBy referenced = await service.GetReferencedByAsync(
            new StudioScope("default", "localhost.db", null), "customer", "42", TestContext.Current.CancellationToken);

        referenced.Should().BeSameAs(ReferencedBy.None);
    }

    [Fact]
    public void A_set_with_no_members_names_no_member()
    {
        RelationshipNodeSet.None.MemberFor("customer", "customer_id").Should().BeNull();

        RelationshipNodeSet set = RelationshipNodeSet.None with
        {
            Members = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["order"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["customer_id"] = "CustomerId" },
            },
        };

        set.MemberFor("ORDER", "Customer_Id").Should().Be("CustomerId", "nodes and columns compare ignoring case, as the builder always did");
        set.MemberFor("order", "total").Should().BeNull();
    }
}
