using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The statements of the connection and session checks (C02-C08, C12) and of Database › Sessions: reads of
/// <c>pg_stat_activity</c>, <c>pg_locks</c>, <c>pg_prepared_xacts</c>, <c>pg_stat_database</c>, PostgreSQL 19's
/// <c>pg_stat_lock</c> and the server's connection settings, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never on a poll.</b> These read the server's process array and lock table, which is monitoring-grade
/// cost - proc-array and lock-table scans - and is the reason they are run only by a health sweep (once per
/// <c>HealthInterval</c> for everybody watching) and by the Sessions page (at the same interval, or on
/// demand, through a single-flight snapshot with a floor of its own). Nothing on the 5-second
/// <c>RefreshInterval</c> path calls a builder here; <c>ActivityViewsOffThePollPathTests</c> holds the code to
/// that.
/// </para>
/// <para>
/// <b>This database, and never another's session.</b> Every session read is bounded to
/// <c>datname = current_database()</c>, to client backends, and away from the studio's own backend
/// (<c>pg_backend_pid()</c>). The one exception is <see cref="ServerConnections" />, which counts the client
/// backends of the whole server - only a count, and the only number <c>max_connections</c> can be compared
/// with, because every database on the server spends the same budget. <see cref="LockTable" /> reads a blocker
/// in another database by process id, and names nothing about it.
/// </para>
/// <para>
/// <b>The lock table once per sweep</b> (X3, H3's half). A regular sweep reads <c>pg_locks</c> in one statement,
/// <see cref="LockTable" />, and every check that needs it takes its answer from the sweep's shared read - C04's relation
/// locks, C04's and T14's daemon-lock holders, C06's and C12's waiters and blockers, and the table checks' strong and
/// <c>ACCESS EXCLUSIVE</c> locks, which travel into their statements as parameters. Before 0.6 five statements of a
/// sweep read it seven times.
/// </para>
/// <para>
/// <b>A session this role cannot see into is still a session.</b> A role that is neither a member of
/// <c>pg_read_all_stats</c> nor of a session's own role is shown that session's row with its database, process
/// id, role id and transaction ids - and with <c>backend_type</c>, state, timings, client and statement null. A
/// filter on <c>backend_type = 'client backend'</c> alone therefore drops every such row, and turns "cannot
/// see" into "nobody there". So a client session is <see cref="ClientSession" />: a client backend, or a row
/// whose <c>backend_type</c> is withheld from this role and that has a role behind it and a database - which every
/// client session has, and Postgres' auxiliary processes, its autovacuum workers and the superuser's logical
/// replication launcher (a role and no database) do not. A hidden background worker started for a
/// role in a database (a parallel worker, a logical replication apply worker) is counted with them; there is no
/// telling it apart.
/// </para>
/// <para>
/// <b>A lock names its owner, never itself.</b> <c>pg_locks</c> holds a lock on an index, a partition, a TOAST
/// table or a sequence by that relation's own oid - and their names are the problem: a tenant partition's name
/// carries the tenant id (<c>mt_doc_order_acme</c>), a hidden document type's index carries its alias and an
/// indexed member (<c>mt_doc_payroll_idx_salary</c>), a TOAST table's carries its owner's oid. So the lock reads
/// resolve every locked relation to the table it belongs to (<see cref="OwnersCte" />) - an index to the table it
/// indexes (<c>pg_index.indrelid</c>), a TOAST table to the table whose <c>reltoastrelid</c> it is, a sequence a
/// column owns to that table (<c>pg_depend</c>), and a partition to its root through <c>pg_inherits</c> (never a
/// <c>pg_partition_*</c> function: a list locks nothing it lists, D27) - and return the owner's schema, name and
/// kind with the part of it that is locked. The locked relation's own name is never selected.
/// </para>
/// <para>
/// <b>Version-proof without a second round trip.</b> <c>pg_locks.waitstart</c> exists from PostgreSQL 14 and
/// <c>reserved_connections</c> from 16. Naming a missing column is <c>42703</c>, which the sweep reports as a
/// failure; so <c>waitstart</c> is read through <c>to_jsonb(l) -&gt;&gt; 'waitstart'</c>, which is simply null
/// on an older server, and the setting through <c>current_setting(…, true)</c>, which is null when it does not
/// exist.
/// </para>
/// <para>
/// <b>No table name is ever written into these statements.</b> A health statement is shown with every finding
/// of its check (Show SQL), and a hidden document type's table must not appear there; nothing here names a
/// relation at all. Every value is a parameter, every limit included.
/// </para>
/// <para>
/// <b><c>pg_blocking_pids</c> only for a session that waits.</b> Postgres' own documentation warns that it
/// takes the lock manager's shared state exclusively for a moment, so it is called for a backend whose
/// <c>wait_event_type</c> is <c>Lock</c> and for nobody else.
/// </para>
/// </remarks>
internal static class HealthConnectionQueries
{
    /// <summary>The predefined role whose members may read every session's state, timings and statement.</summary>
    public const string ReadAllStatsRole = "pg_read_all_stats";

