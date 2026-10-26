using System.Globalization;
using System.Text.RegularExpressions;

using PostgresStudio.Services.Database;

using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>Where <c>pg_stat_statements</c> is installed in this database, which version, and on which server.</summary>
/// <param name="ServerVersion"><c>server_version_num</c>.</param>
/// <param name="Schema">The extension's schema (<c>pg_extension.extnamespace</c>), or <see langword="null" /> when it is not installed here.</param>
/// <param name="Version"><c>pg_extension.extversion</c>, or <see langword="null" />.</param>
internal sealed record StatementExtension(int ServerVersion, string? Schema, string? Version)
{
    /// <summary>The oldest extension version the statement checks read: 1.8 (PostgreSQL 13), the first with <c>total_exec_time</c>.</summary>
    public static readonly Version Oldest = new(1, 8);

    /// <summary>Whether the extension is installed in this database.</summary>
    public bool Installed => Schema is not null && Version is not null;

    /// <summary>The version, parsed; <see langword="null" /> when it is not one.</summary>
    public Version? Parsed => System.Version.TryParse(Version, out Version? parsed) ? parsed : null;

    /// <summary>Whether the checks can read this version at all.</summary>
    public bool Supported => Parsed is { } parsed && parsed >= Oldest;

    /// <summary>1.9 (PostgreSQL 14): <c>toplevel</c>, and the <c>pg_stat_statements_info</c> view.</summary>
    public bool HasToplevelAndInfo => Parsed is { } parsed && parsed >= new Version(1, 9);

    /// <summary>1.10 (PostgreSQL 15): the <c>jit_*</c> columns.</summary>
    public bool HasJit => Parsed is { } parsed && parsed >= new Version(1, 10);

    /// <summary>1.11 (PostgreSQL 17): <c>jit_deform_time</c> and each entry's own <c>stats_since</c>.</summary>
    public bool HasStatsSince => Parsed is { } parsed && parsed >= new Version(1, 11);

    /// <summary>
    /// 1.11 (PostgreSQL 17): <c>blk_read_time</c> renamed <c>shared_blk_read_time</c>, beside a new
    /// <c>local_blk_read_time</c> (<c>PostgresFeature.StatementsSharedBlkTimeNames</c>, B0 §6).
    /// </summary>
    public bool HasSharedBlockTimeNames => HasStatsSince;

    /// <summary>1.12 (PostgreSQL 18): <c>parallel_workers_to_launch</c>, <c>parallel_workers_launched</c> and <c>wal_buffers_full</c> (B0 §6).</summary>
    public bool HasParallelWorkers => Parsed is { } parsed && parsed >= new Version(1, 12);

    /// <summary>
    /// Whether this database has Aurora's <c>pg_catalog.aurora_stat_statements(boolean)</c> (issue #8 A5) - looked for in
    /// <c>pg_catalog</c> alone, where no role short of a superuser creates a function, so a function of that name somebody
    /// made in <c>public</c> is never called as the store's own role (the platform detector's rule, W5-R2-fix B3).
    /// </summary>
    public bool AuroraStatements { get; init; }
}

/// <summary>Which statements the counters read keeps: the union of each check's top few, and never more than <see cref="Max" />.</summary>
/// <param name="TopByLoad">The most time-consuming, per second of their entry's life (T02).</param>
/// <param name="TopByMean">The slowest per call (T02), among those with at least <paramref name="MeanCalls" /> calls.</param>
/// <param name="MeanCalls">The fewest calls a mean is ranked on.</param>
/// <param name="TopGuarded">The writes with the most calls that changed no row (T22), among those with at least <paramref name="GuardedCalls" /> calls.</param>
/// <param name="GuardedCalls">The fewest calls a conflict rate is ranked on.</param>
/// <param name="TopJit">The most JIT time (T23), among those with at least <paramref name="JitCalls" /> calls.</param>
/// <param name="JitCalls">The fewest calls a JIT share is ranked on.</param>
/// <param name="Max">The most statements kept, and so the most whose text is ever returned.</param>
/// <remarks>
/// <para>
/// <b>Each check has a share, and the shares fit.</b> The tops are a union, ranked on the server, and
/// <see cref="Max" /> is a <c>limit</c> after it - so tops that summed to more than <see cref="Max" /> would let the
/// limit, which orders by load, drop the guarded writes or the JIT picks a statement that ranks high by load had
/// pushed out. <see cref="HealthStatementQueries.Counters" /> refuses a selection whose shares do not fit, which makes
/// the limit a guard that never cuts.
/// </para>
/// <para>
/// <b>The Statements page's two shares</b> (W5-Q1, issue #8 G6 and A5): the statements with the most execution time since
/// their counters began (<see cref="TopByTotal" />), and on Aurora the most storage-level reads
/// (<see cref="TopByStorage" />) - so the page's top hundred by either is in the one read the checks already make.
/// </para>
/// </remarks>
internal sealed record StatementSelection(
    int TopByLoad,
    int TopByMean,
    int MeanCalls,
    int TopGuarded,
    int GuardedCalls,
    int TopJit,
    int JitCalls,
    int Max)
{
    /// <summary>The most execution time since the counters began: the Statements page's ranking (G6).</summary>
    public int TopByTotal { get; init; }

    /// <summary>
    /// The most storage-level reads, where Aurora's <c>aurora_stat_statements</c> is joined (A5); a statement with no
    /// storage reads read is never picked by it.
    /// </summary>
    public int TopByStorage { get; init; }

    /// <summary>Whether every share fits in <see cref="Max" /> at once.</summary>
    public bool SharesFit => TopByLoad + TopByMean + TopGuarded + TopJit + TopByTotal + TopByStorage <= Max;
}

