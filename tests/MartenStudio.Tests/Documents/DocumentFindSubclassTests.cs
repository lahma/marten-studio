using Marten.Schema;

using MartenStudio.Services.Documents;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// 1.2-SCOPE-3-FU F7: a hit names the subclass its row is, from <c>mt_doc_type</c> - unless the host hides that subclass with
/// <c>IsDocumentTypeVisible</c>, because a hidden type never answers, by name either.
/// </summary>
public class DocumentFindSubclassTests
{
    private static IDocumentType Hierarchy() =>
        SqlTestStore.DocumentType<SqlTestCustomer>(options => options.Schema.For<SqlTestCustomer>().AddSubClass<SqlTestVipCustomer>());

    private static string VipAlias(IDocumentType type) => type.AliasFor(typeof(SqlTestVipCustomer));

    [Fact]
    public void A_visible_subclass_is_named()
    {
        IDocumentType type = Hierarchy();

        DocumentDataService.SubclassName(type, VipAlias(type), null).Should().Be(nameof(SqlTestVipCustomer));
        DocumentDataService.SubclassName(type, VipAlias(type), static _ => true).Should().Be(nameof(SqlTestVipCustomer));
    }

    [Fact]
    public void A_subclass_the_host_hides_is_not_named()
    {
        IDocumentType type = Hierarchy();

        DocumentDataService.SubclassName(type, VipAlias(type), static candidate => candidate != typeof(SqlTestVipCustomer)).Should().BeNull();
    }

    [Fact]
    public void The_collections_own_type_an_empty_discriminator_and_an_alias_nothing_claims_name_nothing()
    {
        IDocumentType type = Hierarchy();

        DocumentDataService.SubclassName(type, type.Alias, null).Should().BeNull();
        DocumentDataService.SubclassName(type, null, null).Should().BeNull();
        DocumentDataService.SubclassName(type, " ", null).Should().BeNull();
        DocumentDataService.SubclassName(type, "nothing_claims_this", null).Should().BeNull();
    }
}
