using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace KStudio.Services;

/// <summary>
/// What the circuit's Kafka pages are about (D34): the cluster entry in scope, and the entries this visitor may see. The host studio's
/// <c>StudioState</c> is not generalised; this is KStudio's own, and the two never touch.
/// </summary>
/// <remarks>
/// <para>
/// <b>The address.</b> The cluster travels as <c>?cluster=&lt;key&gt;</c> (D9), read once when the state first loads - the cluster list is
/// configuration, so this reads no broker - and written back by the cluster selector only once the circuit is interactive and after
/// <see cref="AddressRead" />. A value that is unknown, or that the policy refuses, is ignored: the first available cluster is taken, and
/// the page does not say which it was, so a refused cluster cannot be told from a missing one.
/// </para>
/// <para>
/// <b>A picked cluster is validated.</b> <see cref="SetClusterAsync" /> accepts only a key in <see cref="AvailableClusters" />, compared
/// ordinally. A <c>change</c> event's value comes from the browser, and Blazor does not check it against the options the server rendered
/// (<c>StudioState</c>'s own remarks): without this a client could name any cluster in the process.
/// </para>
/// <para>
/// <b>Tenants.</b> A broker-per-tenant cluster is one entry per tenant; there is no <c>tenant=</c> on a Kafka address.
/// </para>
/// </remarks>
/// <param name="source">Where the entries come from.</param>
/// <param name="authorization">The cluster policy.</param>
/// <param name="navigation">The circuit's address.</param>
/// <param name="logger">Where event 9610 goes.</param>
internal sealed class KafkaState(
    IKafkaClusterEntrySource source,
    KafkaAuthorization authorization,
    NavigationManager navigation,
    ILogger<KafkaState> logger)
{
    /// <summary>The query-string name the cluster travels under.</summary>
    public const string ClusterParameter = "cluster";

    private Task? initialization;

    /// <summary>The cluster entry in scope, or <see langword="null" /> when the visitor may see none.</summary>
    public KafkaClusterEntry? ActiveCluster { get; private set; }

    /// <summary>The entries the cluster policy passes for this visitor (<c>Topic = null</c>).</summary>
    public IReadOnlyList<KafkaClusterEntry> AvailableClusters { get; private set; } = [];

    /// <summary>Whether the address's own cluster was read; the address is not rewritten before it was.</summary>
    public bool AddressRead { get; private set; }

    /// <summary>Raised after the cluster in scope changed. Awaited one handler after another (hard rule 6: never an <c>Action</c>).</summary>
    public event Func<Task>? ClusterChanged;

    /// <summary>Loads the entries and reads the address's cluster, once; later calls wait for the same load.</summary>
    /// <param name="cancellationToken">Abandons this caller's wait.</param>
    public Task EnsureInitializedAsync(CancellationToken cancellationToken = default) =>
        (initialization ??= InitializeAsync()).WaitAsync(cancellationToken);

    private async Task InitializeAsync()
    {
        IReadOnlyList<KafkaClusterEntry> available = await authorization.FilterClustersAsync(source.Entries).ConfigureAwait(false);
        string? requested = ReadCluster(navigation.Uri);

        KafkaClusterEntry? active = available.FirstOrDefault(x => requested is not null && string.Equals(x.Key, requested, StringComparison.Ordinal))
            ?? (available.Count > 0 ? available[0] : null);

        if (requested is not null && active is not null && !string.Equals(active.Key, requested, StringComparison.Ordinal))
        {
            logger.ClusterAddressIgnored(requested, active.Key);
        }

        AvailableClusters = available;
        ActiveCluster = active;
        AddressRead = true;
    }

    /// <summary>
    /// Takes the cluster the circuit's current address names, as every Kafka page does on its first render: a page reached from a host page,
    /// or by Back, opens the cluster its address names rather than the one the state last held. Only a key in <see cref="AvailableClusters" />
    /// moves the scope; an address that names none, an unknown key and a refused one change nothing (the unknown ones are logged, as at the
    /// first load, and the selector then rewrites the address to the cluster in scope).
    /// </summary>
    public async Task ApplyAddressAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        string? requested = ReadCluster(navigation.Uri);
        if (requested is null)
        {
            return;
        }

        if (AvailableClusters.Any(x => string.Equals(x.Key, requested, StringComparison.Ordinal)))
        {
            await SetClusterAsync(requested).ConfigureAwait(false);
        }
        else if (ActiveCluster is { } active)
        {
            logger.ClusterAddressIgnored(requested, active.Key);
        }
    }

    /// <summary>
    /// Moves the scope to the entry keyed <paramref name="key" />, if the visitor may see it. Anything else - a key the listing does not carry,
    /// or the one already in scope - changes nothing.
    /// </summary>
    /// <param name="key">The key a picker or an address named.</param>
    public async Task SetClusterAsync(string? key)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);

        KafkaClusterEntry? chosen = AvailableClusters.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
        if (chosen is null || ActiveCluster == chosen)
        {
            return;
        }

        ActiveCluster = chosen;
        await NotifyAsync().ConfigureAwait(false);
    }

    /// <summary>The <c>cluster</c> value of <paramref name="uri" />, or <see langword="null" />.</summary>
    /// <param name="uri">An absolute address.</param>
    public static string? ReadCluster(string uri)
    {
        int query = uri.IndexOf('?', StringComparison.Ordinal);
        if (query < 0)
        {
            return null;
        }

        int fragment = uri.IndexOf('#', query);
        string text = fragment < 0 ? uri[query..] : uri[query..fragment];

        return QueryHelpers.ParseQuery(text).TryGetValue(ClusterParameter, out var values) && values.Count > 0 && !string.IsNullOrEmpty(values[0])
            ? values[0]
            : null;
    }

    private async Task NotifyAsync()
    {
        Func<Task>? handlers = ClusterChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Func<Task> handler in handlers.GetInvocationList().Cast<Func<Task>>())
        {
            await handler().ConfigureAwait(false);
        }
    }
}