/// <summary>One statement's counters, from <c>pg_stat_statements(showtext := false)</c>, with the database's totals.</summary>
internal sealed record StatementCounterRow
{
    /// <summary><c>queryid</c>.</summary>
    public required long QueryId { get; init; }

    /// <summary><c>calls</c>.</summary>
    public long Calls { get; init; }

    /// <summary><c>rows</c>: rows returned or affected, summed over the calls.</summary>
    public long Rows { get; init; }

    /// <summary><c>total_exec_time</c>, in milliseconds.</summary>
    public double TotalExecMs { get; init; }

    /// <summary><c>mean_exec_time</c>, in milliseconds.</summary>
    public double MeanExecMs { get; init; }

    /// <summary><c>temp_blks_written</c>: blocks it spilled to temporary files.</summary>
    public long TempBlocksWritten { get; init; }

    /// <summary><c>wal_bytes</c>.</summary>
    public double WalBytes { get; init; }

    /// <summary><c>wal_records</c>.</summary>
    public long WalRecords { get; init; }

    /// <summary>The sum of the <c>jit_*_time</c> columns, in milliseconds - <see langword="null" /> below extension 1.10.</summary>
    public double? JitMs { get; init; }

    /// <summary>When this entry's counters began (<c>stats_since</c>, extension 1.11) - <see langword="null" /> below it.</summary>
    public DateTimeOffset? StatsSince { get; init; }

    /// <summary>How many statements of this role and database the extension holds.</summary>
    public long AllStatements { get; init; }

    /// <summary>Their <c>total_exec_time</c>, summed.</summary>
    public double AllExecMs { get; init; }

    /// <summary>Their <c>wal_bytes</c>, summed.</summary>
    public double AllWalBytes { get; init; }

    /// <summary><c>pg_stat_statements_info.dealloc</c>: entries evicted since the last reset - <see langword="null" /> below 1.9.</summary>
    public long? Dealloc { get; init; }

    /// <summary><c>pg_stat_statements_info.stats_reset</c> - <see langword="null" /> below 1.9.</summary>
    public DateTimeOffset? StatsReset { get; init; }

    /// <summary><c>shared_blks_hit</c>: shared buffers found in the cache.</summary>
    public long SharedBlocksHit { get; init; }

    /// <summary><c>shared_blks_read</c>: shared blocks read from outside the cache.</summary>
    public long SharedBlocksRead { get; init; }

    /// <summary>
    /// The time spent reading shared blocks, in milliseconds - <c>shared_blk_read_time</c> from extension 1.11,
    /// <c>blk_read_time</c> before it - and zero while <c>track_io_timing</c> is off.
    /// </summary>
    public double IoReadMs { get; init; }

    /// <summary><c>parallel_workers_to_launch</c> (extension 1.12) - <see langword="null" /> below it.</summary>
    public long? ParallelWorkersToLaunch { get; init; }

    /// <summary><c>parallel_workers_launched</c> (extension 1.12) - <see langword="null" /> below it.</summary>
    public long? ParallelWorkersLaunched { get; init; }

    /// <summary><c>wal_buffers_full</c> (extension 1.12) - <see langword="null" /> below it.</summary>
    public long? WalBuffersFull { get; init; }

    /// <summary>
    /// Aurora's <c>storage_blks_read</c> (<c>aurora_stat_statements</c>, issue #8 A5): blocks read from the cluster's
    /// storage rather than a cache - <see langword="null" /> where it is not joined.
    /// </summary>
    public long? StorageBlocksRead { get; init; }

    /// <summary>Aurora's <c>orcache_blks_hit</c>: blocks found in the optimised-reads cache - <see langword="null" /> where not joined.</summary>
    public long? OrcacheBlocksHit { get; init; }

    /// <summary>Aurora's <c>storage_blk_read_time</c>, in milliseconds - <see langword="null" /> where not joined.</summary>
    public double? StorageReadMs { get; init; }
}

