using System.Globalization;

namespace KStudio.Core.Clusters;

/// <summary>
/// The rules a cluster a source contributed must meet (decision 5.2): the host's own, applied to the pieces a source gives. A cluster that
/// fails one is skipped and noted by the registry, never thrown, because one bad discovery must not take down the host's own clusters.
/// </summary>
internal static partial class KafkaEngineOptionsValidation
{
    /// <summary>The most characters of a discovered topic's description.</summary>
    internal const int MaxDiscoveredDescriptionLength = 500;

    /// <summary>The problems with <paramref name="cluster" />; empty when there are none.</summary>
    /// <param name="cluster">The contributed cluster.</param>
    public static IReadOnlyList<string> ValidateContributed(KafkaContributedCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        List<string> problems = [];
        string label = "Cluster '" + Display(cluster.Key) + "'";

        if (!KafkaClusterKey.IsValid(cluster.Key))
        {
            problems.Add(label + ": the key must be lower-case letters, digits and hyphens, starting with a letter or digit, at most 63 characters.");
        }

        if (cluster.TenantId is not null && !KafkaClusterKey.IsValidTenantId(cluster.TenantId))
        {
            problems.Add(label + ": a tenant id must be 1 to " + KafkaClusterKey.MaxTenantIdLength.ToString(CultureInfo.InvariantCulture) + " characters with no control character.");
        }

        if (string.IsNullOrWhiteSpace(cluster.ConsumerConfig.BootstrapServers) || string.IsNullOrWhiteSpace(cluster.AdminConfig.BootstrapServers))
        {
            problems.Add(label + ": the configuration has no bootstrap.servers.");
        }

        string? displayName = cluster.Builder.DisplayNameText;

        if (displayName is not null && (displayName.Length is < 1 or > 100))
        {
            problems.Add(label + ": DisplayName must be 1 to 100 characters.");
        }

        string? environment = cluster.Builder.EnvironmentText;

        if (environment is not null && (environment.Length is < 1 or > 40))
        {
            problems.Add(label + ": Environment must be 1 to 40 characters.");
        }

        if (cluster.Builder.ConsumerGroups.Count > MaxConsumerGroups)
        {
            problems.Add(label + ": at most " + MaxConsumerGroups.ToString(CultureInfo.InvariantCulture) + " consumer groups can be declared.");
        }

        if (cluster.Builder.ConsumerGroups.Any(static x => string.IsNullOrWhiteSpace(x) || x.Length > 255))
        {
            problems.Add(label + ": a consumer group id must be 1 to 255 characters.");
        }

        foreach (KeyValuePair<string, KafkaTopicBuilder> topic in cluster.Builder.Topics)
        {
            ValidateTopic(problems, label + ": topic", topic.Key, topic.Value);
        }

        return problems;
    }
}
