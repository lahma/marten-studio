using System.Globalization;
using System.Text.RegularExpressions;

using KStudio.Core.Correlation;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;

namespace KStudio.Core.Clusters;

/// <summary>
/// The engine options' bounds (decision 2.5): at least one cluster, valid and unique keys, and every number inside its range. KStudio's
/// own <c>AddKafkaStudio</c> runs it with validate-on-start; the registry runs it again so that no cluster is ever built from options it
/// would refuse. Each message names the property.
/// </summary>
internal static partial class KafkaEngineOptionsValidation
{
    private const int MaxConsumerGroups = 50;
    private const int MaxPathRules = 32;
    private const int MaxPatternRules = 8;

    /// <summary>The problems with <paramref name="options" />; empty when there are none.</summary>
    /// <param name="options">The options.</param>
    public static IReadOnlyList<string> Validate(KafkaEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Validate(options, KafkaEngineSnapshot.Take(options));
    }

    /// <summary>
    /// The problems with <paramref name="options" />' clusters and topics and with <paramref name="settings" />' values. The registry takes
    /// the snapshot first and validates that same snapshot, so a write to the options between the two cannot skip a bound
    /// (REVIEW-ENG-A-FIX 5).
    /// </summary>
    /// <param name="options">The options, for their clusters and topics.</param>
    /// <param name="settings">The snapshot of the options' other values.</param>
    public static IReadOnlyList<string> Validate(KafkaEngineOptions options, KafkaEngineSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        List<string> problems = [];

        if (options.Clusters.Count == 0)
        {
            problems.Add("At least one Kafka cluster is required: call AddCluster.");
        }

        if (settings.GroupIdPrefix is null || !GroupIdPrefixPattern().IsMatch(settings.GroupIdPrefix))
        {
            problems.Add("GroupIdPrefix must be at most 100 characters of letters, digits, '.', '_' and '-'.");
        }

        Range(problems, nameof(options.RequestTimeout), settings.RequestTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(2));
        Range(problems, nameof(options.AdminIdleTimeout), settings.AdminIdleTimeout, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
        problems.AddRange(settings.Limits.Validate());

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

        if (topic.Rules is { } rules)
        {
            ValidateRules(problems, name, rules);
        }

        CorrelationValidation.Validate(problems, "KStudio: topic '" + Display(name) + "'", topic.Correlations);
    }

    /// <summary>
    /// The redaction rules of a topic (decision 4): names inside their bounds, no rule declared twice (names are compared ignoring case,
    /// because redaction fails closed), and no searchable rule on a header of the built-in deny-list, whatever its case and whether or not
    /// the host opted it out of masking.
    /// </summary>
    private static void ValidateRules(List<string> problems, string topic, KafkaRedactionRules rules)
    {
        string label = "KStudio: topic '" + Display(topic) + "'";
        HashSet<string> seenHeaders = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenAttributes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
        bool seenKey = false;
        int paths = 0;
        int patterns = 0;

        foreach ((RedactionTarget target, string field, string ruleName, bool searchable) in rules.Declared)
        {
            switch (target)
            {
                case RedactionTarget.Key:
                    if (seenKey)
                    {
                        problems.Add(label + " declares a redaction rule on the key twice.");
                    }

                    seenKey = true;
                    break;

                case RedactionTarget.Header:
                    if (field.Length is < 1 or > 256 || field.Any(char.IsControl))
                    {
                        problems.Add(label + ": a redaction rule's header name must be 1 to 256 characters with no control character.");
                    }
                    else if (!seenHeaders.Add(field))
                    {
                        problems.Add(label + " declares a redaction rule on header '" + Display(field) + "' twice (names are compared ignoring case).");
                    }

                    if (searchable && HeaderDenyList.Contains(field))
                    {
                        problems.Add(label + ": header '" + Display(field) + "' is on the built-in deny-list, which can never be searched. Remove Searchable().");
                    }

                    break;

                case RedactionTarget.JsonPath:
                    paths++;

                    if (!JsonPathExpression.TryParse(field, 0, out JsonPathExpression? path, out _))
                    {
                        problems.Add(label + ": the redaction path '" + Display(field) + "' is not a JSON path the search can read ($.name, $.name[0], $.name[*]).");
                    }
                    else if (!seenPaths.Add(path!.Text))
                    {
                        problems.Add(label + " declares a redaction rule on '" + Display(path.Text) + "' twice (member names are compared ignoring case).");
                    }

                    break;

                case RedactionTarget.Pattern:
                    patterns++;
                    ValidatePattern(problems, label, ruleName, field);
                    break;

                default:
                    if (!AttributeNamePattern().IsMatch(field))
                    {
                        problems.Add(label + ": a redaction rule's CloudEvents attribute name must be 1 to 64 lower-case letters and digits.");
                    }
                    else if (!seenAttributes.Add(field))
                    {
                        problems.Add(label + " declares a redaction rule on CloudEvents attribute '" + Display(field) + "' twice.");
                    }

                    break;
            }
        }

        if (paths > MaxPathRules)
        {
            problems.Add(label + " declares " + paths.ToString(CultureInfo.InvariantCulture) + " JSON-path redaction rules; the most is " + MaxPathRules.ToString(CultureInfo.InvariantCulture) + ".");
        }

        if (patterns > MaxPatternRules)
        {
            problems.Add(label + " declares " + patterns.ToString(CultureInfo.InvariantCulture) + " redaction patterns; the most is " + MaxPatternRules.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }

    private static void ValidatePattern(List<string> problems, string label, string ruleName, string pattern)
    {
        // The rule is named "pattern {name}"; a name the host gave is checked, and so is the number it got otherwise (which always passes).
        string name = ruleName.StartsWith("pattern ", StringComparison.Ordinal) ? ruleName["pattern ".Length..] : ruleName;

        if (!PatternNamePattern().IsMatch(name))
        {
            problems.Add(label + ": a pattern name is 1 to 64 letters, digits, '-' or '_'.");
            return;
        }

        string shown = label + ": pattern " + name;

        switch (PayloadPatterns.TryCompile(pattern, out _))
        {
            case PayloadPatternProblem.TooLong:
                problems.Add(shown + " is " + pattern.Length.ToString(CultureInfo.InvariantCulture) + " characters long; the most is " + PayloadPatterns.MaxLength.ToString(CultureInfo.InvariantCulture) + ".");
                break;
            case PayloadPatternProblem.NotARegularExpression:
                problems.Add(shown + " is not a regular expression .NET can read.");
                break;
            case PayloadPatternProblem.NeedsBacktracking:
                problems.Add(shown + " uses a construct that cannot run without backtracking (backreferences, lookarounds, atomic groups and conditionals are refused).");
                break;
            case PayloadPatternProblem.MatchesEmpty:
                problems.Add(shown + " matches an empty text, so it would mask nothing and run on every value.");
                break;
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

    [GeneratedRegex(@"\A[a-z0-9]{1,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AttributeNamePattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{0,100}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GroupIdPrefixPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PatternNamePattern();
}