    /// <summary>The most characters of one statement's text read from <c>pg_stat_activity</c>.</summary>
    public const int MaxQueryCharacters = 4096;

    /// <summary>The most sessions one read lists.</summary>
    public const int MaxSessions = 500;

    /// <summary>The most locks one read lists.</summary>
    public const int MaxLocks = 1000;

    /// <summary>The most offenders a check reads, oldest first.</summary>
    public const int MaxOffenders = 50;

    /// <summary>The most prepared transactions one read lists.</summary>
    public const int MaxPrepared = 100;

    /// <summary>
    /// A row of <c>pg_stat_activity a</c> that is a client session: a client backend, or a row whose
    /// <c>backend_type</c> this role may not read and that has a role behind it and is connected to a database - as a
    /// client session always is, and the superuser's logical replication launcher (a role, no database) is not.
    /// </summary>
    internal const string ClientSession =
        "(a.backend_type = 'client backend' or (a.backend_type is null and a.usesysid is not null and a.datid is not null))";

    /// <summary>
    /// What every session read begins with: a one-row answer to "how much of <c>pg_stat_activity</c> can this
    /// role see?". A role that is neither a member of <c>pg_read_all_stats</c> nor of the session's own role sees
    /// the row with its backend type, state, timings, client and statement nulled - so a client session whose
    /// <c>backend_type</c> is null is one that is counted and cannot be judged.
    /// </summary>
    private const string VisibilityCte =
        "visibility as (\n" +
        "  select pg_has_role(current_user, 'pg_read_all_stats', 'USAGE') as can_read_all_stats,\n" +
        "         count(*) filter (where a.backend_type is null) as hidden_sessions\n" +
        "    from pg_stat_activity a\n" +
        "   where a.datname = current_database()\n" +
        "     and " + ClientSession + "\n" +
        "     and a.pid <> pg_backend_pid()\n" +
        ")";

    /// <summary>
    /// The owner of every relation in a <c>locked (relation)</c> CTE the statement defines before it: one row per
    /// locked oid, with the owning table's schema, name and <c>relkind</c>, and which part of it the lock is on
    /// (<c>self</c>, <c>index</c>, <c>toast</c>, <c>partition</c> or <c>sequence</c>). Needs <c>with recursive</c>.
    /// </summary>
    /// <remarks>
    /// The order is the design: an index first, because a TOAST table's index resolves to the TOAST table and a
    /// partition's index to the partition; then a TOAST table or a column-owned sequence to its table; then a
    /// partition, level by level, to its root. Only a relation Postgres marks <c>relispartition</c> is rolled up -
    /// a table that merely inherits from another is its own. Every step is a catalog read by oid.
    /// </remarks>
    private const string OwnersCte =
        "attached as (\n" +
        "  select k.relation as locked, coalesce(ix.indrelid, k.relation) as rel, ix.indexrelid is not null as via_index\n" +
        "    from locked k\n" +
        "    left join pg_catalog.pg_index ix on ix.indexrelid = k.relation\n" +
        "),\n" +
        "unwrapped as (\n" +
        "  select t.locked, coalesce(toast.oid, seq.owner, t.rel) as rel, t.via_index,\n" +
        "         toast.oid is not null as via_toast, seq.owner is not null as via_sequence\n" +
        "    from attached t\n" +
        "    join pg_catalog.pg_class self on self.oid = t.rel\n" +
        "    left join pg_catalog.pg_class toast on self.relkind = 't' and toast.reltoastrelid = t.rel\n" +
        "    left join lateral (\n" +
        "      select d.refobjid as owner from pg_catalog.pg_depend d\n" +
        "       where self.relkind = 'S' and d.classid = 'pg_catalog.pg_class'::regclass and d.objid = t.rel\n" +
        "         and d.refclassid = 'pg_catalog.pg_class'::regclass and d.refobjsubid > 0 and d.deptype in ('a', 'i')\n" +
        "       limit 1) seq on true\n" +
        "),\n" +
        "rooted (locked, rel, via_index, via_toast, via_sequence, depth) as (\n" +
        "  select u.locked, u.rel, u.via_index, u.via_toast, u.via_sequence, 0 from unwrapped u\n" +
        "  union all\n" +
        "  select r.locked, i.inhparent, r.via_index, r.via_toast, r.via_sequence, r.depth + 1\n" +
        "    from rooted r\n" +
        "    join pg_catalog.pg_class child on child.oid = r.rel and child.relispartition\n" +
        "    join pg_catalog.pg_inherits i on i.inhrelid = r.rel\n" +
        "   where r.depth < 32\n" +
        "),\n" +
        "owners as (\n" +
        "  select distinct on (r.locked) r.locked, n.nspname::text as owner_schema, c.relname::text as owner_name,\n" +
        "         c.relkind::text as owner_kind,\n" +
        "         case when r.via_index then 'index' when r.via_toast then 'toast' when r.depth > 0 then 'partition'\n" +
        "              when r.via_sequence then 'sequence' else 'self' end as part\n" +
        "    from rooted r\n" +
        "    join pg_catalog.pg_class c on c.oid = r.rel\n" +
        "    join pg_catalog.pg_namespace n on n.oid = c.relnamespace\n" +
        "   order by r.locked, r.depth desc\n" +
        ")";

