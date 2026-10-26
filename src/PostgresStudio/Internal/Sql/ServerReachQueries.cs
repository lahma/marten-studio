using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The statement behind "not checked: this needs X" (issue #8 A7): who the studio reads as, whether that role is a
/// superuser, and which of the monitoring and provider roles it is a member of.
/// </summary>
/// <remarks>
/// <para>
/// <b>One catalog read, as the reading role.</b> It runs where every health read runs - in the read-only transaction,
/// under <c>SET LOCAL ROLE</c> to <c>SqlConsoleRole</c> when one is set (D29) - so <c>current_user</c> is the role the
/// checks read as, and <c>pg_has_role</c> answers for it, membership through other roles included. <c>pg_roles</c> is
/// readable by every role and names no password.
/// </para>
/// <para>
/// <b>A role that does not exist is "absent", never an error.</b> The names are joined to <c>pg_roles</c> by name
/// (<c>left join</c>): <c>pg_maintain</c> on 16 and the provider roles on a server of somebody's own simply have no
/// row, where <c>pg_has_role(current_user, 'pg_maintain', ...)</c> would raise <c>42704</c>. Every name is a
/// parameter; nothing here names a relation.
/// </para>
/// </remarks>
internal static class ServerReachQueries
{
    /// <summary>The <c>context.Shared</c> key of the grant read: read once per sweep, whichever check asks first.</summary>
    public const string GrantsKey = "server.grants";

    /// <summary>
    /// The roles the read asks about: the monitoring roles a check may need, and the admin roles of the providers whose
    /// markers <c>PostgresPlatform</c> reads (B0 §5).
    /// </summary>
    public static IReadOnlyList<string> Roles { get; } =
    [
        "pg_read_all_stats", "pg_monitor", "pg_stat_scan_tables", "pg_read_all_settings", "pg_signal_backend", "pg_maintain",
        "rds_superuser", "azure_pg_admin", "cloudsqlsuperuser", "neon_superuser",
    ];

    private const string GrantsSql =
        "select current_user::text as reads_as,\n" +
        "       coalesce((select r.rolsuper from pg_catalog.pg_roles r where r.rolname = current_user), false) as superuser,\n" +
        "       g.name as role_name,\n" +
        "       (r.oid is not null) as role_exists,\n" +
        "       coalesce(pg_catalog.pg_has_role(current_user, r.oid, 'MEMBER'), false) as member\n" +
        "  from pg_catalog.unnest(@roles) with ordinality as g(name, ord)\n" +
        "  left join pg_catalog.pg_roles r on r.rolname = g.name\n" +
        " order by g.ord";

    /// <summary>
    /// One row per role in <see cref="Roles" />, in that order: the reading role, whether it is a superuser, the role's
    /// name, whether it exists on this server and whether the reading role is a member of it.
    /// </summary>
    public static HealthQuery Grants() => new(
        GrantsSql,
        [new NpgsqlParameter("roles", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = Roles.ToArray() }]);
}
