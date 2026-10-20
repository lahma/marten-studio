using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The database-wide half of what <see cref="HealthHorizonQueries.Wraparound" /> reads: the settings, this database's and
/// the server's oldest ages, the two counters a forecast is drawn from, and the relations that are not the store's,
/// counted and never named.
/// </summary>
/// <remarks>Init properties, so a unit test states only the facts its case is about.</remarks>
internal sealed record HealthWraparoundDatabase
{
    /// <summary><c>autovacuum_freeze_max_age</c>: where autovacuum forces a freeze of a table.</summary>
    public long FreezeMaxAge { get; init; } = 200_000_000;

    /// <summary><c>autovacuum_multixact_freeze_max_age</c>: the same line, in multixacts.</summary>
    public long MultixactFreezeMaxAge { get; init; } = 400_000_000;

    /// <summary><c>vacuum_failsafe_age</c> (PostgreSQL 14+), or <see langword="null" /> before.</summary>
    public long? FailsafeAge { get; init; }

    /// <summary><c>vacuum_multixact_failsafe_age</c> (PostgreSQL 14+), or <see langword="null" /> before.</summary>
    public long? MultixactFailsafeAge { get; init; }

    /// <summary><c>server_version_num</c>.</summary>
    public int ServerVersion { get; init; } = 170_000;

    /// <summary>This database's <c>age(datfrozenxid)</c>: what wraparound protection acts on.</summary>
    public long DatabaseXidAge { get; init; }

    /// <summary>This database's <c>mxid_age(datminmxid)</c>.</summary>
    public long DatabaseMultixactAge { get; init; }

    /// <summary>
    /// The oldest <c>age(datfrozenxid)</c> of any database on the server - a number, never a name. Postgres refuses new
    /// transaction ids in every database once the oldest one reaches its stop limit.
    /// </summary>
    public long ServerXidAge { get; init; }

    /// <summary>The oldest <c>mxid_age(datminmxid)</c> of any database on the server.</summary>
    public long ServerMultixactAge { get; init; }

    /// <summary>
    /// <c>pg_snapshot_xmax(pg_current_snapshot())</c>: the next transaction id to be assigned, with its epoch - a counter
    /// that only rises, read without assigning one.
    /// </summary>
    public long XidCounter { get; init; }

    /// <summary>
    /// The next multixact id, as <c>datminmxid + mxid_age(datminmxid)</c> modulo 2^32: a counter that rises with every
    /// multixact created and wraps after four billion, read from shared memory with no privilege.
    /// </summary>
    public long MultixactCounter { get; init; }

    /// <summary>The oldest <c>age(relfrozenxid)</c> of a user table in this database that is not the store's, or <see langword="null" />.</summary>
    public long? OtherOldestXidAge { get; init; }

    /// <summary>Whether that oldest table is a temporary one, which only its own session can vacuum.</summary>
    public bool OtherOldestIsTemporary { get; init; }

    /// <summary>The oldest <c>mxid_age(relminmxid)</c> of a user table in this database that is not the store's.</summary>
    public long? OtherOldestMultixactAge { get; init; }

    /// <summary>How many user tables that are not the store's are past <see cref="FreezeMaxAge" />. Never their names.</summary>
    public int OtherPastForcedXid { get; init; }

    /// <summary>How many of <see cref="OtherPastForcedXid" /> are temporary tables.</summary>
    public int OtherTemporaryPastForcedXid { get; init; }

    /// <summary>How many user tables that are not the store's are past <see cref="MultixactFreezeMaxAge" />.</summary>
    public int OtherPastForcedMultixact { get; init; }

    /// <summary>How many of <see cref="OtherPastForcedMultixact" /> are temporary tables.</summary>
    public int OtherTemporaryPastForcedMultixact { get; init; }

    /// <summary>How many system catalogs are past <see cref="FreezeMaxAge" /> - held back by a session anywhere on the server.</summary>
    public int SystemPastForcedXid { get; init; }

    /// <summary>How many system catalogs are past <see cref="MultixactFreezeMaxAge" />.</summary>
    public int SystemPastForcedMultixact { get; init; }
}

/// <summary>One of the store's tables as <see cref="HealthHorizonQueries.Wraparound" /> reads it: the oldest ages over it, its TOAST table and its partitions.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="Table">The table.</param>
/// <param name="XidAge">The oldest <c>age(relfrozenxid)</c>, or <see langword="null" /> for a partitioned table with no partition.</param>
/// <param name="MultixactAge">The oldest <c>mxid_age(relminmxid)</c>, likewise.</param>
internal sealed record HealthWraparoundTable(string Schema, string Table, long? XidAge, long? MultixactAge)
{
    /// <summary>The key the findings name it by.</summary>
    public string Key => Schema + "." + Table;
}