    /// <summary>
    /// A granted advisory lock's 64-bit key as <c>pg_try_advisory_lock(bigint)</c> spells it - the form Marten
    /// and Weasel take every lock in - or null for any other lock: the two-integer form keeps its keys in
    /// <c>classid</c> and <c>objid</c> with <c>objsubid = 2</c> and is never Marten's.
    /// </summary>
    private const string AdvisoryKey =
        "case when l.locktype = 'advisory' and l.objsubid = 1 then ((l.classid::int8 << 32) | l.objid::int8) end";

    /// <summary>
    /// The server's connection budget and how much of it is spent: client backends on the whole server and in
    /// this database, <c>max_connections</c>, <c>superuser_reserved_connections</c> and - from PostgreSQL 16 -
    /// <c>reserved_connections</c>. C02 and C03.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every backend has a row in <c>pg_stat_activity</c> for every role, whatever it may read of it - but a role
    /// without <c>pg_read_all_stats</c> reads another role's row with its <c>backend_type</c> null, so the counts
    /// take <see cref="ClientSession" />, which counts such a row rather than dropping it. A hidden background
    /// worker that runs for a role in a database is counted with them: over by that much, never under.
    /// </para>
    /// <para>
    /// <b>And the two limits Postgres enforces before <c>max_connections</c></b> (W5-AUR, the 2026-10-26 health-gap analysis):
    /// the login role's <c>CONNECTION LIMIT</c> (<c>pg_roles.rolconnlimit</c>, against that role's sessions on the whole server -
    /// <c>pg_stat_activity.usename</c>, which every role may read, B0 G9) and this database's (<c>pg_database.datconnlimit</c>,
    /// against its client sessions). The login role is <c>session_user</c>, which a <c>SET ROLE</c> to <c>SqlConsoleRole</c> does
    /// not change, because that is the role a new connection is counted against. <c>-1</c> is "no limit", and neither limit is
    /// enforced against a superuser - which is why whether the login role is one is read too.
    /// </para>
    /// </remarks>
    public static HealthQuery ServerConnections() => new(
        "select (select count(*) from pg_stat_activity a where " + ClientSession + ") as client_backends,\n" +
        "       (select count(*) from pg_stat_activity a where " + ClientSession + " and a.datname = current_database()) as this_database,\n" +
        "       current_setting('max_connections')::int4 as max_connections,\n" +
        "       current_setting('superuser_reserved_connections')::int4 as superuser_reserved,\n" +
        "       coalesce(nullif(current_setting('reserved_connections', true), ''), '0')::int4 as reserved,\n" +
        "       current_setting('server_version_num')::int4 as server_version,\n" +
        "       session_user::text as login_role,\n" +
        "       (select r.rolconnlimit from pg_catalog.pg_roles r where r.rolname = session_user) as role_limit,\n" +
        "       coalesce((select r.rolsuper from pg_catalog.pg_roles r where r.rolname = session_user), false) as role_superuser,\n" +
        "       (select count(*) from pg_stat_activity a where " + ClientSession + " and a.usename = session_user) as role_sessions,\n" +
        "       (select d.datconnlimit from pg_catalog.pg_database d where d.datname = current_database()) as database_limit");

    /// <summary>
    /// The modes a lock on a table must be in to stop autovacuum's <c>SHARE UPDATE EXCLUSIVE</c> - every mode it conflicts
    /// with, which is every mode from <c>SHARE UPDATE EXCLUSIVE</c> up (W5-V1's autovacuum triggers).
    /// </summary>
    private const string StrongModes =
        "('ShareUpdateExclusiveLock', 'ShareLock', 'ShareRowExclusiveLock', 'ExclusiveLock', 'AccessExclusiveLock')";

