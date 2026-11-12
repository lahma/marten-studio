using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Settings;

namespace KStudio.Tests.Services;

/// <summary>D43, decision 6: a tenant entry's key is <c>{cluster}/{tenant}</c>, and the address carries it escaped and reads it back whole.</summary>
public class KafkaAddressTests
{
    [Theory]
    [InlineData("wolverine/acme")]
    [InlineData("wolverine/a b&c=d#e?f%g")]
    [InlineData("wolverine/a/b")]
    [InlineData("wolverine")]
    public void A_cluster_entry_key_survives_the_round_trip_through_the_address(string key)
    {
        using KafkaStudioHarness harness = new();
        IStudioSettings settings = harness.Context.Services.GetRequiredService<IStudioSettings>();

        string href = KafkaAddress.Href(settings, KafkaAddress.SearchRoute, key, (KafkaAddress.TopicParameter, "orders.v1"));
        string absolute = "https://host" + (href.StartsWith('/') ? href : "/" + href);

        href.Should().NotContain("cluster=" + key, "a slash in a query value is escaped, so the address does not read as a path");
        KafkaAddress.Get(absolute, KafkaState.ClusterParameter).Should().Be(key);
        KafkaState.ReadCluster(absolute).Should().Be(key);
        KafkaAddress.Get(absolute, KafkaAddress.TopicParameter).Should().Be("orders.v1");
    }

    [Fact]
    public void The_slash_of_a_tenant_key_is_escaped_in_the_query_string()
    {
        using KafkaStudioHarness harness = new();
        IStudioSettings settings = harness.Context.Services.GetRequiredService<IStudioSettings>();

        KafkaAddress.Href(settings, KafkaAddress.SearchRoute, "wolverine/acme").Should().Contain("cluster=wolverine%2Facme");
    }
}
