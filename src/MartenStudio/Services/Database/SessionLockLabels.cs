using System.Globalization;

using JasperFx.Events.Daemon;

using Marten;

namespace MartenStudio.Services.Database;

/// <summary>What a lock is on, in Marten's terms.</summary>
internal enum LockTargetKind
{
    /// <summary><c>StoreOptions.ApplyChangesLockId</c>: a schema apply on startup or from the studio.</summary>
    ApplyChangesLock,

    /// <summary><c>Projections.DaemonLockId</c>: the async daemon's own leadership lock.</summary>
    DaemonLock,

    /// <summary>One async shard's lock (<c>ProjectionLockIds.Compute</c>).</summary>
    ShardLock,

    /// <summary>An advisory lock that is none of Marten's.</summary>
    OtherAdvisoryLock,

    /// <summary>A visible document type's table.</summary>
    DocumentTable,

    /// <summary>One of the event store's tables - <c>mt_streams</c>, <c>mt_events</c>, <c>mt_event_progression</c>.</summary>
    EventTable,

    /// <summary>Marten's own bookkeeping: any other <c>mt_</c> relation.</summary>
    MartenInfrastructure,

    /// <summary>A table Marten manages for the host: a flat-table projection's, an extended schema object.</summary>
    MartenManagedTable,

    /// <summary>
    /// A relation Marten owns that is not this store's own declared object - another registered store's, or an
    /// <c>mt_</c> name no store declares. Labelled by kind and named to nobody: its name can carry another store's
    /// alias, a tenant id or a hidden document type's.
    /// </summary>
    UndeclaredMartenRelation,

    /// <summary>A Postgres system catalog.</summary>
    SystemCatalog,

    /// <summary>A relation Marten does not own. Named only to a visitor who may see details, and masked for them.</summary>
    OtherRelation,

    /// <summary>
    /// A relation that must not be named at all: a document type <c>IsDocumentTypeVisible</c> hides, or one the
    /// studio cannot classify because the store's configuration could not be read.
    /// </summary>
    UnnamedRelation,

    /// <summary>Another transaction - a row lock whose relation is not known, or a virtual transaction id.</summary>
    Transaction,

    /// <summary>Any other kind of lock (<c>object</c>, <c>page</c>, <c>extend</c>, <c>spectoken</c>…).</summary>
    Other,
}

/// <summary>Which part of its owning table a lock is on.</summary>
internal enum LockedPart
{
    /// <summary>The relation itself.</summary>
    Itself,

    /// <summary>One of its indexes.</summary>
    Index,

    /// <summary>Its TOAST table, or the TOAST table's index.</summary>
    Toast,

    /// <summary>One of its partitions, at any depth.</summary>
    Partition,

    /// <summary>A sequence one of its columns owns (an identity or <c>serial</c> column).</summary>
    OwnedSequence,
}

/// <summary>
/// A locked relation as the lock reads return it: the table it belongs to - an index's table, a TOAST table's or
/// a column-owned sequence's table, a partition's root - and which part of it is locked. The locked relation's own
/// name is never read (<see cref="Internal.Sql.HealthConnectionQueries" />).
/// </summary>
/// <param name="Schema">The owner's schema.</param>
/// <param name="Name">The owner's name.</param>
/// <param name="Kind">The owner's <c>pg_class.relkind</c>: <c>r</c>, <c>p</c>, <c>v</c>, <c>m</c>, <c>S</c>, <c>f</c>…</param>
/// <param name="Part">Which part of it is locked.</param>
internal sealed record LockedRelation(string Schema, string Name, string Kind, LockedPart Part = LockedPart.Itself)
{
    /// <summary>A sequence's <c>relkind</c>.</summary>
    public const string SequenceKind = "S";

    /// <summary>The part, from the statement's <c>part</c> column.</summary>
    public static LockedPart PartOf(string? part) => part switch
    {
        "index" => LockedPart.Index,
        "toast" => LockedPart.Toast,
        "partition" => LockedPart.Partition,
        "sequence" => LockedPart.OwnedSequence,
        _ => LockedPart.Itself,
    };
}