    /// <summary>
    /// The lock table, read once (X3, H3's half): one statement that reads <c>pg_locks</c> a single time - a materialized
    /// CTE, so the plan has one <c>Function Scan on pg_lock_status</c> however often the statement refers to it - and
    /// answers every question a regular sweep and Database › Sessions › Blocking ask of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Who asks what.</b> The first row (<c>kind = 0</c>) carries what the sweep's other reads take as parameters, so
    /// none of them reads the lock table again: the relations of this database somebody holds or awaits
    /// <c>ACCESS EXCLUSIVE</c> on (the index read's deparse guard, <c>HealthTableQueries.IndexesSql</c>); the relations
    /// somebody other than a running <c>VACUUM</c> or <c>ANALYZE</c> - or a prepared transaction, whose locks have no
    /// process - holds or awaits a lock on that autovacuum's <c>SHARE UPDATE EXCLUSIVE</c> conflicts with (the table read's
    /// autovacuum triggers, <c>HealthTableQueries.TablesSql</c>); how many relation locks each session of this database
    /// holds (C04); and the one-key advisory locks each holds (C04 and T14 recognise the daemon's leader by them). Then a
    /// row per session in the blocking graph (<c>kind = 1</c>): every client session of this database that waits for a
    /// lock, oldest wait first, with what it waits for and who blocks it, and every session that blocks one of them, with
    /// the modes it holds on what its waiters want. C06 and C12 judge those rows, and Sessions › Blocking draws them.
    /// </para>
    /// <para>
    /// <b>Locks nothing it lists</b> (D27). Relations are resolved to their owners by oid through the catalog
    /// (<see cref="OwnersCte" />) - never a <c>::regclass</c> output cast, which errors on an oid dropped since the
    /// snapshot - and the locked relation's own name is never selected. <c>pg_blocking_pids</c> is called for a session
    /// whose <c>wait_event_type</c> is <c>Lock</c> and nobody else (the PROBE measured it at about 2 ms over 50 waiters among
    /// 500 sessions on PostgreSQL 17, and 6.9 ms called for every backend).
    /// </para>
    /// <para>
    /// <b>A blocker in another database</b> - possible on a shared catalog - is read by process id, and only its state's
    /// class, its timings, whether it has written and whether it is a <c>VACUUM</c> are returned: its state, role,
    /// application and client are this database's to say nothing of, as <c>Blockers</c> said nothing of them in 0.5.
    /// </para>
    /// </remarks>
    internal const string LockTableSql =
        "with recursive locks as materialized (\n" +
        "  select l.locktype, l.database, l.relation, l.page, l.tuple, l.virtualxid, l.transactionid,\n" +
        "         l.classid, l.objid, l.objsubid, l.pid, l.mode, l.granted,\n" +
        "         case when not l.granted then (to_jsonb(l) ->> 'waitstart')::timestamptz end as waitstart\n" +
        "    from pg_locks l\n" +
        "),\n" +
        "here as (select d.oid from pg_catalog.pg_database d where d.datname = current_database()),\n" +
        VisibilityCte + ",\n" +
        "own as (\n" +
        "  select a.pid from pg_stat_activity a\n" +
        "   where a.datname = current_database()\n" +
        "     and " + ClientSession + "\n" +
        "     and a.pid <> pg_backend_pid()\n" +
        "),\n" +
        "maintenance as (\n" +
        "  select v.pid from pg_catalog.pg_stat_progress_vacuum v\n" +
        "  union all\n" +
        "  select z.pid from pg_catalog.pg_stat_progress_analyze z\n" +
        "),\n" +
        "relations as (\n" +
        "  select array_agg(distinct k.relation) filter (where k.mode = 'AccessExclusiveLock') as exclusive,\n" +
        "         array_agg(distinct k.relation) filter (where k.pid is null\n" +
        "             or (k.pid <> pg_backend_pid() and k.pid not in (select m.pid from maintenance m))) as strong\n" +
        "    from locks k\n" +
        "   where k.locktype = 'relation'\n" +
        "     and k.database = (select h.oid from here h)\n" +
        "     and k.mode in " + StrongModes + "\n" +
        "),\n" +
        "counted as (\n" +
        "  select k.pid, count(*) as relation_locks\n" +
        "    from locks k\n" +
        "   where k.granted and k.locktype = 'relation' and k.pid in (select o.pid from own o)\n" +
        "   group by k.pid\n" +
        "),\n" +
        "advisory as (\n" +
        "  select k.pid, ((k.classid::int8 << 32) | k.objid::int8) as advisory_key\n" +
        "    from locks k\n" +
        "   where k.granted and k.locktype = 'advisory' and k.objsubid = 1 and k.pid in (select o.pid from own o)\n" +
        "),\n" +
        "wanted as (\n" +
        "  select distinct on (k.pid) k.pid, k.locktype, k.mode, k.database, k.relation, k.page, k.tuple, k.virtualxid,\n" +
        "         k.transactionid, k.classid, k.objid, k.objsubid, k.waitstart\n" +
        "    from locks k\n" +
        "   where not k.granted and k.pid is not null\n" +
        "   order by k.pid, k.locktype\n" +
        "),\n" +
        "waiting as (\n" +
        "  select w.pid, w.locktype, w.mode, w.database, w.relation, w.page, w.tuple, w.virtualxid, w.transactionid,\n" +
        "         w.classid, w.objid, w.objsubid,\n" +
        "         case when w.locktype = 'advisory' and w.objsubid = 1 then ((w.classid::int8 << 32) | w.objid::int8) end as advisory_key,\n" +
        "         (select t.relation from locks t where t.pid = w.pid and t.locktype = 'tuple' order by t.granted desc limit 1) as tuple_relation,\n" +
        "         w.waitstart is not null as exact_start,\n" +
        "         extract(epoch from (now() - coalesce(w.waitstart, a.state_change)))::float8 as waited_seconds,\n" +
        "         pg_blocking_pids(w.pid) as blocked_by,\n" +
        "         count(*) over () as total\n" +
        "    from wanted w\n" +
        "    join pg_stat_activity a on a.pid = w.pid\n" +
        "   where a.datname = current_database()\n" +
        "     and " + ClientSession + "\n" +
        "     and a.pid <> pg_backend_pid()\n" +
        "     and a.wait_event_type = 'Lock'\n" +
        "   order by coalesce(w.waitstart, a.state_change), w.pid\n" +
        "   limit @limit\n" +
        "),\n" +
        "blocking as (\n" +
        "  select distinct b.pid from waiting w cross join lateral unnest(w.blocked_by) as b(pid) where b.pid <> pg_backend_pid()\n" +
        "),\n" +
        "held as (\n" +
        "  select b.pid, array_agg(distinct k.mode order by k.mode) as modes\n" +
        "    from waiting w\n" +
        "    cross join lateral unnest(w.blocked_by) as b(pid)\n" +
        "    join locks k on k.pid = b.pid and k.granted and k.locktype = w.locktype\n" +
        "                and k.database is not distinct from w.database and k.relation is not distinct from w.relation\n" +
        "                and k.page is not distinct from w.page and k.tuple is not distinct from w.tuple\n" +
        "                and k.virtualxid is not distinct from w.virtualxid and k.transactionid is not distinct from w.transactionid\n" +
        "                and k.classid is not distinct from w.classid and k.objid is not distinct from w.objid\n" +
        "                and k.objsubid is not distinct from w.objsubid\n" +
        "   group by b.pid\n" +
        "),\n" +
        "nodes as (\n" +
        "  select w.pid from waiting w\n" +
        "  union\n" +
        "  select b.pid from blocking b\n" +
        "),\n" +
        "locked as (\n" +
        "  select w.relation from waiting w where w.locktype in ('relation', 'tuple') and w.relation is not null\n" +
        "  union\n" +
        "  select w.tuple_relation from waiting w where w.tuple_relation is not null\n" +
        "),\n" +
        OwnersCte + "\n" +
        "select 0 as kind, v.can_read_all_stats, v.hidden_sessions, current_setting('server_version_num')::int4 as server_version,\n" +
        "       r.exclusive, r.strong,\n" +
        "       (select array_agg(c.pid order by c.pid) from counted c) as relation_lock_pids,\n" +
        "       (select array_agg(c.relation_locks order by c.pid) from counted c) as relation_lock_counts,\n" +
        "       (select array_agg(d.pid order by d.pid, d.advisory_key) from advisory d) as advisory_pids,\n" +
        "       (select array_agg(d.advisory_key order by d.pid, d.advisory_key) from advisory d) as advisory_keys,\n" +
        "       coalesce((select max(w.total) from waiting w), 0)::int8 as waiting_total,\n" +
        "       null::int4 as pid, null::bool as same_database, null::bool as known, null::text as state, null::text as usename,\n" +
        "       null::text as application_name, null::text as client_addr, null::timestamptz as backend_start,\n" +
        "       null::text as wait_event_type, null::text as wait_event, null::float8 as transaction_seconds,\n" +
        "       null::float8 as state_seconds, null::bool as has_xid, null::bool as vacuum, null::text as backend_type,\n" +
        "       null::bool as prevents_wraparound, null::bool as waiter, null::text as locktype, null::text as mode,\n" +
        "       null::text as owner_schema, null::text as owner_name, null::text as owner_kind, null::text as part,\n" +
        "       null::text as tuple_owner_schema, null::text as tuple_owner_name, null::text as tuple_owner_kind, null::text as tuple_part,\n" +
        "       null::int8 as advisory_key, null::bool as exact_start, null::float8 as waited_seconds,\n" +
        "       null::int4[] as blocked_by, null::text[] as held_modes\n" +
        "  from visibility v cross join relations r\n" +
        "union all\n" +
        "select 1, null, null, null, null, null, null, null, null, null, null,\n" +
        "       n.pid, a.datname = current_database(), a.pid is not null,\n" +
        "       case when a.datname = current_database() then a.state end,\n" +
        "       case when a.datname = current_database() then a.usename::text end,\n" +
        "       case when a.datname = current_database() then a.application_name end,\n" +
        "       case when a.datname = current_database() then host(a.client_addr) end,\n" +
        "       case when a.datname = current_database() then a.backend_start end,\n" +
        "       a.wait_event_type,\n" +
        "       case when a.datname = current_database() then a.wait_event end,\n" +
        "       extract(epoch from (now() - a.xact_start))::float8,\n" +
        "       extract(epoch from (now() - a.state_change))::float8,\n" +
        "       a.backend_xid is not null,\n" +
        "       exists (select 1 from pg_catalog.pg_stat_progress_vacuum v where v.pid = n.pid),\n" +
        "       a.backend_type::text,\n" +
        "       case when a.backend_type = 'autovacuum worker' then a.query like 'autovacuum:%(to prevent wraparound)%' end,\n" +
        "       w.pid is not null, w.locktype, w.mode,\n" +
        "       case when w.locktype in ('relation', 'tuple') then ro.owner_schema end,\n" +
        "       case when w.locktype in ('relation', 'tuple') then ro.owner_name end,\n" +
        "       case when w.locktype in ('relation', 'tuple') then ro.owner_kind end,\n" +
        "       case when w.locktype in ('relation', 'tuple') then ro.part end,\n" +
        "       tro.owner_schema, tro.owner_name, tro.owner_kind, tro.part,\n" +
        "       w.advisory_key, w.exact_start, w.waited_seconds, w.blocked_by, h.modes\n" +
        "  from nodes n\n" +
        "  left join pg_stat_activity a on a.pid = n.pid\n" +
        "  left join waiting w on w.pid = n.pid\n" +
        "  left join held h on h.pid = n.pid\n" +
        "  left join owners ro on ro.locked = w.relation\n" +
        "  left join owners tro on tro.locked = w.tuple_relation\n" +
        " order by 1, 41 desc nulls last, 12";