/// <summary>One statement's text, as much of it as is read.</summary>
/// <param name="QueryId">Its <c>queryid</c>.</param>
/// <param name="Text">
/// The text as the extension recorded it: the constants of a plannable statement replaced by <c>$n</c> - but not of
/// every statement. A utility statement can keep its constants as written (a <c>COPY (select … 'x') to stdout</c> on
/// PostgreSQL 17; <c>CALL</c> and <c>DECLARE</c> on 13–15), which is why the checks withhold such a text rather than show it.
/// </param>
/// <param name="Truncated">Whether the read cut it at <see cref="HealthStatementQueries.MaxTextCharacters" />.</param>
internal sealed record StatementTextRow(long QueryId, string Text, bool Truncated);

/// <summary>Why a statement is not planned, when it is not.</summary>
internal enum StatementPlanRefusal
{
    /// <summary>Nothing refused.</summary>
    None,

    /// <summary>The text was cut, so it is not the statement.</summary>
    Truncated,

    /// <summary>Not a plain <c>select</c>: planning anything else takes a stronger lock than a read.</summary>
    NotASelect,

    /// <summary>A locking clause: <c>for update</c>, <c>for share</c>.</summary>
    LockingClause,

    /// <summary>More than one statement, a dollar-quoted body, a function the console refuses.</summary>
    Shape,
}

/// <summary>
/// The statements of the statement checks (T02, T22, T23): reads of <c>pg_stat_statements</c>, and the generic plan
/// of a statement it recorded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Counters without text, then text for a few - and what that does and does not save.</b>
/// <see cref="Counters" /> calls <c>pg_stat_statements(showtext := false)</c>: with <c>showtext</c> off the extension
/// never opens its query-text file, which holds every statement it has seen (up to <c>pg_stat_statements.max</c>,
/// 5,000 by default). It keeps only the union of each check's top few (<see cref="StatementSelection" />), at most
/// <see cref="MaxTexts" /> rows, ranked in SQL so the rest never leave the server. <see cref="Texts" /> then asks for
/// the text of exactly those <c>queryid</c>s - but the <c>queryid</c> filter is applied to what the function returns:
/// PostgreSQL 17's <c>pg_stat_statements_internal</c>, called with <c>showtext := true</c>, loads the whole text file
/// and fetches every entry's text before any <c>where</c> sees a row. So one text call costs what the plain view
/// costs, and what is bounded is the rows it returns, never the work behind them. That is why the text call is made
/// only for statements whose text the process does not already hold (<c>StatementTextCache</c>): a sweep whose kept
/// statements are all known makes none.
/// </para>
/// <para>
/// <b>As the store's own role, and only its own statements.</b> Both run through
/// <c>HealthSqlRunner.QueryAsApplicationRoleAsync</c> - not as <c>SqlConsoleRole</c>, which would be shown
/// <c>&lt;insufficient privilege&gt;</c> for Marten's statements - and both are bounded to
/// <c>userid = current_user</c> and <c>dbid</c> = this database, so what is read is what the store's role ran here
/// itself. Each statement says so in a comment, which is also what "Show SQL" shows; "Show SQL" prints the
/// extension's schema as <see cref="WithheldNames.Token" />, because it is drawn for everybody the finding is shown to
/// and that schema may be one a visitor may not see.
/// </para>
/// <para>
/// <b>Version-proof by construction.</b> The columns differ by the extension's version - which is the installed
/// one, <c>pg_extension.extversion</c>, not the server's: a database whose extension was never updated has the
/// old function signature on a new server. So <see cref="Extension" /> is read first and the counters statement
/// names only the columns that version has.
/// </para>
/// <para>
/// <b>The extension's schema is found, not assumed.</b> It is wherever <c>CREATE EXTENSION</c> put it
/// (<c>pg_extension.extnamespace</c>), and every health statement runs with <c>search_path</c> pinned to
/// <c>pg_catalog</c>, so the function and the view are always qualified - through <see cref="SqlIdentifier" />,
/// like every identifier here.
/// </para>
/// <para>
/// <b>A plan, never an execution.</b> <see cref="Explain" /> is the generic plan, in <c>FORMAT JSON</c> - never
/// <c>ANALYZE</c> - of a plain <c>select</c> only: planning takes an <c>AccessShareLock</c> on what it plans, as any
/// read does, where planning an <c>insert</c>, <c>update</c>, <c>delete</c> or a <c>select … for update</c> takes the
/// lock its execution would. It runs as <c>SqlConsoleRole</c>, like the Query page's <c>EXPLAIN</c>.
/// </para>
/// <para>
/// <b>Aurora's storage reads, joined and never trusted for the fence</b> (issue #8 A5, W5-Q1). Where the platform is Aurora
/// and <c>pg_catalog.aurora_stat_statements(boolean)</c> exists, <see cref="Counters" /> joins it to the extension's own
/// rows on <c>(userid, dbid, toplevel, queryid)</c> and takes its extra columns by name (<c>to_jsonb(x) -&gt;&gt; 'col'</c>):
/// on PostgreSQL 17 its documented "every pg_stat_statements column, then eleven more" collides with the extension's own
/// <c>local_blk_read_time</c>, so neither its column list nor its order is relied on (B0's Aurora table). Its rows are
/// fenced to <c>userid = current_user</c> and this database exactly as the extension's are, before the join.
/// </para>
/// </remarks>
internal static partial class HealthStatementQueries
{
    /// <summary>
    /// The most statements one sweep keeps, and so the most whose text one call returns: the checks' twenty, the Statements
    /// page's hundred by execution time, and on Aurora its hundred by storage reads (W5-Q1).
    /// </summary>
    public const int MaxTexts = 220;

