using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;

namespace PostgresStudio.Integration.Tests.Diagnostics;

/// <summary>
/// The diagnostics API live (API design §5.4): G9's connections by application as <c>app</c> and as <c>probe</c>, with idle in
/// transaction and a lock wait produced on purpose; a full snapshot with <c>IncludeActivity</c> and <c>RunDeeperChecks</c>
/// that creates nothing; three databases behind a host's own <see cref="IPostgresDatabaseSource" />, one disposed mid-run;
/// and a host that registers the studio for the API alone and maps nothing.
/// </summary>
public class DiagnosticsLiveTests(PostgresVersionMatrix matrix, PostgresFixture postgres)
{
    private const int Major = 17;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PostgresDiagnosticsRequest Connections(bool activity) => new() { Findings = false, Connections = true, IncludeActivity = activity };

    public static TheoryData<MatrixRole> Roles => [MatrixRole.App, MatrixRole.Probe];

    [PostgresTheory]
    [MemberData(nameof(Roles))]
    public async Task Connections_by_application_count_each_applications_sessions_and_name_them_only_with_IncludeActivity(MatrixRole role)
    {
        MatrixServer server = await matrix.ServerAsync(Major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("ms_dx_g9_" + role.ToString().ToLowerInvariant(), Token);
        await using (NpgsqlConnection owner = await server.OpenAsync(MatrixRole.App, database, Token))
        await using (NpgsqlCommand create = new("create table public.jobs (id int primary key); insert into public.jobs values (1)", owner))
        {
            await create.ExecuteNonQueryAsync(Token);
        }

        // As app, the sessions' own role: two of dx-a (one idle, one holding a lock in an open transaction) and two of dx-b
        // (one idle, one waiting on that lock).
        await using NpgsqlConnection idleA = await OpenAsync(server, database, "dx-a");
        await using NpgsqlConnection holder = await OpenAsync(server, database, "dx-a");
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand take = new("lock table public.jobs in access exclusive mode", holder, held))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        await using NpgsqlConnection idleB = await OpenAsync(server, database, "dx-b");
        await using NpgsqlConnection waiter = await OpenAsync(server, database, "dx-b");
        await using NpgsqlCommand blocked = new("select count(*) from public.jobs", waiter) { CommandTimeout = 120 };
        Task<object?> waiting = blocked.ExecuteScalarAsync(Token);

        await WaitUntilWaitingAsync(server, database);

        try
        {
            await using MatrixHost host = await MatrixHost.CreateAsync(server, role, database);
            IPostgresDiagnostics diagnostics = host.Services.GetRequiredService<IPostgresDiagnostics>();
            PostgresDatabaseRef target = new("default", "");

            PostgresDiagnosticsSnapshot without = await diagnostics.GetSnapshotAsync(target, Connections(activity: false), Token);
            PostgresDiagnosticsSnapshot with = await diagnostics.GetSnapshotAsync(target, Connections(activity: true), Token);

            without.Connections!.Outcome.Should().Be(PostgresReadOutcome.Read, without.Connections.Reason);
            without.Connections.ByApplication.Should().BeEmpty("names are Activity-grade");
            without.Connections.Database.Total.Should().BeGreaterThanOrEqualTo(4, "the four sessions are this database's, whoever reads them");
            without.Connections.MaxConnections.Should().BePositive();
            without.Withheld.Should().ContainKey("connections.byApplication");
            without.ToJson().Should().NotContain("dx-a").And.NotContain("dx-b");

            PostgresConnectionCounts a = with.Connections!.ByApplication.Should().ContainSingle(static x => x.ApplicationName == "dx-a").Subject;
            PostgresConnectionCounts b = with.Connections.ByApplication.Should().ContainSingle(static x => x.ApplicationName == "dx-b").Subject;
            a.Total.Should().Be(2);
            b.Total.Should().Be(2);

            if (role == MatrixRole.App)
            {
                // The applications' rows are app's own sessions, whose states it reads. (The database's row may still count a
                // session of another role - the superuser's that seeded the table - as state unknown.)
                (a.Idle, a.IdleInTransaction, a.StateUnknown).Should().Be((1, 1, 0));
                (b.Idle, b.Active, b.Waiting, b.StateUnknown).Should().Be((1, 1, 1, 0));
                a.OldestTransaction.Should().NotBeNull("the holder's transaction is open");
            }
            else
            {
                with.Connections.AllSessionsVisible.Should().BeFalse("probe may not read another role's session states");
                (a.StateUnknown, b.StateUnknown).Should().Be((2, 2), "counted, whatever the role may read of them");
                (b.Waiting, a.IdleInTransaction).Should().Be((0, 0), "a state the role may not read is never guessed");
            }
        }
        finally
        {
            await held.RollbackAsync(Token);
            await waiting;
        }
    }

