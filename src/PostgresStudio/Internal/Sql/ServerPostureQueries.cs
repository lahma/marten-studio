using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The Server page's statements and the server-posture checks' (W5-S2: S04-S10, K08): the curated <c>pg_settings</c> read,
/// the extensions, how the data source's own connection reaches the server, and who may create in the source's schemas -
/// catalog reads, nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Catalog and shared memory only, and nothing that opens what it lists.</b> <c>pg_settings</c> reads the session's own
/// settings; <c>pg_db_role_setting</c>, <c>pg_roles</c>, <c>pg_namespace</c> and <c>pg_extension</c> are catalogs read under
/// the catalog's own lock; <c>pg_available_extensions</c> and <c>pg_available_extension_versions</c> read the extension
/// control files on the server's disk; <c>pg_stat_ssl</c> reads the backend array. No size function, no deparse function and
/// no per-object function over a list (D27): the matrix runs each read behind an <c>ACCESS EXCLUSIVE</c> lock under a 300 ms
/// <c>lock_timeout</c> on 14 to 19 (<c>MatrixReads</c>).
/// </para>
/// <para>
/// <b>Markers and settings from <c>pg_settings</c>, never <c>current_setting</c></b> (B0 §5): a session can give itself a
/// placeholder that <c>current_setting</c> reports and <c>pg_settings</c> does not.
/// </para>
/// <para>
/// <b>The studio's own transaction is not the store's.</b> Every health read runs in <c>ReadOnlySqlSession</c>'s
/// transaction, which sets <c>statement_timeout</c>, <c>lock_timeout</c> and <c>idle_in_transaction_session_timeout</c>
/// with <c>SET LOCAL</c> (<see cref="StudioSetSettings" />) - so <c>pg_settings.setting</c> for those three is the studio's
/// value, and its <c>source</c> is <c>session</c>. For them the read takes <c>reset_val</c> instead, the value every new
/// session of the data source starts with, and the role-and-database, role, database or global entry in
/// <c>pg_db_role_setting</c> for the session's login (<c>session_user</c>, which <c>SET ROLE</c> does not change) that
/// Postgres would apply, with that entry's own value. Whose value <c>reset_val</c> is, is then the caller's to decide
/// (<c>ServerSettingSource</c>): the connection's startup options outrank every such entry, and an entry whose value is not
/// <c>reset_val</c>, with no startup option the connection string names to explain it, leaves the source not known.
/// </para>
/// <para>
/// <b>Every value is a parameter.</b> A setting's or a role's name written as a literal is structure, as
/// <see cref="HealthServerQueries" /> writes them; nothing here names a relation of the source's.
/// </para>
/// </remarks>
internal static class ServerPostureQueries
{
    /// <summary>The <c>context.Shared</c> key of the curated settings read, which the page, S09, S10 and K08 share.</summary>
    public const string SettingsKey = "server.curated-settings";

    /// <summary>The <c>context.Shared</c> key of the connection posture read, which the page, S04, S05, S08 and S10 share.</summary>
    public const string PostureKey = "server.posture";

    /// <summary>The <c>context.Shared</c> key of the extensions read, which the page and S07 share.</summary>
    public const string ExtensionsKey = "server.extensions";

    /// <summary>The most extensions one read lists; a database with more is cut, and the count says so.</summary>
    public const int MaxExtensions = 200;

    /// <summary>
    /// The settings the studio's read-only transaction sets for itself (<c>ReadOnlySqlSession</c>), whose
    /// <c>pg_settings.setting</c> inside it is the studio's value and not the store's.
    /// </summary>
    public static IReadOnlyList<string> StudioSetSettings { get; } = ["idle_in_transaction_session_timeout", "lock_timeout", "statement_timeout"];