    /// <summary>The lock table, read once for a sweep and for Database › Sessions (<see cref="LockTableSql" />).</summary>
    public static HealthQuery LockTable() => new(LockTableSql, [Integer("limit", MaxSessions)]);

    /// <summary>
    /// PostgreSQL 19's <c>pg_stat_lock</c>: per lock type, how many lock requests had to wait, for how long in all
    /// (milliseconds), and how often a backend's fast-path slots were full - cumulative since <c>stats_reset</c>, for the
    /// whole server. Asked only of a server the lock table read says is 19 or later: below it the view does not exist, and
    /// naming it is <c>42P01</c>.
    /// </summary>
    public static HealthQuery LockStatistics() => new(
        "select s.locktype, s.waits, s.wait_time, s.fastpath_exceeded, s.stats_reset\n" +
        "  from pg_catalog.pg_stat_lock s\n" +
        " order by s.locktype");

    /// <summary>
    /// The sessions of this database that are idle inside a transaction, oldest first, with what they hold -
    /// whether they have a transaction id (they wrote), a snapshot (they pin vacuum), relation locks (they
    /// block DDL) - and whether they hold one of Marten's own daemon locks. C04.
    /// </summary>
    /// <remarks>
    /// The lock counts and the daemon-lock holders come from the sweep's one read of the lock table
    /// (<see cref="LockTable" />) as parameters, so this statement reads <c>pg_stat_activity</c> and nothing of
    /// <c>pg_locks</c>.
    /// </remarks>
    /// <param name="floor">The shortest idle time worth reading at all; the evaluator applies the thresholds.</param>
    /// <param name="daemonLockHolders">The sessions holding one of the advisory keys the source's daemon leads with.</param>
    /// <param name="relationLocks">How many relation locks each session holds, by process id.</param>
    public static HealthQuery IdleInTransaction(
        TimeSpan floor,
        IReadOnlyCollection<int> daemonLockHolders,
        IReadOnlyDictionary<int, long> relationLocks)
    {
        ArgumentNullException.ThrowIfNull(daemonLockHolders);
        ArgumentNullException.ThrowIfNull(relationLocks);

        int[] pids = [.. relationLocks.Keys.Order()];
        long[] counts = [.. pids.Select(x => relationLocks[x])];

        return new HealthQuery(
            "with " + VisibilityCte + ",\n" +
            "idle as (\n" +
            "  select a.pid, a.usename::text as usename, a.application_name, host(a.client_addr) as client_addr,\n" +
            "         a.backend_start, a.xact_start, a.state,\n" +
            "         extract(epoch from (now() - a.state_change))::float8 as idle_seconds,\n" +
            "         extract(epoch from (now() - a.xact_start))::float8 as transaction_seconds,\n" +
            "         a.backend_xid is not null as has_xid,\n" +
            "         a.backend_xmin is not null as has_xmin,\n" +
            "         coalesce((select r.relation_locks from unnest(@relation_lock_pids::int4[], @relation_lock_counts::int8[]) as r(pid, relation_locks)\n" +
            "                    where r.pid = a.pid), 0)::int8 as relation_locks,\n" +
            "         a.pid = any(@daemon_lock_holders::int4[]) as holds_daemon_lock,\n" +
            "         count(*) over () as total\n" +
            "    from pg_stat_activity a\n" +
            "   where a.datname = current_database()\n" +
            "     and " + ClientSession + "\n" +
            "     and a.pid <> pg_backend_pid()\n" +
            "     and a.state in ('idle in transaction', 'idle in transaction (aborted)')\n" +
            "     and a.state_change <= now() - make_interval(secs => @floor_seconds)\n" +
            "   order by a.state_change\n" +
            "   limit @limit\n" +
            ")\n" +
            "select v.can_read_all_stats, v.hidden_sessions, i.pid, i.usename, i.application_name, i.client_addr,\n" +
            "       i.backend_start, i.xact_start, i.state, i.idle_seconds, i.transaction_seconds, i.has_xid, i.has_xmin,\n" +
            "       i.relation_locks, i.holds_daemon_lock, i.total\n" +
            "  from visibility v left join idle i on true",
            [
                Seconds("floor_seconds", floor),
                IntegerArray("daemon_lock_holders", daemonLockHolders),
                IntegerArray("relation_lock_pids", pids),
                LongArray("relation_lock_counts", counts),
                Integer("limit", MaxOffenders),
            ]);
    }