/// <summary>What kind of thing holds the vacuum horizon back.</summary>
internal enum HealthHorizonHolderKind
{
    /// <summary>A session of this database, with a transaction id or a snapshot.</summary>
    Session,

    /// <summary>A session of another database on the server: it holds back the shared catalogs, and so every database's freeze.</summary>
    OtherDatabaseSession,

    /// <summary>A prepared transaction of this database.</summary>
    Prepared,

    /// <summary>A prepared transaction of another database.</summary>
    OtherDatabasePrepared,

    /// <summary>A standby's <c>hot_standby_feedback</c>, through its WAL sender's <c>backend_xmin</c>.</summary>
    Standby,

    /// <summary>A replication slot's <c>xmin</c>: it holds back vacuum of every table in every database.</summary>
    SlotXmin,

    /// <summary>A replication slot's <c>catalog_xmin</c>: the system catalogs only, and so the freeze of every database.</summary>
    SlotCatalogXmin,
}

/// <summary>One holder of the horizon as <see cref="HealthHorizonQueries.Holders" /> reads it - a session, a prepared transaction or a standby.</summary>
/// <remarks>Init properties, so a unit test states only the facts its case is about.</remarks>
internal sealed record HealthHorizonHolderRow
{
    /// <summary>What it is.</summary>
    public required HealthHorizonHolderKind Kind { get; init; }

    /// <summary>How far back it holds the horizon, in transactions: <c>age()</c> of its oldest transaction id or snapshot.</summary>
    public required long XidAge { get; init; }

    /// <summary>
    /// The age of the id it <em>owns</em> - a session's own <c>backend_xid</c>, a prepared transaction's - or
    /// <see langword="null" /> for a snapshot alone or a standby's feedback, which carry the oldest id running elsewhere.
    /// </summary>
    public long? OwnXidAge { get; init; }

    /// <summary>
    /// The least it can have held it, as Postgres' own timestamps bound it: a running statement's age (its snapshot is at
    /// least that old), the time a session has sat idle in its transaction (its snapshot or its transaction id is older),
    /// a prepared transaction's age - or <see langword="null" /> when this role may not read it (another role's session)
    /// or Postgres does not record it.
    /// </summary>
    /// <remarks>
    /// Never the transaction's age alone: a session in a read-committed transaction takes a fresh snapshot with every
    /// statement, so a transaction open for a day may hold the horizon back by a minute - Marten's own HotCold leader at
    /// 9.39 does exactly that, measured live: its <c>pg_sleep(60)</c> lock monitor runs in a transaction it never ends, and
    /// its <c>backend_xmin</c> is never more than a minute old. That age is <see cref="TransactionOpen" />, the most it can be.
    /// </remarks>
    public TimeSpan? AtLeast { get; init; }

    /// <summary>A session's <c>now() - xact_start</c>: the most it can have held the horizon. <see langword="null" /> otherwise.</summary>
    public TimeSpan? TransactionOpen { get; init; }

    /// <summary>
    /// Which timestamp <see cref="AtLeast" /> came from: <c>statement</c>, <c>idle</c>, <c>transaction</c> or
    /// <c>prepared</c> - never the session's own state text.
    /// </summary>
    public string? Bound { get; init; }

    /// <summary>Its backend's process id. A detail.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Its role, or a prepared transaction's owner. A detail.</summary>
    public string? User { get; init; }

    /// <summary>Its <c>application_name</c>. A detail.</summary>
    public string? Application { get; init; }

    /// <summary>Its client's address. A detail.</summary>
    public string? Client { get; init; }

    /// <summary>When its backend started. A detail.</summary>
    public DateTimeOffset? BackendStart { get; init; }

    /// <summary>Its state - a session's, or a WAL sender's.</summary>
    public string? State { get; init; }

    /// <summary>A prepared transaction's global id. A detail.</summary>
    public string? Gid { get; init; }

    /// <summary>Whether a session has written - holds a transaction id - rather than only a snapshot.</summary>
    public bool HasTransactionId { get; init; }

    /// <summary>Whether this role may not read into it: another role's session, whose state and timings Postgres withholds.</summary>
    public bool Hidden { get; init; }

    /// <summary>Whether the session holds one of the store's own daemon locks - Marten's HotCold leader.</summary>
    public bool HoldsDaemonLock { get; init; }

