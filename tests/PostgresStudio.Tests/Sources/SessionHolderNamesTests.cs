using System.Runtime.CompilerServices;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// Which databases of the server Database › Sessions › Holders names to a visitor (issue #20, D29, D30), through the real
/// container and service: this database always; another only when a registered source holds a database of that name on that
/// server and the source policy admits it - asked by registered key and database identity, with no tenant and no capability;
/// a source whose databases can change while the process runs is never enumerated (D27); and past
/// <c>ViewDatabaseActivity</c> every database is named, the write policy asked about this database alone.
/// </summary>
public class SessionHolderNamesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset ReadAt = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static string ConnectionString(string database, string host = "127.0.0.1") =>
        "Host=" + host + ";Port=1;Database=" + database + ";Username=app;Password=secret;Timeout=1";

    private static SessionHolderGroup Group(string database, string application, long total, bool thisDatabase = false) =>
        new(database, application, "app_user", "10.0.3.7", false, total, 0, total, 0, 0, TimeSpan.FromMinutes(1), null, thisDatabase);

    /// <summary>The server's sessions: this database <c>app</c>, two registered on this server, one registered on another, a tenant, a stranger.</summary>
    private static SessionServerHolders Holders() => new(
        true,
        100,
        3,
        0,
        170000,
        41,
        6,
        [
            Group("app", "billing-api", 10, thisDatabase: true),
            Group("billing", "billing-api", 9),
            Group("secret", "payroll-sentinel", 8),
            Group("reports", "bi-sentinel", 7),
            Group("acme", "tenant-sentinel", 4),
            Group("analytics", "etl-sentinel", 2),
        ]);

    private sealed class FakeReader : ISessionActivityReader
    {
        public Task<SessionSnapshot> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken) => Task.FromResult(
            new SessionSnapshot(ReadAt, SessionVisibility.Everything, 0, [], 0, [], 10, [], [new HealthStatement("select 1", [])]) { Holders = Holders() });
    }

    /// <summary>A host's own list of tenant databases: a source whose databases can change while the process runs.</summary>
    private sealed class FakeTenants : IPostgresDatabaseSource
    {
        private int lists;

        public int Lists => Volatile.Read(ref lists);

        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref lists);
            await Task.CompletedTask;
            yield return "acme";
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NpgsqlDataSource?>(NpgsqlDataSource.Create(ConnectionString(name)));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope circuit;

        public Harness(bool policies, Action<PostgresStudioOptions>? configure = null)
        {
            Policy = new TestSourceAuthorizationService();
            Tenants = new FakeTenants();

            ServiceCollection services = new();
            services.AddLogging();
            services.AddSingleton(NpgsqlDataSource.Create(ConnectionString("app")));
            services.AddSingleton<IPostgresDatabaseSource>(Tenants);
            services.AddPostgresStudio(options =>
            {
                if (policies)
                {
                    options.SourceAuthorizationPolicy = "src";
                    options.WriteAuthorizationPolicy = "write";
                }

                options.AddDataSource("billing", static _ => NpgsqlDataSource.Create(ConnectionString("billing")));
                options.AddDataSource("secret", static _ => NpgsqlDataSource.Create(ConnectionString("secret")));
                options.AddDataSource("reports", static _ => NpgsqlDataSource.Create(ConnectionString("reports", host: "localhost")));
                options.AddDatabaseSource("tenants", static sp => sp.GetRequiredService<IPostgresDatabaseSource>());
                configure?.Invoke(options);
            });
            services.AddSingleton<IAuthorizationService>(Policy);
            services.AddScoped<AuthenticationStateProvider, TestAuthenticationStateProvider>();
            services.AddSingleton<ISessionActivityReader>(new FakeReader());

            provider = services.BuildServiceProvider();
            circuit = provider.CreateAsyncScope();
        }

        public TestSourceAuthorizationService Policy { get; }

        public FakeTenants Tenants { get; }

        public ISessionActivityService Sessions => circuit.ServiceProvider.GetRequiredService<ISessionActivityService>();

        public async ValueTask DisposeAsync()
        {
            await circuit.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private static readonly StudioScope Default = new("default", "default", null);

    // ----------------------------------------------------------------------------------------------
    // A visitor short of the gate
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_database_is_named_only_when_a_registered_source_on_this_server_holds_it_and_the_policy_admits_it()
    {
        await using Harness harness = new(policies: true);
        harness.Policy.Allow(static resource => resource.DatabaseIdentifier != "secret");

        SessionActivitySummary summary = await harness.Sessions.GetSummaryAsync(Default, Token);

        summary.Refusal.Should().Be(DatabaseRefusal.None, summary.Reason);
        summary.Holders.Should().NotBeNull();
        SessionHoldersSummary holders = summary.Holders!;

        holders.Databases.Select(static x => x.Database).Should().Equal(["app", "billing"],
            "this database, and the one registered database on this server the policy admits");
        holders.Others.Should().NotBeNull();
        holders.Others!.Total.Should().Be(8 + 7 + 4 + 2, "secret (refused), reports (another server), acme (a dynamic source's) and analytics (nobody's), together");
        holders.Others.Database.Should().BeNull();

        System.Text.Json.JsonSerializer.Serialize(summary).Should()
            .NotContain("secret").And.NotContain("reports").And.NotContain("acme").And.NotContain("analytics")
            .And.NotContain("payroll-sentinel").And.NotContain("bi-sentinel").And.NotContain("tenant-sentinel").And.NotContain("etl-sentinel")
            .And.NotContain("10.0.3.7", "no application, role or address is anywhere in a summary");

        // Asked by the registered key and the database's own identity, with no capability - and only about what could be named.
        harness.Policy.Calls.Where(static x => x.Resource.Capability is null && x.Resource.DatabaseIdentifier is not "default")
            .Select(static x => (x.Resource.SourceName, x.Resource.DatabaseIdentifier)).Distinct()
            .Should().BeEquivalentTo([("billing", "billing"), ("secret", "secret")]);
        harness.Policy.Calls.Select(static x => x.Resource.DatabaseIdentifier).Should().NotContain(["reports", "acme", "analytics"],
            "another server's database, a dynamic source's and an unregistered one are never asked about");
    }

    [Fact]
    public async Task A_source_whose_databases_can_change_is_never_enumerated_to_decide_it()
    {
        await using Harness harness = new(policies: true);

        SessionActivitySummary summary = await harness.Sessions.GetSummaryAsync(Default, Token);

        harness.Tenants.Lists.Should().Be(0, "a host-provided source of tenant databases is DynamicMultiple, and enumerating one is not read-only (D27)");
        summary.Holders!.Databases.Select(static x => x.Database).Should().NotContain("acme");
        summary.Holders.Others!.Total.Should().BeGreaterThanOrEqualTo(4, "its database is counted, unnamed");
    }

    [Fact]
    public async Task With_no_policy_configured_the_registered_databases_of_this_server_are_named_and_the_rest_are_not()
    {
        await using Harness harness = new(policies: false);

        SessionActivitySummary summary = await harness.Sessions.GetSummaryAsync(Default, Token);

        summary.Holders!.Databases.Select(static x => x.Database).Should().BeEquivalentTo("app", "billing", "secret");
        summary.Holders.Others!.Total.Should().Be(7 + 4 + 2, "another server's database, a dynamic source's and an unregistered one");
    }

    [Fact]
    public async Task A_policy_that_throws_about_a_database_leaves_it_unnamed_and_the_summary_stands()
    {
        await using Harness harness = new(policies: true);
        harness.Policy.Allow(static resource => resource.DatabaseIdentifier == "billing"
            ? throw new InvalidOperationException("the host's handler failed")
            : true);

        SessionActivitySummary summary = await harness.Sessions.GetSummaryAsync(Default, Token);

        summary.Refusal.Should().Be(DatabaseRefusal.None, summary.Reason);
        summary.Holders!.Databases.Select(static x => x.Database).Should().BeEquivalentTo("app", "secret");
        summary.Holders.Others!.Total.Should().Be(9 + 7 + 4 + 2, "billing's sessions are counted, not named, because its policy could not say yes");
    }

    // ----------------------------------------------------------------------------------------------
    // Past the gate
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Past_the_gate_every_database_is_named_and_the_write_policy_is_asked_about_this_database_alone()
    {
        await using Harness harness = new(policies: true, static options => options.Capabilities = PostgresStudioCapabilities.All());
        harness.Policy.Allow(static resource => resource.Capability is null || resource.DatabaseIdentifier == "default");

        SessionActivitySummary summary = await harness.Sessions.GetSummaryAsync(Default, Token);
        summary.Details.IsOpen.Should().BeTrue();

        SessionActivityDetails details = await harness.Sessions.GetDetailsAsync(Default, Token);

        details.Refusal.Should().Be(DatabaseRefusal.None, details.Reason);
        details.Holders.Databases.Select(static x => x.Database).Should().BeEquivalentTo("app", "billing", "secret", "reports", "acme", "analytics");
        details.Holders.Groups.Select(static x => x.Application).Should().Contain("payroll-sentinel").And.Contain("tenant-sentinel");
        details.Holders.Groups.Should().OnlyContain(static x => x.Role == "app_user" && x.ClientAddress == "10.0.3.7");

        harness.Policy.Calls.Where(static x => x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity))
            .Select(static x => x.Resource.DatabaseIdentifier).Distinct()
            .Should().Equal(["default"], "ViewDatabaseActivity is asked about the database the page is on, as for every session detail");
    }

    [Fact]
    public async Task Short_of_the_gate_the_details_carry_no_holder()
    {
        await using Harness harness = new(policies: true);

        SessionActivityDetails details = await harness.Sessions.GetDetailsAsync(Default, Token);

        details.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        details.Holders.Groups.Should().BeEmpty();
        details.Holders.Databases.Should().BeEmpty();
    }
}