    /// <summary>
    /// The roles whose membership the posture read asks about, for the login and for the reading role: the managed
    /// platforms' admin roles (B0 §5) and the three predefined roles that reach the server's files and programs.
    /// </summary>
    public static IReadOnlyList<string> PostureRoles { get; } =
    [
        "rds_superuser", "azure_pg_admin", "cloudsqlsuperuser", "neon_superuser",
        "pg_read_server_files", "pg_write_server_files", "pg_execute_server_program",
    ];

    /// <summary>The predefined roles that read or write the server's files or run programs on it, as <c>COPY</c> does.</summary>
    public static IReadOnlyList<string> FileAccessRoles { get; } = ["pg_read_server_files", "pg_write_server_files", "pg_execute_server_program"];

    private const string SettingsSql =
        "select s.name,\n" +
        "       case when f.studio_set then s.reset_val else s.setting end as setting,\n" +
        "       s.unit,\n" +
        "       case when f.studio_set then r.entry_source else s.source end as source,\n" +
        "       s.pending_restart,\n" +
        "       s.context,\n" +
        "       s.boot_val,\n" +
        "       f.studio_set,\n" +
        "       r.entry_value\n" +
        "  from pg_catalog.pg_settings s\n" +
        " cross join lateral (select (s.source = 'session' and s.name = any(@studio_set)) as studio_set) f\n" +
        "  left join lateral (\n" +
        "       select case when d.setdatabase <> 0 and d.setrole <> 0 then 'database user'\n" +
        "                   when d.setrole <> 0 then 'user'\n" +
        "                   when d.setdatabase <> 0 then 'database'\n" +
        "                   else 'global' end as entry_source,\n" +
        "              pg_catalog.substr(c.item, pg_catalog.strpos(c.item, '=') + 1) as entry_value\n" +
        "         from pg_catalog.pg_db_role_setting d\n" +
        "         cross join lateral pg_catalog.unnest(d.setconfig) c(item)\n" +
        "        where f.studio_set\n" +
        "          and d.setdatabase in (0, (select b.oid from pg_catalog.pg_database b where b.datname = pg_catalog.current_database()))\n" +
        "          and d.setrole in (0, (select a.oid from pg_catalog.pg_roles a where a.rolname = session_user))\n" +
        "          and pg_catalog.split_part(c.item, '=', 1) = s.name\n" +
        "        order by (d.setdatabase <> 0 and d.setrole <> 0) desc, (d.setrole <> 0) desc, (d.setdatabase <> 0) desc\n" +
        "        limit 1) r on true\n" +
        " where s.name = any(@names) or s.name like any(@prefixes)\n" +
        " order by s.name";

    private const string PostureSql =
        "with roles as (\n" +
        "  select g.name, r.oid\n" +
        "    from pg_catalog.unnest(@roles) as g(name)\n" +
        "    join pg_catalog.pg_roles r on r.rolname = g.name\n" +
        "),\n" +
        "ssl as (\n" +
        "  select s.ssl, s.version from pg_catalog.pg_stat_ssl s where s.pid = pg_catalog.pg_backend_pid()\n" +
        ")\n" +
        "select session_user::text as login_role,\n" +
        "       coalesce((select a.rolsuper from pg_catalog.pg_roles a where a.rolname = session_user), false) as login_superuser,\n" +
        "       coalesce((select pg_catalog.array_agg(x.name order by x.name) from roles x\n" +
        "                  where pg_catalog.pg_has_role(session_user, x.oid, 'MEMBER')), '{}'::text[]) as login_member_of,\n" +
        "       current_user::text as reading_role,\n" +
        "       coalesce((select a.rolsuper from pg_catalog.pg_roles a where a.rolname = current_user), false) as reading_superuser,\n" +
        "       coalesce((select pg_catalog.array_agg(x.name order by x.name) from roles x\n" +
        "                  where pg_catalog.pg_has_role(current_user, x.oid, 'MEMBER')), '{}'::text[]) as reading_member_of,\n" +
        "       (select s.ssl from ssl s) as ssl,\n" +
        "       (select s.version from ssl s) as ssl_version,\n" +
        "       pg_catalog.host(pg_catalog.inet_client_addr()) as client_address,\n" +
        "       (select case when a.rolvaliduntil = 'infinity'::timestamptz then null else a.rolvaliduntil end\n" +
        "          from pg_catalog.pg_roles a where a.rolname = session_user) as valid_until,\n" +
        "       pg_catalog.pg_is_in_recovery() as in_recovery,\n" +
        "       pg_catalog.pg_postmaster_start_time() as started,\n" +
        "       (select s.setting from pg_catalog.pg_settings s where s.name = 'data_checksums') as data_checksums,\n" +
        "       exists (select 1 from pg_catalog.pg_settings s where s.name = 'pg_stat_statements.max') as statements_loaded";

