using KStudio.Core.Catalog;
using KStudio.Core.Wire;

namespace KStudio.Tests.Catalog;

/// <summary>Decision 5.4's table and sentences: what each call needs, and what a refusal tells the visitor to grant.</summary>
public class KafkaAccessTests
{
    [Theory]
    [InlineData("Metadata", "Describe", "Topic")]
    [InlineData("DescribeTopics", "Describe", "Topic")]
    [InlineData("ListOffsets", "Describe", "Topic")]
    [InlineData("Fetch", "Read", "Topic")]
    [InlineData("DescribeConfigs", "DescribeConfigs", "Topic")]
    [InlineData("GroupOffsets", "Describe", "Group")]
    [InlineData("DescribeGroup", "Describe", "Group")]
    [InlineData("DescribeCluster", "Describe", "Cluster")]
    public void Each_call_needs_the_operation_the_table_says(string call, string operation, string resource)
    {
        (KafkaAclOperation Operation, KafkaResourceType ResourceType) needs = KafkaAccess.Needs(Enum.Parse<WireCall>(call));

        needs.Should().Be((Enum.Parse<KafkaAclOperation>(operation), Enum.Parse<KafkaResourceType>(resource)));
    }

    [Fact]
    public void Every_wire_call_has_a_row()
    {
        foreach (WireCall call in Enum.GetValues<WireCall>())
        {
            Action needs = () => KafkaAccess.Needs(call);
            needs.Should().NotThrow();
        }
    }

    [Theory]
    [InlineData("Read", "Topic", "orders.v1", "Not available: needs `READ` on topic `orders.v1`")]
    [InlineData("DescribeConfigs", "Topic", "orders.v1", "Not available: needs `DESCRIBE_CONFIGS` on topic `orders.v1`")]
    [InlineData("Describe", "Group", "orders-service", "Not available: needs `DESCRIBE` on group `orders-service`")]
    [InlineData("Describe", "Topic", "orders.v1", "Not available: needs `DESCRIBE` on topic `orders.v1`")]
    [InlineData("Describe", "Cluster", "kafka-cluster", "Not available: needs `DESCRIBE` on the cluster")]
    public void A_refusal_says_what_to_grant(string operation, string resource, string name, string expected)
    {
        WireError.NotAuthorized refusal = new(Enum.Parse<KafkaAclOperation>(operation), Enum.Parse<KafkaResourceType>(resource), name);

        KafkaAccessText.Needs(refusal).Should().Be(expected);
        KafkaAccessText.Describe(refusal).Should().Be(expected);
    }

    [Fact]
    public void Other_errors_say_what_happened()
    {
        KafkaAccessText.Describe(new WireError.AuthenticationFailed("SaslAuthenticationFailed")).Should().Contain("refused KStudio's credentials");
        KafkaAccessText.Describe(new WireError.Unsupported("ListOffsets")).Should().Be("Not available: this broker does not support ListOffsets");
        KafkaAccessText.Describe(new WireError.NotFound(KafkaResourceType.Topic, "orders")).Should().Be("Not found: topic `orders`");
        KafkaAccessText.Describe(new WireError.Unavailable("Local_TimedOut", true)).Should().Contain("did not answer");
        KafkaAccessText.Describe(new WireError.Failed("InvalidRequest", false)).Should().Contain("InvalidRequest");
    }
}
