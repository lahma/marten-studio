using KStudio.Core.Catalog;
using KStudio.Core.Clusters;

using Microsoft.Extensions.Logging;

namespace KStudio.Services;

/// <summary>One row of the home's table: a topic the host declared, or one the cluster lists.</summary>
/// <param name="Name">The topic's name.</param>
/// <param name="Description">The declared description, when there is one.</param>
/// <param name="Partitions">How many partitions metadata lists, or <see langword="null" /> for a declared topic it does not list.</param>
/// <param name="KeyFormat">The key format: the declared one, or UTF-8 text.</param>
/// <param name="KeyFormatDeclared">Whether the host declared the key format; an undeclared one is shown as assumed.</param>
/// <param name="Declared">Whether the host declared the topic at all.</param>
internal sealed record KafkaTopicRow(
    string Name,
    string? Description,
    int? Partitions,
    KafkaKeyFormat KeyFormat,
    bool KeyFormatDeclared,
    bool Declared)
{
    /// <summary>Whether metadata lists the topic (a declared topic it does not list is missing, or not describable by this application's credentials).</summary>
    public bool Listed => Partitions is not null;

    /// <summary>Whether the name starts with <c>__</c>.</summary>
    public bool IsInternal => Name.StartsWith("__", StringComparison.Ordinal);
}

/// <summary>
/// The home's list, after the filter and the policy (D-UI decision 7): <see cref="Rows"/> are the declared topics first and then the others, in the
/// order they are paged; the internal topics are apart.
/// </summary>
/// <param name="RefusalText">Why the cluster's topics could not be listed (a sentence from <see cref="KafkaAccessText" />), or <see langword="null" />.</param>
/// <param name="Rows">Declared topics first, then the others; each group ordinal by name. Empty when the list was withheld.</param>
/// <param name="Internal">Listed topics whose names start with <c>__</c>, ordinal; the declared ones are in <see cref="Rows" />.</param>
internal sealed record KafkaTopicListing(string? RefusalText, IReadOnlyList<KafkaTopicRow> Rows, IReadOnlyList<KafkaTopicRow> Internal)
{
    /// <summary>The rows that are declared topics: the first group.</summary>
    public int DeclaredCount => Rows.TakeWhile(static x => x.Declared).Count();
}

/// <summary>What the Settings page says about discovery: the source of this cluster, and what the sources could not list.</summary>
/// <param name="Source">The name of the source that listed this cluster, or <see langword="null" /> for the host's own registration.</param>
/// <param name="Notes">What the sources did not list, with the source of each.</param>
internal sealed record KafkaDiscoveryView(string? Source, IReadOnlyList<KafkaRegistryNote> Notes)
{
    /// <summary>Whether there is anything to say.</summary>
    public bool IsEmpty => Source is null && Notes.Count == 0;

    /// <summary>The sources the section is about: this cluster's, and each note's.</summary>
    public string SourceNames => string.Join(", ", new string?[] { Source }.Concat(Notes.Select(static x => (string?) x.Source)).OfType<string>().Distinct(StringComparer.Ordinal));
}

/// <summary>What the topic page gets: the topic, or one value for both "missing" and "refused".</summary>
internal abstract record KafkaTopicPageResult
{
    private KafkaTopicPageResult()
    {
    }

    /// <summary>The topic does not exist, is not describable by this application's credentials, is not a valid name, or the policy refuses it: indistinguishable (D35).</summary>
    public sealed record NotAvailable : KafkaTopicPageResult;

    /// <summary>The cluster could not be asked: the sentence says why.</summary>
    /// <param name="Text">A sentence from <see cref="KafkaAccessText" />.</param>
    public sealed record Withheld(string Text) : KafkaTopicPageResult;

    /// <summary>The topic.</summary>
    /// <param name="Overview">The engine's overview.</param>
    /// <param name="Declared">Whether the host declared the topic.</param>
    /// <param name="Description">The declared description.</param>
    /// <param name="KeyFormat">The key format, declared or assumed.</param>
    /// <param name="KeyFormatDeclared">Whether the key format was declared.</param>
    /// <param name="Discovered">What a cluster source (Wolverine) found out about the topic, or <see langword="null" />.</param>
    public sealed record Found(
        KafkaTopicOverview Overview,
        bool Declared,
        string? Description,
        KafkaKeyFormat KeyFormat,
        bool KeyFormatDeclared,
        KafkaDiscoveredTopic? Discovered = null) : KafkaTopicPageResult;
}

