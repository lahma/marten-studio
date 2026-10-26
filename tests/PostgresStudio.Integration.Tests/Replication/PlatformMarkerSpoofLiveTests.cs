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
/// database owner or a <c>CREATEROLE</c> role can make is tried, on PostgreSQL 14, 17 and 18; the test's output says which
/// Postgres accepted, and the detector must say self-hosted all the same.
/// </summary>
/// <remarks>
/// On servers of its own, never the version matrix's: it creates roles, and the matrix's no-DDL fingerprint lists roles.
/// </remarks>
public class PlatformMarkerSpoofLiveTests
{
    private const string Database = "spoof_r2";

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

        string[] settings = [.. await SettingsSeenAsync(asOwner)];
        TestContext.Current.TestOutputHelper?.WriteLine("placeholders the owner's session sees: {0}", string.Join(", ", settings));

        accepted.Should().Contain("public.aurora_version() (by the database owner)", "the plant the brief names is one Postgres accepts");

        // A placeholder anybody may set for their own connection - SET in the session, or -c in the startup options - never
        // reaches the studio's connection; and even run in such a session, the detector's own statement does not count it.
        string[] markers = await MarkersInASessionThatSetThemAsync(asOwner);
        TestContext.Current.TestOutputHelper?.WriteLine("markers the detector's statement reads in a session that SET them itself: {0}", markers.Length == 0 ? "none" : string.Join(", ", markers));
        markers.Should().BeEmpty("a session's SET and a client's startup option are not the server's configuration");
    }

    /// <summary>
    /// Runs the detector's own statement in a connection whose startup options set <c>cloudsql.iam_authentication</c> and
    /// which then SETs <c>rds.extensions</c> and <c>azure.extensions</c> itself, and returns the markers it reported.
    /// </summary>
    private static async Task<string[]> MarkersInASessionThatSetThemAsync(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder = new(connectionString) { Options = "-c cloudsql.iam_authentication=on" };

        await using NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(Token);

        await using (NpgsqlCommand set = new("set \"rds.extensions\" = 'pg_stat_statements'; set \"azure.extensions\" = 'pg_stat_statements'", connection))
        {
            await set.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlCommand seen = new("select pg_catalog.current_setting('cloudsql.iam_authentication', true), pg_catalog.current_setting('rds.extensions', true)", connection);
        await using (NpgsqlDataReader reader = await seen.ExecuteReaderAsync(Token))
        {
            await reader.ReadAsync(Token);
            reader.GetString(0).Should().Be("on", "the startup option took");
            reader.GetString(1).Should().Be("pg_stat_statements", "the SET took");
        }

        var query = HealthServerQueries.Platform();
        await using NpgsqlCommand detect = new(query.Sql, connection);
        await using NpgsqlDataReader markers = await detect.ExecuteReaderAsync(Token);
        await markers.ReadAsync(Token);

        return [.. MarkerColumns.Where(name => markers.GetBoolean(markers.GetOrdinal(name)))];
    }

    private static string OwnerOf(ReplicationServer server, string user, string password)
    {
        NpgsqlConnectionStringBuilder builder = new(server.SuperuserConnectionString) { Database = Database, Username = user, Password = password, Pooling = false };
        return builder.ConnectionString;
    }

    private static async Task<IReadOnlyList<string>> SettingsSeenAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select s.name || ' (' || s.source || ')' from pg_catalog.pg_settings s where s.name like 'rds.%' or s.name like 'azure.%' or s.name like 'cloudsql.%' order by 1",
            connection);

        List<string> names = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
