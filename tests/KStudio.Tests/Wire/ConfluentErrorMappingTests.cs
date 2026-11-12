using System.Reflection;

using Confluent.Kafka;

using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;

namespace KStudio.Tests.Wire;

/// <summary>Decision 3.4's table, every row, per call; and the seam's promise that no Confluent type crosses it.</summary>
public class ConfluentErrorMappingTests
{
    private static readonly WirePartition Partition = new("orders", 2);

    private static WireError Map(ErrorCode code, WireCall call, bool fatal = false) =>
        ConfluentErrors.Map(new Error(code, "reason", fatal), call, "orders", Partition, 42);

    [Theory]
    [InlineData("Fetch", "Read")]
    [InlineData("DescribeConfigs", "DescribeConfigs")]
    [InlineData("Metadata", "Describe")]
    [InlineData("DescribeTopics", "Describe")]
    [InlineData("ListOffsets", "Describe")]
    [InlineData("GroupOffsets", "Describe")]
    public void A_topic_authorization_failure_names_the_operation_the_call_needed(string call, string expected)
    {
        Map(ErrorCode.TopicAuthorizationFailed, Enum.Parse<WireCall>(call))
            .Should().Be(new WireError.NotAuthorized(Enum.Parse<KafkaAclOperation>(expected), KafkaResourceType.Topic, "orders"));
    }

