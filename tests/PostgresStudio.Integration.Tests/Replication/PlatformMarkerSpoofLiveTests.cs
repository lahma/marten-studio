using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Integration.Tests.Replication;

/// <summary>
/// W5-R2-fix B3: no role short of a superuser can make the platform detector call a server of somebody's own a provider's -
/// which would silence R02-R04 (not applicable on Aurora) and S02's <c>fsync</c> (Neon) with nothing logged. Every plant a
/// database owner or a <c>CREATEROLE</c> role can make is tried, on PostgreSQL 14, 17 and 18, and the test asserts what
/// Postgres did with each (W5-R2-fix2 4): the function and a bare <c>neon_superuser</c> are accepted and fenced by the
/// detector; a database's or a role's <c>SET</c> of a provider's placeholder, and Neon's attributes, are refused; a session's
/// <c>SET</c>, <c>SET LOCAL</c> and a startup option take, and <c>pg_settings</c> lists none of them. The detector must say
/// self-hosted all the same.
/// </summary>
/// <remarks>
/// On servers of its own, never the version matrix's: it creates roles, and the matrix's no-DDL fingerprint lists roles.
/// </remarks>
public class PlatformMarkerSpoofLiveTests
{
    private const string Database = "spoof_r2";

    /// <summary>Every provider setting <c>pg_settings</c> lists, with where it came from.</summary>
    private const string ProviderSettingsSql =
        "select s.name || ' (' || s.source || ')' from pg_catalog.pg_settings s where s.name like 'rds.%' or s.name like 'azure.%' or s.name like 'cloudsql.%' order by 1";

    /// <summary>The detector statement's marker columns.</summary>
    private static readonly string[] MarkerColumns = ["rds", "aurora", "azure", "cloud_sql", "neon"];

    public static TheoryData<string> Images => ["postgres:14.24-alpine", "postgres:17.11-alpine", "postgres:18.6-alpine"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresTheory]
    [MemberData(nameof(Images))]
    public async Task A_database_owners_plants_never_make_the_detector_say_a_provider(string image)
    {
        await using ReplicationServer server = await ReplicationServer.StartAsync(image, null, null);
        await server.ExecuteAsync("create database " + Database + " owner app");
        await server.ExecuteAsync("create role creator login password 'creator' createrole");

        string asOwner = OwnerOf(server, "app", "app");
        string asCreator = OwnerOf(server, "creator", "creator");

        (string Plant, string By, string Sql)[] plants =
        [
            ("public.aurora_version()", "the database owner", "create function public.aurora_version() returns varchar language sql as $$ select '16.4.1'::varchar $$"),
            ("ALTER DATABASE SET rds.extensions", "the database owner", "alter database " + Database + " set \"rds.extensions\" = 'pg_stat_statements'"),
            ("ALTER DATABASE SET cloudsql.iam_authentication", "the database owner", "alter database " + Database + " set \"cloudsql.iam_authentication\" = 'on'"),
            ("ALTER ROLE app SET azure.extensions", "the role itself", "alter role app set \"azure.extensions\" = 'pg_stat_statements'"),
            ("ALTER ROLE app IN DATABASE SET rds.allowed_extensions", "the role itself", "alter role app in database " + Database + " set \"rds.allowed_extensions\" = '*'"),
            ("CREATE ROLE neon_superuser NOLOGIN", "a CREATEROLE role", "create role neon_superuser nologin"),
            ("CREATE ROLE neon_superuser_r REPLICATION BYPASSRLS", "a CREATEROLE role", "create role neon_superuser_r nologin replication bypassrls"),
        ];

        List<string> accepted = [];
        List<string> refused = [];

        foreach ((string plant, string by, string sql) in plants)
        {
            try
            {
                await ReplicationServer.Execute(by == "a CREATEROLE role" ? asCreator : asOwner, sql);
                accepted.Add(plant + " (by " + by + ")");
            }
            catch (PostgresException exception)
            {
                refused.Add(plant + " (by " + by + "): " + exception.SqlState);
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            "{0}\naccepted:\n  {1}\nrefused:\n  {2}", image, string.Join("\n  ", accepted), string.Join("\n  ", refused));

        // The detector, as the owner, in the planted database - where every database- and role-level SET applies.
        await using StudioHost host = ReplicationServer.HostOver(asOwner, TimeProvider.System);

        HealthReport report = await host.CircuitAsync("admin", services => services.GetRequiredService<IHealthService>()
            .GetReportAsync(new StudioScope("default", "default", null), HealthReportRequest.Regular, Token));

        foreach (string code in new[] { "R02", "R03", "R04" })
        {
            report.Checks.Single(x => x.Code == code).Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} still runs on a server of somebody's own", code);
        }

        // The platform itself, read the way every check reads it.
        PostgresPlatform platform = await host.CircuitAsync("admin", async services =>
        {
            ResolvedSource resolved = await services.GetRequiredService<StudioSourceResolver>().ResolveAsync(new StudioScope("default", "default", null), null, Token);
            await using HealthConnection connection = new(resolved.Target);
            HealthCheckContext context = new(
                resolved,
                services.GetRequiredService<IStudioSettings>(),
                NoStudioRuntime.Instance,
                services,
                new HealthSqlRunner(connection, new ReadOnlySqlOptions(), observer: null),
                DateTimeOffset.UtcNow,
                new HealthSweepMemo(),
                Token);

            return (await PostgresServerFacts.ReadAsync(context, Token)).Platform;
        });

        platform.Kind.Should().Be(PostgresPlatformKind.SelfHosted, "no plant a role short of a superuser can make is a marker");

        bool planted = await ReplicationServer.Scalar<bool>(asOwner, "select exists (select 1 from pg_catalog.pg_proc p where p.proname = 'aurora_version')");
        planted.Should().BeTrue("the owner's function is there; the detector must not count it");

        // What Postgres does with each plant, as observed (W5-R2-fix2 4): the function and the bare role are accepted and
        // fenced by the detector; every database- and role-level SET of a provider's placeholder is refused to a non-superuser,
        // and so are the attributes Neon's role carries.
        accepted.Should().BeEquivalentTo(["public.aurora_version() (by the database owner)", "CREATE ROLE neon_superuser NOLOGIN (by a CREATEROLE role)"]);
        refused.Should().BeEquivalentTo(
        [
            "ALTER DATABASE SET rds.extensions (by the database owner): 42501",
            "ALTER DATABASE SET cloudsql.iam_authentication (by the database owner): 42501",
            "ALTER ROLE app SET azure.extensions (by the role itself): 42501",
            "ALTER ROLE app IN DATABASE SET rds.allowed_extensions (by the role itself): 42501",
            "CREATE ROLE neon_superuser_r REPLICATION BYPASSRLS (by a CREATEROLE role): 42501",
        ]);

        (await SettingsListedAsync(asOwner)).Should().BeEmpty("nothing the owner tried left a provider's setting in pg_settings");

        // A placeholder anybody may set for their own connection - SET, SET LOCAL, or -c in the startup options - takes, and
        // is never listed in pg_settings, which is what the detector reads; the source fence beside it is defence in depth.
        (string[] listed, string[] markers) = await MarkersInASessionThatSetThemAsync(asOwner);
        TestContext.Current.TestOutputHelper?.WriteLine(
            "in a session that set them itself: pg_settings lists {0}; the detector's statement reads {1}",
            listed.Length == 0 ? "none of them" : string.Join(", ", listed), markers.Length == 0 ? "no marker" : string.Join(", ", markers));
        listed.Should().BeEmpty("Postgres never lists a placeholder in pg_settings, however it was set");
        markers.Should().BeEmpty();
    }