    [PostgresFact]
    public async Task A_full_snapshot_with_activity_and_deeper_checks_creates_nothing()
    {
        MatrixServer server = await matrix.ServerAsync(Major);
        server.SkipIfUnavailable();

        string database = await server.CreateDatabaseAsync("ms_dx_noddl", Token);
        await server.ExecuteAsync(
            "create table public.orders (id bigint primary key, customer int, total numeric); "
            + "insert into public.orders select g, g % 50, g from pg_catalog.generate_series(1, 2000) g; "
            + "create index orders_customer on public.orders (customer); analyze public.orders",
            database,
            Token);

        IReadOnlySet<string> before = await server.SnapshotAsync(database, Token);

        await using (MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database))
        {
            PostgresDiagnosticsSnapshot snapshot = await host.Services.GetRequiredService<IPostgresDiagnostics>().GetSnapshotAsync(
                new PostgresDatabaseRef("default", ""),
                PostgresDiagnosticsRequest.AllSections with { IncludeActivity = true, RunDeeperChecks = true },
                Token);

            snapshot.Outcome.Should().Be(PostgresReadOutcome.Read, snapshot.Reason);
            snapshot.Server!.VersionNumber.Should().Be(server.VersionNumber);
            snapshot.Checks.Should().NotBeEmpty();
            snapshot.Storage!.Outcome.Should().Be(PostgresReadOutcome.Read, snapshot.Storage.Reason);
            snapshot.Storage.LargestTables.Should().Contain(static x => x.Schema == "public" && x.Name == "orders" && x.TotalBytes > 0);
            snapshot.Storage.GrowthBytesPerDay.Should().BeNull("one reading is no window");
            snapshot.Storage.SizesAreEstimates.Should().BeTrue();
            snapshot.Settings!.Outcome.Should().Be(PostgresReadOutcome.Read, snapshot.Settings.Reason);
            snapshot.Settings.Settings.Should().Contain(static x => x.Name == "shared_buffers" && x.Value != null);
            snapshot.Settings.Settings.Should().OnlyContain(static x => !x.PendingRestart);
            snapshot.Settings.Extensions.Should().Contain(static x => x.Name == "pg_stat_statements" && x.InstalledVersion == null,
                "it is installed in the server's postgres database, not in this one");
            snapshot.Statements!.Outcome.Should().NotBe(PostgresReadOutcome.Read, "pg_stat_statements is not installed in this database");
            snapshot.Connections!.Outcome.Should().Be(PostgresReadOutcome.Read, snapshot.Connections.Reason);
            snapshot.Withheld.Should().BeEmpty();
        }