    /// <summary>The statements of this database that have been running a while, longest first. C05.</summary>
    /// <param name="floor">The shortest run worth reading at all; the evaluator applies the threshold.</param>
    public static HealthQuery LongRunningStatements(TimeSpan floor) => new(
        "with " + VisibilityCte + ",\n" +
        "running as (\n" +
        "  select a.pid, a.usename::text as usename, a.application_name, host(a.client_addr) as client_addr,\n" +
        "         a.backend_start, a.query_start, a.wait_event_type,\n" +
        "         extract(epoch from (now() - a.query_start))::float8 as running_seconds,\n" +
        "         count(*) over () as total\n" +
        "    from pg_stat_activity a\n" +
        "   where a.datname = current_database()\n" +
        "     and " + ClientSession + "\n" +
        "     and a.pid <> pg_backend_pid()\n" +
        "     and a.state = 'active'\n" +
        "     and a.query_start <= now() - make_interval(secs => @floor_seconds)\n" +
        "   order by a.query_start\n" +
        "   limit @limit\n" +
        ")\n" +
        "select v.can_read_all_stats, v.hidden_sessions, r.pid, r.usename, r.application_name, r.client_addr,\n" +
        "       r.backend_start, r.query_start, r.wait_event_type, r.running_seconds, r.total\n" +
        "  from visibility v left join running r on true",
        [Seconds("floor_seconds", floor), Integer("limit", MaxOffenders)]);