/// <summary>
/// The catalog pages' reads (D-UI decision 9), scoped to a circuit. Every method asks <see cref="KafkaAuthorization" /> itself - the page frame's
/// gate is a convenience (hard rule 5) - lists go through <c>FilterTopicsAsync</c>, a single topic is asked before any broker call, and a refusal is a
/// value and an event 9603, never an exception. Catalog reads are not audited (spec B.10.4: scans, record views, exports, tails and API calls are).
/// </summary>
/// <param name="authorization">The cluster policy.</param>
/// <param name="catalog">The engine's catalog.</param>
/// <param name="registry">The engine's clusters, for the declared topics.</param>
/// <param name="settings">The engine's settings reader.</param>
/// <param name="logger">Where event 9603 goes.</param>
internal sealed class KafkaCatalogService(
    KafkaAuthorization authorization,
    IKafkaCatalog catalog,
    KafkaClusterRegistry registry,
    KafkaSettingsReader settings,
    ILogger<KafkaCatalogService> logger)
{
    /// <summary>The home's page size, and so the most topics one span read is asked for.</summary>
    public const int PageSize = KafkaCatalog.MaxSpanTopics;

    /// <summary>The cluster's id, controller and brokers, or <see langword="null" /> when the policy refuses the cluster.</summary>
    /// <param name="entry">The cluster entry.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    public async Task<KafkaClusterOverview?> GetClusterAsync(KafkaClusterEntry entry, CancellationToken cancellationToken)
    {
        if (!await MayReachClusterAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await catalog.GetClusterOverviewAsync(entry.Key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The cluster's topics the visitor may reach and <paramref name="filter" /> keeps (a case-insensitive substring of the name), or
    /// <see langword="null" /> when the policy refuses the cluster.
    /// </summary>
    /// <param name="entry">The cluster entry.</param>
    /// <param name="filter">The filter text, or <see langword="null" /> for none.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    public Task<KafkaTopicListing?> ListTopicsAsync(KafkaClusterEntry entry, string? filter, CancellationToken cancellationToken) =>
        ListTopicsAsync(entry, filter, null, cancellationToken);

    /// <summary>The same, for the topics the visitor may use <paramref name="capability" /> on as well: a search offers only the topics it may search.</summary>
    /// <param name="entry">The cluster entry.</param>
    /// <param name="filter">The filter text, or <see langword="null" /> for none.</param>
    /// <param name="capability">The capability the list is wanted for, or <see langword="null" /> for a plain read.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    public async Task<KafkaTopicListing?> ListTopicsAsync(KafkaClusterEntry entry, string? filter, KafkaCapability? capability, CancellationToken cancellationToken)
    {
        if (!await MayReachClusterAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        KafkaField<KafkaTopicList> listed = await catalog.ListTopicsAsync(entry.Key, cancellationToken).ConfigureAwait(false);

        if (!listed.IsAvailable)
        {
            return new KafkaTopicListing(KafkaAccessText.Describe(listed.Error!), [], []);
        }

        KafkaClusterDefinition definition = registry.Get(entry.Key);
        Dictionary<string, KafkaTopicSummary> byName = listed.Value.Topics
            .Concat(listed.Value.InternalTopics)
            .GroupBy(static x => x.Name, StringComparer.Ordinal)
            .ToDictionary(static x => x.Key, static x => x.First(), StringComparer.Ordinal);

        // The text filter first - it costs nothing - and then the policy, which is one question per topic that is left.
        IEnumerable<string> declaredNames = definition.Topics.Values
            .Where(static x => x.Declared)
            .Select(static x => x.Name)
            .Where(name => Matches(name, filter))
            .Order(StringComparer.Ordinal);

        IReadOnlyList<string> declared = await authorization.FilterTopicsAsync(entry, declaredNames, null, capability, cancellationToken).ConfigureAwait(false);
        HashSet<string> declaredSet = new(declared, StringComparer.Ordinal);

        IReadOnlyList<string> others = await authorization.FilterTopicsAsync(
            entry,
            byName.Values
                .Where(x => !x.IsInternal && !definition.Topics.ContainsKey(x.Name) && Matches(x.Name, filter))
                .Select(static x => x.Name)
                .Order(StringComparer.Ordinal),
            null,
            capability,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<string> internals = await authorization.FilterTopicsAsync(
            entry,
            byName.Values
                .Where(x => x.IsInternal && !definition.Topics.ContainsKey(x.Name) && Matches(x.Name, filter))
                .Select(static x => x.Name)
                .Order(StringComparer.Ordinal),
            null,
            capability,
            cancellationToken).ConfigureAwait(false);

        List<KafkaTopicRow> rows = [.. declared.Where(declaredSet.Contains).Select(name => Row(definition, byName, name))];
        rows.AddRange(others.Select(name => Row(definition, byName, name)));

        return new KafkaTopicListing(null, rows, [.. internals.Select(name => Row(definition, byName, name))]);
    }

    /// <summary>
    /// The offset spans of <paramref name="topics" />, one page of the home's list (at most <see cref="PageSize" />), read together; a topic the
    /// policy refuses, or whose name is not valid, is left out. <see langword="null" /> when the policy refuses the cluster.
    /// </summary>
    /// <param name="entry">The cluster entry.</param>
    /// <param name="topics">The topics of the current page.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    public async Task<KafkaField<IReadOnlyList<KafkaTopicSpan>>?> GetSpansAsync(
        KafkaClusterEntry entry, IReadOnlyList<string> topics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topics);

        if (!await MayReachClusterAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        IReadOnlyList<string> allowed = await authorization.FilterTopicsAsync(entry, topics, PageSize, null, cancellationToken).ConfigureAwait(false);
        KafkaTopicName[] names = [.. allowed.Where(static x => KafkaTopicName.IsValid(x)).Select(static x => new KafkaTopicName(x))];

        return await catalog.ListTopicSpansAsync(entry.Key, names, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A topic's overview. A name that is not valid, one the policy refuses and one the cluster does not list are the same value, and the first two
    /// make no broker call.
    /// </summary>
    /// <param name="entry">The cluster entry.</param>
    /// <param name="topic">The topic as the address named it.</param>
    /// <param name="cancellationToken">Abandons the read.</param>
    public async Task<KafkaTopicPageResult> GetTopicAsync(KafkaClusterEntry entry, string? topic, CancellationToken cancellationToken)
    {
        if (!await MayReachClusterAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return new KafkaTopicPageResult.NotAvailable();
        }

        if (!KafkaTopicName.TryCreate(topic, out KafkaTopicName name))
        {
            return new KafkaTopicPageResult.NotAvailable();
        }

        if (!await authorization.IsAuthorizedAsync(entry, name.Value, null, cancellationToken).ConfigureAwait(false))
        {
            await RefusedAsync(entry, name.Value).ConfigureAwait(false);
            return new KafkaTopicPageResult.NotAvailable();
        }

        KafkaField<KafkaTopicOverview> overview = await catalog.GetTopicOverviewAsync(entry.Key, name, cancellationToken).ConfigureAwait(false);

        if (!overview.IsAvailable)
        {
            return overview.Error is KStudio.Core.Wire.WireError.NotFound
                ? new KafkaTopicPageResult.NotAvailable()
                : new KafkaTopicPageResult.Withheld(KafkaAccessText.Describe(overview.Error!));
        }

        KafkaTopicDescriptor descriptor = registry.Get(entry.Key).Descriptor(name.Value);
        return new KafkaTopicPageResult.Found(overview.Value, descriptor.Declared, descriptor.Description, descriptor.KeyFormat, descriptor.KeyFormatDeclared, descriptor.Discovered);
    }

    /// <summary>
    /// The cluster's settings as the page shows them, or <see langword="null" /> when the policy refuses the cluster. The raw dictionaries never
    /// leave the engine's reader (D33 for librdkafka).
    /// </summary>
    /// <param name="entry">The cluster entry.</param>
    /// <param name="cancellationToken">Abandons the wait for the policy.</param>
    public async Task<KafkaClusterSettingsView?> GetSettingsAsync(KafkaClusterEntry entry, CancellationToken cancellationToken)
    {
        if (!await MayReachClusterAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return settings.Read(registry.Get(entry.Key));
    }

    /// <summary>
    /// What the cluster sources listed and could not list, for the Settings page, or <see langword="null" /> when the policy refuses the cluster. The
    /// notes are the registry's, so they are asked for under the same cluster gate as the settings they sit beside: a note can name a broker, a tenant
    /// or a topic its source did not list.
    /// </summary>
    /// <param name="entry">The cluster entry.</param>
    /// <param name="cancellationToken">Abandons the wait for the policy.</param>
    public async Task<KafkaDiscoveryView?> GetDiscoveryAsync(KafkaClusterEntry entry, CancellationToken cancellationToken)
    {
        if (!await MayReachClusterAsync(entry, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new KafkaDiscoveryView(registry.Get(entry.Key).Source, registry.Notes);
    }

    private async Task<bool> MayReachClusterAsync(KafkaClusterEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (await authorization.IsAuthorizedAsync(entry, null, null, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        await RefusedAsync(entry, null).ConfigureAwait(false);
        return false;
    }

    private async Task RefusedAsync(KafkaClusterEntry entry, string? topic)
    {
        string user = await authorization.UserNameAsync().ConfigureAwait(false);
        logger.ClusterRefused(user, entry.Cluster, entry.TenantId, topic, authorization.PolicyFor(null) ?? "(none)");
    }

    private static bool Matches(string name, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static KafkaTopicRow Row(KafkaClusterDefinition definition, Dictionary<string, KafkaTopicSummary> listed, string name)
    {
        KafkaTopicDescriptor descriptor = definition.Descriptor(name);

        return new KafkaTopicRow(
            name,
            descriptor.Description,
            listed.TryGetValue(name, out KafkaTopicSummary? summary) ? summary.PartitionCount : null,
            descriptor.KeyFormat,
            descriptor.KeyFormatDeclared,
            descriptor.Declared);
    }
}