    private const string ExtensionsSql =
        "select n.name,\n" +
        "       (n.name = any(@named)) as named,\n" +
        "       e.extversion::text as installed_version,\n" +
        "       a.default_version::text as default_version,\n" +
        "       pg_catalog.count(*) over () as total\n" +
        "  from (select e.extname::text as name from pg_catalog.pg_extension e\n" +
        "        union\n" +
        "        select g.name from pg_catalog.unnest(@named) as g(name)) n\n" +
        "  left join pg_catalog.pg_extension e on e.extname = n.name\n" +
        "  left join pg_catalog.pg_available_extensions a on a.name = n.name\n" +
        " order by (n.name = any(@named)) desc, n.name\n" +
        " limit @limit";

    private const string ExtensionTrustSql =
        "select v.name::text, pg_catalog.bool_or(v.trusted) as trusted\n" +
        "  from pg_catalog.pg_available_extension_versions v\n" +
        "  join pg_catalog.pg_available_extensions a on a.name = v.name and a.default_version = v.version\n" +
        " where v.name = any(@names)\n" +
        " group by v.name";

    private const string UpdatePathsSql =
        "select c.name,\n" +
        "       exists (select 1 from pg_catalog.pg_extension_update_paths(c.name::name) p\n" +
        "                where p.source = c.installed and p.target = c.target and p.path is not null) as updatable\n" +
        "  from rows from (pg_catalog.unnest(@names), pg_catalog.unnest(@installed), pg_catalog.unnest(@targets)) as c(name, installed, target)";

    private const string SchemaCreateSql =
        "select n.nspname::text as schema,\n" +
        "       exists (select 1 from pg_catalog.aclexplode(coalesce(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a\n" +
        "                where a.grantee = 0 and a.privilege_type = 'CREATE') as public_create\n" +
        "  from pg_catalog.pg_namespace n\n" +
        " where n.nspname = any(@schemas)\n" +
        " order by n.nspname";

    /// <summary>
    /// The curated settings (<c>ServerSettingsCatalog</c>): the named ones and every member of the named families, each with
    /// its value and unit as the store's sessions run with them, who set it, whether a change waits for a restart, its
    /// <c>context</c> (for a remedy's restart note) and its built-in value (S09 judges an <c>enable_*</c> switched off only
    /// where its default is on). A setting the reading role is not shown, or that this release does not have, is simply not
    /// a row.
    /// </summary>
    /// <param name="names">The named settings.</param>
    /// <param name="prefixes">The families, as <c>LIKE</c> patterns (<c>autovacuum%</c>, <c>enable\_%</c>).</param>
    public static HealthQuery Settings(IReadOnlyCollection<string> names, IReadOnlyCollection<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(prefixes);

        return new HealthQuery(
            SettingsSql,
            [
                Texts("names", names),
                Texts("prefixes", prefixes),
                Texts("studio_set", StudioSetSettings),
            ]);
    }

