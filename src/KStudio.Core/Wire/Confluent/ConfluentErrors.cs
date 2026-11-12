using Confluent.Kafka;

namespace KStudio.Core.Wire.Confluent;

/// <summary>
/// The one table that turns Confluent's <see cref="ErrorCode" /> into the engine's error values (decision 3.4). Every Confluent exception
/// is caught at the seam and mapped here, so nothing above it sees a Confluent type.
/// </summary>
internal static class ConfluentErrors
{
    /// <summary>Maps one Confluent error.</summary>
    /// <param name="error">The Confluent error.</param>
    /// <param name="call">The wire call it came from.</param>
    /// <param name="resourceName">The topic or group the call was about; the cluster's name for a cluster call.</param>
    /// <param name="partition">The partition, for an out-of-range error.</param>
    /// <param name="position">The position that was asked for, for an out-of-range error.</param>
    public static WireError Map(
        Error error,
        WireCall call,
        string? resourceName = null,
        WirePartition? partition = null,
        long? position = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        string code = error.Code.ToString();
        string name = resourceName ?? string.Empty;

        // An error the client says it cannot recover from is a failure, whatever its code is called.
        if (error.IsFatal)
        {
            return new WireError.Failed(code, IsFatal: true);
        }

        return error.Code switch
        {
            ErrorCode.TopicAuthorizationFailed => new WireError.NotAuthorized(
                call switch
                {
                    WireCall.Fetch => KafkaAclOperation.Read,
                    WireCall.DescribeConfigs => KafkaAclOperation.DescribeConfigs,
                    _ => KafkaAclOperation.Describe,
                },
                KafkaResourceType.Topic,
                name),
            ErrorCode.GroupAuthorizationFailed => new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Group, name),
            ErrorCode.ClusterAuthorizationFailed => new WireError.NotAuthorized(KafkaAclOperation.Describe, KafkaResourceType.Cluster, "kafka-cluster"),

            ErrorCode.SaslAuthenticationFailed
                or ErrorCode.Local_Authentication
                or ErrorCode.IllegalSaslState
                or ErrorCode.UnsupportedSaslMechanism
                or ErrorCode.Local_Ssl => new WireError.AuthenticationFailed(code),

            ErrorCode.UnsupportedVersion
                or ErrorCode.Local_UnsupportedFeature
                or ErrorCode.Local_NotImplemented => new WireError.Unsupported(call.ToString()),

            ErrorCode.OffsetOutOfRange
                or ErrorCode.Local_AutoOffsetReset => new WireError.OffsetOutOfRange(partition ?? new WirePartition(name, -1), position),

            ErrorCode.UnknownTopicOrPart
                or ErrorCode.Local_UnknownTopic
                or ErrorCode.Local_UnknownPartition
                or ErrorCode.UnknownTopicId => new WireError.NotFound(KafkaResourceType.Topic, name),

            ErrorCode.GroupIdNotFound
                or ErrorCode.Local_UnknownGroup => new WireError.NotFound(KafkaResourceType.Group, name),

            ErrorCode.Local_Transport
                or ErrorCode.Local_AllBrokersDown
                or ErrorCode.Local_TimedOut
                or ErrorCode.Local_TimedOutQueue
                or ErrorCode.RequestTimedOut
                or ErrorCode.LeaderNotAvailable
                or ErrorCode.NotLeaderForPartition
                or ErrorCode.BrokerNotAvailable
                or ErrorCode.NetworkException
                or ErrorCode.Local_Resolve
                or ErrorCode.KafkaStorageError
                or ErrorCode.GroupCoordinatorNotAvailable
                or ErrorCode.NotCoordinatorForGroup
                or ErrorCode.GroupLoadInProgress
                or ErrorCode.Local_WaitCoord
                or ErrorCode.FencedLeaderEpoch
                or ErrorCode.UnknownLeaderEpoch
                or ErrorCode.OffsetNotAvailable => new WireError.Unavailable(code, Retriable: true),

            _ => new WireError.Failed(code, IsFatal: false),
        };
    }

    /// <summary>Maps an exception that carries no Confluent error: it is a failure, named by its type.</summary>
    /// <param name="exception">The exception.</param>
    public static WireError Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            KafkaException kafka => Map(kafka.Error, WireCall.Metadata),
            _ => new WireError.Failed(exception.GetType().Name, IsFatal: false),
        };
    }
}