/// <summary>What one lock is on, as the studio labels it.</summary>
/// <param name="Kind">What kind of thing.</param>
/// <param name="Label">
/// The label anybody the store policy passes for the database may read: this store's own declared, visible
/// objects by name (the Schema screen shows them already) and Postgres' system catalogs; anything else by kind
/// alone.
/// </param>
/// <param name="Schema">
/// The owning relation's schema, for a relation the details may name - this store's own, a system catalog, or one
/// Marten does not own - and never for a hidden one, another store's, or an index, partition or TOAST table.
/// </param>
/// <param name="Name">The owning relation's name, likewise.</param>
/// <param name="Row">Whether the wait is for a row of the relation rather than the relation itself.</param>
internal sealed record LockTarget(LockTargetKind Kind, string Label, string? Schema = null, string? Name = null, bool Row = false)
{
    /// <summary>Whether it is one of Marten's own.</summary>
    public bool IsMarten => Kind is LockTargetKind.ApplyChangesLock or LockTargetKind.DaemonLock or LockTargetKind.ShardLock
        or LockTargetKind.DocumentTable or LockTargetKind.EventTable or LockTargetKind.MartenInfrastructure
        or LockTargetKind.MartenManagedTable or LockTargetKind.UndeclaredMartenRelation;
}

/// <summary>
/// Labels locks in Marten's terms - the apply-changes lock, the daemon's lock, a shard's lock, an
/// <c>mt_streams</c> row, a document table - from the store's own configuration, touching no connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Advisory keys.</b> Marten and Weasel take every advisory lock through
/// <c>pg_try_advisory_lock(bigint)</c> / <c>pg_try_advisory_xact_lock(bigint)</c> with an <c>int</c> id, so the
/// key <c>pg_locks</c> shows (reassembled from <c>classid</c> and <c>objid</c> where <c>objsubid = 1</c>) is the id
/// itself, sign included. The ids are <c>StoreOptions.ApplyChangesLockId</c> (default 4004, read at Marten
/// V9.39.0's <c>MartenActivator</c>), <c>Projections.DaemonLockId</c> (JasperFx's <c>DaemonSettings</c>, default
/// 4444) and one id per async shard, <c>ProjectionLockIds.Compute(Events.DatabaseSchemaName, shard, DaemonLockId)</c>
/// - the set Marten's own <c>HighWaterDetector.daemonAdvisoryLockIds()</c> computes to recognise its daemon's
/// sessions, unchanged between Marten 9.39.0 / JasperFx 2.74.0 and Marten 9.50.2 / JasperFx 2.87.1. Marten's
/// probe drops negative ids because it matches on <c>classid = 0</c>; matching the whole 64-bit key does not
/// need to.
/// </para>
/// <para>
/// <b>Relations, by their owner.</b> A lock read never hands this class an index's, a partition's or a TOAST
/// table's name: it hands it the table that relation belongs to and which part of it is locked
/// (<see cref="LockedRelation" />). That owner is classified by the database browser's own classifier over
/// <b>every</b> registered store's declarations (<see cref="DatabaseAccess.DeclareEveryStore" />, no connection),
/// and named in a label only when it is <b>this store's own declared, visible object</b> - its document tables by
/// alias, its event tables, its declared infrastructure, its projection tables - or a Postgres system catalog.
/// Everything else is labelled by kind:
/// </para>
/// <list type="bullet">
/// <item>A hidden document type's table is "a relation" - not even its part is said, and not even to a visitor
/// with details, because <c>IsDocumentTypeVisible</c> hides a type from everyone.</item>
/// <item>Another store's table, and an <c>mt_</c> name no store declares - a stale table, a type Marten has not
/// learned yet - is "a Marten relation this store does not declare", named to nobody: its name can carry another
/// store's alias, a tenant id, or a hidden type's alias.</item>
/// <item>A relation Marten does not own is "a relation Marten does not own"; its name is the details' to show,
/// masked through the visitor's withheld schemas.</item>
/// <item>A per-tenant event sequence (<c>mt_events_sequence_&lt;tenant&gt;</c>) rolls up into "a tenant's event
/// sequence", because its name is a tenant id; a tenant partition rolls up into its table by the SQL.</item>
/// </list>
/// <para>
/// When any store's configuration cannot be read, nothing is called Marten's and nothing is named:
/// classification fails closed, as D27's does.
/// </para>
/// </remarks>
internal sealed class SessionLockLabels
{
    private readonly string storeKey;
    private readonly long? applyChangesLockId;
    private readonly long? daemonLockId;
    private readonly Dictionary<long, string> shardLocks;
    private readonly DatabaseObjectClassifier? classifier;

    private SessionLockLabels(
        string storeKey,
        long? applyChangesLockId,
        long? daemonLockId,
        Dictionary<long, string> shardLocks,
        DatabaseObjectClassifier? classifier)
    {
        this.storeKey = storeKey;
        this.applyChangesLockId = applyChangesLockId;
        this.daemonLockId = daemonLockId;
        this.shardLocks = shardLocks;
        this.classifier = classifier;
    }