    /// <summary>
    /// One row about the connection the read runs on - which is a connection of the source's own data source, so it is what
    /// the application's connections are: the login (<c>session_user</c>, before any <c>SET ROLE</c>), whether it is a
    /// superuser and which admin and file-access roles it is a member of; the same for the role the read runs as
    /// (<c>current_user</c>, <c>SqlConsoleRole</c> when one is set); whether the backend's connection is encrypted and over
    /// which address it came (a Unix-domain socket has none); when the login's password expires; whether the server is a
    /// standby; when the server started; whether it checks page checksums; and whether <c>pg_stat_statements</c>' library was
    /// loaded when the server started.
    /// </summary>
    /// <remarks>
    /// <b>Loaded, told without calling it.</b> <c>pg_stat_statements</c> defines its <c>pg_stat_statements.*</c> settings in
    /// <c>_PG_init</c> only while <c>shared_preload_libraries</c> is being processed - the one case in which its functions do
    /// not fail with <c>55000</c>, which is how <c>StatementReads</c> tells the same state - so the setting's row is the
    /// library loaded at start, read by any role, naming no schema and no extension function. A placeholder cannot fake it: a
    /// session's own <c>SET</c> of an unknown setting is never a <c>pg_settings</c> row (B0 §5).
    /// </remarks>
    public static HealthQuery Posture() => new(PostureSql, [Texts("roles", PostureRoles)]);

    /// <summary>
    /// Every extension installed in this database and every one of <paramref name="named" /> whether or not it is, with the
    /// installed version and the server's default version - the named ones first, then by name, at most
    /// <see cref="MaxExtensions" />. Not whether a version is trusted: that is <c>pg_available_extension_versions</c>, which
    /// reads every version's control and script files, and the sweep does not pay for it (<see cref="ExtensionTrust" />).
    /// </summary>
    /// <param name="named">The extensions the page shows a chip for.</param>
    public static HealthQuery Extensions(IReadOnlyCollection<string> named)
    {
        ArgumentNullException.ThrowIfNull(named);

        return new HealthQuery(
            ExtensionsSql,
            [
                Texts("named", named),
                new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = MaxExtensions },
            ]);
    }

    /// <summary>
    /// Whether each of <paramref name="names" />' default version is trusted - creatable by a role with <c>CREATE</c> on the
    /// database. The Server page's alone, for a chip it cannot word from what the studio knows, never the sweep's: the view
    /// reads every version's control and script files.
    /// </summary>
    /// <param name="names">The extensions to ask about.</param>
    public static HealthQuery ExtensionTrust(IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return new HealthQuery(ExtensionTrustSql, [Texts("names", names)]);
    }

    /// <summary>
    /// For each extension, whether the server has an update path from its installed version to its default one
    /// (<c>pg_extension_update_paths</c>) - asked only for the few whose versions differ (S07), never for the list.
    /// </summary>
    /// <param name="candidates">Each extension, its installed version and the server's default.</param>
    public static HealthQuery UpdatePaths(IReadOnlyCollection<(string Name, string Installed, string Target)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return new HealthQuery(
            UpdatePathsSql,
            [
                Texts("names", [.. candidates.Select(static x => x.Name)]),
                Texts("installed", [.. candidates.Select(static x => x.Installed)]),
                Texts("targets", [.. candidates.Select(static x => x.Target)]),
            ]);
    }

    /// <summary>
    /// For each of <paramref name="schemas" /> that exists, whether <c>PUBLIC</c> - every role - holds <c>CREATE</c> on it,
    /// read from its ACL as Postgres applies it (a null ACL is the owner's default, which grants <c>PUBLIC</c> nothing). S06.
    /// </summary>
    /// <param name="schemas">The source's own schemas.</param>
    public static HealthQuery SchemaCreate(IReadOnlyCollection<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        return new HealthQuery(SchemaCreateSql, [Texts("schemas", schemas)]);
    }

    private static NpgsqlParameter Texts(string name, IReadOnlyCollection<string> values) =>
        new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };
}
