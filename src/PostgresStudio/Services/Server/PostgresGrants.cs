using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Server;

/// <summary>
/// A privilege a read needs and the reading role may lack: a predefined role, or <c>SELECT</c> on one column.
/// </summary>
/// <remarks>
/// A grant is only ever named in words, in a "not checked" sentence (<see cref="PostgresGrants.NotChecked" />), never as
/// a statement to paste: <see cref="HealthRemedy" /> refuses <c>GRANT</c> and <c>REVOKE</c> in every kind of remedy.
/// </remarks>
internal sealed record PostgresGrant
{
    private PostgresGrant(string privilege, string? includedIn, string? relation)
    {
        Privilege = privilege;
        IncludedIn = includedIn;
        Relation = relation;
    }

    /// <summary>
    /// <c>pg_read_all_stats</c>: other roles' sessions in <c>pg_stat_activity</c>, lag in <c>pg_stat_replication</c>,
    /// autovacuum's progress, <c>pg_aios</c> (B0 §4, §7c). <c>pg_monitor</c> includes it.
    /// </summary>
    public static PostgresGrant ReadAllStats { get; } = new("pg_read_all_stats", "pg_monitor", null);

    /// <summary><c>pg_stat_scan_tables</c>: <c>pgstattuple</c> and <c>pgstatindex</c> (B0 §7c). <c>pg_monitor</c> includes it.</summary>
    public static PostgresGrant StatScanTables { get; } = new("pg_stat_scan_tables", "pg_monitor", null);

    /// <summary><c>pg_read_all_settings</c>: every setting, those only a superuser reads included. <c>pg_monitor</c> includes it.</summary>
    public static PostgresGrant ReadAllSettings { get; } = new("pg_read_all_settings", "pg_monitor", null);

    /// <summary><c>pg_monitor</c>: the three above together.</summary>
    public static PostgresGrant Monitor { get; } = new("pg_monitor", null, null);

    /// <summary><c>pg_signal_backend</c>: cancelling or ending another role's session - which the studio never does.</summary>
    public static PostgresGrant SignalBackend { get; } = new("pg_signal_backend", null, null);

    /// <summary><c>pg_maintain</c> (PostgreSQL 17+): <c>VACUUM</c>, <c>ANALYZE</c> and <c>REINDEX</c> on every relation.</summary>
    public static PostgresGrant Maintain { get; } = new("pg_maintain", null, null);

    /// <summary>
    /// Every predefined role <see cref="PostgresGrants" /> names, for the unit tests that pin each one's sentence on every
    /// platform.
    /// </summary>
    public static IReadOnlyList<PostgresGrant> Roles { get; } = [ReadAllStats, StatScanTables, ReadAllSettings, Monitor, SignalBackend, Maintain];

    /// <summary>
    /// <c>SELECT</c> on one column of one relation - what <c>pg_stats</c> needs to show that column's most common values,
    /// which neither <c>pg_read_all_stats</c> nor <c>pg_monitor</c> gives (B0 §8c).
    /// </summary>
    /// <param name="relation">The relation, qualified as the evidence names it (<c>store.docs</c>).</param>
    /// <param name="column">The column.</param>
    public static PostgresGrant ColumnSelect(string relation, string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        return new PostgresGrant("SELECT on column " + column + " of " + relation, null, relation);
    }

    /// <summary>The privilege, in Postgres' own words: a role's name, or "SELECT on column c of t".</summary>
    public string Privilege { get; }

    /// <summary>The predefined role that includes it, or <see langword="null" />.</summary>
    public string? IncludedIn { get; }

    /// <summary>The relation an object privilege is on, or <see langword="null" /> for a predefined role.</summary>
    public string? Relation { get; }

    /// <summary>Whether it is membership of a predefined role, which a platform's administrator grants.</summary>
    public bool IsRole => Relation is null;

    /// <summary>The grant as a "this needs ..." sentence names it: "pg_read_all_stats (pg_monitor includes it)".</summary>
    public string Described => IncludedIn is { } umbrella ? Privilege + " (" + umbrella + " includes it)" : Privilege;
}

/// <summary>Whether the reading role is a member of one role, and whether that role exists on this server.</summary>
/// <param name="Name">The role.</param>
/// <param name="Exists">Whether the server has it - <c>pg_maintain</c> below 17, a provider's role elsewhere, do not.</param>
/// <param name="Member">Whether the reading role is a member of it, directly or through another role.</param>
internal sealed record PostgresRoleMembership(string Name, bool Exists, bool Member);