    /// <summary>
    /// How many holders of this kind there were, of which this read returned the oldest - for this database's kinds only;
    /// another database's holders are an age and nothing else, not even a count (1).
    /// </summary>
    public long Total { get; init; } = 1;
}

/// <summary>
/// The statements behind wraparound and the vacuum horizon (T12's database half, T13 and T14): <c>pg_database</c>,
/// <c>pg_class</c>'s freeze ages, the counters, and the four kinds of horizon holder.
/// </summary>
/// <remarks>
/// <para>
/// <b>A list locks nothing it lists</b> (AGENTS.md D27). The wraparound read is a scan of <c>pg_class</c> - the
/// catalog, an <c>AccessShareLock</c> on it and nothing else - and calls <c>age()</c> and <c>mxid_age()</c>, which
/// read a transaction id and shared memory. Partitions are walked through <c>pg_inherits</c>, never
/// <c>pg_partition_tree()</c>. The counters are <c>pg_snapshot_xmax(pg_current_snapshot())</c> (PostgreSQL 13+,
/// which assigns no transaction id) and <c>mxid_age(datminmxid)</c>; a read-only transaction assigns none either.
/// </para>
/// <para>
/// <b>Names are values, and the store's tables are the only ones named.</b> The store's tables travel as two
/// <c>text[]</c> parameters, as <see cref="HealthTableQueries" /> passes them, and the statement returns names only
/// for those; every other table in the database - a legacy schema, a Quartz job store, a temporary table, a hidden
/// document type's - is counted and never selected by name (D27).
/// </para>
/// <para>
/// <b>Never on a poll, and fenced.</b> <see cref="Holders" /> reads <c>pg_stat_activity</c>, <c>pg_locks</c> and
/// <c>pg_prepared_xacts</c>, which only <see cref="HealthConnectionQueries" /> and this builder may (the convention
/// test <c>ActivityViewsOffThePollPathTests</c> names both), and it is called only by T14, in the regular sweep at
/// <c>HealthInterval</c>. Its own read was needed rather than H3's because the horizon is a transaction id: H3's reads
/// say whether a session has one, and this one how old it is. The session predicate is H3's own
/// (<see cref="HealthConnectionQueries.ClientSession" />): a row whose <c>backend_type</c> is withheld from this role is
/// still a session, and it still holds the horizon back - Postgres shows every role every session's
/// <c>backend_xid</c> and <c>backend_xmin</c>, and withholds only its state, timings, client and statement (measured
/// on PostgreSQL 17). Other databases' sessions and prepared transactions are read as their oldest age and nothing
/// else: they hold back the shared catalogs, and so every database's freeze, and nothing about them is this
/// database's to say.
/// </para>
/// <para>
/// <b>Version-proof without a second round trip.</b> A column a supported server may not have is read through
/// <c>to_jsonb(row) -&gt;&gt; 'name'</c>, which is null where it does not exist (<c>inactive_since</c> and
/// <c>invalidation_reason</c> from 17, <c>conflicting</c> from 16), and a setting through
/// <c>current_setting(..., true)</c> (the failsafe ages, from 14). Marten 8 and later need PostgreSQL 13, which has
/// everything else here.
/// </para>
/// </remarks>
internal static class HealthHorizonQueries
{
    /// <summary>The most holders of one kind a read returns, oldest first; the count says how many there were.</summary>
    public const int MaxHolders = 20;

