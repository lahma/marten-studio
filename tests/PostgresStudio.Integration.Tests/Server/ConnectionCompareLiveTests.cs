using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Connection;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// 1.1-PASTE on a real server, through the studio's own services in a circuit: a pasted string is compared with the real data
/// source's, the source policy is asked first and with no tenant, the pasted host is never connected to, and the sentinels - the
/// passwords, the option values, a rejected keyword's value, and the data source's own password - are in no model, no log line at
/// Trace and no audit entry.
/// </summary>
public class ConnectionCompareLiveTests(PostgresFixture postgres)
{
    private const string Database = "ps_conn_compare";

    private const string Login = "paste_live_login";

    private const string LivePassword = "S3NT1NEL-live-pw";

    private const string Sentinel = "S3NT1NEL";

    private static readonly JsonSerializerOptions Dump = new() { IncludeFields = true, MaxDepth = 64 };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_pasted_string_is_compared_with_the_real_one_and_nothing_of_it_is_connected_to_logged_or_audited()
    {
        await OwnedDatabases.ExecuteAsync(postgres.ConnectionString, "drop role if exists " + Login, Token);
        OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Database], Token);

        // Something is listening where the pasted string points: the proof it is never connected to is that nothing arrives.
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint) listener.LocalEndpoint).Port;

        try
        {
            await OwnedDatabases.ExecuteAsync(
                postgres.ConnectionString,
                "create role " + Login + " login password '" + LivePassword + "'; grant connect on database " + Database + " to " + Login + ";",
                Token);

            NpgsqlConnectionStringBuilder application = new(owned[0]) { Username = Login, Password = LivePassword, ApplicationName = "paste-live", MaxPoolSize = 30 };

            await using StudioHost host = StudioHost.Create(
                services => services.AddSingleton(_ => NpgsqlDataSource.Create(application.ConnectionString)),
                static options => options.Capabilities = PostgresStudioCapabilities.All());

            await using StudioCircuit circuit = host.Circuit("admin");
            await circuit.Get<StudioState>().EnsureInitializedAsync(Token);
            StudioScope scope = new("default", "default", null);
            IConnectionCompareService service = circuit.Get<IConnectionCompareService>();

            string pasted = "Host=127.0.0.1;Port=" + port + ";Database=" + Database + ";Username=" + Login + ";Application Name=paste-candidate;Maximum Pool Size=60;"
                + "SSL Mode=VerifyFull;Timeout=1;Password=" + Sentinel + "-pw;PWD=" + Sentinel + "-pwd;SSL Password=" + Sentinel + "-ssl;"
                + "Options='-c app.secret=" + Sentinel + "-opt'";

            ConnectionCompareView view = await service.CompareAsync(scope with { TenantId = "ignored" }, pasted, Token);

            view.Outcome.Should().Be(ConnectionCompareOutcome.Compared, view.Reason);
            view.CurrentReadable.Should().BeTrue();

            ConnectionCompareRow name = view.Rows.Single(static x => x.Keyword == "Application Name");
            name.Current!.Value.Should().Be("paste-live");
            name.Pasted.Value.Should().Be("paste-candidate");
            name.Differs.Should().BeTrue();

            view.Rows.Single(static x => x.Keyword == "Maximum Pool Size").Pasted.Value.Should().Be("60");
            view.Rows.Single(static x => x.Keyword == "Database").Differs.Should().BeFalse("the same database, whichever string says it");
            view.Rows.Single(static x => x.Keyword == "Password").Differs.Should().BeFalse("the data source's own password cannot be seen, so it is never compared");
            view.RemovedSecrets.Should().Equal("Password", "SSL Password");

            listener.Pending().Should().BeFalse("the pasted string was parsed and never connected to");

            IReadOnlyList<StudioActionLogEntry> ring = await circuit.Get<StudioActionLog>()
                .GetVisibleAsync(circuit.Get<StudioAuthorization>(), circuit.Get<StudioCapabilityGuard>(), 50, Token);

            ring.Should().BeEmpty("a comparison reads nothing and runs nothing, so it is not audited");

            string everything = JsonSerializer.Serialize(view, Dump)
                + string.Join('\n', host.Logs.Lines.Select(static x => x.Message))
                + string.Join('\n', ring.Select(static x => x.Message));

            everything.Should().NotContain(Sentinel).And.NotContain(LivePassword, "neither the pasted passwords nor the data source's own are anywhere");
            host.Logs.WarningsOrWorse.Should().BeEmpty();

            ConnectionCompareView refused = await service.CompareAsync(scope, "Host=a;Bogus=" + Sentinel + "-bad", Token);
            refused.Outcome.Should().Be(ConnectionCompareOutcome.Rejected);
            refused.Reason.Should().Be("Npgsql does not know the keyword \"Bogus\".");
            string.Join('\n', host.Logs.Lines.Select(static x => x.Message)).Should().NotContain(Sentinel);
        }
        finally
        {
            await owned.DisposeAsync();
            await OwnedDatabases.ExecuteAsync(postgres.ConnectionString, "drop role if exists " + Login, CancellationToken.None);
        }
    }

    [PostgresFact]
    public async Task A_visitor_the_source_policy_refuses_is_told_so_and_the_policy_was_asked_about_the_whole_database()
    {
        OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Database + "_gate"], Token);

        try
        {
            await using StudioHost host = StudioHost.Create(
                services => services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0])),
                static options => options.Capabilities = PostgresStudioCapabilities.All(),
                sourcePolicy: static (_, user) => user.Identity?.Name == "friend");

            await using StudioCircuit circuit = host.Circuit("stranger");
            await circuit.Get<StudioState>().EnsureInitializedAsync(Token);

            ConnectionCompareView view = await circuit.Get<IConnectionCompareService>()
                .CompareAsync(new StudioScope("default", "default", "acme"), "Host=a;Password=" + Sentinel, Token);

            view.Outcome.Should().Be(ConnectionCompareOutcome.Refused);
            view.Reason.Should().Contain("PostgresStudioOptions.SourceAuthorizationPolicy");
            view.Rows.Should().BeEmpty();
            host.Calls.All.Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.Capability == null, "no capability: the page's own gate");
            string.Join('\n', host.Logs.Lines.Select(static x => x.Message)).Should().NotContain(Sentinel);
        }
        finally
        {
            await owned.DisposeAsync();
        }
    }
}
