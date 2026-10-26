using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Sources;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace PostgresStudio.Services.Health;

/// <summary>What the platform read found on the server: one flag per provider marker.</summary>
/// <param name="RdsSettings">An <c>rds.extensions</c> or <c>rds.allowed_extensions</c> setting exists.</param>
/// <param name="AuroraVersionFunction">An <c>aurora_version()</c> function exists.</param>
/// <param name="AzureExtensions">An <c>azure.extensions</c> setting exists.</param>
/// <param name="CloudSqlSettings">A <c>cloudsql.*</c> setting exists.</param>
/// <param name="NeonSuperuserRole">A <c>neon_superuser</c> role exists.</param>
internal sealed record PostgresPlatformMarkers(
    bool RdsSettings,
    bool AuroraVersionFunction,
    bool AzureExtensions,
    bool CloudSqlSettings,
    bool NeonSuperuserRole)
{
    /// <summary>A server with none of them.</summary>
    public static PostgresPlatformMarkers None { get; } = new(false, false, false, false, false);
}

/// <summary>
/// The managed-platform detector: which provider runs the server, read from markers the provider documents - and
/// used for nothing but wording and mutes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only wording and mutes.</b> A platform never raises a finding and never decides a severity. It shapes three
/// things: S01 says "community support ends; your provider's schedule may differ" instead of speaking as though the
/// community's dates were the host's; R01's remedy names the place the provider lets a server setting be changed;
/// and S02 stands down for a setting the provider documents as its own design (Neon's <c>fsync = off</c>). From W5-R2 it
/// also says what does not apply: R02-R04 and the Replication page's sections where Aurora's storage layer, or a
/// provider's own archiving, does the work (issue #8 A2). A server the detector does not recognise is
/// <see cref="PostgresPlatformKind.SelfHosted" />, which is also the wording that is right for a provider the studio has
/// never heard of: the setting is somebody's to change.
/// </para>
/// <para>
/// <b>No marker a role short of a superuser can plant</b> (W5-R2-fix B3), because a platform silences findings: each server
/// marker is fenced in <c>HealthServerQueries.Platform</c> - a setting read from <c>pg_settings</c>, which never lists a
/// placeholder a session or a client made and refuses a database's or role's one to all but a superuser (and, as defence
/// in depth, only from the server's own configuration), the Aurora function only in <c>pg_catalog</c>, Neon's role only
/// with the attributes only a superuser grants.
/// </para>
/// <para>
/// <b>Every marker is the provider's own, documented, and read without a privilege</b> (verified 2026-10-17; a marker
/// that could not be verified from the provider's documentation was dropped):
/// </para>
/// <list type="bullet">
/// <item><b>Amazon RDS</b>: the <c>rds.extensions</c> setting ("You can also see the current extensions list using
/// <c>psql</c> by showing the <c>rds.extensions</c> parameter" - <i>Supported PostgreSQL extension versions</i>, Amazon
/// RDS User Guide) and <c>rds.allowed_extensions</c> (same page, and the Aurora User Guide's <i>Working with extensions
/// and foreign data wrappers</i>).</item>
/// <item><b>Amazon Aurora</b>: the <c>aurora_version()</c> function, "returns the Aurora PostgreSQL version" (<i>aurora_version</i>,
/// Aurora User Guide). Looked up in <c>pg_proc</c>, in <c>pg_catalog</c> only, never called.</item>
/// <item><b>Azure</b>: the <c>azure.extensions</c> server parameter, "Run <c>SHOW azure.extensions;</c>" (<i>PostgreSQL
/// extensions in Azure Database for PostgreSQL</i>, Microsoft Learn).</item>
/// <item><b>Cloud SQL</b>: any of the <c>cloudsql.*</c> database flags (<c>cloudsql.iam_authentication</c>,
/// <c>cloudsql.enable_pgaudit</c>, … - <i>Configure database flags</i>, Cloud SQL for PostgreSQL), which that page says
/// are read with <c>SELECT name, setting FROM pg_settings</c>.</item>
/// <item><b>Neon</b>: the <c>neon_superuser</c> role ("In place of the Postgres superuser role, Neon provides a
/// <c>neon_superuser</c> role" - <i>Postgres compatibility</i>, Neon docs), or a host under <c>.neon.tech</c>, the
/// domain of every endpoint the same docs show (<c>ep-cool-darkness-123456.us-east-2.aws.neon.tech</c>).</item>
/// <item><b>Supabase</b>: a host under <c>.supabase.co</c> (<c>db.[PROJECT-REF].supabase.co</c>, the direct connection
/// and the dedicated pooler) or <c>.supabase.com</c> (the shared pooler's <c>…pooler.supabase.com</c>) - <i>Connect to your
/// database</i>, Supabase docs. <b>No server-side marker:</b> the <c>supautils.*</c> settings come from an extension
/// Supabase publishes that other distributions also install, and no Supabase page documents a setting only its servers
/// have, so a server reached through a proxy of the host's own is not recognised.</item>
/// </list>
/// <para>
/// The checks happen in that order - Aurora before RDS, because an Aurora cluster has the <c>rds.*</c> settings too -
/// and the first that matches wins.
/// </para>
/// </remarks>
/// <param name="Kind">Which platform.</param>
/// <param name="Marker">What said so, in words, for the evidence; <see langword="null" /> for a self-hosted server.</param>
internal sealed record PostgresPlatform(PostgresPlatformKind Kind, string? Marker)
{
    /// <summary>A server nothing marks as a provider's.</summary>
    public static PostgresPlatform SelfHosted { get; } = new(PostgresPlatformKind.SelfHosted, null);

