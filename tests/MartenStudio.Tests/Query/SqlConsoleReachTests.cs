using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Query;

/// <summary>A conjoined document type, so there is a tenant to be scoped to.</summary>
public class ReachTicket
{
    public Guid Id { get; set; }

    public string Subject { get; set; } = string.Empty;
}

/// <summary>
/// DB-0-fix-3, F-a (security): the SQL console reads every tenant's rows, so it is authorized for the
/// database as a whole - and so is everything else <c>RunSql</c> grants.
/// </summary>
/// <remarks>
/// <para>
/// A statement somebody types is sent as it is; nothing puts a tenant into it. The console resolved
/// <c>(db, acme, RunSql)</c>, so a host whose policy allows one tenant handed the whole database to anyone
/// it granted a tenant. <c>RunSql</c> is now asked with <c>TenantId = null</c> everywhere it is asked: the
/// console run, the <c>EXPLAIN</c> for a Mode A clause, and the question that lifts Mode A's nested-read
/// rules - a subquery reads another table with no tenant predicate either.
/// </para>
/// <para>
/// No database: the store is a connection string that is never opened, and every refusal happens before
/// Marten or Postgres is touched. The live half is in <c>SqlConsoleLiveTests</c>.
/// </para>
/// </remarks>
public class SqlConsoleReachTests
{
    private const string ConnectionString =
        "Host=marten-studio-sql-reach.invalid;Database=shared;Username=none;Password=none;Timeout=2";

    private const string Acme = "acme";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The reviewer's case: a policy that allows <c>(db, acme)</c> and nothing wider. The console is refused
    /// from the <c>acme</c> scope, with the tenant-less scope on the exception and in the audit entry.
    /// </summary>
    [Fact]
    public async Task The_console_from_one_tenants_scope_is_refused_as_the_whole_database()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        Func<Task> running = () => harness.Queries.RunSqlAsync(harness.Scope(Acme), new SqlConsoleRequest("select 1"), Token);

        StudioNotAuthorizedException refused = (await running.Should().ThrowAsync<StudioNotAuthorizedException>(
            "the console reads every tenant's rows, and the visitor may address only acme")).Which;

        refused.Scope.TenantId.Should().BeNull();
        refused.Scope.DatabaseId.Should().Be(harness.Database);

        harness.Policies.Calls.Should().NotContain(
            static x => x.Resource.TenantId == Acme && x.Resource.Capability == nameof(StudioCapability.RunSql),
            "RunSql is never asked about a tenant: nothing narrows a statement to one");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(QueryService.RunSqlAction);
        entry.Succeeded.Should().BeFalse();
        entry.TenantId.Should().BeNull("the audit records the scope that was refused - the database as a whole");
    }

    /// <summary>
    /// The positive control: with the database as a whole allowed, the same call passes the gate and fails
    /// at the connection nothing listens at - and the question asked was the tenant-less one, with the
    /// capability on it. The run is audited against that scope too.
    /// </summary>
    [Fact]
    public async Task With_the_whole_database_allowed_the_console_passes_the_gate()
    {
        await using Harness harness = await Harness.CreateAsync();

        SqlConsoleResult result = await harness.Queries.RunSqlAsync(harness.Scope(Acme), new SqlConsoleRequest("select 1"), Token);

        result.Error.Should().NotBeNull("nothing is listening at this connection string");
        result.Error!.SqlState.Should().Be("08006");

        harness.Policies.Calls.Should().Contain(
            x => x.Resource.DatabaseIdentifier == harness.Database
                && x.Resource.TenantId == null
                && x.Resource.Capability == nameof(StudioCapability.RunSql));

        harness.Ring.GetLatest().Should().ContainSingle().Which.TenantId.Should().BeNull();
    }

    /// <summary>
    /// Mode A lifts its nested-read rules for a visitor who may run SQL - and a subquery reads beyond the
    /// tenant as surely as the console does. So "may run SQL" is the console's own question: a visitor
    /// allowed <c>acme</c> and not the database gets the subquery refused, with nothing sent.
    /// </summary>
    [Fact]
    public async Task A_subquery_clause_is_refused_to_a_visitor_allowed_one_tenant_and_not_the_database()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope(Acme),
            new MartenQueryRequest(Harness.Alias, "where id in (select id from public.mt_doc_somebodyelse)"),
            Token);

        result.Rejection.Should().NotBeNull("the nested-read rules stay on for somebody the console would refuse");
        result.Rows.Should().BeEmpty();

        harness.Policies.Calls.Should().Contain(
            x => x.Resource.DatabaseIdentifier == harness.Database && x.Resource.TenantId == null,
            "the question that lifts the rules is the console's, about the database as a whole");
    }

    /// <summary>
    /// The anti-vacuity half: with the database as a whole allowed, the same clause is not refused by the
    /// guard - it goes on to the connection nothing listens at.
    /// </summary>
    [Fact]
    public async Task The_same_subquery_clause_passes_the_guard_when_the_database_is_allowed()
    {
        await using Harness harness = await Harness.CreateAsync();

        Func<Task> running = () => harness.Queries.RunMartenQueryAsync(
            harness.Scope(Acme),
            new MartenQueryRequest(Harness.Alias, "where id in (select id from public.mt_doc_somebodyelse)"),
            Token);

        (await running.Should().ThrowAsync<Exception>("the clause reached the connection"))
            .Which.Should().NotBeOfType<StudioNotAuthorizedException>();
    }

    /// <summary>
    /// And a plain clause - Mode A without <c>RunSql</c>'s lift - still works within the tenant for the
    /// same visitor: it is a read of their own tenant's rows, and needs no more than the tenant.
    /// </summary>
    [Fact]
    public async Task A_plain_clause_still_runs_for_a_visitor_allowed_one_tenant()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        Func<Task> running = () => harness.Queries.RunMartenQueryAsync(
            harness.Scope(Acme), new MartenQueryRequest(Harness.Alias, "where 1 = 1"), Token);

        (await running.Should().ThrowAsync<Exception>("the clause reached the connection"))
            .Which.Should().NotBeOfType<StudioNotAuthorizedException>();
    }

    [Theory]
    [InlineData(Acme)]
    [InlineData(null)]
    public void The_sql_scope_is_the_database_as_a_whole(string? tenantId)
    {
        var scope = new StudioScope("default", "db", tenantId);

        StudioScope sql = QueryService.SqlScope(scope);

        sql.TenantId.Should().BeNull();
        sql.StoreKey.Should().Be("default");
        sql.DatabaseId.Should().Be("db");
    }

    /// <summary>A studio over a conjoined store that is never connected to, with the policy in the test's hand.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public const string Alias = "reachticket";

        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, string database)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Policies = policies;
            Database = database;
        }

        public TestStoreAuthorizationService Policies { get; }

        public string Database { get; }

        public IQueryService Queries => scope.ServiceProvider.GetRequiredService<IQueryService>();

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public StudioScope Scope(string? tenantId) => new(MartenStoreRegistry.DefaultStoreKey, Database, tenantId);

        public static async Task<Harness> CreateAsync()
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(options =>
            {
                options.Connection(ConnectionString);
                options.Events.TenancyStyle = TenancyStyle.Conjoined;
                options.Schema.For<ReachTicket>().MultiTenanted();
            });

            services.AddMartenStudio(options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.StoreAuthorizationPolicy = "studio-store";
                options.WriteAuthorizationPolicy = "studio-write";
                options.KnownTenantIds.Add(Acme);
                options.KnownTenantIds.Add("globex");
            });

            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            return new Harness(provider, policies, databases[0].Id.Identity);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
