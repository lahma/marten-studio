namespace PostgresStudio.Services.Health;

/// <summary>Something that keeps state per database, and forgets a database when <see cref="HealthDatabaseBound" /> says so.</summary>
internal interface IPerDatabaseState
{
    /// <summary>Forgets everything kept for one database of one source.</summary>
    /// <param name="storeKey">The source's key.</param>
    /// <param name="databaseId">The database's identity.</param>
    void Forget(string storeKey, string databaseId);
}

/// <summary>
/// The process-wide bound on how many databases the health advisor keeps state for (API design §3.5): the least recently
/// used past <see cref="DefaultCapacity" /> loses its history ring, its sweep-cache slots and its statement texts together.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The sweep cache (a slot per database and kind), the history (sixty samples a database, and V1's first-seen map
/// for T14 inside it) and the statement checks' text cache (up to 256 texts of 4,096 characters a database) each grow by one
/// entry per database ever swept. With Marten that was "what visitors opened"; with a host's database source (issue #8 G11),
/// the export's rotation and a host's nightly loop over the diagnostics API, it is every tenant - up to hundreds of megabytes
/// of statement texts alone.
/// </para>
/// <para>
/// <b>Used is swept or read.</b> A database is touched by every sweep of it (<see cref="HealthSweeper" />) and every read of the
/// sweep cache for it (<see cref="HealthSweepCache.GetAsync" />), so a page open on a database keeps it, whatever the export's
/// rotation sweeps beside it. A database evicted and swept again starts its history from scratch, which the Health page says
/// as it always has ("Watching for under a minute").
/// </para>
/// <para>
/// Each participant registers itself when it is built; the bound forgets outside its own lock, so a participant's lock is
/// never taken under it. A sweep running for an evicted database keeps its slot (<see cref="HealthSweepCache" />), and its
/// database is the most recently touched anyway.
/// </para>
/// </remarks>
internal sealed class HealthDatabaseBound
{
    /// <summary>How many databases keep state: about the most a person or a page watches, far fewer than a tenant fleet.</summary>
    public const int DefaultCapacity = 64;

    private readonly LinkedList<(string StoreKey, string DatabaseId)> order = new();
    private readonly Dictionary<string, LinkedListNode<(string StoreKey, string DatabaseId)>> nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPerDatabaseState> participants = [];
    private readonly Lock gate = new();

    /// <param name="capacity">How many databases keep state; <see cref="DefaultCapacity" /> in a host.</param>
    public HealthDatabaseBound(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    /// <summary>How many databases keep state.</summary>
    public int Capacity { get; }

    /// <summary>How many databases are tracked now, for a test.</summary>
    internal int Count
    {
        get
        {
            lock (gate)
            {
                return nodes.Count;
            }
        }
    }

    /// <summary>Adds something that keeps state per database.</summary>
    public void Register(IPerDatabaseState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (gate)
        {
            if (!participants.Contains(state))
            {
                participants.Add(state);
            }
        }
    }

    /// <summary>
    /// Marks one database as just used, and forgets the least recently used one past the capacity - in every participant.
    /// </summary>
    /// <param name="storeKey">The source's key.</param>
    /// <param name="databaseId">The database's identity.</param>
    public void Touch(string storeKey, string databaseId)
    {
        ArgumentNullException.ThrowIfNull(storeKey);
        ArgumentNullException.ThrowIfNull(databaseId);

        string key = storeKey + "|" + databaseId;
        List<(string StoreKey, string DatabaseId)> evicted = [];
        IPerDatabaseState[] forgetting;

        lock (gate)
        {
            if (nodes.TryGetValue(key, out LinkedListNode<(string, string)>? node))
            {
                order.Remove(node);
                order.AddFirst(node);
                return;
            }

            nodes[key] = order.AddFirst((storeKey, databaseId));

            while (nodes.Count > Capacity && order.Last is { } oldest)
            {
                order.RemoveLast();
                nodes.Remove(oldest.Value.StoreKey + "|" + oldest.Value.DatabaseId);
                evicted.Add(oldest.Value);
            }

            forgetting = evicted.Count == 0 ? [] : [.. participants];
        }

        foreach ((string evictedStore, string evictedDatabase) in evicted)
        {
            foreach (IPerDatabaseState state in forgetting)
            {
                state.Forget(evictedStore, evictedDatabase);
            }
        }
    }
}