    /// <summary>
    /// The database-wide wraparound facts, and one row for each of the store's tables that exists: its oldest
    /// transaction-id and multixact ages over itself, its TOAST table and its partitions. One row with null table
    /// columns when none exists, so the database's facts are read either way.
    /// </summary>
    internal const string WraparoundSql =
        """
        with recursive settings as (
            select pg_catalog.current_setting('autovacuum_freeze_max_age')::bigint as freeze_max_age,
                   pg_catalog.current_setting('autovacuum_multixact_freeze_max_age')::bigint as multixact_freeze_max_age,
                   nullif(pg_catalog.current_setting('vacuum_failsafe_age', true), '')::bigint as failsafe_age,
                   nullif(pg_catalog.current_setting('vacuum_multixact_failsafe_age', true), '')::bigint as multixact_failsafe_age,
                   pg_catalog.current_setting('server_version_num')::int as server_version
        ),
        here as (
            select pg_catalog.age(d.datfrozenxid)::bigint as xid_age,
                   pg_catalog.mxid_age(d.datminmxid)::bigint as mxid_age,
                   (d.datminmxid::text::bigint + pg_catalog.mxid_age(d.datminmxid)::bigint) % 4294967296 as multixact_counter
            from pg_catalog.pg_database d
            where d.datname = pg_catalog.current_database()
        ),
        server as (
            select pg_catalog.max(pg_catalog.age(d.datfrozenxid))::bigint as xid_age,
                   pg_catalog.max(pg_catalog.mxid_age(d.datminmxid))::bigint as mxid_age
            from pg_catalog.pg_database d
            where d.datconnlimit <> -2
        ),
        listed as (
            select c.oid, n.nspname::text as schema_name, c.relname::text as table_name
            from rows from (pg_catalog.unnest(@schemas), pg_catalog.unnest(@tables)) as m(schema_name, table_name)
            join pg_catalog.pg_namespace n on n.nspname = m.schema_name
            join pg_catalog.pg_class c on c.relnamespace = n.oid and c.relname = m.table_name
            where c.relkind in ('r', 'p')
        ),
        tree (root, relid) as (
            select l.oid, l.oid from listed l
            union all
            select t.root, i.inhrelid
            from tree t
            join pg_catalog.pg_inherits i on i.inhparent = t.relid
            join pg_catalog.pg_class ch on ch.oid = i.inhrelid
            where ch.relispartition
        ),
        members (root, relid) as (
            select t.root, t.relid from tree t
            union all
            select t.root, c.reltoastrelid
            from tree t
            join pg_catalog.pg_class c on c.oid = t.relid
            where c.reltoastrelid <> 0
        ),
        aged as (
            select c.oid,
                   c.relkind,
                   c.relpersistence = 't' as temporary,
                   n.nspname in ('pg_catalog', 'information_schema') as system,
                   pg_catalog.age(c.relfrozenxid)::bigint as xid_age,
                   pg_catalog.mxid_age(c.relminmxid)::bigint as mxid_age,
                   greatest(pg_catalog.age(c.relfrozenxid), pg_catalog.age(toast.relfrozenxid))::bigint as tree_xid_age,
                   greatest(pg_catalog.mxid_age(c.relminmxid), pg_catalog.mxid_age(toast.relminmxid))::bigint as tree_mxid_age
            from pg_catalog.pg_class c
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            left join pg_catalog.pg_class toast on toast.oid = c.reltoastrelid
            where c.relkind in ('r', 't', 'm')
        ),
        stored as (
            select m.root, pg_catalog.max(a.xid_age) as xid_age, pg_catalog.max(a.mxid_age) as mxid_age
            from members m
            join aged a on a.oid = m.relid
            group by m.root
        ),
        others as (
            select a.*
            from aged a
            where a.relkind in ('r', 'm')
              and not exists (select 1 from members m where m.relid = a.oid)
        ),
        counted as (
            select pg_catalog.max(o.tree_xid_age) filter (where not o.system) as oldest_xid,
                   pg_catalog.max(o.tree_mxid_age) filter (where not o.system) as oldest_mxid,
                   pg_catalog.count(*) filter (where not o.system and o.tree_xid_age > s.freeze_max_age)::int as past_xid,
                   pg_catalog.count(*) filter (where not o.system and o.temporary and o.tree_xid_age > s.freeze_max_age)::int as temporary_past_xid,
                   pg_catalog.count(*) filter (where not o.system and o.tree_mxid_age > s.multixact_freeze_max_age)::int as past_mxid,
                   pg_catalog.count(*) filter (where not o.system and o.temporary and o.tree_mxid_age > s.multixact_freeze_max_age)::int as temporary_past_mxid,
                   pg_catalog.count(*) filter (where o.system and o.tree_xid_age > s.freeze_max_age)::int as system_past_xid,
                   pg_catalog.count(*) filter (where o.system and o.tree_mxid_age > s.multixact_freeze_max_age)::int as system_past_mxid,
                   coalesce((select o2.temporary from others o2 where not o2.system order by o2.tree_xid_age desc limit 1), false) as oldest_temporary
            from others o
            cross join settings s
        )
        select s.freeze_max_age,
               s.multixact_freeze_max_age,
               s.failsafe_age,
               s.multixact_failsafe_age,
               s.server_version,
               h.xid_age,
               h.mxid_age,
               v.xid_age,
               v.mxid_age,
               pg_catalog.pg_snapshot_xmax(pg_catalog.pg_current_snapshot())::text::bigint,
               h.multixact_counter,
               o.oldest_xid,
               o.oldest_temporary,
               o.oldest_mxid,
               o.past_xid,
               o.temporary_past_xid,
               o.past_mxid,
               o.temporary_past_mxid,
               o.system_past_xid,
               o.system_past_mxid,
               t.schema_name,
               t.table_name,
               t.xid_age,
               t.mxid_age
        from settings s
        cross join here h
        cross join server v
        cross join counted o
        left join (
            select l.schema_name, l.table_name, st.xid_age, st.mxid_age
            from listed l
            left join stored st on st.root = l.oid
        ) t on true
        order by t.schema_name, t.table_name
        """;

