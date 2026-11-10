using System.Globalization;

using KStudio.Core.Wire;

namespace KStudio.Core.Catalog;

/// <summary>
/// The ACL table as code (decision 5.4): what each wire call needs, so that a refusal can say what to grant.
/// </summary>
internal static class KafkaAccess
{
    /// <summary>What a wire call needs from the broker's authorizer.</summary>
    /// <param name="call">The call.</param>
    public static (KafkaAclOperation Operation, KafkaResourceType ResourceType) Needs(WireCall call) => call switch
    {
        WireCall.Fetch => (KafkaAclOperation.Read, KafkaResourceType.Topic),
        WireCall.DescribeConfigs => (KafkaAclOperation.DescribeConfigs, KafkaResourceType.Topic),
        WireCall.GroupOffsets or WireCall.DescribeGroup => (KafkaAclOperation.Describe, KafkaResourceType.Group),
        WireCall.DescribeCluster => (KafkaAclOperation.Describe, KafkaResourceType.Cluster),
        _ => (KafkaAclOperation.Describe, KafkaResourceType.Topic),
    };

    /// <summary>The Kafka name of an operation, as ACL tooling spells it.</summary>
    /// <param name="operation">The operation.</param>
    public static string OperationName(KafkaAclOperation operation) => operation switch
    {
        KafkaAclOperation.Read => "READ",
        KafkaAclOperation.DescribeConfigs => "DESCRIBE_CONFIGS",
        _ => "DESCRIBE",
    };

    /// <summary>The lower-case name of a resource's kind.</summary>
    /// <param name="type">The kind.</param>
    public static string ResourceTypeName(KafkaResourceType type) => type switch
    {
        KafkaResourceType.Group => "group",
        KafkaResourceType.Cluster => "cluster",
        _ => "topic",
    };
}

/// <summary>The one formatter for what an engine error says to a visitor (decision 5.4).</summary>
internal static class KafkaAccessText
{
    /// <summary>
    /// "Not available: needs <c>READ</c> on topic <c>orders.v1</c>" for a refusal by an ACL; the cluster has no name:
    /// "Not available: needs <c>DESCRIBE</c> on the cluster".
    /// </summary>
    /// <param name="refusal">The refusal.</param>
    public static string Needs(WireError.NotAuthorized refusal)
    {
        string operation = "`" + KafkaAccess.OperationName(refusal.Operation) + "`";

        return refusal.ResourceType == KafkaResourceType.Cluster
            ? $"Not available: needs {operation} on the cluster"
            : $"Not available: needs {operation} on {KafkaAccess.ResourceTypeName(refusal.ResourceType)} `{refusal.ResourceName}`";
    }

    /// <summary>A sentence for any error: a refusal says what to grant, the rest say what happened.</summary>
    /// <param name="error">The error.</param>
    public static string Describe(WireError error) => error switch
    {
        WireError.NotAuthorized refusal => Needs(refusal),
        WireError.AuthenticationFailed failure => "Not available: the cluster refused KStudio's credentials (" + failure.Code + ")",
        WireError.Unsupported unsupported => "Not available: this broker does not support " + unsupported.Api,
        WireError.NotFound notFound => string.Create(
            CultureInfo.InvariantCulture,
            $"Not found: {KafkaAccess.ResourceTypeName(notFound.ResourceType)} `{notFound.Name}`"),
        WireError.OffsetOutOfRange => "Not available: the offset is below the log start",
        WireError.Unavailable unavailable => "Not available: the cluster did not answer (" + unavailable.Code + ")",
        WireError.Failed failed => "Not available: " + failed.Code,
        _ => "Not available",
    };
}
