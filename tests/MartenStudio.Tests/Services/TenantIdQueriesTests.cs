using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-2 OPEN-5: tenant discovery's last tier is built in <c>Internal/Sql</c> - moved out of <c>TenantDiscovery</c> with the
/// statement unchanged - and nothing in the statement but quoted names and the one constant limit.
/// </summary>
public class TenantIdQueriesTests
{
    [Fact]
    public void The_statement_is_the_one_tier_three_always_ran()
    {
        string sql = TenantIdQueries.DistinctTenantIds([("app_events", "mt_streams"), ("app", "mt_doc_note")], 200);

        sql.Should().Be(
            "select tenant_id from ("
            + "(select distinct tenant_id from \"app_events\".\"mt_streams\" order by tenant_id limit 201)"
            + " union "
            + "(select distinct tenant_id from \"app\".\"mt_doc_note\" order by tenant_id limit 201)"
            + ") as tenants where tenant_id is not null order by tenant_id limit 201");
    }

    [Fact]
    public void A_name_is_quoted_and_a_name_that_cannot_be_is_refused_rather_than_spliced()
    {
        TenantIdQueries.DistinctTenantIds([("app", "c;drop")], 5).Should().Contain("\"app\".\"c;drop\"").And.Contain("limit 6");

        Action hostile = () => TenantIdQueries.DistinctTenantIds([("a\"b", "c")], 5);
        hostile.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_table_is_refused_rather_than_an_empty_union()
    {
        Action none = () => TenantIdQueries.DistinctTenantIds([], 200);

        none.Should().Throw<ArgumentOutOfRangeException>();
    }
}