    /// <summary>A granted advisory lock's 64-bit key, as <c>pg_try_advisory_lock(bigint)</c> spells it - H3's spelling.</summary>
    private const string AdvisoryKey =
        "case when l.locktype = 'advisory' and l.objsubid = 1 then ((l.classid::bigint << 32) | l.objid::bigint) end";

    /// <summary>
    /// A row of <c>pg_stat_activity a</c> that is a session holding the horizon: H3's client session
    /// (<see cref="HealthConnectionQueries.ClientSession" />), with a transaction id or a snapshot, and not running a lazy
    /// <c>VACUUM</c>.
    /// </summary>
    /// <remarks>
    /// A lazy <c>VACUUM</c> - autovacuum's, or a DBA's <c>VACUUM (FREEZE)</c>, the very cure T12 recommends - shows a
    /// <c>backend_xmin</c>, but Postgres marks it <c>PROC_IN_VACUUM</c> and <c>ComputeXidHorizons</c> skips it: it holds
    /// nothing back, and naming it would go Critical exactly when the DBA does the right thing (H9 review, B1). It is told
    /// apart by its row in <c>pg_stat_progress_vacuum</c>, whose process id every role may read. <c>VACUUM FULL</c> is not
    /// in that view (it reports in <c>pg_stat_progress_cluster</c>), is not <c>PROC_IN_VACUUM</c>, and does hold the
    /// horizon, so it stays a holder; so does <c>ANALYZE</c>.
    /// <para>
    /// <b>Its parallel workers too</b> (H9 re-review, F1). A manual <c>VACUUM</c> of a table with two or more large indexes
    /// starts parallel workers (autovacuum never does), which are <c>PROC_IN_VACUUM</c> as well but have no row in
    /// <c>pg_stat_progress_vacuum</c>. A role that may read the row sees <c>backend_type = 'parallel worker'</c>, which H3's
    /// predicate already leaves out. A role that may not sees <c>backend_type</c> null - and <c>leader_pid</c> null, because
    /// Postgres fills both only inside <c>HAS_PGSTAT_PERMISSIONS</c>, while the process id, database, role, application name,
    /// <c>backend_xid</c> and <c>backend_xmin</c> are every role's (<c>pg_stat_get_activity</c>, <c>pgstatfuncs.c</c> 663-674 and
    /// 744-746 on 13, 351-362, 432-434 and 633 on 17); <c>pg_stat_progress_vacuum</c>'s process id and database are every role's
    /// too (276-277 on 17). So a withheld row with no transaction id whose <c>backend_xmin</c>, role and database are a running
    /// <c>VACUUM</c>'s is left out: a parallel worker restores its leader's snapshot and runs as its leader's role. Only a
    /// withheld session of that same role, in that database, holding nothing but a snapshot at exactly that <c>VACUUM</c>'s
    /// xmin, could be left out with it - a session whose row this role may read never is, because its <c>backend_type</c> says
    /// what it is. Not measured: Postgres' parallel vacuum was not run against the live suite.
    /// </para>
    /// </remarks>
    private const string HoldingSession =
        HealthConnectionQueries.ClientSession + "\n       and (a.backend_xid is not null or a.backend_xmin is not null)"
        + "\n       and not exists (select 1 from pg_catalog.pg_stat_progress_vacuum v where v.pid = a.pid)"
        + "\n       and not (a.backend_type is null and a.backend_xid is null and exists ("
        + "\n           select 1 from pg_catalog.pg_stat_activity w"
        + "\n           join pg_catalog.pg_stat_progress_vacuum v on v.pid = w.pid"
        + "\n           where w.backend_xmin = a.backend_xmin and w.usesysid = a.usesysid and w.datid = a.datid))";

    /// <summary>
    /// A session's age in transactions of the one id it <em>owns</em>: its own transaction id, or null. Unlike its snapshot's
    /// <c>backend_xmin</c> - the oldest transaction running anywhere when the snapshot was taken - this id was handed out to the
    /// session itself, so the time the counter passed it dates the session's own hold (H9 re-review, blocking 1).
    /// </summary>
    private const string SessionOwnXidAge = "pg_catalog.age(a.backend_xid)::bigint";

