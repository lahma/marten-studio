using Npgsql;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Health;

/// <summary>One lock a health statement's session held while its transaction was still open.</summary>
/// <param name="LockType">pg_locks.locktype - <c>relation</c>, <c>virtualxid</c>, <c>advisory</c>...</param>
/// <param name="Mode">pg_locks.mode - <c>AccessShareLock</c>...</param>
/// <param name="Schema">The locked relation's schema, for a relation lock.</param>
/// <param name="Relation">The locked relation's name, for a relation lock.</param>
/// <param name="Statement">The statement that had just run.</param>
internal sealed record HeldLock(string LockType, string Mode, string? Schema, string? Relation, string Statement)
{
    /// <summary>A catalog read: an <c>AccessShareLock</c> on a relation in <c>pg_catalog</c> (or a catalog's TOAST).</summary>
    public bool IsCatalogRead =>
        LockType == "relation" && Mode == "AccessShareLock" && Schema is "pg_catalog" or "pg_toast" or "information_schema";

    /// <summary>The lock every transaction holds on its own virtual transaction id.</summary>
    public bool IsOwnTransaction => LockType == "virtualxid" && Mode == "ExclusiveLock";

    public override string ToString() => $"{Mode} on {LockType} {Schema}.{Relation} after: {Statement}";
}

/// <summary>
/// The no-DDL / no-lock assertion for a health sweep, for every Health packet's live tests to reuse.
/// </summary>
/// <remarks>
/// <para>
/// <b>No lock beyond a catalog read.</b> Registered in the studio's container as its
/// <see cref="IHealthStatementObserver" />, it is told about every health statement while that statement's
/// read-only transaction is still open, and reads <c>pg_locks</c> for the statement's backend <em>from a
/// second connection</em> - so what it records is what the session really held, and the probe's own reads
/// are not in it. <see cref="BeyondCatalogReads" /> is everything but an <c>AccessShareLock</c> on a catalog
/// relation and the transaction's lock on its own virtual id: a check that reads a user table, takes an
/// advisory lock, or calls a function that locks what it lists (D27) shows up there.
/// </para>
/// <para>
/// <b>No DDL.</b> <see cref="SnapshotAsync" /> lists every relation, routine, type, trigger and constraint in
/// a set of schemas - the test's own, never the whole database, because other test classes create objects in
/// parallel - and a sweep must leave the snapshot exactly as it found it.
/// </para>
/// <para>
/// Usage, for a later packet: <c>HealthSweepProbe probe = new(postgres)</c>; build the host with
/// <c>services: s =&gt; s.AddSingleton&lt;IHealthStatementObserver&gt;(probe)</c>; take a snapshot; run the
/// sweep; then <c>probe.StatementsObserved.Should().BePositive()</c> (so "no locks" is not "nothing ran"),
/// <c>probe.BeyondCatalogReads.Should().BeEmpty()</c> and compare a second snapshot to the first.
/// </para>
/// </remarks>
internal sealed class HealthSweepProbe(PostgresFixture postgres) : IHealthStatementObserver
{
    private const string LocksSql =
        "select l.locktype, l.mode, n.nspname, c.relname "
        + "from pg_catalog.pg_locks l "
        + "left join pg_catalog.pg_class c on c.oid = l.relation "
        + "left join pg_catalog.pg_namespace n on n.oid = c.relnamespace "
        + "where l.pid = @pid and l.granted";

    private readonly List<HeldLock> locks = [];
    private readonly Lock gate = new();
    private int statements;

    /// <summary>How many health statements the probe was told about.</summary>
    public int StatementsObserved => Volatile.Read(ref statements);

    /// <summary>Every lock every observed session held.</summary>
    public IReadOnlyList<HeldLock> Locks
    {
        get
        {
            lock (gate)
            {
                return [.. locks];
            }
        }
    }

    /// <summary>Every lock beyond a catalog read and the transaction's own.</summary>
    public IReadOnlyList<HeldLock> BeyondCatalogReads => [.. Locks.Where(static x => !x.IsCatalogRead && !x.IsOwnTransaction)];

    /// <inheritdoc />
    public async Task ObserveAsync(int backendProcessId, HealthStatement statement, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref statements);

        await using NpgsqlConnection second = await postgres.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(LocksSql, second);
        command.Parameters.AddWithValue("pid", backendProcessId);

        List<HeldLock> held = [];

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                held.Add(new HeldLock(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    statement.Sql));
            }
        }

        lock (gate)
        {
            locks.AddRange(held);
        }
    }

    /// <summary>
    /// Every object in <paramref name="schemas" />, by name and kind - read on a connection of the test's
    /// own, so nothing of the studio's is in the measurement.
    /// </summary>
    public static async Task<IReadOnlySet<string>> SnapshotAsync(PostgresFixture postgres, params string[] schemas)
    {
        const string Sql =
            "select 'schema:' || n.nspname from pg_catalog.pg_namespace n where n.nspname = any(@schemas) "
            + "union all select 'relation:' || n.nspname || '.' || c.relname || ':' || c.relkind::text from pg_catalog.pg_class c "
            + "join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = any(@schemas) "
            + "union all select 'routine:' || n.nspname || '.' || p.proname || '(' || pg_catalog.pg_get_function_identity_arguments(p.oid) || ')' "
            + "from pg_catalog.pg_proc p join pg_catalog.pg_namespace n on n.oid = p.pronamespace where n.nspname = any(@schemas) "
            + "union all select 'type:' || n.nspname || '.' || t.typname from pg_catalog.pg_type t "
            + "join pg_catalog.pg_namespace n on n.oid = t.typnamespace where n.nspname = any(@schemas) "
            + "union all select 'trigger:' || n.nspname || '.' || c.relname || '.' || g.tgname from pg_catalog.pg_trigger g "
            + "join pg_catalog.pg_class c on c.oid = g.tgrelid join pg_catalog.pg_namespace n on n.oid = c.relnamespace "
            + "where n.nspname = any(@schemas) "
            + "union all select 'constraint:' || n.nspname || '.' || o.conname from pg_catalog.pg_constraint o "
            + "join pg_catalog.pg_namespace n on n.oid = o.connamespace where n.nspname = any(@schemas)";

        await using NpgsqlConnection connection = await postgres.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(Sql, connection);
        command.Parameters.AddWithValue("schemas", schemas);

        HashSet<string> objects = new(StringComparer.Ordinal);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }
}