    /// <summary>
    /// In a connection whose startup options set <c>cloudsql.iam_authentication</c>, which then <c>SET</c>s
    /// <c>rds.extensions</c> and <c>azure.extensions</c> and, in a transaction, <c>SET LOCAL</c>s <c>rds.allowed_extensions</c>:
    /// asserts each took, and returns which of them <c>pg_settings</c> lists and which markers the detector's own statement
    /// reports, both read inside that transaction.
    /// </summary>
    private static async Task<(string[] Listed, string[] Markers)> MarkersInASessionThatSetThemAsync(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString) { Options = "-c cloudsql.iam_authentication=on" };

        await using NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(Token);

        await using (NpgsqlCommand set = new("set \"rds.extensions\" = 'pg_stat_statements'; set \"azure.extensions\" = 'pg_stat_statements'", connection))
        {
            await set.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Token);

        await using (NpgsqlCommand setLocal = new("set local \"rds.allowed_extensions\" = '*'", connection, transaction))
        {
            await setLocal.ExecuteNonQueryAsync(Token);
        }

        await using (NpgsqlCommand seen = new(
            "select pg_catalog.current_setting('cloudsql.iam_authentication', true), pg_catalog.current_setting('rds.extensions', true), "
            + "pg_catalog.current_setting('azure.extensions', true), pg_catalog.current_setting('rds.allowed_extensions', true)",
            connection,
            transaction))
        await using (NpgsqlDataReader reader = await seen.ExecuteReaderAsync(Token))
        {
            await reader.ReadAsync(Token);
            reader.GetString(0).Should().Be("on", "the startup option took");
            reader.GetString(1).Should().Be("pg_stat_statements", "the SET took");
            reader.GetString(2).Should().Be("pg_stat_statements", "the SET took");
            reader.GetString(3).Should().Be("*", "the SET LOCAL took");
        }

        List<string> listed = [];

        await using (NpgsqlCommand list = new(ProviderSettingsSql, connection, transaction))
        await using (NpgsqlDataReader reader = await list.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                listed.Add(reader.GetString(0));
            }
        }

        var query = HealthServerQueries.Platform();
        string[] markers;

        await using (NpgsqlCommand detect = new(query.Sql, connection, transaction))
        await using (NpgsqlDataReader reader = await detect.ExecuteReaderAsync(Token))
        {
            await reader.ReadAsync(Token);
            markers = [.. MarkerColumns.Where(name => reader.GetBoolean(reader.GetOrdinal(name)))];
        }

        await transaction.RollbackAsync(Token);
        return ([.. listed], markers);
    }

    private static string OwnerOf(ReplicationServer server, string user, string password)
    {
        NpgsqlConnectionStringBuilder builder = new(server.SuperuserConnectionString) { Database = Database, Username = user, Password = password, Pooling = false };
        return builder.ConnectionString;
    }

    private static async Task<IReadOnlyList<string>> SettingsListedAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(ProviderSettingsSql, connection);

        List<string> names = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