/// <summary>
/// What the reading role may read beyond its own (issue #8 A7's mechanism): who it is, whether it is a superuser, and
/// which monitoring and provider roles it is a member of - read once per sweep, and the sentences every packet words a
/// "not checked" row with.
/// </summary>
/// <remarks>
/// <para>
/// <b>As the role the reads run as.</b> <see cref="SharedAsync" /> runs <see cref="ServerReachQueries.Grants" /> through
/// <see cref="HealthCheckContext.Sql" />, which is <c>SqlConsoleRole</c> when the host set one (D29), and memoises it for
/// the sweep: the first check that needs to know asks, every other gets the same answer.
/// </para>
/// <para>
/// <b>"Not checked" is a sentence that names the grant</b> (W5 §2.6): the privilege, the role the reads run as, and
/// who can grant it on the platform the server runs on - <see cref="NotChecked" />, and
/// <see cref="ExtensionNotInstalled" /> for an extension. Who can grant a predefined role, from each provider's own page
/// (read by B0 §5, 2026-10-20):
/// </para>
/// <list type="bullet">
/// <item>self-hosted, a superuser;</item>
/// <item>Amazon RDS and Aurora, the master user, a member of <c>rds_superuser</c> - <i>Understanding the rds_superuser
/// role</i>, https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/Appendix.PostgreSQL.CommonDBATasks.Roles.rds_superuser.html;</item>
/// <item>Azure Database for PostgreSQL, the server admin, a member of <c>azure_pg_admin</c> -
/// https://learn.microsoft.com/en-us/azure/postgresql/security/security-manage-database-users;</item>
/// <item>Cloud SQL, a member of <c>cloudsqlsuperuser</c> such as the <c>postgres</c> user -
/// https://docs.cloud.google.com/sql/docs/postgres/users;</item>
/// <item>Neon, a member of <c>neon_superuser</c> - https://neon.com/docs/manage/roles;</item>
/// <item>Supabase, the <c>postgres</c> role, which "has admin privileges" - https://supabase.com/docs/guides/database/postgres/roles.</item>
/// </list>
/// <para>
/// A column's <c>SELECT</c> is the table owner's to grant on every platform.
/// </para>
/// </remarks>
/// <param name="ReadsAs">The role the reads run as: <c>current_user</c> inside the read's transaction.</param>
/// <param name="Superuser">Whether that role is a superuser - which no provider above hands out (B0 §5).</param>
/// <param name="Roles">Each role <see cref="ServerReachQueries.Roles" /> asks about, in that order.</param>
internal sealed record PostgresGrants(string ReadsAs, bool Superuser, IReadOnlyList<PostgresRoleMembership> Roles)
{
    /// <summary>The admin roles of the managed platforms: membership is "provider admin", never a superuser (B0 §5).</summary>
    public static IReadOnlyList<string> ProviderAdminRoles { get; } = ["rds_superuser", "azure_pg_admin", "cloudsqlsuperuser", "neon_superuser"];

