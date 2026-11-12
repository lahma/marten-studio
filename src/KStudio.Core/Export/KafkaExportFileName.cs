using System.Globalization;
using System.Text;

namespace KStudio.Core.Export;

/// <summary>
/// The name of an export file (D-M5 decision 2.6): <c>kstudio-{cluster}-{part}-{yyyyMMdd-HHmmss}Z.{ext}</c>, every character outside
/// <c>[A-Za-z0-9._-]</c> becoming <c>_</c>, at most 120 characters with the topic part cut first. The name holds no predicate, key or value.
/// </summary>
internal static class KafkaExportFileName
{
    /// <summary>The longest file name.</summary>
    public const int MaxLength = 120;

    private const int MaxClusterChars = 40;

    /// <summary>The part of a file name for the topics a source covers: the topic for one, <c>{n}-topics</c> for more.</summary>
    /// <param name="topics">The topics (at least one).</param>
    public static string TopicsPart(IReadOnlyList<string> topics)
    {
        ArgumentNullException.ThrowIfNull(topics);
        return topics.Count switch
        {
            0 => "records",
            1 => topics[0],
            _ => topics.Count.ToString(CultureInfo.InvariantCulture) + "-topics",
        };
    }

    /// <summary>The part of a file name for one record: <c>{topic}-{partition}-{offset}</c>.</summary>
    /// <param name="topic">The topic.</param>
    /// <param name="partition">The partition.</param>
    /// <param name="offset">The offset.</param>
    public static string RecordPart(string topic, int partition, long offset) =>
        string.Create(CultureInfo.InvariantCulture, $"{topic}-{partition}-{offset}");

    /// <summary>The file name.</summary>
    /// <param name="cluster">The cluster's entry key.</param>
    /// <param name="part">What the file covers (<see cref="TopicsPart" /> or <see cref="RecordPart" />).</param>
    /// <param name="now">When it was written, in UTC.</param>
    /// <param name="extension">The extension, without the dot.</param>
    public static string For(string cluster, string part, DateTimeOffset now, string extension)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(extension);

        string clusterText = Clean(cluster, MaxClusterChars);
        string suffix = "-" + now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z." + Clean(extension, 8);
        string prefix = "kstudio-" + clusterText + "-";

        // The topic part gives way first, so the time and the extension always survive.
        int room = Math.Max(1, MaxLength - prefix.Length - suffix.Length);
        return prefix + Clean(part, room) + suffix;
    }

    private static string Clean(string text, int maxChars)
    {
        StringBuilder builder = new(Math.Min(text.Length, maxChars));

        foreach (char c in text)
        {
            if (builder.Length >= maxChars)
            {
                break;
            }

            builder.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '_');
        }

        return builder.Length == 0 ? "_" : builder.ToString();
    }
}