    /// <summary>Every advisory key Marten's daemon negotiates leadership with: the base id and every shard's.</summary>
    public IReadOnlyCollection<long> DaemonLockIds =>
        daemonLockId is { } id ? [id, .. shardLocks.Keys.Where(x => x != id)] : [.. shardLocks.Keys];

    /// <summary>The labels for <paramref name="store" />, registered under <paramref name="storeKey" />.</summary>
    /// <param name="store">The store.</param>
    /// <param name="storeKey">Its registration key.</param>
    /// <param name="classifier">
    /// The classifier over every registered store's declarations (<see cref="DatabaseAccess.DeclareEveryStore" />
    /// or <see cref="DatabaseAccess.ReadDeclarations(ResolvedScope)" />), or <see langword="null" /> when they
    /// could not all be read - or when only advisory keys are wanted - which names no relation at all.
    /// </param>
    public static SessionLockLabels For(IDocumentStore store, string storeKey, DatabaseObjectClassifier? classifier)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(storeKey);

        long? apply = null;
        long? daemon = null;
        Dictionary<long, string> shards = [];

        if (store.Options is StoreOptions options)
        {
            apply = options.ApplyChangesLockId;

            int baseLockId = options.Projections.DaemonLockId;
            daemon = baseLockId;

            string schema = store.Options.Events.DatabaseSchemaName;

            try
            {
                foreach (var shard in options.Projections.AllShards())
                {
                    try
                    {
                        shards.TryAdd(ProjectionLockIds.Compute(schema, shard.Name, baseLockId), shard.Name.Identity);
                    }
                    catch (OverflowException)
                    {
                        // Math.Abs(int.MinValue) inside the hash: a shard whose id cannot be computed is labelled
                        // as nobody's, which is what it would be in pg_locks too.
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A projection that cannot list its shards: its locks read as "not Marten's" - a label, not a
                // judgement, and C04 then treats such a session as it would any other.
            }
        }

        return new SessionLockLabels(storeKey, apply, daemon, shards, classifier);
    }

    /// <summary>What one lock is on.</summary>
    /// <param name="lockType"><c>pg_locks.locktype</c>.</param>
    /// <param name="relation">The locked relation's owner, for a <c>relation</c> or <c>tuple</c> lock.</param>
    /// <param name="advisoryKey">The advisory key, for an <c>advisory</c> lock in the one-key form.</param>
    /// <param name="row">
    /// For a wait on another transaction, the owner of the row the waiter holds the tuple lock on.
    /// </param>
    public LockTarget Describe(
        string lockType,
        LockedRelation? relation = null,
        long? advisoryKey = null,
        LockedRelation? row = null)
    {
        ArgumentNullException.ThrowIfNull(lockType);

        switch (lockType)
        {
            case "advisory":
                return Advisory(advisoryKey);

            case "relation":
                return Relation(relation, row: false);

            case "tuple":
                return Relation(relation, row: true);

            case "transactionid" when row is not null:
                return Relation(row, row: true);

            case "transactionid":
            case "virtualxid":
                return new LockTarget(LockTargetKind.Transaction, "another transaction to finish");

            default:
                return new LockTarget(LockTargetKind.Other, "a lock of kind '" + lockType + "'");
        }
    }

    /// <summary>An advisory lock, by its key.</summary>
    public LockTarget Advisory(long? key)
    {
        if (key is not { } value)
        {
            return new LockTarget(LockTargetKind.OtherAdvisoryLock, "an advisory lock that is not Marten's");
        }

        if (value == applyChangesLockId)
        {
            return new LockTarget(
                LockTargetKind.ApplyChangesLock,
                "Marten's apply-changes lock (ApplyChangesLockId " + value.ToString(CultureInfo.InvariantCulture) + ")");
        }

        if (value == daemonLockId)
        {
            return new LockTarget(
                LockTargetKind.DaemonLock,
                "the async daemon's lock (DaemonLockId " + value.ToString(CultureInfo.InvariantCulture) + ")");
        }

        if (shardLocks.TryGetValue(value, out string? shard))
        {
            return new LockTarget(LockTargetKind.ShardLock, "the async daemon's lock for shard " + shard);
        }

        return new LockTarget(LockTargetKind.OtherAdvisoryLock, "an advisory lock that is not Marten's");
    }

    /// <summary>A relation, or one of its rows, by the owner the lock read resolved it to.</summary>
    /// <param name="relation">The owner, or <see langword="null" /> when the read could not resolve one.</param>
    /// <param name="row">Whether the lock is on a row of it.</param>
    public LockTarget Relation(LockedRelation? relation, bool row)
    {
        if (relation is null)
        {
            return Unnamed(row);
        }

        string schema = relation.Schema;
        string name = relation.Name;

        // A row of a partition is a row of the table; any other part is said in front of the owner's label.
        string part = row ? "a row of " : PartWords(relation.Part);

        if (schema.StartsWith("pg_toast", StringComparison.Ordinal))
        {
            // A TOAST table nothing owns any more: its name is an oid, and says nothing worth saying.
            return new LockTarget(LockTargetKind.SystemCatalog, part + "a TOAST table", Row: row);
        }

        if (schema.StartsWith("pg_temp", StringComparison.Ordinal))
        {
            // Some session's temporary table, in a schema Postgres names for its backend: the name is that session's.
            return new LockTarget(LockTargetKind.OtherRelation, part + "a temporary table", Row: row);
        }

        if (BrowsableSchemaMatcher.IsSystemSchema(schema))
        {
            return new LockTarget(LockTargetKind.SystemCatalog, part + "the system catalog " + schema + "." + name, schema, name, row);
        }

        if (classifier is null)
        {
            return Unnamed(row);
        }

        if (relation.Kind == LockedRelation.SequenceKind && DatabaseObjectClassifier.IsPerTenantEventSequence(name))
        {
            // mt_events_sequence_<tenant>: the name is a tenant id, so it is this store's event sequence, a tenant's.
            return classifier.ClassifyRelation(schema, DatabaseObjectClassifier.EventSequence) is { } sequence && IsThisStores(sequence)
                ? new LockTarget(LockTargetKind.EventTable, "a tenant's event sequence (" + schema + ")", schema, DatabaseObjectClassifier.EventSequence, row)
                : Undeclared(part, row);
        }

        // Null is a hidden document type's table: named to nobody, the details included, and not even its part said.
        if (classifier.ClassifyRelation(schema, name) is not { } ownership)
        {
            return Unnamed(row);
        }

        if (ownership.IsMarten && !IsThisStores(ownership))
        {
            return Undeclared(part, row);
        }

        return ownership.Owner switch
        {
            DatabaseObjectOwner.MartenDocument => new LockTarget(
                LockTargetKind.DocumentTable,
                part + "the " + (ownership.Alias ?? name) + " document table (" + schema + "." + name + ")",
                schema,
                name,
                row),

            DatabaseObjectOwner.MartenEventStore => new LockTarget(
                LockTargetKind.EventTable,
                row ? "an " + name + " row (" + schema + ")" : part + "the event store's " + name + " table (" + schema + ")",
                schema,
                name,
                row),

            DatabaseObjectOwner.MartenInfrastructure => new LockTarget(
                LockTargetKind.MartenInfrastructure, part + "Marten's " + schema + "." + name, schema, name, row),

            DatabaseObjectOwner.MartenProjectionOrExtended => new LockTarget(
                LockTargetKind.MartenManagedTable, part + "the Marten-managed table " + schema + "." + name, schema, name, row),

            _ => new LockTarget(LockTargetKind.OtherRelation, part + "a relation Marten does not own", schema, name, row),
        };
    }

    /// <summary>What a lock on <paramref name="part" /> of a relation says in front of the relation's label.</summary>
    private static string PartWords(LockedPart part) => part switch
    {
        LockedPart.Index => "an index of ",
        LockedPart.Toast => "the TOAST data of ",
        LockedPart.Partition => "a partition of ",
        LockedPart.OwnedSequence => "a sequence owned by ",
        _ => string.Empty,
    };

    private static LockTarget Unnamed(bool row) =>
        new(LockTargetKind.UnnamedRelation, row ? "a row of a relation" : "a relation", Row: row);

    private static LockTarget Undeclared(string part, bool row) =>
        new(LockTargetKind.UndeclaredMartenRelation, part + "a Marten relation this store does not declare", Row: row);

    /// <summary>Whether <paramref name="ownership" /> is this store's own declaration, the first store to declare it.</summary>
    private bool IsThisStores(DatabaseObjectOwnership ownership) =>
        string.Equals(ownership.StoreKey, storeKey, StringComparison.OrdinalIgnoreCase);
}