    /// <summary>
    /// The most characters of one statement's text read - the same 4,096 Database › Sessions reads of
    /// <c>pg_stat_activity</c>, and the default <c>track_activity_query_size</c>.
    /// </summary>
    public const int MaxTextCharacters = 4096;

    /// <summary>The comment every statement read as the store's own role begins with: what "Show SQL" says about it.</summary>
    internal const string ApplicationRoleComment =
        "-- Read as the store's own Postgres role, not SqlConsoleRole: pg_stat_statements shows a role only its own statements' text.\n";

    /// <summary>The most parameters a planned statement may have; Postgres' own limit is 65,535.</summary>
    internal const int MaxParameters = 65_535;

    /// <summary>
    /// The server's version, and the extension's schema and version in this database - one row, whether it is
    /// installed or not. A catalog read.
    /// </summary>
    public static HealthQuery Extension() => new(
        "select current_setting('server_version_num')::int4 as server_version,\n" +
        "       n.nspname::text as extension_schema,\n" +
        "       e.extversion::text as extension_version,\n" +
        "       exists (select 1 from pg_catalog.pg_proc p\n" +
        "                where p.proname = 'aurora_stat_statements'\n" +
        "                  and p.pronamespace = 'pg_catalog'::pg_catalog.regnamespace\n" +
        "                  and p.pronargs = 1 and p.proargtypes[0] = 'pg_catalog.bool'::pg_catalog.regtype) as aurora_statements\n" +
        "  from (select 1) one\n" +
        "  left join pg_catalog.pg_extension e on e.extname = 'pg_stat_statements'\n" +
        "  left join pg_catalog.pg_namespace n on n.oid = e.extnamespace");

