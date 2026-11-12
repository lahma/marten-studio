using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace KStudio.Core.Clusters;

/// <summary>
/// A cluster's key: <c>^[a-z0-9][a-z0-9-]{0,62}$</c>. It travels in the address as <c>?cluster=</c>. One tenant of a broker-per-tenant cluster a
/// source contributed (D43) is an <em>entry</em> keyed <c>{base}/{tenant}</c>: the base never contains <c>/</c>, so the entry key is split at the first one.
/// </summary>
internal static partial class KafkaClusterKey
{
    /// <summary>The separator between a cluster's key and a tenant's id in an entry key.</summary>
    public const char TenantSeparator = '/';

    /// <summary>The most characters of a tenant id.</summary>
    public const int MaxTenantIdLength = 128;

    /// <summary>Whether <paramref name="key" /> is a valid cluster key.</summary>
    /// <param name="key">The key.</param>
    public static bool IsValid([NotNullWhen(true)] string? key) => key is not null && Pattern().IsMatch(key);

    /// <summary>Whether <paramref name="tenantId" /> can be part of an entry key: 1 to 128 characters, not blank, no control character and no <c>|</c>, which the engine's log-once keys use as a separator.</summary>
    /// <param name="tenantId">The tenant id.</param>
    public static bool IsValidTenantId([NotNullWhen(true)] string? tenantId) =>
        !string.IsNullOrWhiteSpace(tenantId) && tenantId.Length <= MaxTenantIdLength && !tenantId.Any(static c => char.IsControl(c) || c == '|');

    /// <summary>The entry key of a cluster, or of one of its tenants.</summary>
    /// <param name="baseKey">The cluster's key.</param>
    /// <param name="tenantId">The tenant, or <see langword="null" />.</param>
    public static string Compose(string baseKey, string? tenantId) =>
        tenantId is null ? baseKey : baseKey + TenantSeparator + tenantId;

    /// <summary>The cluster's key of an entry key: the part before the first <c>/</c>.</summary>
    /// <param name="entryKey">An entry key.</param>
    public static string BaseOf(string entryKey)
    {
        ArgumentNullException.ThrowIfNull(entryKey);

        int separator = entryKey.IndexOf(TenantSeparator, StringComparison.Ordinal);

        return separator < 0 ? entryKey : entryKey[..separator];
    }

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
