using System.Globalization;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// S04 and S06-S10, K05 and K06 on real servers (W5-S2): each fires on the condition it looks for and stays quiet without
/// it, through a full regular sweep of the studio's own services - on the version matrix's servers, as their roles, or as a
/// login a test makes for itself and drops after.
/// </summary>
/// <remarks>
/// <para>
/// <b>No Marten type</b>, so these live here (W5-index); MartenStudio's suite names each code as proven in this one.
/// </para>
/// <para>
/// A login a test makes is named for the run (<see cref="PostgresVersionMatrix.RunId" />) and dropped in a <c>finally</c>,
/// because the matrix's servers are shared by every class of the run. Nothing a test changes is the seeded fixtures'.
/// </para>
/// </remarks>
public class ServerPostureLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- S04 ----------------------------------------------------------------------------------------------------------

    /// <summary>The container's <c>postgres</c> is a superuser: S04 fires, as Info with no environment named.</summary>
    [PostgresFact]
    [HealthFires("S04")]
    public async Task S04_fires_for_the_containers_superuser()
    {
        MatrixServer server = await ServerAsync(17);

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.Superuser));
        HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S04");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        HealthFinding finding = result.Findings.Should().ContainSingle().Subject;
        finding.Variant.Should().Be("superuser");
        finding.Severity.Should().Be(HealthSeverity.Info, "the host names no environment, so this is not Production");
    }

    /// <summary>B0's <c>app</c> owns its schema and nothing more: S04 is quiet.</summary>
    [PostgresFact]
    [HealthQuiet("S04")]
    public async Task S04_is_quiet_for_a_login_that_owns_its_schema_and_nothing_more()
    {
        MatrixServer server = await ServerAsync(17);

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App), environment: "Production");
        HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S04");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        result.Findings.Should().BeEmpty();
        result.Note.Should().Contain("role app");
    }

    /// <summary>A member of a file-access role is S04's third kind, a Warning in Production.</summary>
    [PostgresFact]
    public async Task S04_a_member_of_pg_read_server_files_is_the_file_access_variant()
    {
        MatrixServer server = await ServerAsync(17);
        string role = "s2_files_" + PostgresVersionMatrix.RunId;

        await server.ExecuteAsync("create role " + role + " login password '" + role + "'; grant pg_read_server_files to " + role, cancellationToken: Token);

        try
        {
            await using PostureHost host = await PostureHost.CreateAsync(Login(server, role), environment: "Production");
            HealthFinding finding = PostureHost.Result(await host.SweepAsync(), "S04").Findings.Should().ContainSingle().Subject;

            finding.Variant.Should().Be("file-access");
            finding.Severity.Should().Be(HealthSeverity.Warning);
        }
        finally
        {
            await DropRoleAsync(server, role);
        }
    }

    // ---- S06 ----------------------------------------------------------------------------------------------------------

    /// <summary>A cluster made on 14 gives every role CREATE on <c>public</c> (B0 §6): S06 names it.</summary>
    [PostgresFact]
    [HealthFires("S06")]
    public async Task S06_fires_on_14_where_PUBLIC_may_create_in_public()
    {
        MatrixServer server = await ServerAsync(14);

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App));
        HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S06");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        HealthFinding finding = result.Findings.Should().ContainSingle().Subject;
        finding.Subject.Should().Be("public");
        finding.Caveat.Should().StartWith("Below PostgreSQL 15");
    }

    /// <summary>From 15 <c>public</c> is <c>pg_database_owner</c>'s and PUBLIC holds no CREATE on it: S06 is quiet.</summary>
    [PostgresFact]
    [HealthQuiet("S06")]
    public async Task S06_is_quiet_on_17_where_initdb_grants_PUBLIC_nothing()
    {
        MatrixServer server = await ServerAsync(17);

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App));
        HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S06");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        result.Findings.Should().BeEmpty();

        // Quiet because public was looked at and found closed, not because nothing was looked at.
        await using MatrixHost matrixHost = await MatrixHost.CreateAsync(server, MatrixRole.App);
        (HealthCheckContext context, HealthConnection connection) = matrixHost.Context();
        await using HealthConnection _ = connection;

        PublicSchemaCreateFacts facts = await new PublicSchemaCreateCheck().GatherAsync(context, Token);
        facts.OwnSchemas.Should().Contain("public");
        facts.Schemas.Should().Contain(new SchemaCreateRow("public", false), "public is examined, and PUBLIC holds no CREATE on it from 15");
    }

    // ---- S07 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>pgstattuple</c> ships 1.4 and 1.5 in the image, 1.5 the default: installed at 1.4, S07 names it; after
    /// <c>ALTER EXTENSION … UPDATE</c>, it is quiet.
    /// </summary>
    [PostgresFact]
    [HealthFires("S07")]
    [HealthQuiet("S07")]
    public async Task S07_fires_for_pgstattuple_installed_at_an_old_version_and_is_quiet_once_it_is_updated()
    {
        MatrixServer server = await ServerAsync(17);
        string database = await server.CreateDatabaseAsync("s2_s07_" + PostgresVersionMatrix.RunId, Token);

        string? oldest = await ScalarAsync<string>(server, database,
            "select v.version from pg_catalog.pg_available_extension_versions v join pg_catalog.pg_available_extensions a on a.name = v.name "
            + "where v.name = 'pgstattuple' and v.version <> a.default_version order by v.version limit 1");
        oldest.Should().Be("1.4", "the image ships pgstattuple 1.4 beside its default 1.5, which is what this test installs");

        await server.ExecuteAsync("create extension pgstattuple version '1.4'", database, Token);

        await using (PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App, database)))
        {
            HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S07");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            HealthFinding finding = result.Findings.Should().ContainSingle().Subject;
            finding.Subject.Should().Be("pgstattuple");
            finding.Title.Should().Be("Extension pgstattuple is at 1.4; the server offers 1.5");
        }

        await server.ExecuteAsync("alter extension pgstattuple update", database, Token);

        await using (PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App, database)))
        {
            HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S07");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            result.Findings.Should().BeEmpty();
        }
    }

    // ---- S08 ----------------------------------------------------------------------------------------------------------

    /// <summary>A login whose password expires in three days: S08 is Critical.</summary>
    [PostgresFact]
    [HealthFires("S08")]
    public async Task S08_fires_for_a_login_whose_password_expires_in_three_days()
    {
        MatrixServer server = await ServerAsync(17);
        string role = "s2_expiring_" + PostgresVersionMatrix.RunId;
        string until = DateTimeOffset.UtcNow.AddDays(3).ToString("yyyy-MM-dd HH:mm:ss'+00'", CultureInfo.InvariantCulture);

        await server.ExecuteAsync("create role " + role + " login password '" + role + "' valid until '" + until + "'", cancellationToken: Token);

        try
        {
            await using PostureHost host = await PostureHost.CreateAsync(Login(server, role));
            HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S08");

            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            result.Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Critical);
        }
        finally
        {
            await DropRoleAsync(server, role);
        }
    }

    /// <summary>B0's <c>app</c> has no <c>VALID UNTIL</c>: S08 is quiet.</summary>
    [PostgresFact]
    [HealthQuiet("S08")]
    public async Task S08_is_quiet_for_a_login_with_no_expiry()
    {
        MatrixServer server = await ServerAsync(17);

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App));
        HealthCheckResult result = PostureHost.Result(await host.SweepAsync(), "S08");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
        result.Findings.Should().BeEmpty();
    }

    // ---- S09 and S10 --------------------------------------------------------------------------------------------------

    /// <summary>
    /// A login whose own settings switch the sequential scan off and start every transaction read-only: S09 names
    /// <c>enable_seqscan</c>, set by the role, and S10's <c>read-only</c> is Critical - each as the login's sessions see it.
    /// </summary>
    [PostgresFact]
    [HealthFires("S09")]
    [HealthFires("S10")]
    public async Task S09_and_S10_fire_for_a_login_whose_own_settings_switch_a_planner_method_off_and_start_read_only()
    {
        MatrixServer server = await ServerAsync(17);
        string role = "s2_settings_" + PostgresVersionMatrix.RunId;

        await server.ExecuteAsync(
            "create role " + role + " login password '" + role + "';"
            + "alter role " + role + " set enable_seqscan = off;"
            + "alter role " + role + " set default_transaction_read_only = on",
            cancellationToken: Token);

        try
        {
            await using PostureHost host = await PostureHost.CreateAsync(Login(server, role));
            HealthSweep sweep = await host.SweepAsync();

            HealthCheckResult s09 = PostureHost.Result(sweep, "S09");
            s09.Outcome.Should().Be(HealthCheckOutcome.Checked, s09.Problem?.Reason);
            HealthFinding planner = s09.Findings.Should().ContainSingle().Subject;
            planner.Subject.Should().Be("enable_seqscan");
            planner.Evidence.Should().Contain(x => x.Label == "Set by" && x.Value == "the role's own settings (ALTER ROLE … SET)");
            planner.Remedy.Should().BeNull("a role's setting is reset by its owner, in words");

            HealthCheckResult s10 = PostureHost.Result(sweep, "S10");
            s10.Outcome.Should().Be(HealthCheckOutcome.Checked, s10.Problem?.Reason);
            HealthFinding readOnly = s10.Findings.Should().ContainSingle().Subject;
            readOnly.Variant.Should().Be("read-only");
            readOnly.Severity.Should().Be(HealthSeverity.Critical);
        }
        finally
        {
            await DropRoleAsync(server, role);
        }
    }

    /// <summary>B0's <c>app</c> plans with every method and writes on a primary: S09 and S10 are quiet.</summary>
    [PostgresFact]
    [HealthQuiet("S09")]
    [HealthQuiet("S10")]
    public async Task S09_and_S10_are_quiet_for_a_login_at_the_servers_defaults_on_a_primary()
    {
        MatrixServer server = await ServerAsync(17);

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App));
        HealthSweep sweep = await host.SweepAsync();

        foreach (string code in (string[]) ["S09", "S10"])
        {
            HealthCheckResult result = PostureHost.Result(sweep, code);
            result.Outcome.Should().Be(HealthCheckOutcome.Checked, result.Problem?.Reason);
            result.Findings.Should().BeEmpty(code + " has nothing to say about a login at the defaults");
        }
    }

    // ---- K05 and K06 --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every capability on, <c>BrowsableSchemas</c> with <c>"*"</c> and no <c>SqlConsoleRole</c>: K05 and K06 both fire,
    /// through a live host's own options.
    /// </summary>
    [PostgresFact]
    [HealthFires("K05")]
    [HealthFires("K06")]
    public async Task K05_and_K06_fire_for_a_console_and_a_browser_with_no_role_to_narrow_them()
    {
        MatrixServer server = await ServerAsync(17);

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App), options =>
        {
            options.Capabilities = PostgresStudioCapabilities.All();
            options.BrowsableSchemas.Add("*");
        });

        HealthSweep sweep = await host.SweepAsync();

        PostureHost.Result(sweep, "K05").Findings.Should().ContainSingle().Which.WhyItMatters.Should().Contain("PostgresStudioOptions.SqlConsoleRole");
        PostureHost.Result(sweep, "K06").Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning);
    }

    /// <summary>The same with <c>SqlConsoleRole</c> set to a role the login is a member of: both are quiet.</summary>
    [PostgresFact]
    [HealthQuiet("K05")]
    [HealthQuiet("K06")]
    public async Task K05_and_K06_are_quiet_once_SqlConsoleRole_narrows_them()
    {
        MatrixServer server = await ServerAsync(17);

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.ConsoleLogin), options =>
        {
            options.Capabilities = PostgresStudioCapabilities.All();
            options.BrowsableSchemas.Add("*");
            options.SqlConsoleRole = "console_role";
        });

        HealthSweep sweep = await host.SweepAsync();

        foreach (string code in (string[]) ["K05", "K06"])
        {
            HealthCheckResult result = PostureHost.Result(sweep, code);
            result.Outcome.Should().Be(HealthCheckOutcome.Checked);
            result.Findings.Should().BeEmpty();
        }
    }

    /// <summary>
    /// <c>ReadOnly</c> mutes both, whatever the capabilities say: it turns <c>RunSql</c> and <c>BrowseDatabase</c> off.
    /// </summary>
    [PostgresFact]
    public async Task K05_and_K06_are_quiet_under_ReadOnly()
    {
        MatrixServer server = await ServerAsync(17);

        await using PostureHost host = await PostureHost.CreateAsync(server.ConnectionString(MatrixRole.App), options =>
        {
            options.Capabilities = PostgresStudioCapabilities.All();
            options.BrowsableSchemas.Add("*");
            options.ReadOnly = true;
        });

        HealthSweep sweep = await host.SweepAsync();

        PostureHost.Result(sweep, "K05").Findings.Should().BeEmpty();
        PostureHost.Result(sweep, "K06").Findings.Should().BeEmpty();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private async Task<MatrixServer> ServerAsync(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();
        return server;
    }

    /// <summary>A connection string as a login a test made, to the seeded database.</summary>
    internal static string Login(MatrixServer server, string role, string database = MatrixServer.Database) =>
        new NpgsqlConnectionStringBuilder(server.ConnectionString(MatrixRole.App, database)) { Username = role, Password = role }.ConnectionString;

    internal static Task DropRoleAsync(MatrixServer server, string role) =>
        server.ExecuteAsync("drop role if exists " + role, cancellationToken: CancellationToken.None);

    private static async Task<T?> ScalarAsync<T>(MatrixServer server, string database, string sql)
    {
        await using NpgsqlConnection connection = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync(Token);
        return value is null or DBNull ? default : (T) value;
    }
}