    /// <summary>Reads <see cref="Extension" />'s row.</summary>
    public static StatementExtension ReadExtension(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new StatementExtension(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2))
        {
            AuroraStatements = !reader.IsDBNull(3) && reader.GetBoolean(3),
        };
    }

    /// <summary>
    /// The counters of the statements <paramref name="selection" /> keeps, with the database's totals on every
    /// row - <c>pg_stat_statements(showtext := false)</c>, as the store's own role, for its own statements here.
    /// </summary>
    /// <param name="extension">What is installed; it must be <see cref="StatementExtension.Supported" />.</param>
    /// <param name="selection">Which statements to keep.</param>
    /// <param name="storageReads">
    /// Whether to join Aurora's <c>pg_catalog.aurora_stat_statements</c> for its storage-level reads (issue #8 A5): only
    /// where the platform is Aurora and <see cref="StatementExtension.AuroraStatements" /> says the function is there.
    /// </param>
    public static HealthQuery Counters(StatementExtension extension, StatementSelection selection, bool storageReads = false)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(selection);

        if (!extension.Supported || extension.Schema is not { } schema)
        {
            throw new ArgumentException("The counters can only be read from pg_stat_statements 1.8 or later.", nameof(extension));
        }

        if (!selection.SharesFit)
        {
            throw new ArgumentException(
                "The checks' shares must fit in the statements kept, or the limit would drop a check's picks.", nameof(selection));
        }

        return new HealthQuery(
            CountersSql(extension, SqlIdentifier.Quote(schema), storageReads),
        [
            Integer("top_load", selection.TopByLoad),
            Integer("top_mean", selection.TopByMean),
            Integer("mean_calls", selection.MeanCalls),
            Integer("top_guarded", selection.TopGuarded),
            Integer("guarded_calls", selection.GuardedCalls),
            Integer("top_jit", selection.TopJit),
            Integer("jit_calls", selection.JitCalls),
            Integer("top_total", selection.TopByTotal),
            Integer("top_storage", storageReads ? selection.TopByStorage : 0),
            Integer("max", Math.Min(selection.Max, MaxTexts)),
        ],
            CountersSql(extension, WithheldNames.Token, storageReads));
    }

    /// <summary>The counters statement, with the extension's schema spelled <paramref name="schema" />.</summary>
    private static string CountersSql(StatementExtension extension, string schema, bool storageReads)
    {
        string function = schema + "." + SqlIdentifier.Quote("pg_stat_statements");

        string info = extension.HasToplevelAndInfo
            ? "  select i.dealloc, i.stats_reset from " + schema + "." + SqlIdentifier.Quote("pg_stat_statements_info") + " i\n"
            : "  select null::int8 as dealloc, null::timestamptz as stats_reset\n";

        string jit = !extension.HasJit
            ? "null::float8"
            : "s.jit_generation_time + s.jit_inlining_time + s.jit_optimization_time + s.jit_emission_time"
              + (extension.HasStatsSince ? " + s.jit_deform_time" : string.Empty);

        string since = extension.HasStatsSince ? "s.stats_since" : "null::timestamptz";
        string toplevel = extension.HasToplevelAndInfo ? "\n     and s.toplevel" : string.Empty;

        // Read by name for the installed extension's version, never by position: 1.11 renamed blk_read_time, and 1.12 added
        // the parallel workers and wal_buffers_full (PostgresFeature.StatementsSharedBlkTimeNames, B0 §6).
        string ioRead = extension.HasSharedBlockTimeNames ? "s.shared_blk_read_time" : "s.blk_read_time";
        string parallel = extension.HasParallelWorkers
            ? "s.parallel_workers_to_launch::int8 as parallel_to_launch, s.parallel_workers_launched::int8 as parallel_launched,\n"
              + "         s.wal_buffers_full::int8 as wal_buffers_full"
            : "null::int8 as parallel_to_launch, null::int8 as parallel_launched, null::int8 as wal_buffers_full";

        string aurora = storageReads ? AuroraSql(extension.HasToplevelAndInfo) : string.Empty;
        string storage = storageReads
            ? "a.storage_blks_read, a.orcache_blks_hit, a.storage_blk_read_time"
            : "null::int8 as storage_blks_read, null::int8 as orcache_blks_hit, null::float8 as storage_blk_read_time";
        string join = !storageReads
            ? string.Empty
            : "\n    left join aurora a on a.queryid = s.queryid and a.userid = s.userid and a.dbid = s.dbid"
              + (extension.HasToplevelAndInfo ? " and a.toplevel = s.toplevel" : string.Empty);

        return
            ApplicationRoleComment +
            "with info as (\n" + info + "),\n" +
            aurora +
            "s as (\n" +
            "  select s.queryid, s.calls, s.rows, s.total_exec_time, s.mean_exec_time, s.temp_blks_written,\n" +
            "         s.wal_bytes::float8 as wal_bytes, s.wal_records,\n" +
            "         " + jit + " as jit_time,\n" +
            "         " + since + " as stats_since,\n" +
            "         s.shared_blks_hit, s.shared_blks_read, " + ioRead + "::float8 as io_read_ms,\n" +
            "         " + parallel + ",\n" +
            "         " + storage + "\n" +
            "    from " + function + "(showtext := false) s" + join + "\n" +
            "   where " + OwnStatements + "\n" +
            "     and s.queryid is not null" + toplevel + "\n" +
            "),\n" +
            "totals as (\n" +
            "  select count(*) as statements, coalesce(sum(s.total_exec_time), 0) as exec_time, coalesce(sum(s.wal_bytes), 0) as wal_bytes\n" +
            "    from s\n" +
            "),\n" +
            "ranked as (\n" +
            "  select s.*,\n" +
            "         row_number() over (order by s.total_exec_time\n" +
            "                                  / greatest(coalesce(extract(epoch from pg_catalog.now() - coalesce(s.stats_since, i.stats_reset))::float8, 1), 1) desc,\n" +
            "                                s.queryid) as by_load,\n" +
            "         row_number() over (order by case when s.calls >= @mean_calls then s.mean_exec_time end desc nulls last, s.queryid) as by_mean,\n" +
            "         row_number() over (order by case when " + Guarded("s") + " then s.calls - s.rows end desc nulls last, s.queryid) as by_guarded,\n" +
            "         row_number() over (order by case when s.calls >= @jit_calls and s.jit_time > 0 then s.jit_time end desc nulls last, s.queryid) as by_jit,\n" +
            "         row_number() over (order by s.total_exec_time desc, s.queryid) as by_total,\n" +
            "         row_number() over (order by s.storage_blks_read desc nulls last, s.queryid) as by_storage\n" +
            "    from s cross join info i\n" +
            ")\n" +
            "select r.queryid, r.calls, r.rows, r.total_exec_time, r.mean_exec_time, r.temp_blks_written, r.wal_bytes, r.wal_records,\n" +
            "       r.jit_time, r.stats_since, t.statements, t.exec_time, t.wal_bytes, i.dealloc, i.stats_reset,\n" +
            "       r.shared_blks_hit, r.shared_blks_read, r.io_read_ms, r.parallel_to_launch, r.parallel_launched, r.wal_buffers_full,\n" +
            "       r.storage_blks_read, r.orcache_blks_hit, r.storage_blk_read_time\n" +
            "  from ranked r cross join totals t cross join info i\n" +
            " where r.by_load <= @top_load\n" +
            "    or (r.by_mean <= @top_mean and r.calls >= @mean_calls)\n" +
            "    or (r.by_guarded <= @top_guarded and " + Guarded("r") + ")\n" +
            "    or (r.by_jit <= @top_jit and r.calls >= @jit_calls and r.jit_time > 0)\n" +
            "    or r.by_total <= @top_total\n" +
            "    or (r.by_storage <= @top_storage and r.storage_blks_read is not null)\n" +
            " order by r.by_load\n" +
            " limit @max";
    }

    /// <summary>
    /// Aurora's <c>aurora_stat_statements</c> rows, its join keys and its storage columns by name, fenced to the store's own
    /// role and this database as the extension's are - a CTE of the counters statement (issue #8 A5).
    /// </summary>
    /// <param name="toplevel">Whether the extension has <c>toplevel</c>, which the join then keys on too.</param>
    private static string AuroraSql(bool toplevel) =>
        "aurora as (\n" +
        "  select (a.j ->> 'queryid')::int8 as queryid, (a.j ->> 'userid')::oid as userid, (a.j ->> 'dbid')::oid as dbid,\n" +
        (toplevel ? "         (a.j ->> 'toplevel')::bool as toplevel,\n" : string.Empty) +
        "         (a.j ->> 'storage_blks_read')::int8 as storage_blks_read,\n" +
        "         (a.j ->> 'orcache_blks_hit')::int8 as orcache_blks_hit,\n" +
        "         (a.j ->> 'storage_blk_read_time')::float8 as storage_blk_read_time\n" +
        "    from (select pg_catalog.to_jsonb(x) as j from pg_catalog." + SqlIdentifier.Quote("aurora_stat_statements") + "(false) x) a\n" +
        "   where " + Own("(a.j ->> 'userid')::oid", "(a.j ->> 'dbid')::oid") + "\n" +
        "),\n";

    /// <summary>Reads one of <see cref="Counters" />' rows.</summary>
    public static StatementCounterRow ReadCounters(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new StatementCounterRow
        {
            QueryId = reader.GetInt64(0),
            Calls = reader.GetInt64(1),
            Rows = reader.GetInt64(2),
            TotalExecMs = reader.GetDouble(3),
            MeanExecMs = reader.GetDouble(4),
            TempBlocksWritten = reader.GetInt64(5),
            WalBytes = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
            WalRecords = reader.GetInt64(7),
            JitMs = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            StatsSince = reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            AllStatements = reader.GetInt64(10),
            AllExecMs = reader.GetDouble(11),
            AllWalBytes = reader.IsDBNull(12) ? 0 : reader.GetDouble(12),
            Dealloc = reader.IsDBNull(13) ? null : reader.GetInt64(13),
            StatsReset = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
            SharedBlocksHit = reader.GetInt64(15),
            SharedBlocksRead = reader.GetInt64(16),
            IoReadMs = reader.IsDBNull(17) ? 0 : reader.GetDouble(17),
            ParallelWorkersToLaunch = reader.IsDBNull(18) ? null : reader.GetInt64(18),
            ParallelWorkersLaunched = reader.IsDBNull(19) ? null : reader.GetInt64(19),
            WalBuffersFull = reader.IsDBNull(20) ? null : reader.GetInt64(20),
            StorageBlocksRead = reader.IsDBNull(21) ? null : reader.GetInt64(21),
            OrcacheBlocksHit = reader.IsDBNull(22) ? null : reader.GetInt64(22),
            StorageReadMs = reader.IsDBNull(23) ? null : reader.GetDouble(23),
        };
    }

    /// <summary>
    /// The text of these statements - <c>pg_stat_statements(showtext := true)</c>, as the store's own role, its own
    /// statements here - each cut at <see cref="MaxTextCharacters" />.
    /// </summary>
    /// <remarks>
    /// The <c>queryid</c> filter bounds the rows returned and nothing else: the extension loads its whole text file and
    /// fetches every entry's text before the filter sees a row (PostgreSQL 17's <c>pg_stat_statements_internal</c>). So a
    /// caller asks only for the texts it does not already hold, and makes no call at all when it holds them all.
    /// </remarks>
    /// <param name="extension">What is installed.</param>
    /// <param name="queryIds">At most <see cref="MaxTexts" /> of them.</param>
    /// <exception cref="ArgumentException">There are none, or more than <see cref="MaxTexts" />.</exception>
    public static HealthQuery Texts(StatementExtension extension, IReadOnlyCollection<long> queryIds)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(queryIds);

        if (queryIds.Count is 0 or > MaxTexts)
        {
            throw new ArgumentException($"Text is read for one to {MaxTexts} statements at a time.", nameof(queryIds));
        }

        if (!extension.Supported || extension.Schema is not { } schema)
        {
            throw new ArgumentException("Text can only be read from pg_stat_statements 1.8 or later.", nameof(extension));
        }

        string Sql(string schemaSpelled) =>
            ApplicationRoleComment +
            "select s.queryid, pg_catalog.left(s.query, @characters) as query, pg_catalog.length(s.query) > @characters as truncated\n" +
            "  from " + schemaSpelled + "." + SqlIdentifier.Quote("pg_stat_statements") + "(showtext := true) s\n" +
            " where " + OwnStatements + "\n" +
            "   and s.queryid = any(@ids)" + (extension.HasToplevelAndInfo ? "\n   and s.toplevel" : string.Empty);

        return new HealthQuery(
            Sql(SqlIdentifier.Quote(schema)),
        [
            Integer("characters", MaxTextCharacters),
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = queryIds.Distinct().ToArray() },
        ],
            Sql(WithheldNames.Token));
    }

    /// <summary>Reads one of <see cref="Texts" />' rows.</summary>
    public static StatementTextRow ReadText(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new StatementTextRow(reader.GetInt64(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1), !reader.IsDBNull(2) && reader.GetBoolean(2));
    }

    /// <summary>
    /// Why <paramref name="text" /> would not be planned, or <see cref="StatementPlanRefusal.None" />: a plain,
    /// single <c>select</c>, read whole, that the SQL console's own guard would run.
    /// </summary>
    /// <remarks>
    /// Decided on <see cref="SqlLexer" />'s tokens, never on the raw text: a comment, a literal or a quoted identifier
    /// is not a word, so <c>for/**/update</c> is the locking clause it is to Postgres, and a <c>;</c> or <c>for update</c>
    /// inside a string is not one.
    /// </remarks>
    public static StatementPlanRefusal Refusal(string text, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (truncated)
        {
            return StatementPlanRefusal.Truncated;
        }

        SqlLexer lexer = new(text, reportLiterals: true);
        bool first = true;

        while (lexer.TryRead(out SqlToken token))
        {
            if (token.Kind == SqlTokenKind.Unreadable)
            {
                return StatementPlanRefusal.Shape;
            }

            if (first)
            {
                first = false;

                if (!token.IsWord("select"))
                {
                    return StatementPlanRefusal.NotASelect;
                }

                continue;
            }

            if (token.Is(';') || (token.Kind == SqlTokenKind.Literal && token.Text.StartsWith('$')))
            {
                return StatementPlanRefusal.Shape;
            }

            if (token.IsWord("for") && LocksRows(lexer))
            {
                return StatementPlanRefusal.LockingClause;
            }
        }

        if (first)
        {
            return StatementPlanRefusal.NotASelect;
        }

        if (!ReadOnlySqlGuard.Check(text).Allowed)
        {
            return StatementPlanRefusal.Shape;
        }

        return HighestParameter(text) > MaxParameters ? StatementPlanRefusal.Shape : StatementPlanRefusal.None;
    }

    /// <summary>
    /// Whether the words after a <c>for</c> make it a locking clause - <c>update</c>, <c>share</c>, <c>no key update</c>,
    /// <c>key share</c> - read from a copy, so the caller's walk goes on where it was.
    /// </summary>
    private static bool LocksRows(SqlLexer after)
    {
        if (!after.TryRead(out SqlToken next) || next.Kind != SqlTokenKind.Word)
        {
            return false;
        }

        if (next.IsWord("update") || next.IsWord("share"))
        {
            return true;
        }

        if (next.IsWord("key"))
        {
            return after.TryRead(out SqlToken share) && share.IsWord("share");
        }

        return next.IsWord("no")
               && after.TryRead(out SqlToken key) && key.IsWord("key")
               && after.TryRead(out SqlToken update) && update.IsWord("update");
    }

    /// <summary>
    /// The generic plan, in <c>FORMAT JSON</c>, of a statement <c>pg_stat_statements</c> recorded - never <c>ANALYZE</c>
    /// - or <see langword="null" /> when <see cref="Refusal" /> refuses it: the text prepared under a name of the
    /// studio's own, explained under <c>plan_cache_mode = force_generic_plan</c>, and deallocated, in one batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not <c>EXPLAIN (GENERIC_PLAN)</c>.</b> That option keeps a statement's <c>$n</c> placeholders as unknowns
    /// only when the protocol brings no values for them, and Npgsql sends every command through the extended protocol:
    /// with nothing bound, <c>Bind</c> fails ("bind message supplies 0 parameters, but prepared statement requires 2"),
    /// and with anything bound - nulls included - the planner folds the bound values in as constants, so a filter on
    /// null plans to a one-time <c>false</c> with no scan at all. Both measured on PostgreSQL 17. A statement prepared and
    /// executed under <c>force_generic_plan</c> is planned with no values whatever <c>execute</c> passes, which is the
    /// same generic plan <c>GENERIC_PLAN</c> prints - and the setting exists from PostgreSQL 12, so the plan is read on
    /// every server the studio supports (measured on 15 and 17).
    /// </para>
    /// <para>
    /// <b>Read-only, and nothing left behind.</b> <c>prepare</c>, <c>set local</c>, <c>explain … execute</c> and
    /// <c>deallocate</c> are all allowed in the read-only transaction the runner opens, and planning takes the same
    /// <c>AccessShareLock</c> a read does. A prepared statement outlives its transaction - and Npgsql's reset of a pooled
    /// connection skips <c>DISCARD ALL</c> while the connection has statements of Npgsql's own prepared - so the batch
    /// ends by deallocating it, and when the batch stops before that the caller deallocates it on every way out
    /// (<see cref="Deallocate" />). The statement text never contains a <c>;</c> (<see cref="Refusal" />), so it is one
    /// statement of the batch.
    /// </para>
    /// <para>
    /// <b>One name, always.</b> The prepared statement is <see cref="PlanStatementName" /> and nothing else: the
    /// <c>explain … execute</c> a batch runs is recorded by <c>pg_stat_statements</c> like any statement, so a name of
    /// its own per plan would add an entry to the host's statistics on every plan, for ever. The sweep owns its
    /// connection and plans one statement at a time, so one name never meets itself; a name another batch left on the
    /// connection is the caller's to deallocate and retry (<c>42P05</c>). What one plan adds to the host's statistics is
    /// then one entry per number of placeholders, the same every time.
    /// </para>
    /// <para>
    /// <b>"Show SQL" does not print the statement.</b> It is another program's text, and "Show SQL" is drawn for every
    /// visitor the finding is shown to, without the mask the finding's own evidence goes through; it prints the batch's
    /// shape and where the text came from (<see cref="HealthQuery.Shown" />).
    /// </para>
    /// </remarks>
    /// <param name="queryId">The statement's <c>queryid</c>, for "Show SQL".</param>
    /// <param name="text">Its text, as <see cref="Texts" /> read it.</param>
    /// <param name="truncated">Whether that text was cut.</param>
    public static HealthQuery? Explain(long queryId, string text, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (Refusal(text, truncated) != StatementPlanRefusal.None)
        {
            return null;
        }

        int count = HighestParameter(text);
        string prepared = SqlIdentifier.Quote(PlanStatementName);
        string arguments = count == 0 ? string.Empty : "(" + string.Join(", ", Enumerable.Repeat("null", count)) + ")";

        // The ';' after the text is on a line of its own: a text that ends in a '--' comment would otherwise swallow it.
        string Batch(string statement) =>
            PlanPrefix + prepared + " as " + statement + "\n;\n"
            + "set local plan_cache_mode = force_generic_plan;\n"
            + "explain (format json) execute " + prepared + arguments + ";\n"
            + "deallocate " + prepared;

        return new HealthQuery(
            Batch(text),
            [],
            Batch(StatementShown + queryId.ToString(CultureInfo.InvariantCulture) + ", shown masked as the finding's Statement>"));
    }

    /// <summary>The name every plan's prepared statement has: the studio's own, and the same every time.</summary>
    public const string PlanStatementName = "marten_studio_plan";

    /// <summary>Deallocates the plan's prepared statement, for a batch that stopped before it did.</summary>
    public static HealthQuery Deallocate() => new("deallocate " + SqlIdentifier.Quote(PlanStatementName));

    /// <summary>What every plan batch begins with.</summary>
    internal const string PlanPrefix = "prepare ";

    /// <summary>What "Show SQL" prints in place of the statement a plan batch prepares.</summary>
    internal const string StatementShown = "<the text pg_stat_statements recorded for queryid ";

    /// <summary>Reads the plan: one row, one <c>json</c> column.</summary>
    public static string ReadPlan(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return reader.GetString(0);
    }

    /// <summary>The highest <c>$n</c> placeholder in <paramref name="text" />, or zero.</summary>
    internal static int HighestParameter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int highest = 0;

        foreach (Match match in Placeholder().Matches(text))
        {
            if (int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                highest = Math.Max(highest, n);
            }
            else
            {
                return int.MaxValue;
            }
        }

        return highest;
    }

    /// <summary>The store's own role's statements, in this database: the bound on every read here.</summary>
    private const string OwnStatements =
        "s.userid = (select r.oid from pg_catalog.pg_roles r where r.rolname = current_user)\n" +
        "     and s.dbid = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database())";

    /// <summary><see cref="OwnStatements" />'s bound over another row's user and database columns: Aurora's, read by name.</summary>
    private static string Own(string userid, string dbid) =>
        userid + " = (select r.oid from pg_catalog.pg_roles r where r.rolname = current_user)\n" +
        "     and " + dbid + " = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database())";

    /// <summary>
    /// A write that may be a guarded one: called often enough to judge, some calls changed no row, and the ones that
    /// did wrote WAL - which a <c>select</c> that sometimes finds nothing does not, so it does not crowd the writes out.
    /// </summary>
    private static string Guarded(string alias) =>
        alias + ".calls >= @guarded_calls and " + alias + ".rows < " + alias + ".calls and "
        + alias + ".wal_records >= " + alias + ".rows and " + alias + ".wal_records > 0";

    private static NpgsqlParameter Integer(string name, int value) => new(name, NpgsqlDbType.Integer) { Value = value };

    [GeneratedRegex(@"\$([0-9]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Placeholder();
}
