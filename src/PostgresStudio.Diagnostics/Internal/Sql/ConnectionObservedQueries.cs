using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// What the server saw of the studio's own connection (NP-DESIGN §4), for the Connection page and the connection checks:
/// one catalog statement, and one for the database's session statistics - reads of <c>pg_settings</c>,
/// <c>pg_stat_ssl</c>, <c>pg_stat_gssapi</c> and <c>pg_stat_database</c>, nothing that opens what it lists.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>reset_val</c>, not <c>setting</c> and not <c>current_setting</c>.</b> The studio's read-only transaction sets
/// <c>statement_timeout</c>, <c>lock_timeout</c> and <c>idle_in_transaction_session_timeout</c> for itself and pins
/// <c>search_path</c> and <c>DateStyle</c> (<c>HealthSessionQueries.PinSessionSql</c>), so the live value of any of them is the
/// studio's. <c>reset_val</c> is the value a new session of this login in this database starts with: the connection's
/// start-up options over a role-and-database, role, database or global entry over the server's configuration - the value the
/// application's own connections get. The settings are read from <c>pg_settings</c> by name, as
/// <see cref="ServerPostureQueries" /> reads them, never by <c>current_setting</c>, which would also answer for a placeholder a
/// session made.
/// </para>
/// <para>
/// <b>Nothing a visitor shapes reaches the statement as text.</b> The setting names are the statement's own and are
/// parameters; the option names the connection string lists are a parameter too and are compared to <c>pg_settings</c> by
/// equality, so the statement only learns which of them exist.
/// </para>
/// <para>
/// <b>PostgreSQL 12 and later</b> (<c>pg_stat_gssapi</c>). The session statistics' columns (<c>sessions</c>, <c>session_time</c>)
/// are PostgreSQL 14's, read by name out of <c>to_jsonb(row)</c> so that an older server answers null rather than failing the
/// statement, as <see cref="AuroraQueries" /> reads what a release may not have.
/// </para>
/// </remarks>
internal static class ConnectionObservedQueries
{
    /// <summary>The <c>context.Shared</c> key of the connection facts read, which the Connection page and C16 to C18, S11 to S13 share.</summary>
    public const string FactsKey = "connection.facts";

    /// <summary>The most option names one read asks about.</summary>
    public const int MaxOptionNames = 50;

    /// <summary>The settings the statement reads, by their names in <c>pg_settings</c>.</summary>
    public static IReadOnlyList<string> SettingNames { get; } =
    [
        "TimeZone", "client_encoding", "search_path", "application_name", "idle_session_timeout", "statement_timeout",
        "idle_in_transaction_session_timeout", "transaction_timeout", "password_encryption",
    ];

    private const string ObservedSql =
        "with s as (\n" +
        "  select p.name, p.reset_val from pg_catalog.pg_settings p where p.name = any(@names)\n" +
        "),\n" +
        "ssl as (\n" +
        "  select t.ssl, t.version, t.cipher, t.bits from pg_catalog.pg_stat_ssl t where t.pid = pg_catalog.pg_backend_pid()\n" +
        "),\n" +
        "gss as (\n" +
        "  select g.gss_authenticated, g.encrypted from pg_catalog.pg_stat_gssapi g where g.pid = pg_catalog.pg_backend_pid()\n" +
        ")\n" +
        "select current_user::text as current_user_name,\n" +
        "       session_user::text as session_user_name,\n" +
        "       pg_catalog.current_database()::text as database_name,\n" +
        "       pg_catalog.host(pg_catalog.inet_server_addr()) as server_address,\n" +
        "       pg_catalog.inet_server_port() as server_port,\n" +
        "       pg_catalog.pg_is_in_recovery() as in_recovery,\n" +
        "       (select s.reset_val from s where s.name = 'TimeZone') as time_zone,\n" +
        "       (select s.reset_val from s where s.name = 'client_encoding') as client_encoding,\n" +
        "       (select s.reset_val from s where s.name = 'search_path') as search_path,\n" +
        "       (select s.reset_val from s where s.name = 'application_name') as application_name,\n" +
        "       (select s.reset_val::bigint from s where s.name = 'idle_session_timeout') as idle_session_timeout_ms,\n" +
        "       (select s.reset_val::bigint from s where s.name = 'statement_timeout') as statement_timeout_ms,\n" +
        "       (select s.reset_val::bigint from s where s.name = 'idle_in_transaction_session_timeout') as idle_in_transaction_timeout_ms,\n" +
        "       (select s.reset_val::bigint from s where s.name = 'transaction_timeout') as transaction_timeout_ms,\n" +
        "       (select s.reset_val from s where s.name = 'password_encryption') as password_encryption,\n" +
        "       (select t.ssl from ssl t) as ssl,\n" +
        "       (select t.version from ssl t) as ssl_version,\n" +
        "       (select t.cipher from ssl t) as ssl_cipher,\n" +
        "       (select t.bits from ssl t) as ssl_bits,\n" +
        "       (select g.gss_authenticated from gss g) as gss_authenticated,\n" +
        "       (select g.encrypted from gss g) as gss_encrypted,\n" +
        "       coalesce((select pg_catalog.array_agg(lower(o.name) order by o.name) from pg_catalog.pg_settings o where lower(o.name) = any(@options)), '{}'::text[]) as known_options";

    private const string SessionsSql =
        "select (pg_catalog.to_jsonb(d) ->> 'sessions')::bigint as sessions,\n" +
        "       (pg_catalog.to_jsonb(d) ->> 'session_time')::double precision as session_time_ms,\n" +
        "       d.stats_reset\n" +
        "  from pg_catalog.pg_stat_database d\n" +
        " where d.datname = pg_catalog.current_database()";

    /// <summary>
    /// The one statement of NP-DESIGN §4: who the server thinks the studio's connection is, the settings a new session
    /// starts with, the connection's TLS and GSS state, and which of <paramref name="optionNames" /> exist as settings.
    /// </summary>
    /// <param name="optionNames">Lower-case names from a connection string's <c>Options</c>, at most <see cref="MaxOptionNames" />.</param>
    public static HealthQuery Observed(IReadOnlyCollection<string> optionNames)
    {
        ArgumentNullException.ThrowIfNull(optionNames);

        return new HealthQuery(ObservedSql, [Texts("names", SettingNames), Texts("options", [.. optionNames.Take(MaxOptionNames)])]);
    }

    /// <summary>This database's session count and total session time since its statistics were reset (PostgreSQL 14 and later; null before).</summary>
    public static HealthQuery Sessions() => new(SessionsSql);

    private static NpgsqlParameter Texts(string name, IReadOnlyCollection<string> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };
}
