using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// 1.1-NP3 on a real server, through the studio's own services in a circuit: the Connection page of a data source the test made
/// - its role, its application name, its pool - what the server reports of the studio's connection, and the login's sessions by
/// application behind the activity gate. The login's password is unique to the test and must be nowhere the page, the audit ring
/// or the log can show it: the data source only works with it, so it was certainly in play.
/// </summary>
public class ConnectionPageLiveTests(PostgresFixture postgres)
{
    private const string Database = "ps_conn_page";

    private const string Login = "np3_live_login";

    private const string Password = "S3NT1NEL-live-pw";

    private static readonly JsonSerializerOptions Dump = new() { IncludeFields = true, MaxDepth = 64 };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_page_reads_the_real_connection_the_servers_view_of_it_and_the_logins_sessions_and_shows_no_password()
    {
        await OwnedDatabases.ExecuteAsync(postgres.ConnectionString, "drop role if exists " + Login, Token);
        OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Database], Token);

        try
        {
            await OwnedDatabases.ExecuteAsync(
                postgres.ConnectionString,
                "create role " + Login + " login password '" + Password + "'; grant connect on database " + Database + " to " + Login + ";",
                Token);

            NpgsqlConnectionStringBuilder application = new(owned[0]) { Username = Login, Password = Password, ApplicationName = "np3-live", MaxPoolSize = 30 };
            NpgsqlConnectionStringBuilder other = new(application.ConnectionString) { ApplicationName = "np3-other" };

            await using StudioHost host = StudioHost.Create(
                services => services.AddSingleton(_ => NpgsqlDataSource.Create(application.ConnectionString)),
                static options => options.Capabilities = PostgresStudioCapabilities.All());

            // A second application of the same login, held open while the page reads.
            await using NpgsqlConnection extra = new(other.ConnectionString);
            await extra.OpenAsync(Token);

            await using StudioCircuit circuit = host.Circuit("admin");
            await circuit.Get<StudioState>().EnsureInitializedAsync(Token);
            StudioScope scope = new("default", "default", null);
            IConnectionInsightService service = circuit.Get<IConnectionInsightService>();

            ConnectionPageView view = await service.GetAsync(scope, Token);

            view.Refusal.Should().Be(ConnectionRefusal.None, view.Reason);
            view.Readable.Should().BeTrue();
            view.Header.Single(static x => x.Label == "Role").Value.Should().Be(Login);
            view.Header.Single(static x => x.Label == "Database").Value.Should().Be(Database);
            view.Header.Single(static x => x.Label == "Pool").Value.Should().StartWith("max 30");
            view.Tls.Text.Should().StartWith("not encrypted", "a plain container has no TLS, and the role can see its own pg_stat_ssl row");

            ConnectionSettingView name = view.Settings.Single(static x => x.Row.Keyword == "Application Name");
            name.Row.Value.Should().Be("np3-live");
            name.From.Should().Be("connection string");
            view.Settings.Single(static x => x.Row.Keyword == "Maximum Pool Size").Row.Value.Should().Be("30");
            view.Settings.Single(static x => x.Row.Keyword == "Password").From.Should().Be("not visible");

            view.Observed.NotRead.Should().BeNull();
            view.Observed.Lines.Single(static x => x.Label == "Logged in as").Value.Should().Be(Login);
            view.Observed.Lines.Single(static x => x.Label == "Database").Value.Should().Be(Database);
            view.Observed.Lines.Single(static x => x.Label == "statement_timeout").Value.Should().Be("off", "what a new session of the login starts with, not the studio's transaction's");
            view.Activity.IsOpen.Should().BeTrue("the host switched every capability on and set no policy");

            ConnectionApplicationsView applications = await service.GetApplicationsAsync(scope, Token);

            applications.Refusal.Should().Be(DatabaseRefusal.None, applications.Reason);
            applications.Role.Should().Be(Login);
            ConnectionApplicationRow row = applications.Rows.Should().Contain(static x => x.Application == "np3-other").Subject;
            row.Sessions.Should().Be(1);
            row.TlsKnown.Should().Be(1);
            row.TlsOn.Should().Be(0);
            row.OldestConnection.Should().NotBeNull();

            IReadOnlyList<StudioActionLogEntry> ring = await circuit.Get<StudioActionLog>()
                .GetVisibleAsync(circuit.Get<StudioAuthorization>(), circuit.Get<StudioCapabilityGuard>(), 50, Token);

            ring.Should().Contain(static x => x.Action == ConnectionInsightService.ApplicationsAction && x.Succeeded);

            string recorded = string.Join('\n', host.Logs.Lines.Select(static x => x.Message)) + string.Join('\n', ring.Select(static x => x.Message));
            string everything = JsonSerializer.Serialize(view, Dump) + JsonSerializer.Serialize(applications, Dump) + recorded;

            everything.Should().NotContain(Password, "the password is in no model, no log line and no audit entry");
            recorded.Should().NotContain("np3-other", "the ring and the log name no application: only the page does");
            host.Logs.WarningsOrWorse.Should().BeEmpty();
        }
        finally
        {
            // The login's sessions end with the database; the role is the cluster's, and is dropped here.
            await owned.DisposeAsync();
            await OwnedDatabases.ExecuteAsync(postgres.ConnectionString, "drop role if exists " + Login, CancellationToken.None);
        }
    }
}