    /// <summary>Whether a provider runs the server.</summary>
    public bool IsManaged => Kind != PostgresPlatformKind.SelfHosted;

    /// <summary>The platform's name, for evidence and wording.</summary>
    public string Name => Kind switch
    {
        PostgresPlatformKind.AmazonRds => "Amazon RDS for PostgreSQL",
        PostgresPlatformKind.AmazonAurora => "Amazon Aurora PostgreSQL",
        PostgresPlatformKind.Azure => "Azure Database for PostgreSQL",
        PostgresPlatformKind.CloudSql => "Google Cloud SQL for PostgreSQL",
        PostgresPlatformKind.Neon => "Neon",
        PostgresPlatformKind.Supabase => "Supabase",
        _ => "self-hosted (no provider marker found)",
    };

    /// <summary>
    /// Where a server setting such as <c>max_slot_wal_keep_size</c> is changed on this platform, as the end of a
    /// sentence: "set it in …". Each is the provider's own name for the place, from the same pages as the markers.
    /// </summary>
    public string WhereSettingsChange => Kind switch
    {
        PostgresPlatformKind.AmazonRds => "the instance's DB parameter group (Amazon RDS)",
        PostgresPlatformKind.AmazonAurora => "the cluster's DB cluster parameter group (Aurora)",
        PostgresPlatformKind.Azure => "the server's Server parameters (Azure portal or az postgres flexible-server parameter set)",
        PostgresPlatformKind.CloudSql => "the instance's database flags (Cloud SQL)",
        PostgresPlatformKind.Neon => "a request to Neon Support - Neon does not let a project change server settings itself",
        PostgresPlatformKind.Supabase => "the project's Postgres configuration, where Supabase allows it",
        _ => "postgresql.conf, followed by a reload",
    };

    /// <summary>
    /// The platform the markers and the data source's hosts point at, or <see cref="SelfHosted" />. Pure: the unit
    /// tests hold every marker to it.
    /// </summary>
    /// <param name="markers">What the server said.</param>
    /// <param name="hosts">The data source's hosts, as its connection string names them.</param>
    public static PostgresPlatform Detect(PostgresPlatformMarkers markers, IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(markers);
        ArgumentNullException.ThrowIfNull(hosts);

        string[] names = [.. hosts.Where(static x => !string.IsNullOrWhiteSpace(x)).Select(static x => x.Trim().TrimEnd('.').ToLowerInvariant())];

        if (markers.AuroraVersionFunction)
        {
            return new PostgresPlatform(PostgresPlatformKind.AmazonAurora, "the server has Aurora's aurora_version() function");
        }

        if (markers.RdsSettings)
        {
            return new PostgresPlatform(PostgresPlatformKind.AmazonRds, "the server has Amazon RDS's rds.extensions settings");
        }

        if (markers.AzureExtensions)
        {
            return new PostgresPlatform(PostgresPlatformKind.Azure, "the server has Azure's azure.extensions server parameter");
        }

        if (markers.CloudSqlSettings)
        {
            return new PostgresPlatform(PostgresPlatformKind.CloudSql, "the server has Cloud SQL's cloudsql.* database flags");
        }

        if (markers.NeonSuperuserRole)
        {
            return new PostgresPlatform(PostgresPlatformKind.Neon, "the server has Neon's neon_superuser role");
        }

        if (names.Any(static x => x.EndsWith(".neon.tech", StringComparison.Ordinal)))
        {
            return new PostgresPlatform(PostgresPlatformKind.Neon, "the data source's host is a Neon endpoint (*.neon.tech)");
        }

        if (names.Any(static x => x.EndsWith(".supabase.co", StringComparison.Ordinal) || x.EndsWith(".supabase.com", StringComparison.Ordinal)))
        {
            return new PostgresPlatform(PostgresPlatformKind.Supabase, "the data source's host is a Supabase one (*.supabase.co or *.supabase.com)");
        }

        return SelfHosted;
    }
}

/// <summary>The server a store database lives on: its version and its platform, read once per sweep.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="VersionNumber"><c>server_version_num</c> - <c>170004</c> for 17.4.</param>
/// <param name="Version"><c>server_version</c>, as the server spells it.</param>
/// <param name="Platform">Who runs it.</param>
internal sealed record PostgresServerFacts(DateTimeOffset ReadAt, int VersionNumber, string Version, PostgresPlatform Platform)
{
    /// <summary>
    /// The major version: <c>17</c> for 17.4, and for a server older than 10 - which numbered its majors with two parts
    /// - the first part times a hundred plus the second (<c>906</c> for 9.6).
    /// </summary>
    public int Major => VersionNumber >= 100_000 ? VersionNumber / 10_000 : VersionNumber / 100;

