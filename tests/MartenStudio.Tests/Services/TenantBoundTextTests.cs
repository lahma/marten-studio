using MartenStudio.Services.Tenants;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-2 T6: the bound texts the Tenants overview asks Postgres about, and the way back - a partition is kept only when its one
/// value is exactly a tenant of the page, so a bound that merely looks alike matches nobody.
/// </summary>
public class TenantBoundTextTests
{
    [Fact]
    public void A_plain_id_has_one_bound()
    {
        TenantBoundText.Variants("acme").Should().Equal("FOR VALUES IN ('acme')");
    }

    [Fact]
    public void A_quote_is_doubled()
    {
        TenantBoundText.Variants("o'hara").Should().Equal("FOR VALUES IN ('o''hara')");
    }

    [Fact]
    public void A_backslash_adds_the_variant_for_a_server_that_does_not_treat_strings_as_conforming()
    {
        TenantBoundText.Variants("a\\b").Should().Equal("FOR VALUES IN ('a\\b')", "FOR VALUES IN ('a\\\\b')");
    }

    [Theory]
    [InlineData("FOR VALUES IN ('acme')", "acme")]
    [InlineData("FOR VALUES IN ('o''hara')", "o'hara")]
    public void A_returned_bound_is_read_again_and_kept_only_for_a_tenant_of_the_page(string bound, string expected)
    {
        TenantBoundText.TenantOf(bound, new HashSet<string>(["acme", "o'hara"], StringComparer.Ordinal)).Should().Be(expected);
    }

    [Theory]
    [InlineData("FOR VALUES IN ('acme', 'globex')")]
    [InlineData("FOR VALUES IN ('ACME')")]
    [InlineData("FOR VALUES IN ('acme ')")]
    [InlineData("DEFAULT")]
    [InlineData("FOR VALUES FROM (1) TO (2)")]
    [InlineData(null)]
    public void A_bound_of_several_values_another_spelling_or_another_shape_matches_nobody(string? bound)
    {
        TenantBoundText.TenantOf(bound, new HashSet<string>(["acme", "globex"], StringComparer.Ordinal)).Should().BeNull();
    }

    [Fact]
    public void A_tenant_not_on_the_page_is_not_returned_even_when_its_bound_is()
    {
        TenantBoundText.TenantOf("FOR VALUES IN ('globex')", new HashSet<string>(["acme"], StringComparer.Ordinal)).Should().BeNull();
    }
}
