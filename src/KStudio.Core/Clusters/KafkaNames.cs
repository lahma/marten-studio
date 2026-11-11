using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace KStudio.Core.Clusters;

/// <summary>A cluster's key: <c>^[a-z0-9][a-z0-9-]{0,62}$</c>. It travels in the address as <c>?cluster=</c>.</summary>
internal static partial class KafkaClusterKey
{
    /// <summary>Whether <paramref name="key" /> is a valid cluster key.</summary>
    /// <param name="key">The key.</param>
    public static bool IsValid([NotNullWhen(true)] string? key) => key is not null && Pattern().IsMatch(key);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}

/// <summary>A topic's name: <c>^[A-Za-z0-9._-]{1,249}$</c>, and not <c>.</c> or <c>..</c>.</summary>
/// <param name="Value">The name.</param>
internal readonly partial record struct KafkaTopicName(string Value)
{
    /// <summary>Whether <paramref name="name" /> is a valid topic name.</summary>
    /// <param name="name">The name.</param>
    public static bool IsValid([NotNullWhen(true)] string? name) =>
        name is not null && name is not ("." or "..") && Pattern().IsMatch(name);

    /// <summary>Creates a name, or says it is not valid.</summary>
    /// <param name="name">The name as given.</param>
    /// <param name="topic">The name, when valid.</param>
    public static bool TryCreate(string? name, out KafkaTopicName topic)
    {
        if (IsValid(name))
        {
            topic = new KafkaTopicName(name);
            return true;
        }

        topic = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,249}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();
}