    /// <summary>
    /// How the session branches pick the oldest under their <c>limit</c>: furthest back first and, among sessions equally far
    /// back, the one that owns an id - ordinal 17 is <c>own_xid_age</c> - before the snapshots pinned at it (H9 re-review 2,
    /// follow-up 3). A writer whose own id is the point its waiters' snapshots are pinned at ties them on <c>xid_age</c>, and
    /// cut by the limit it would leave them matched to nothing, so the leader's snapshot would be named after all.
    /// </summary>
    private const string OwnerFirst = "order by 2 desc nulls last, 17 desc nulls last";

    /// <summary>A holder's age in transactions: the older of its transaction id and its snapshot.</summary>
    private const string SessionXidAge =
        "greatest(pg_catalog.age(a.backend_xid), pg_catalog.age(a.backend_xmin))::bigint";

    /// <summary>
    /// The least time a session can have held the horizon, as Postgres' own timestamps bound it: a running statement's age
    /// (its snapshot was taken no later than it began), or the time it has sat idle in its transaction (its snapshot or its
    /// transaction id existed before its last statement ended) - and never more than its transaction has been open, because
    /// a <c>CALL</c> or a <c>DO</c> that commits in a loop, or a <c>CREATE INDEX CONCURRENTLY</c>, runs one statement across
    /// many short transactions (H9 review, B2). Null for anything else: a hidden session's state is null, and the
    /// transaction's age alone is never the bound.
    /// </summary>
    private const string SessionAtLeast =
        "extract(epoch from (pg_catalog.now() - case\n" +
        "             when a.state = 'active' then greatest(a.query_start, a.xact_start)\n" +
        "             when a.state like 'idle in transaction%' then greatest(a.state_change, a.xact_start) end))::float8";

    /// <summary>
    /// Which of Postgres' timestamps <see cref="SessionAtLeast" /> came from - <c>statement</c>, <c>idle</c> or
    /// <c>transaction</c> (a statement older than its transaction) - so the finding can say why, without the session's
    /// state text, which another database's session does not carry.
    /// </summary>
    private const string SessionBound =
        "case when a.state = 'active' then case when a.query_start >= a.xact_start or a.xact_start is null then 'statement' else 'transaction' end\n" +
        "             when a.state like 'idle in transaction%' then case when a.state_change >= a.xact_start or a.xact_start is null then 'idle' else 'transaction' end end";

    /// <summary>The most time a session can have held it: how long its transaction has been open.</summary>
    private const string SessionTransactionOpen =
        "extract(epoch from (pg_catalog.now() - a.xact_start))::float8";