    /// <summary>The major version as Postgres names it: "17", "9.6".</summary>
    public string MajorName => Major >= 100
        ? (Major / 100).ToString(CultureInfo.InvariantCulture) + "." + (Major % 100).ToString(CultureInfo.InvariantCulture)
        : Major.ToString(CultureInfo.InvariantCulture);

    /// <summary>The sweep's one read of it, shared by S01, S02 and R01 (<see cref="HealthCheckContext.Shared{T}" />).</summary>
    public static Task<PostgresServerFacts> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(HealthServerQueries.PlatformKey, token => ReadAsync(context, token));
    }

    /// <summary>Reads it.</summary>
    /// <remarks>
    /// One read per database per sweep, of the database the context names (<see cref="HealthCheckContext.Target" />), and
    /// nothing in it is about a source: a per-instance fact Postgres or a platform answers in SQL - which Aurora instance
    /// answered (issue #8, A1) - belongs in this same statement and record, and one a host supplies (A8's capacity) beside
    /// it from <see cref="HealthCheckContext.Services" />.
    /// </remarks>
    public static async Task<PostgresServerFacts> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<(int Number, string Version, PostgresPlatformMarkers Markers)> rows = await context.Sql
            .QueryAsync(
                HealthServerQueries.Platform(),
                static reader => (
                    reader.GetInt32(0),
                    reader.GetString(1),
                    new PostgresPlatformMarkers(reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6))),
                cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count != 1)
        {
            throw new InvalidOperationException("The server's version and platform came back as " + rows.Count.ToString(CultureInfo.InvariantCulture) + " rows.");
        }

        (int number, string version, PostgresPlatformMarkers markers) = rows[0];
        PostgresPlatform platform = PostgresPlatform.Detect(markers, DataSourceEndpoints.Of(context.Target)?.Hosts ?? []);

        ILogger? logger = context.Services.GetService<ILogger<PostgresPlatform>>();
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            logger.PostgresPlatformDetected(context.Settings.Text.Product, platform.Kind.ToString(), platform.Marker ?? "no provider marker", context.StoreKey, context.DatabaseId);
        }

        return new PostgresServerFacts(context.Now, number, version, platform);
    }
}

/// <summary>Where a store database's data source connects: its hosts and ports, read from its connection string.</summary>
/// <param name="Endpoints">Every host the data source names, with the port it uses there.</param>
/// <param name="Multiplexing">
/// Whether the data source multiplexes - sends each command over whichever physical connection is free, so that two
/// statements of one <c>NpgsqlConnection</c> may reach two server processes with no pooler involved.
/// </param>
internal sealed record DataSourceEndpoints(IReadOnlyList<(string Host, int Port)> Endpoints, bool Multiplexing)
{
    /// <summary>The hosts alone.</summary>
    public IReadOnlyList<string> Hosts => [.. Endpoints.Select(static x => x.Host)];

    /// <summary>
    /// The endpoints of <paramref name="database" />'s data source, or <see langword="null" /> when it exposes none the
    /// studio can read (<see cref="IStudioDatabase.DataSource" /> is <see langword="null" />, or a connection string Npgsql
    /// will not parse).
    /// </summary>
    /// <remarks>
    /// Read the way <c>DatabasePoolSettings</c> reads the pool settings, without opening a connection, and without
    /// keeping the connection string. A multi-host data source names its hosts comma-separated, each with an optional
    /// <c>:port</c> of its own; a host without one uses <c>Port</c>.
    /// </remarks>
    public static DataSourceEndpoints? Of(IStudioDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (database.DataSource is not { } dataSource)
        {
            return null;
        }

        try
        {
            NpgsqlConnectionStringBuilder settings = new(dataSource.ConnectionString);
            return new DataSourceEndpoints(Parse(settings.Host, settings.Port), settings.Multiplexing);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A connection string's <c>Host</c> as endpoints: <c>a,b:6432</c> is <c>a</c> on <paramref name="port" /> and <c>b</c> on 6432.</summary>
    internal static IReadOnlyList<(string Host, int Port)> Parse(string? hosts, int port)
    {
        List<(string Host, int Port)> endpoints = [];

        foreach (string raw in (hosts ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string host = raw;
            int at = port;

            // "host:port", but not an IPv6 address on its own ("::1") - one written with a port is "[::1]:5433".
            int colon = raw.LastIndexOf(':');
            if (colon > 0 && (raw.IndexOf(':', StringComparison.Ordinal) == colon || raw.StartsWith('['))
                && int.TryParse(raw.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int explicitPort))
            {
                host = raw[..colon];
                at = explicitPort;
            }

            endpoints.Add((host.Trim('[', ']'), at));
        }

        return endpoints;
    }
}
