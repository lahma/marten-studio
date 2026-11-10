using System.Globalization;
using System.Text.RegularExpressions;

namespace KStudio.Core.Clusters;

/// <summary>
/// The engine options' bounds (decision 2.5): at least one cluster, valid and unique keys, and every number inside its range. KStudio's
/// own <c>AddKafkaStudio</c> runs it with validate-on-start; the registry runs it again so that no cluster is ever built from options it
/// would refuse. Each message names the property.
/// </summary>
internal static partial class KafkaEngineOptionsValidation
{
    private const int MaxConsumerGroups = 50;

    /// <summary>The problems with <paramref name="options" />; empty when there are none.</summary>
    /// <param name="options">The options.</param>
    public static IReadOnlyList<string> Validate(KafkaEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> problems = [];

        if (options.Clusters.Count == 0)
        {
            problems.Add("At least one Kafka cluster is required: call AddCluster.");
        }

        if (!GroupIdPrefixPattern().IsMatch(options.GroupIdPrefix ?? string.Empty) || options.GroupIdPrefix is null)
        {
            problems.Add("GroupIdPrefix must be at most 100 characters of letters, digits, '.', '_' and '-'.");
        }

        Range(problems, nameof(options.RequestTimeout), options.RequestTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
        Range(problems, nameof(options.AdminIdleTimeout), options.AdminIdleTimeout, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
        problems.AddRange(options.Limits.Validate());

        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (KafkaEngineOptions.ClusterRegistration cluster in options.Clusters)
        {
            ValidateCluster(problems, cluster, keys);
        }

        foreach (KeyValuePair<string, KafkaTopicBuilder> topic in options.Topics)
        {
            ValidateTopic(problems, "Topic", topic.Key, topic.Value);
        }

        return problems;
    }

    private static void ValidateCluster(List<string> problems, KafkaEngineOptions.ClusterRegistration cluster, HashSet<string> keys)
    {
        string label = "Cluster '" + Display(cluster.Key) + "'";

        if (!KafkaClusterKey.IsValid(cluster.Key))
        {
            problems.Add(label + ": the key must be lower-case letters, digits and hyphens, starting with a letter or digit, at most 63 characters.");
        }
        else if (!keys.Add(cluster.Key))
        {
            problems.Add(label + ": the key is used by more than one cluster.");
        }

        if (!cluster.Consumer.TryGetValue("bootstrap.servers", out string? consumerServers) || string.IsNullOrWhiteSpace(consumerServers)
            || !cluster.Admin.TryGetValue("bootstrap.servers", out string? adminServers) || string.IsNullOrWhiteSpace(adminServers))
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
    }

    private static void ValidateTopic(List<string> problems, string label, string name, KafkaTopicBuilder topic)
    {
        if (!KafkaTopicName.IsValid(name))
        {
            problems.Add(label + " '" + Display(name) + "': the name must be 1 to 249 characters of letters, digits, '.', '_' and '-', and not '.' or '..'.");
        }

        string? description = topic.DescriptionText;

        if (description is not null && (description.Length is < 1 or > 500))
        {
            problems.Add(label + " '" + Display(name) + "': Description must be 1 to 500 characters.");
        }
    }

    private static void Range(List<string> problems, string name, TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min || value > max)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{name} must be between {min} and {max}, but is {value}."));
        }
    }

    /// <summary>A name as a message shows it: bounded, so a very long one cannot flood a log.</summary>
    private static string Display(string? value) =>
        value is null ? string.Empty : value.Length > 64 ? value[..64] + "…" : value;

    [GeneratedRegex("^[A-Za-z0-9._-]{0,100}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GroupIdPrefixPattern();
}