    /// <summary>
    /// What holds the vacuum horizon back, besides the replication slots: this database's sessions and prepared
    /// transactions, oldest first, with who they are; the oldest session and the oldest prepared transaction of any
    /// other database, as an age and nothing else; and every standby whose feedback holds it - with, in front, how
    /// much of <c>pg_stat_activity</c> this role can see. One row with nulls when nothing holds it.
    /// </summary>
    internal const string HoldersSql =
        "with visibility as (\n" +
        "    select pg_catalog.pg_has_role(current_user, 'pg_read_all_stats', 'USAGE') as can_read_all_stats,\n" +
        "           pg_catalog.count(*) filter (where a.backend_type is null) as hidden_sessions\n" +
        "    from pg_catalog.pg_stat_activity a\n" +
        "    where a.datname = pg_catalog.current_database()\n" +
        "      and " + HealthConnectionQueries.ClientSession + "\n" +
        "      and a.pid <> pg_catalog.pg_backend_pid()\n" +
        "),\n" +
        "holders as (\n" +
        "    (select 0 as kind, " + SessionXidAge + " as xid_age,\n" +
        "            " + SessionAtLeast + " as at_least,\n" +
        "            " + SessionTransactionOpen + " as transaction_open,\n" +
        "            " + SessionBound + " as bound,\n" +
        "            a.pid, a.usename::text as usename, a.application_name::text as application_name,\n" +
        "            pg_catalog.host(a.client_addr) as client_addr, a.backend_start, a.state::text as state, null::text as gid,\n" +
        "            a.backend_xid is not null as has_xid, a.backend_type is null as hidden,\n" +
        "            exists (select 1 from pg_catalog.pg_locks l where l.pid = a.pid and l.granted and l.locktype = 'advisory'\n" +
        "                       and " + AdvisoryKey + " = any(@daemon_lock_ids)) as holds_daemon_lock,\n" +
        "            pg_catalog.count(*) over () as total,\n" +
        "            " + SessionOwnXidAge + " as own_xid_age\n" +
        "     from pg_catalog.pg_stat_activity a\n" +
        "     where a.datname = pg_catalog.current_database()\n" +
        "       and " + HoldingSession + "\n" +
        "       and a.pid <> pg_catalog.pg_backend_pid()\n" +
        "     " + OwnerFirst + "\n" +
        "     limit @limit)\n" +
        "    union all\n" +
        "    (select 1, " + SessionXidAge + ",\n" +
        "            " + SessionAtLeast + ",\n" +
        "            " + SessionTransactionOpen + ",\n" +
        "            " + SessionBound + ",\n" +
        "            null::int, null::text, null::text, null::text, null::timestamptz, null::text, null::text,\n" +
        "            a.backend_xid is not null, a.backend_type is null, false,\n" +
        "            1::bigint,\n" +
        "            " + SessionOwnXidAge + "\n" +
        "     from pg_catalog.pg_stat_activity a\n" +
        "     where a.datname is distinct from pg_catalog.current_database()\n" +
        "       and " + HoldingSession + "\n" +
        "     " + OwnerFirst + "\n" +
        "     limit 1)\n" +
        "    union all\n" +
        "    (select 2, pg_catalog.age(p.transaction)::bigint,\n" +
        "            extract(epoch from (pg_catalog.now() - p.prepared))::float8,\n" +
        "            null::float8,\n" +
        "            'prepared',\n" +
        "            null::int, p.owner::text, null::text, null::text, null::timestamptz, null::text, p.gid::text,\n" +
        "            true, false, false,\n" +
        "            pg_catalog.count(*) over (),\n" +
        "            pg_catalog.age(p.transaction)::bigint\n" +
        "     from pg_catalog.pg_prepared_xacts p\n" +
        "     where p.database = pg_catalog.current_database()\n" +
        "     order by 2 desc nulls last\n" +
        "     limit @limit)\n" +
        "    union all\n" +
        "    (select 3, pg_catalog.age(p.transaction)::bigint,\n" +
        "            extract(epoch from (pg_catalog.now() - p.prepared))::float8,\n" +
        "            null::float8,\n" +
        "            'prepared',\n" +
        "            null::int, null::text, null::text, null::text, null::timestamptz, null::text, null::text,\n" +
        "            true, false, false,\n" +
        "            1::bigint,\n" +
        "            pg_catalog.age(p.transaction)::bigint\n" +
        "     from pg_catalog.pg_prepared_xacts p\n" +
        "     where p.database is distinct from pg_catalog.current_database()\n" +
        "     order by 2 desc nulls last\n" +
        "     limit 1)\n" +
        "    union all\n" +
        "    (select 4, pg_catalog.age(r.backend_xmin)::bigint,\n" +
        "            null::float8,\n" +
        "            null::float8,\n" +
        "            null::text,\n" +
        "            r.pid, r.usename::text, r.application_name::text, pg_catalog.host(r.client_addr), r.backend_start, r.state::text,\n" +
        "            null::text, false, r.state is null, false,\n" +
        "            pg_catalog.count(*) over (),\n" +
        "            null::bigint\n" +
        "     from pg_catalog.pg_stat_replication r\n" +
        "     where r.backend_xmin is not null\n" +
        "     order by 2 desc nulls last\n" +
        "     limit @limit)\n" +
        ")\n" +
        "select v.can_read_all_stats, v.hidden_sessions, h.kind, h.xid_age, h.at_least, h.transaction_open, h.bound, h.pid, h.usename,\n" +
        "       h.application_name, h.client_addr, h.backend_start, h.state, h.gid, h.has_xid, h.hidden, h.holds_daemon_lock, h.total,\n" +
        "       h.own_xid_age\n" +
        "from visibility v\n" +
        "left join holders h on true\n" +
        "order by h.kind, h.xid_age desc";

    /// <summary>Every statement here, for the tests that hold them to a catalog read.</summary>
    internal static IReadOnlyList<string> Statements { get; } = [WraparoundSql, HoldersSql];