    /// <summary>
    /// The extensions a check may need whose <c>trusted</c> flag the studio states: none of them is trusted, so creating
    /// one needs a superuser or the platform's administrator, never a database owner. <c>pg_stat_statements</c> and
    /// <c>pgstattuple</c> are measured on <c>postgres:17.11-alpine</c>'s <c>pg_available_extension_versions</c>
    /// (<c>ServerGrantsLiveTests</c>); hypopg 1.4.3's control file has no <c>trusted = true</c> (B0 §2).
    /// </summary>
    public static IReadOnlyDictionary<string, bool> KnownExtensions { get; } = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["pg_stat_statements"] = false,
        ["pgstattuple"] = false,
        ["hypopg"] = false,
    };

    /// <summary>The sweep's one read of it, whichever check asks first (<see cref="HealthCheckContext.Shared{T}" />).</summary>
    public static Task<PostgresGrants> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(ServerReachQueries.GrantsKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it, through the context's runner, as the role the reads run as.</summary>
    public static async Task<PostgresGrants> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var query = ServerReachQueries.Grants();
        IReadOnlyList<(string ReadsAs, bool Superuser, PostgresRoleMembership Role)> rows = await context.Sql
            .QueryAsync(
                query,
                static reader => (reader.GetString(0), reader.GetBoolean(1), new PostgresRoleMembership(reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4))),
                cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("The grant read returned no rows; it returns one per role it asks about.");
        }

        return new PostgresGrants(rows[0].ReadsAs, rows[0].Superuser, [.. rows.Select(static x => x.Role)]);
    }

    /// <summary>Whether the reading role is a member of <paramref name="role" /> (a superuser is a member of every role).</summary>
    public bool IsMember(string role) => Superuser || Roles.Any(x => x.Member && string.Equals(x.Name, role, StringComparison.Ordinal));

    /// <summary>
    /// Whether the reading role holds <paramref name="grant" />: a superuser, a member of the role, or of the role that
    /// includes it. A column privilege is not read here and is never said to be held.
    /// </summary>
    public bool Holds(PostgresGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return grant.IsRole && (IsMember(grant.Privilege) || (grant.IncludedIn is { } umbrella && IsMember(umbrella)));
    }

    /// <summary>Whether the reading role is a member of a managed platform's admin role - "provider admin", not a superuser.</summary>
    public bool IsProviderAdmin => ProviderAdminRoles.Any(name => Roles.Any(x => x.Member && string.Equals(x.Name, name, StringComparison.Ordinal)));

    /// <summary>
    /// The sentence a "not checked" row says: "Not checked: this needs pg_read_all_stats (pg_monitor includes it) for role
    /// app_reader. On Amazon Aurora PostgreSQL the master user (a member of rds_superuser) can grant it."
    /// </summary>
    /// <param name="needed">What the read needs.</param>
    /// <param name="platform">Who runs the server, for who can grant it.</param>
    /// <param name="readsAs">The role the reads run as (<see cref="ReadsAs" />).</param>
    public static string NotChecked(PostgresGrant needed, PostgresPlatform platform, string readsAs)
    {
        ArgumentNullException.ThrowIfNull(needed);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(readsAs);

        return "Not checked: this needs " + needed.Described + " for role " + readsAs + ". " + Grantor(needed, platform);
    }

    /// <summary>
    /// What a check whose statement Postgres refused (<c>42501</c>) says when it does not know which grant it lacked: the
    /// role, and what covers the kind of read the check makes - <c>pg_monitor</c>, which covers every statistics view the
    /// advisor reads, and who can grant it; or, for a check that reads a bounded slice of rows ("Run deeper checks"),
    /// <c>SELECT</c> on the tables it reads, which their owner grants and no monitoring role gives.
    /// </summary>
    /// <param name="platform">Who runs the server.</param>
    /// <param name="readsAs">The role the reads run as.</param>
    /// <param name="tier">What the refused check reads.</param>
    public static string Refused(PostgresPlatform platform, string readsAs, HealthCostTier tier = HealthCostTier.Catalog)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(readsAs);

        string refused = "Not checked: Postgres refused role " + readsAs + " a read this check needs (SQLSTATE 42501); ";

        return tier == HealthCostTier.BoundedData
            ? refused + "it reads a bounded slice of the rows of the tables it judges, which needs SELECT on them - their owner "
              + "can grant it, and no monitoring role gives it."
            : refused + "pg_monitor covers every statistics view the advisor reads. " + Grantor(PostgresGrant.Monitor, platform);
    }

    /// <summary>
    /// The sentence for an extension a check needs that this database does not have: "Not checked: this needs the
    /// pgstattuple extension, which this database does not have. On a self-hosted server a superuser can run CREATE
    /// EXTENSION pgstattuple; it is not a trusted extension, so a database owner cannot."
    /// </summary>
    /// <param name="name">The extension.</param>
    /// <param name="trusted">Whether it is trusted (<c>pg_available_extension_versions.trusted</c>; <see cref="KnownExtensions" />).</param>
    /// <param name="platform">Who runs the server.</param>
    public static string ExtensionNotInstalled(string name, bool trusted, PostgresPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(platform);

        string create = "CREATE EXTENSION " + name;
        string who = trusted
            ? "It is a trusted extension, so a role with CREATE on this database can run " + create + "."
            : platform.Kind switch
            {
                PostgresPlatformKind.AmazonRds or PostgresPlatformKind.AmazonAurora =>
                    "On " + platform.Name + " the master user (a member of rds_superuser) can run " + create + ", where the engine version offers it.",
                PostgresPlatformKind.Azure =>
                    "On " + platform.Name + " the server admin (a member of azure_pg_admin) can run " + create + " once the azure.extensions server parameter allows it.",
                PostgresPlatformKind.CloudSql =>
                    "On " + platform.Name + " a member of cloudsqlsuperuser, such as the postgres user, can run " + create + ".",
                PostgresPlatformKind.Neon =>
                    "On " + platform.Name + " a member of neon_superuser can run " + create + ".",
                PostgresPlatformKind.Supabase =>
                    "On " + platform.Name + " the postgres role can run " + create + ".",
                _ => "On a self-hosted server a superuser can run " + create + "; it is not a trusted extension, so a database owner cannot.",
            };

        return "Not checked: this needs the " + name + " extension, which this database does not have. " + who;
    }

    /// <summary>Who can grant <paramref name="grant" /> on <paramref name="platform" />, as a sentence.</summary>
    internal static string Grantor(PostgresGrant grant, PostgresPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(platform);

        if (!grant.IsRole)
        {
            return "The owner of " + grant.Relation + " can grant it.";
        }

        return platform.Kind switch
        {
            PostgresPlatformKind.AmazonRds or PostgresPlatformKind.AmazonAurora =>
                "On " + platform.Name + " the master user (a member of rds_superuser) can grant it.",
            PostgresPlatformKind.Azure =>
                "On " + platform.Name + " the server admin (a member of azure_pg_admin) can grant it.",
            PostgresPlatformKind.CloudSql =>
                "On " + platform.Name + " a member of cloudsqlsuperuser, such as the postgres user, can grant it.",
            PostgresPlatformKind.Neon =>
                "On " + platform.Name + " a member of neon_superuser can grant it.",
            PostgresPlatformKind.Supabase =>
                "On " + platform.Name + " the postgres role can grant it.",
            _ => "On a self-hosted server a superuser can grant it.",
        };
    }

    /// <summary>The roles the reading role is a member of, for evidence: "pg_monitor, pg_read_all_stats", or "none".</summary>
    public string MemberOf => Superuser
        ? "superuser"
        : string.Join(", ", Roles.Where(static x => x.Member).Select(static x => x.Name)) is { Length: > 0 } names
            ? names
            : "none of " + Roles.Count.ToString(CultureInfo.InvariantCulture) + " monitoring and provider roles";
}