        MatrixServer.AssertUnchanged(before, await server.SnapshotAsync(database, Token), "a snapshot reads; it never creates, alters or drops anything");
    }

    [PostgresFact]
    public async Task Three_databases_behind_a_database_source_list_without_a_data_source_and_one_disposed_fails_alone()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ms_dx_admin", "ms_dx_t1", "ms_dx_t2", "ms_dx_t3"], Token);
        await using TenantDatabases tenants = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["t1"] = owned[1],
            ["t2"] = owned[2],
            ["t3"] = owned[3],
        });

        ManualClock clock = new(DateTimeOffset.UtcNow);
        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(owned[0]);

        await using StudioHost host = StudioHost.Create(
            services => services.AddSingleton<TimeProvider>(clock),
            studio =>
            {
                studio.IncludeDefaultDataSource = false;
                studio.AddDataSource("admin", _ => admin);
                studio.AddDatabaseSource("tenants", _ => tenants);
            });

        IPostgresDiagnostics diagnostics = host.Services.GetRequiredService<IPostgresDiagnostics>();

        List<PostgresDatabaseRef> listed = [];
        await foreach (PostgresDatabaseRef database in diagnostics.ListDatabasesAsync(Token))
        {
            listed.Add(database);
        }

        listed.Should().Equal(
            new PostgresDatabaseRef("admin", "admin"),
            new PostgresDatabaseRef("tenants", "t1"),
            new PostgresDatabaseRef("tenants", "t2"),
            new PostgresDatabaseRef("tenants", "t3"));
        tenants.Built.Should().Be(0, "listing builds no data source");

        foreach (PostgresDatabaseRef database in listed)
        {
            PostgresDiagnosticsSnapshot snapshot = await diagnostics.GetSnapshotAsync(database, PostgresDiagnosticsRequest.FindingsOnly, Token);
            snapshot.Outcome.Should().Be(PostgresReadOutcome.Read, database + ": " + snapshot.Reason);
            snapshot.Database.Should().Be(database);
        }

        await tenants.DisposeOneAsync("t2");
        clock.Advance(TimeSpan.FromMinutes(5));

        Dictionary<string, PostgresDiagnosticsSnapshot> after = [];
        foreach (PostgresDatabaseRef database in listed.Skip(1))
        {
            after[database.Database] = await diagnostics.GetSnapshotAsync(database, PostgresDiagnosticsRequest.FindingsOnly, Token);
        }

        after["t2"].Outcome.Should().Be(PostgresReadOutcome.Failed, "its data source was disposed: " + after["t2"].Reason);
        after["t2"].Reason.Should().NotBeNullOrWhiteSpace();
        after["t1"].Outcome.Should().Be(PostgresReadOutcome.Read, after["t1"].Reason);
        after["t3"].Outcome.Should().Be(PostgresReadOutcome.Read, after["t3"].Reason);

        PostgresDiagnosticsSnapshot unknown = await diagnostics.GetSnapshotAsync(new PostgresDatabaseRef("tenants", "t9"), PostgresDiagnosticsRequest.FindingsOnly, Token);
        unknown.Outcome.Should().Be(PostgresReadOutcome.NotFound);
    }

    [PostgresFact]
    public async Task A_host_that_registers_the_studio_and_maps_nothing_starts_serves_no_studio_and_answers_a_snapshot()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ms_dx_api_only"], Token);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<AuthenticationStateProvider, NobodyAuthenticationStateProvider>();
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(owned[0]));
        builder.Services.AddPostgresStudio();

        await using WebApplication app = builder.Build();
        app.MapGet("/", () => "the host's own page");

        await app.StartAsync(Token);

        try
        {
            ((IEndpointRouteBuilder) app).DataSources.SelectMany(static x => x.Endpoints).OfType<RouteEndpoint>()
                .Select(static x => x.RoutePattern.RawText ?? string.Empty)
                .Should().OnlyContain(static x => x == "/", "the API maps nothing; only the host's own endpoint is there");

            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using HttpClient client = new() { BaseAddress = new Uri(address) };

            (await client.GetAsync("/postgres", Token)).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            (await client.GetAsync("/postgres/health", Token)).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
            (await client.GetStringAsync("/", Token)).Should().Be("the host's own page");

            PostgresDiagnosticsSnapshot snapshot = await app.Services.GetRequiredService<IPostgresDiagnostics>()
                .GetSnapshotAsync(new PostgresDatabaseRef("default", ""), PostgresDiagnosticsRequest.FindingsOnly, Token);

            snapshot.Outcome.Should().Be(PostgresReadOutcome.Read, snapshot.Reason);
            snapshot.Database.Should().Be(new PostgresDatabaseRef("default", "default"));
            snapshot.Producer.Should().StartWith("PostgresStudio ");
            snapshot.Checks.Should().NotBeEmpty();
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<NpgsqlConnection> OpenAsync(MatrixServer server, string database, string application)
    {
        NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(server.ConnectionString(MatrixRole.App, database))
        {
            ApplicationName = application,
            Pooling = false,
        }.ConnectionString);

        await connection.OpenAsync(Token);
        return connection;
    }

    /// <summary>Until <c>pg_locks</c> shows the waiter waiting: a condition, never a delay.</summary>
    private static async Task WaitUntilWaitingAsync(MatrixServer server, string database)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));

        await using NpgsqlConnection probe = new(new NpgsqlConnectionStringBuilder(server.ConnectionString(MatrixRole.Superuser, database)) { Pooling = false }.ConnectionString);
        await probe.OpenAsync(deadline.Token);

        do
        {
            await using NpgsqlCommand command = new(
                "select exists (select 1 from pg_catalog.pg_locks l join pg_catalog.pg_stat_activity a on a.pid = l.pid "
                + "where not l.granted and a.application_name = 'dx-b')",
                probe);

            if ((bool) (await command.ExecuteScalarAsync(deadline.Token))!)
            {
                return;
            }
        }
        while (await timer.WaitForNextTickAsync(deadline.Token));
    }

    /// <summary>A host's own database source: one database per tenant, each data source built on first use and counted.</summary>
    private sealed class TenantDatabases(IReadOnlyDictionary<string, string> connectionStrings) : IPostgresDatabaseSource, IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, NpgsqlDataSource> built = new(StringComparer.Ordinal);
        private int count;

        public int Built => count;

        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string name in connectionStrings.Keys.Order(StringComparer.Ordinal))
            {
                await Task.Yield();
                yield return name;
            }
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NpgsqlDataSource?>(connectionStrings.TryGetValue(name, out string? connectionString)
                ? built.GetOrAdd(name, _ =>
                {
                    Interlocked.Increment(ref count);
                    return NpgsqlDataSource.Create(connectionString);
                })
                : null);

        public async Task DisposeOneAsync(string name)
        {
            if (built.TryGetValue(name, out NpgsqlDataSource? dataSource))
            {
                await dataSource.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (NpgsqlDataSource dataSource in built.Values)
            {
                await dataSource.DisposeAsync();
            }
        }
    }

    private sealed class NobodyAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