    [Fact]
    public void A_group_authorization_failure_is_a_describe_refusal_on_the_group()
    {
        ConfluentErrors.Map(new Error(ErrorCode.GroupAuthorizationFailed), WireCall.GroupOffsets, "orders-service")
            .Should().Be(new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Group, "orders-service"));
    }

    [Fact]
    public void A_cluster_authorization_failure_is_a_describe_refusal_on_the_cluster()
    {
        Map(ErrorCode.ClusterAuthorizationFailed, WireCall.DescribeCluster)
            .Should().Be(new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Cluster, "kafka-cluster"));
    }

    [Theory]
    [InlineData(ErrorCode.SaslAuthenticationFailed)]
    [InlineData(ErrorCode.Local_Authentication)]
    [InlineData(ErrorCode.IllegalSaslState)]
    [InlineData(ErrorCode.UnsupportedSaslMechanism)]
    [InlineData(ErrorCode.Local_Ssl)]
    public void Authentication_problems_are_authentication_failures(ErrorCode code)
    {
        Map(code, WireCall.Metadata).Should().Be(new WireError.AuthenticationFailed(code.ToString()));
    }

    [Theory]
    [InlineData(ErrorCode.UnsupportedVersion)]
    [InlineData(ErrorCode.Local_UnsupportedFeature)]
    [InlineData(ErrorCode.Local_NotImplemented)]
    public void An_api_the_broker_lacks_is_unsupported_and_names_the_call(ErrorCode code)
    {
        Map(code, WireCall.ListOffsets).Should().Be(new WireError.Unsupported("ListOffsets"));
    }

    [Theory]
    [InlineData(ErrorCode.OffsetOutOfRange)]
    [InlineData(ErrorCode.Local_AutoOffsetReset)]
    public void A_position_below_the_log_start_is_an_out_of_range_error_with_the_partition(ErrorCode code)
    {
        Map(code, WireCall.Fetch).Should().Be(new WireError.OffsetOutOfRange(Partition, 42));
    }

    [Theory]
    [InlineData(ErrorCode.UnknownTopicOrPart)]
    [InlineData(ErrorCode.Local_UnknownTopic)]
    [InlineData(ErrorCode.Local_UnknownPartition)]
    [InlineData(ErrorCode.UnknownTopicId)]
    public void An_unknown_topic_or_partition_is_not_found(ErrorCode code)
    {
        Map(code, WireCall.Metadata).Should().Be(new WireError.NotFound(KafkaResourceType.Topic, "orders"));
    }

    [Theory]
    [InlineData(ErrorCode.GroupIdNotFound)]
    [InlineData(ErrorCode.Local_UnknownGroup)]
    public void An_unknown_group_is_not_found(ErrorCode code)
    {
        Map(code, WireCall.DescribeGroup).Should().Be(new WireError.NotFound(KafkaResourceType.Group, "orders"));
    }

    [Theory]
    [InlineData(ErrorCode.Local_Transport)]
    [InlineData(ErrorCode.Local_AllBrokersDown)]
    [InlineData(ErrorCode.Local_TimedOut)]
    [InlineData(ErrorCode.Local_TimedOutQueue)]
    [InlineData(ErrorCode.RequestTimedOut)]
    [InlineData(ErrorCode.LeaderNotAvailable)]
    [InlineData(ErrorCode.NotLeaderForPartition)]
    [InlineData(ErrorCode.BrokerNotAvailable)]
    [InlineData(ErrorCode.NetworkException)]
    [InlineData(ErrorCode.Local_Resolve)]
    [InlineData(ErrorCode.KafkaStorageError)]
    [InlineData(ErrorCode.GroupCoordinatorNotAvailable)]
    [InlineData(ErrorCode.NotCoordinatorForGroup)]
    [InlineData(ErrorCode.GroupLoadInProgress)]
    [InlineData(ErrorCode.Local_WaitCoord)]
    [InlineData(ErrorCode.FencedLeaderEpoch)]
    [InlineData(ErrorCode.UnknownLeaderEpoch)]
    [InlineData(ErrorCode.OffsetNotAvailable)]
    public void A_cluster_that_did_not_answer_is_unavailable_and_retriable(ErrorCode code)
    {
        Map(code, WireCall.Metadata).Should().Be(new WireError.Unavailable(code.ToString(), Retriable: true));
    }

    [Fact]
    public void Anything_else_is_a_failure_named_by_its_code()
    {
        Map(ErrorCode.InvalidRequest, WireCall.Metadata).Should().Be(new WireError.Failed("InvalidRequest", IsFatal: false));
    }

    [Theory]
    [InlineData(ErrorCode.TopicAuthorizationFailed)]
    [InlineData(ErrorCode.Local_Transport)]
    [InlineData(ErrorCode.Local_Fatal)]
    public void A_fatal_error_is_a_fatal_failure_whatever_its_code(ErrorCode code)
    {
        Map(code, WireCall.Metadata, fatal: true).Should().Be(new WireError.Failed(code.ToString(), IsFatal: true));
    }

    [Fact]
    public void An_exception_with_no_confluent_error_is_a_failure_named_by_its_type()
    {
        ConfluentErrors.Map(new InvalidOperationException("secret detail")).Should().Be(new WireError.Failed("InvalidOperationException", IsFatal: false));
        ConfluentErrors.Map(new KafkaException(new Error(ErrorCode.Local_TimedOut))).Should().Be(new WireError.Unavailable("Local_TimedOut", Retriable: true));
    }

    /// <summary>
    /// Acceptance criterion 2: no Confluent type is in any <c>KStudio.Core.Wire</c> signature. The seam's types are read by reflection, every
    /// member's parameter, return, property, field and base types, generic arguments included.
    /// </summary>
    [Fact]
    public void No_confluent_type_is_in_a_wire_seam_signature()
    {
        Type[] seam =
        [
            .. typeof(IKafkaWire).Assembly.GetTypes()
                .Where(static x => x.Namespace == "KStudio.Core.Wire" && !x.Name.StartsWith('<') && !x.Name.Contains("DisplayClass", StringComparison.Ordinal)),
        ];

        seam.Should().HaveCountGreaterThan(10, "scanning nothing would pass for the wrong reason");

        List<string> offenders = [];

        foreach (Type type in seam)
        {
            foreach (Type used in SignatureTypes(type))
            {
                if (IsConfluent(used))
                {
                    offenders.Add(type.Name + " uses " + used);
                }
            }
        }

        offenders.Should().BeEmpty("no Confluent type may cross the wire seam: " + string.Join("; ", offenders));
    }

    /// <summary>The anti-vacuity check: the signature walk finds a Confluent type where there is one.</summary>
    [Fact]
    public void The_signature_walk_finds_a_confluent_type_in_a_signature()
    {
        SignatureTypes(typeof(OffendingSeam)).Any(IsConfluent).Should().BeTrue();
    }

    private static bool IsConfluent(Type type) =>
        type.Assembly.GetName().Name == "Confluent.Kafka" || (type.IsGenericType && type.GetGenericArguments().Any(IsConfluent)) || (type.HasElementType && IsConfluent(type.GetElementType()!));

    private static IEnumerable<Type> SignatureTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (Type implemented in type.GetInterfaces())
        {
            yield return implemented;
        }

        foreach (FieldInfo field in type.GetFields(all))
        {
            yield return field.FieldType;
        }

        foreach (PropertyInfo property in type.GetProperties(all))
        {
            yield return property.PropertyType;
        }

        foreach (MethodBase method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            if (method is MethodInfo info)
            {
                yield return info.ReturnType;
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
    }

    private sealed class OffendingSeam
    {
        public static Error Leaks() => default!;
    }
}
