using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Query;
using PostgresStudio.Services.Sources;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;

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

        Func<Task> running = () => harness.Console.RunSqlAsync(harness.Scope(Acme), new SqlConsoleRequest("select 1"), Token);

        StudioNotAuthorizedException refused = (await running.Should().ThrowAsync<StudioNotAuthorizedException>(
            "the console reads every tenant's rows, and the visitor may address only acme")).Which;

        refused.Scope.TenantId.Should().BeNull();
        refused.Scope.DatabaseId.Should().Be(harness.Database);

        harness.Policies.Calls.Should().NotContain(
            static x => x.Resource.TenantId == Acme && x.Resource.Capability == nameof(StudioCapability.RunSql),
            "RunSql is never asked about a tenant: nothing narrows a statement to one");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(SqlConsoleService.RunSqlAction);
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

        SqlConsoleResult result = await harness.Console.RunSqlAsync(harness.Scope(Acme), new SqlConsoleRequest("select 1"), Token);

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
    /// H1, item 2 (D29): the question that lifts the nested-read rules names the store and database the scope resolved to -
    /// the registered key and the database's identity - never the visitor's spelling. A handler that refuses <c>RunSql</c>
    /// on the store by the name it was promised is not passed by <c>?store=DEFAULT</c> and no <c>?db=</c>: before, it was,
    /// and the subquery went to the database.
    /// </summary>
    [Fact]
    public async Task The_question_that_lifts_the_nested_read_rules_names_the_registered_key_and_the_databases_identity()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource =>
            resource.Capability != nameof(StudioCapability.RunSql) || resource.StoreName != MartenStoreRegistry.DefaultStoreKey);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            new StudioScope("DEFAULT", string.Empty, null),
            new MartenQueryRequest(Harness.Alias, "where id in (select id from public.mt_doc_somebodyelse)"),
            Token);

        result.Rejection.Should().NotBeNull("the policy refuses RunSql on the store it was asked about by its own name");

        harness.Policies.Calls.Where(static x => x.Resource.Capability == nameof(StudioCapability.RunSql)).Should()
            .NotBeEmpty()
            .And.OnlyContain(x => x.Resource.StoreName == MartenStoreRegistry.DefaultStoreKey && x.Resource.DatabaseIdentifier == harness.Database);
    }

    /// <summary>H1, item 1: an alias the visitor typed reaches the refusal - thrown, audited and logged whole - escaped and bounded.</summary>
    [Fact]
    public async Task An_alias_that_names_nothing_is_refused_with_the_visitors_text_escaped()
    {
        await using Harness harness = await Harness.CreateAsync();
        const string Hostile = "no\r\nsuch\u001b[2J";

        Func<Task> running = () => harness.Queries.RunMartenQueryAsync(harness.Scope(null), new MartenQueryRequest(Hostile, "where 1 = 1"), Token);

        string message = (await running.Should().ThrowAsync<KeyNotFoundException>()).Which.Message;

        message.Should().Contain("'" + StudioLogValue.Cap(Hostile) + "'").And.NotContain("\r").And.NotContain("\u001b");
        harness.Ring.GetLatest().Should().Contain(x => x.Message == message, "the refusal is audited in the same words");
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

    /// <summary>
    /// POLISH P6: the <c>EXPLAIN</c> behind a Mode A result is fetched through the console's gate, directly. From
    /// the <c>acme</c> scope, a policy that allows only <c>(db, acme)</c> refuses it - a clause's rows, and no plan
    /// - and nothing about the plan is ever asked with the tenant.
    /// </summary>
    [Fact]
    public async Task A_mode_A_plan_is_refused_to_a_visitor_allowed_one_tenant_and_not_the_database()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        ExplainResult plan = await harness.Explain(Acme);

        plan.HasPlan.Should().BeFalse();
        plan.Error.Should().BeNull("nothing was sent");
        plan.UnavailableReason.Should().Contain(SqlConsoleService.SqlRefusedMessage);

        harness.Policies.Calls.Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null,
            "the plan is the console's, asked about the database as a whole");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(QueryService.ExplainAction);
        entry.Succeeded.Should().BeFalse();
        entry.TenantId.Should().BeNull();
        entry.Capability.Should().Be(nameof(StudioCapability.RunSql));
    }

    /// <summary>
    /// With the store policy passing everything, the write policy is the one asked - with <c>RunSql</c> and no
    /// tenant - and a write policy that allows <c>RunSql</c> only for <c>acme</c> still refuses the plan.
    /// </summary>
    [Fact]
    public async Task A_mode_A_plan_asks_the_write_policy_for_RunSql_with_no_tenant()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource => resource.Capability is null || resource.TenantId == Acme);

        ExplainResult plan = await harness.Explain(Acme);

        plan.HasPlan.Should().BeFalse();
        plan.UnavailableReason.Should().Contain(SqlConsoleService.SqlRefusedMessage);

        harness.Policies.Calls.Should().Contain(x =>
            x.Resource.DatabaseIdentifier == harness.Database
            && x.Resource.TenantId == null
            && x.Resource.Capability == nameof(StudioCapability.RunSql));
        harness.Policies.Calls.Should().NotContain(
            static x => x.Resource.Capability == nameof(StudioCapability.RunSql) && x.Resource.TenantId != null);
    }

    /// <summary>The positive control: the database as a whole allowed, the plan passes the gate and fails at the connection.</summary>
    [Fact]
    public async Task With_the_whole_database_allowed_the_plan_passes_the_gate()
    {
        await using Harness harness = await Harness.CreateAsync();

        ExplainResult plan = await harness.Explain(Acme);

        plan.UnavailableReason.Should().BeNull("the gate let it through");
        plan.Error.Should().NotBeNull("nothing is listening at this connection string");
        harness.Ring.GetLatest().Should().ContainSingle().Which.TenantId.Should().BeNull();
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

    // ------------------------------------------------------------------------------------------------
    // INT-W1: H1's rules on 0.6's new call sites - X2's what-if gates, M1's stepper refusal
    // ------------------------------------------------------------------------------------------------

    /// <summary>An index the plan of a sequential scan would suggest, as a surface offers one.</summary>
    private static readonly HypotheticalIndex Imagined = HypotheticalIndexCandidates.FromPlan(QueryPlanReader.Read(
        """[{"Plan":{"Node Type":"Seq Scan","Relation Name":"orders","Schema":"public","Alias":"o","Filter":"(o.customer_id = 42)","Total Cost":10}}]""").Plan!)[0];

    /// <summary>
    /// INT-W1, H1 item 2 on X2's Mode A what-if: the answer that lifts the clause guard's nested-read rules is asked by the
    /// registered key and the database's identity, as a run's is. Before, <c>?store=DEFAULT</c> and no <c>?db=</c> passed a
    /// handler that refuses <c>RunSql</c> on <c>default</c>, and the what-if went on to the connection.
    /// </summary>
    [Fact]
    public async Task The_Mode_A_what_ifs_RunSql_question_names_the_registered_key_and_the_databases_identity()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource =>
            resource.Capability != nameof(StudioCapability.RunSql) || resource.StoreName != MartenStoreRegistry.DefaultStoreKey);

        WhatIfResult result = await harness.Queries.WhatIfAsync(
            new StudioScope("DEFAULT", string.Empty, null), new MartenQueryRequest(Harness.Alias, "where data ->> 'Subject' = 'x'"), Imagined, Token);

        result.Refusal.Should().Be(PostgresStudio.Services.Database.DatabaseRefusal.WritePolicy, result.Reason);
        harness.Policies.Calls.Should().Contain(x => x.Resource.Capability == nameof(StudioCapability.RunSql)
            && x.Resource.StoreName == MartenStoreRegistry.DefaultStoreKey && x.Resource.DatabaseIdentifier == harness.Database);
    }

    /// <summary>INT-W1, H1 item 1 on X2's Mode A what-if: an alias that names nothing is refused with the visitor's text escaped.</summary>
    [Fact]
    public async Task A_Mode_A_what_if_over_an_alias_that_names_nothing_is_refused_with_the_visitors_text_escaped()
    {
        await using Harness harness = await Harness.CreateAsync();
        const string Hostile = "no\r\nsuch\u001b[2J";

        WhatIfResult result = await harness.Queries.WhatIfAsync(harness.Scope(null), new MartenQueryRequest(Hostile, "where 1 = 1"), Imagined, Token);

        result.Refusal.Should().Be(PostgresStudio.Services.Database.DatabaseRefusal.NotFound);
        result.Reason.Should().Contain("'" + StudioLogValue.Cap(Hostile) + "'").And.NotContain("\r").And.NotContain("\u001b");
    }

    /// <summary>
    /// INT-W1, H1 item 2 on X2's Documents what-if: the plan's gate - LINQ mode's - is asked again by the registered key and the
    /// database's identity once the scope resolves, and the refusal audited against that resource. Before, a handler that
    /// refuses <c>RunLinqQueries</c> on <c>default</c> was passed by <c>?store=DEFAULT</c>.
    /// </summary>
    [Fact]
    public async Task The_Documents_what_ifs_plan_gate_names_the_registered_key_and_the_databases_identity()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource =>
            resource.Capability != nameof(StudioCapability.RunLinqQueries) || resource.StoreName != MartenStoreRegistry.DefaultStoreKey);

        WhatIfResult result = await harness.Documents.WhatIfAsync(
            new StudioScope("DEFAULT", string.Empty, null), Harness.Alias, new DocumentListRequest(), Imagined, Token);

        result.Refusal.Should().Be(PostgresStudio.Services.Database.DatabaseRefusal.WritePolicy, result.Reason);
        harness.Ring.GetLatest().Should().Contain(x => x.Action == DocumentDataService.WhatIfAction && !x.Succeeded
            && x.StoreKey == MartenStoreRegistry.DefaultStoreKey && x.DatabaseId == harness.Database,
            "the refusal is audited against the resource the policy refused");
    }

    /// <summary>
    /// INT-W1, H1 item 2 on X2's LINQ what-if: the plan's question, for the database as a whole, is asked as LINQ mode's own
    /// plan asks it - by the registered key and the database's identity. A run scoped to a tenant the policy passes reached the
    /// plan's question; before, it was asked about <c>DEFAULT</c> and passed a handler that refuses the whole of <c>default</c>.
    /// </summary>
    [DynamicLinqFact]
    public async Task The_LINQ_what_ifs_plan_question_names_the_registered_key_and_the_databases_identity()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource => !(resource.Capability == nameof(StudioCapability.RunLinqQueries)
            && resource.StoreName == MartenStoreRegistry.DefaultStoreKey && resource.TenantId is null));

        WhatIfResult result = await harness.Linq.WhatIfAsync(
            new StudioScope("DEFAULT", string.Empty, Acme), new LinqQueryRequest(Harness.Alias, "Subject == \"x\""), Imagined, Token);

        result.Refusal.Should().Be(PostgresStudio.Services.Database.DatabaseRefusal.WritePolicy, result.Reason);
        result.Reason.Should().Be(LinqQueryService.PlanRefusedMessage, "the run passed for the tenant; the plan, for the whole database, did not");
    }

    /// <summary>
    /// INT-W1, H1 item 1 on M1's stepper - and on time travel, whose refusal it repeats word for word: an aggregate type the
    /// address names is the visitor's text, and reaches the refusal escaped and bounded.
    /// </summary>
    [Fact]
    public async Task An_aggregate_type_the_address_names_reaches_the_stepper_and_time_travel_refusals_escaped()
    {
        await using Harness harness = await Harness.CreateAsync();
        const string Hostile = "No.Such\r\nType\u001b[2J";

        StreamStepWindow window = await harness.Events.StepAsync(harness.Scope(null), Guid.NewGuid().ToString(), Hostile, 1, Token);
        AggregateSnapshot snapshot = await harness.Events.AggregateAtVersionAsync(harness.Scope(null), Guid.NewGuid().ToString(), Hostile, 0, Token);

        foreach (string? message in new[] { window.Error?.Message, snapshot.Error?.Message })
        {
            message.Should().Be("'" + StudioLogValue.Cap(Hostile) + "' is not one of this store's aggregate types.");
        }
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

        public ISqlConsoleService Console => scope.ServiceProvider.GetRequiredService<ISqlConsoleService>();

        public IDocumentDataService Documents => scope.ServiceProvider.GetRequiredService<IDocumentDataService>();

        public ILinqQueryService Linq => scope.ServiceProvider.GetRequiredService<ILinqQueryService>();

        public IEventDataService Events => scope.ServiceProvider.GetRequiredService<IEventDataService>();

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public StudioScope Scope(string? tenantId) => new(MartenStoreRegistry.DefaultStoreKey, Database, tenantId);

        /// <summary>
        /// The plan for a Mode A clause over the conjoined type, composed for <paramref name="tenantId" /> as the
        /// page would have composed it, fetched the way the page's result fetches it.
        /// </summary>
        public Task<ExplainResult> Explain(string? tenantId)
        {
            var table = new DocumentTableInfo
            {
                Schema = "public",
                Table = "mt_doc_reachticket",
                Alias = Alias,
                TenancyStyle = TenancyStyle.Conjoined,
                MetadataColumns = [new DocumentMetadataColumnInfo(DocumentMetadataColumn.TenantId, "tenant_id")],
            };

            ComposedQuery composed = QuerySqlComposer.Compose(table, "where data ->> 'Subject' = 'x'", 10, tenantId);

            return ((QueryService) Queries).TryExplainAsync(Scope(tenantId), composed, TestContext.Current.CancellationToken);
        }

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
