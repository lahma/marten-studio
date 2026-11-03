using PostgresStudio.Services.Connection;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// The pooler signs, moved out of K04 (1.1-NP1): the four that K04 and C10 have always read, held to the same cases,
/// and the Amazon RDS Proxy sign the Connection page and its checks read.
/// </summary>
public sealed class PoolerSignsTests
{
    [Theory]
    [InlineData("ep-cool-darkness-123456-pooler.us-east-2.aws.neon.tech", 5432, "Neon's pooled endpoint", "NeonPooler")]
    [InlineData("aws-0-eu-central-1.pooler.supabase.com", 6543, "port 6543", "SupabaseTransaction")]
    [InlineData("db.apbkobhfnmcqqzqeeqss.supabase.co", 6543, "port 6543", "SupabaseTransaction")]
    [InlineData("studio.postgres.database.azure.com", 6432, "Azure server, its built-in PgBouncer", "AzurePgBouncer")]
    [InlineData("pgbouncer.internal", 6432, "PgBouncer's default port", "PgBouncerPort")]
    [InlineData("the-proxy.proxy-demo.us-east-1.rds.amazonaws.com", 5432, "Amazon RDS Proxy", "RdsProxy")]
    public void A_documented_pooler_host_or_port_is_a_sign(string host, int port, string why, string kind)
    {
        PoolerSign sign = PoolerSigns.Find([(host, port)]).Should().ContainSingle().Subject;

        sign.Kind.ToString().Should().Be(kind);
        sign.Text.Should().Contain(host + ":" + port).And.Contain(why);
    }

    [Theory]
    [InlineData("ep-cool-darkness-123456.us-east-2.aws.neon.tech", 5432)]
    [InlineData("aws-0-eu-central-1.pooler.supabase.com", 5432)]
    [InlineData("db.apbkobhfnmcqqzqeeqss.supabase.co", 5432)]
    [InlineData("studio.postgres.database.azure.com", 5432)]
    [InlineData("localhost", 5432)]
    [InlineData("db-pooler", 5432)]
    [InlineData("mydb.abcdefgh.us-east-1.rds.amazonaws.com", 5432)]
    [InlineData("mydb.cluster-abcdefgh.us-east-1.rds.amazonaws.com", 5432)]
    [InlineData("proxy-demo.us-east-1.rds.amazonaws.com", 5432)]
    [InlineData("the-proxy.proxy-demo.us-east-1.example.com", 5432)]
    public void A_direct_connection_or_a_session_mode_pooler_is_not_a_sign(string host, int port)
    {
        PoolerSigns.Find([(host, port)]).Should().BeEmpty();
        PoolerSigns.TransactionModeTexts([(host, port)]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("the-proxy.proxy-demo.us-east-1.rds.amazonaws.com")]
    [InlineData("THE-PROXY.PROXY-DEMO.US-EAST-1.RDS.AMAZONAWS.COM")]
    [InlineData("the-proxy.proxy-demo.us-east-1.rds.amazonaws.com.")]
    [InlineData("my-reader.endpoint.proxy-demo.eu-west-2.rds.amazonaws.com")]
    [InlineData("the-proxy.proxy-demo.cn-north-1.rds.amazonaws.com.cn")]
    public void An_RDS_Proxy_endpoint_is_the_proxy_label_under_a_region_of_rds_amazonaws_com(string host)
    {
        PoolerSigns.IsRdsProxyHost(host).Should().BeTrue();
    }

    [Fact]
    public void An_RDS_Proxy_is_not_a_sign_for_K04_which_is_about_a_lock_a_transaction_pooler_hands_away()
    {
        // RDS Proxy pins a session on pg_advisory_lock / pg_try_advisory_lock (rds-proxy-pinning), which keeps the lock with its holder.
        PoolerSigns.TransactionModeTexts([("the-proxy.proxy-demo.us-east-1.rds.amazonaws.com", 5432)]).Should().BeEmpty();
        PoolerSigns.Find([("the-proxy.proxy-demo.us-east-1.rds.amazonaws.com", 5432)]).Single().IsPgBouncerFamily.Should().BeFalse();
    }

    [Theory]
    [InlineData(6432)]
    [InlineData(6543)]
    public void An_RDS_Proxy_shaped_host_on_a_pooler_port_is_the_RDS_Proxy_and_no_longer_a_K04_sign(int port)
    {
        // Before the move to the engine such a host was a K04 sign by its port; the proxy- label wins now, and it is not transaction mode.
        PoolerSigns.Find([("the-proxy.proxy-demo.us-east-1.rds.amazonaws.com", port)]).Single().Kind.Should().Be(PoolerSignKind.RdsProxy);
        PoolerSigns.TransactionModeTexts([("the-proxy.proxy-demo.us-east-1.rds.amazonaws.com", port)]).Should().BeEmpty();
    }

    [Fact]
    public void Every_host_of_a_multi_host_data_source_is_looked_at()
    {
        PoolerSigns.TransactionModeTexts([("primary.example", 5432), ("pooled.example", 6432)]).Should().ContainSingle().Which.Should().StartWith("pooled.example:6432");
    }

    /// <summary>The words K04's evidence carries are unchanged by the move.</summary>
    [Fact]
    public void The_texts_K04_carries_are_the_ones_it_always_did()
    {
        PoolerSigns.TransactionModeTexts([("a-pooler.x", 5432), ("h", 6543), ("s.postgres.database.azure.com", 6432), ("b", 6432)])
            .Should().Equal(
                "a-pooler.x:5432 - the -pooler suffix of Neon's pooled endpoint, PgBouncer in transaction mode",
                "h:6543 - port 6543, where Supabase's poolers take transaction-mode connections",
                "s.postgres.database.azure.com:6432 - port 6432 of an Azure server, its built-in PgBouncer, which pools by transaction by default",
                "b:6432 - port 6432, PgBouncer's default port");
    }
}