    /// <summary>The database-wide wraparound facts and the store's tables' oldest ages (<see cref="WraparoundSql" />).</summary>
    /// <param name="tables">The store's tables, a hidden document type's left out.</param>
    public static HealthQuery Wraparound(IReadOnlyList<HealthTableTarget> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        string[] schemas = new string[tables.Count];
        string[] names = new string[tables.Count];

        for (int i = 0; i < tables.Count; i++)
        {
            schemas[i] = tables[i].Schema;
            names[i] = tables[i].Table;
        }

        return new HealthQuery(
            WraparoundSql,
            [
                new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas },
                new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names },
            ]);
    }

    /// <summary>The sessions, prepared transactions and standbys holding the horizon back (<see cref="HoldersSql" />).</summary>
    /// <param name="daemonLockIds">The advisory keys the store's daemon negotiates leadership with.</param>
    public static HealthQuery Holders(IReadOnlyCollection<long> daemonLockIds)
    {
        ArgumentNullException.ThrowIfNull(daemonLockIds);

        return new HealthQuery(
            HoldersSql,
            [
                new NpgsqlParameter("daemon_lock_ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = daemonLockIds.ToArray() },
                new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = MaxHolders },
            ]);
    }

    /// <summary>One row of <see cref="WraparoundSql" />: the database's facts, and the store table it carries, if any.</summary>
    public static (HealthWraparoundDatabase Database, HealthWraparoundTable? Table) ReadWraparound(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        HealthWraparoundDatabase database = new()
        {
            FreezeMaxAge = reader.GetInt64(0),
            MultixactFreezeMaxAge = reader.GetInt64(1),
            FailsafeAge = NullableInt64(reader, 2),
            MultixactFailsafeAge = NullableInt64(reader, 3),
            ServerVersion = reader.GetInt32(4),
            DatabaseXidAge = reader.GetInt64(5),
            DatabaseMultixactAge = reader.GetInt64(6),
            ServerXidAge = reader.GetInt64(7),
            ServerMultixactAge = reader.GetInt64(8),
            XidCounter = reader.GetInt64(9),
            MultixactCounter = reader.GetInt64(10),
            OtherOldestXidAge = NullableInt64(reader, 11),
            OtherOldestIsTemporary = reader.GetBoolean(12),
            OtherOldestMultixactAge = NullableInt64(reader, 13),
            OtherPastForcedXid = reader.GetInt32(14),
            OtherTemporaryPastForcedXid = reader.GetInt32(15),
            OtherPastForcedMultixact = reader.GetInt32(16),
            OtherTemporaryPastForcedMultixact = reader.GetInt32(17),
            SystemPastForcedXid = reader.GetInt32(18),
            SystemPastForcedMultixact = reader.GetInt32(19),
        };

        HealthWraparoundTable? table = reader.IsDBNull(20)
            ? null
            : new HealthWraparoundTable(reader.GetString(20), reader.GetString(21), NullableInt64(reader, 22), NullableInt64(reader, 23));

        return (database, table);
    }

    /// <summary>One row of <see cref="HoldersSql" />: the visibility columns, and the holder it carries, if any.</summary>
    public static (bool CanReadAllStats, long HiddenSessions, HealthHorizonHolderRow? Holder) ReadHolder(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        bool canReadAllStats = reader.GetBoolean(0);
        long hidden = reader.GetInt64(1);

        if (reader.IsDBNull(2))
        {
            return (canReadAllStats, hidden, null);
        }

        HealthHorizonHolderKind kind = reader.GetInt32(2) switch
        {
            0 => HealthHorizonHolderKind.Session,
            1 => HealthHorizonHolderKind.OtherDatabaseSession,
            2 => HealthHorizonHolderKind.Prepared,
            3 => HealthHorizonHolderKind.OtherDatabasePrepared,
            _ => HealthHorizonHolderKind.Standby,
        };

        HealthHorizonHolderRow holder = new()
        {
            Kind = kind,
            XidAge = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
            AtLeast = NullableSeconds(reader, 4),
            TransactionOpen = NullableSeconds(reader, 5),
            Bound = NullableText(reader, 6),
            ProcessId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            User = NullableText(reader, 8),
            Application = NullableText(reader, 9),
            Client = NullableText(reader, 10),
            BackendStart = reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            State = NullableText(reader, 12),
            Gid = NullableText(reader, 13),
            HasTransactionId = reader.GetBoolean(14),
            Hidden = reader.GetBoolean(15),
            HoldsDaemonLock = reader.GetBoolean(16),
            Total = reader.GetInt64(17),
            OwnXidAge = NullableInt64(reader, 18),
        };

        return (canReadAllStats, hidden, holder);
    }

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullableText(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static TimeSpan? NullableSeconds(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : TimeSpan.FromSeconds(Math.Max(0d, reader.GetDouble(ordinal)));
}