    /// <summary>
    /// This database's prepared transactions, oldest first, and <c>max_prepared_transactions</c> - one row
    /// with nulls when there are none, so the setting is read either way. C07 and Database › Sessions.
    /// </summary>
    public static HealthQuery PreparedTransactions() => new(
        "with settings as (select current_setting('max_prepared_transactions')::int4 as max_prepared)\n" +
        "select s.max_prepared, p.gid, p.owner::text as owner, p.prepared,\n" +
        "       extract(epoch from (now() - p.prepared))::float8 as age_seconds, p.transaction::text as transaction_id\n" +
        "  from settings s\n" +
        "  left join pg_prepared_xacts p on p.database = current_database()\n" +
        " order by p.prepared nulls last\n" +
        " limit @limit",
        [Integer("limit", MaxPrepared)]);

    /// <summary>
    /// This database's cumulative counters from <c>pg_stat_database</c>: commits, rollbacks, deadlocks, temp
    /// files and bytes, and when they were last reset. C08 turns them into rates through the history.
    /// </summary>
    public static HealthQuery DatabaseCounters() => new(
        "select d.xact_commit, d.xact_rollback, d.deadlocks, d.temp_files, d.temp_bytes, d.stats_reset\n" +
        "  from pg_stat_database d\n" +
        " where d.datname = current_database()");

