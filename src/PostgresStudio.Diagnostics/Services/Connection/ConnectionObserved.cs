using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Connection;

/// <summary>
/// What the server saw of the studio's own connection (NP-DESIGN §4), read in one statement plus the parameters Npgsql
/// already holds for an open connection.
/// </summary>
/// <remarks>
/// <para>
/// <b>The studio's connection, not the application's.</b> It has its own <c>application_name</c> (<see cref="ApplicationName" />),
/// but the same data source, pool, role and database as the application's connections, so a setting made at role or
/// database level (<c>ALTER ROLE … SET</c>, <c>ALTER DATABASE … SET</c>) is included, and one the application sets for
/// itself with <c>SET</c> is not.
/// </para>
/// <para>
/// <b>Every setting is its <c>reset_val</c></b> - the value a new session of this role in this database starts with - and
/// never its live value: the studio's read-only transaction sets <c>statement_timeout</c>, <c>lock_timeout</c> and
/// <c>idle_in_transaction_session_timeout</c> for itself, and pins <c>search_path</c> and <c>DateStyle</c>
/// (<c>HealthSessionQueries.PinSessionSql</c>), so <c>current_setting</c> of any of them would report the studio's value
/// as the application's. The role is the login (<see cref="SessionUser" />), not the role a read runs as
/// (<see cref="CurrentUser" />, <c>SqlConsoleRole</c>).
/// </para>
/// </remarks>
/// <param name="CurrentUser"><c>current_user</c>: the role the read ran as (<c>SqlConsoleRole</c> when one is set).</param>
/// <param name="SessionUser"><c>session_user</c>: the login, which the application's connections are too.</param>
/// <param name="Database"><c>current_database()</c>.</param>
/// <param name="ServerAddress"><c>inet_server_addr()</c>, or <see langword="null" /> over a Unix-domain socket.</param>
/// <param name="ServerPort"><c>inet_server_port()</c>, or <see langword="null" /> over a Unix-domain socket.</param>
/// <param name="InRecovery"><c>pg_is_in_recovery()</c>: a standby answered.</param>
/// <param name="TimeZone">The session's start-up <c>TimeZone</c>.</param>
/// <param name="ClientEncoding">The start-up <c>client_encoding</c>.</param>
/// <param name="SearchPath">The start-up <c>search_path</c>.</param>
/// <param name="ApplicationName">The studio's own connection's <c>application_name</c>.</param>
/// <param name="IdleSessionTimeoutMs"><c>idle_session_timeout</c> in milliseconds (0 is off), or <see langword="null" /> before PostgreSQL 14.</param>
/// <param name="StatementTimeoutMs"><c>statement_timeout</c> a new session starts with, in milliseconds (0 is off).</param>
/// <param name="IdleInTransactionTimeoutMs"><c>idle_in_transaction_session_timeout</c> a new session starts with, in milliseconds (0 is off).</param>
/// <param name="TransactionTimeoutMs"><c>transaction_timeout</c> in milliseconds (0 is off), or <see langword="null" /> before PostgreSQL 17.</param>
/// <param name="PasswordEncryption"><c>password_encryption</c>: <c>scram-sha-256</c> or <c>md5</c>.</param>
/// <param name="Ssl"><c>pg_stat_ssl.ssl</c> for this backend, or <see langword="null" /> without a row.</param>
/// <param name="SslVersion">The TLS version, when <paramref name="Ssl" />.</param>
/// <param name="SslCipher">The TLS cipher, when <paramref name="Ssl" />.</param>
/// <param name="SslBits">The cipher's key bits, when <paramref name="Ssl" />.</param>
/// <param name="GssAuthenticated"><c>pg_stat_gssapi.gss_authenticated</c>, or <see langword="null" /> without a row.</param>
/// <param name="GssEncrypted"><c>pg_stat_gssapi.encrypted</c>, or <see langword="null" /> without a row.</param>
/// <param name="KnownOptionNames">Which of the names a connection string's <c>Options</c> set exist in <c>pg_settings</c>.</param>
internal sealed record ConnectionObserved(
    string CurrentUser,
    string SessionUser,
    string Database,
    string? ServerAddress,
    int? ServerPort,
    bool InRecovery,
    string? TimeZone,
    string? ClientEncoding,
    string? SearchPath,
    string? ApplicationName,
    long? IdleSessionTimeoutMs,
    long? StatementTimeoutMs,
    long? IdleInTransactionTimeoutMs,
    long? TransactionTimeoutMs,
    string? PasswordEncryption,
    bool? Ssl,
    string? SslVersion,
    string? SslCipher,
    int? SslBits,
    bool? GssAuthenticated,
    bool? GssEncrypted,
    IReadOnlySet<string> KnownOptionNames)
{
    /// <summary>
    /// The parameters the server reported at start-up, which Npgsql holds for the open connection
    /// (<c>NpgsqlConnection.PostgresParameters</c>, no query): <c>server_version</c>, <c>server_encoding</c>,
    /// <c>is_superuser</c>, <c>session_authorization</c>, <c>DateStyle</c> and the like.
    /// </summary>
    public IReadOnlyDictionary<string, string> ReportedParameters { get; init; } = new Dictionary<string, string>(0);

    /// <summary>
    /// <c>inet_client_addr()</c> for the studio's connection: the address the server sees the connection come from, or
    /// <see langword="null" /> over a Unix-domain socket. It is only ever compared with loopback (the Connection page's TLS fact: a
    /// loopback host whose peer is not loopback is a container or NAT in between) and is never shown.
    /// </summary>
    public string? ClientAddress { get; init; }

    /// <summary>
    /// Whether the server sees the connection come over a socket or from the loopback interface, so nothing leaves the machine. It is
    /// <see cref="ServerConnectionPosture.KindOf" />'s rule (the Server page's tile and S05 read the same one), not a second copy of it.
    /// </summary>
    public bool PeerIsLocal => ServerConnectionPosture.KindOf(ClientAddress) != ServerConnectionKind.Network;

    /// <summary>
    /// The server's own idle cut for a session, in whole seconds (rounded down, but at least 1: a cut of under a second is still a cut,
    /// and zero means off), or <see langword="null" /> when it applies none.
    /// </summary>
    public int? IdleSessionTimeoutSeconds => IdleSessionTimeoutMs is > 0 ? (int) Math.Max(1, Math.Min(IdleSessionTimeoutMs.Value / 1000, int.MaxValue)) : null;
}
