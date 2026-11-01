using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Query;

using static MartenStudio.Tests.Database.DatabaseTestStores;

namespace MartenStudio.Tests.Database;

/// <summary>
/// A hidden document type's per-tenant partition (0.10-A review): <c>mt_doc_dbsecret_acme</c> is, by its name alone, Marten
/// infrastructure - not a hidden table - so the Plan page's facts judge a relation by every table above it in
/// <c>pg_inherits</c> as well, and withhold a partition whose root is hidden.
/// </summary>
public class PlanFactsHiddenPartitionTests
{
    private static DatabaseGate Gate() => new(
        StoreSchemas(),
        LiveSchemas(),
        [],
        capabilityEnabled: true,
        readOnly: false,
        authorized: true,
        Classifier());

    private static PlanFactsResolvedRow Partition(string[] ancestorSchemas, string[] ancestorNames) =>
        new(1, 16_385, DocumentSchema, "mt_doc_dbsecret_acme", "r") { AncestorSchemas = ancestorSchemas, AncestorNames = ancestorNames };

    [Fact]
    public void A_partition_of_a_hidden_type_is_not_hidden_by_its_own_name_which_is_why_its_root_is_asked()
    {
        DatabaseGate gate = Gate();

        PlanFactsService.IsVisible(gate, DocumentSchema, "mt_doc_dbsecret").Should().BeFalse("the hidden type's own table");
        PlanFactsService.IsVisible(gate, DocumentSchema, "mt_doc_dbsecret_acme").Should().BeTrue("by its name alone it is Marten infrastructure, which the classifier shows");
    }

    [Fact]
    public void A_partition_whose_root_is_hidden_is_withheld_and_one_whose_root_is_visible_is_not()
    {
        DatabaseGate gate = Gate();

        PlanFactsService.IsVisible(gate, Partition([DocumentSchema], ["mt_doc_dbsecret"])).Should().BeFalse("the root is the hidden type's table");
        PlanFactsService.IsVisible(gate, Partition([], [])).Should().BeTrue("with no ancestor read there is nothing to hide it by - the catalog read supplies them");
        PlanFactsService.IsVisible(gate, Partition([DocumentSchema], ["mt_doc_person"])).Should().BeTrue("a visible type's partition");
    }

    [Fact]
    public void Any_hidden_ancestor_withholds_the_relation_whatever_its_depth_or_schema()
    {
        DatabaseGate gate = Gate();

        // A grandparent hidden, the parent not: sub-partitioned.
        PlanFactsService.IsVisible(gate, Partition([DocumentSchema, DocumentSchema], ["mt_doc_dbsecret_acme_2026", "mt_doc_dbsecret"])).Should().BeFalse();

        // An ancestor in a schema the visitor may not see.
        PlanFactsService.IsVisible(gate, Partition(["legacy_hidden"], ["parts"])).Should().BeFalse();
    }
}