    /// <summary>
    /// Database › Sessions: every client session of this database but the studio's own, oldest transaction
    /// first, with the visibility row in front - one row with nulls when there is nobody else. The statement's
    /// text is cut to <see cref="MaxQueryCharacters" /> on the server and flagged when Postgres had already cut it
    /// at <c>track_activity_query_size</c>, because a cut text can end in the first half of a name.
    /// </summary>
    public static HealthQuery SessionSnapshot() => new(
        "with " + VisibilityCte + ",\n" +
        "tracked as (select s.setting::int4 as track_size from pg_settings s where s.name = 'track_activity_query_size'),\n" +
        "sessions as (\n" +
        "  select a.pid, a.usename::text as usename, a.application_name, host(a.client_addr) as client_addr,\n" +
        "         a.backend_start, a.xact_start, a.query_start, a.state_change, a.state, a.wait_event_type, a.wait_event,\n" +
        "         a.backend_xid is not null as has_xid, a.backend_xmin is not null as has_xmin,\n" +
        "         extract(epoch from (now() - a.xact_start))::float8 as transaction_seconds,\n" +
        "         extract(epoch from (now() - a.query_start))::float8 as query_seconds,\n" +
        "         extract(epoch from (now() - a.state_change))::float8 as state_seconds,\n" +
        "         case when a.wait_event_type = 'Lock' then pg_blocking_pids(a.pid) end as blocked_by,\n" +
        "         left(a.query, @max_query) as query,\n" +
        "         octet_length(a.query) as query_bytes,\n" +
        "         char_length(a.query) as query_characters,\n" +
        "         count(*) over () as total\n" +
        "    from pg_stat_activity a\n" +
        "   where a.datname = current_database()\n" +
        "     and " + ClientSession + "\n" +
        "     and a.pid <> pg_backend_pid()\n" +
        "   order by a.xact_start nulls last, a.backend_start, a.pid\n" +
        "   limit @limit\n" +
        ")\n" +
        "select v.can_read_all_stats, v.hidden_sessions, (select t.track_size from tracked t) as track_size,\n" +
        "       s.pid, s.usename, s.application_name, s.client_addr, s.backend_start, s.xact_start, s.query_start,\n" +
        "       s.state_change, s.state, s.wait_event_type, s.wait_event, s.has_xid, s.has_xmin,\n" +
        "       s.transaction_seconds, s.query_seconds, s.state_seconds, s.blocked_by,\n" +
        "       s.query, s.query_bytes, s.query_characters, s.total\n" +
        "  from visibility v left join sessions s on true",
        [Integer("max_query", MaxQueryCharacters), Integer("limit", MaxSessions)]);

    /// <summary>
    /// Database › Sessions: the locks held and awaited by this database's sessions, waiters first, without
    /// each transaction's lock on its own virtual id (every transaction holds one, and it says nothing). A lock
    /// on a relation is returned as its owner and the part of it locked (<see cref="OwnersCte" />).
    /// </summary>
    public static HealthQuery LockSnapshot() => new(
        "with recursive held as (\n" +
        "  select l.pid, l.locktype, l.mode, l.granted,\n" +
        "         case when l.locktype in ('relation', 'tuple') then l.relation end as relation,\n" +
        "         " + AdvisoryKey + " as advisory_key,\n" +
        "         extract(epoch from (now() - (to_jsonb(l) ->> 'waitstart')::timestamptz))::float8 as waited_seconds,\n" +
        "         count(*) over () as total\n" +
        "    from pg_locks l\n" +
        "    join pg_stat_activity a on a.pid = l.pid\n" +
        "   where a.datname = current_database()\n" +
        "     and " + ClientSession + "\n" +
        "     and l.pid <> pg_backend_pid()\n" +
        "     and l.locktype <> 'virtualxid'\n" +
        "   order by l.granted, l.pid, l.locktype\n" +
        "   limit @limit\n" +
        "),\n" +
        "locked as (select distinct h.relation from held h where h.relation is not null),\n" +
        OwnersCte + "\n" +
        "select h.pid, h.locktype, h.mode, h.granted, o.owner_schema, o.owner_name, o.owner_kind, o.part,\n" +
        "       h.advisory_key, h.waited_seconds, h.total\n" +
        "  from held h\n" +
        "  left join owners o on o.locked = h.relation\n" +
        " order by h.granted, h.pid, h.locktype",
        [Integer("limit", MaxLocks)]);

    private static NpgsqlParameter Seconds(string name, TimeSpan value) =>
        new(name, NpgsqlDbType.Double) { Value = Math.Max(value.TotalSeconds, 0d) };

    private static NpgsqlParameter Integer(string name, int value) =>
        new(name, NpgsqlDbType.Integer) { Value = value };

    private static NpgsqlParameter LongArray(string name, IReadOnlyCollection<long> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = values.ToArray() };

    private static NpgsqlParameter IntegerArray(string name, IReadOnlyCollection<int> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = values.ToArray() };
}
