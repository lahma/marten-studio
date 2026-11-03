using System.Runtime.CompilerServices;
using System.Security.Claims;

using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Query;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services;

using Weasel.Core;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>An event the live tests append.</summary>
public sealed record LinqLiveOrderPlaced(string Customer);

/// <summary>Another, so a type predicate has something to tell apart.</summary>
public sealed record LinqLiveOrderShipped(string Tracking);

/// <summary>A live fact for LINQ over events: Docker, and a Marten whose LINQ mode is available.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LinqEventLiveFactAttribute : FactAttribute
{
    public LinqEventLiveFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = LinqLiveAvailability.SkipReason;
        SkipType = typeof(LinqLiveAvailability);
        SkipUnless = nameof(LinqLiveAvailability.CanRun);
    }
}

/// <summary>
/// A store with the events' shape under test, the studio on top of it, and the registered
/// <see cref="ILinqQueryService" /> - so the gating is exercised rather than bypassed.
/// </summary>
internal sealed class LinqEventHarness : IAsyncDisposable
{
    public const string Acme = "acme";
    public const string Globex = "globex";

    /// <summary>Allows the <c>acme</c> tenant and nothing wider.</summary>
    public const string AcmeOnlyPolicy = "linq-events-acme-only";

    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    private LinqEventHarness(ServiceProvider provider)
    {
        this.provider = provider;
        scope = provider.CreateScope();
    }

    public ILinqQueryService Linq => scope.ServiceProvider.GetRequiredService<ILinqQueryService>();

    public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

    public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

    public static StudioScope AllTenants { get; } = new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

    public static StudioScope For(string tenant) => new(MartenStoreRegistry.DefaultStoreKey, string.Empty, tenant);

    public static async Task<LinqEventHarness> CreateAsync(
        Action<StoreOptions> store,
        Action<MartenStudioOptions>? studio = null,
        Func<IDocumentStore, Task>? seed = null,
        bool apply = true)
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization(static options => options.AddPolicy(AcmeOnlyPolicy, static policy => policy.RequireAssertion(
            static context => context.Resource is MartenStoreResource { TenantId: Acme })));
        services.AddSingleton<AuthenticationStateProvider, SignedIn>();

        services.AddMarten(options =>
        {
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.UseSystemTextJsonForSerialization(EnumStorage.AsString);
            options.Events.AddEventType<LinqLiveOrderPlaced>();
            options.Events.AddEventType<LinqLiveOrderShipped>();
            store(options);
        });

        services.AddMartenStudio(options =>
        {
            options.Capabilities.RunLinqQueries = true;
            options.QueryTimeout = TimeSpan.FromSeconds(30);
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(Globex);
            studio?.Invoke(options);
        });

        ServiceProvider built = services.BuildServiceProvider();
        var harness = new LinqEventHarness(built);

        if (apply)
        {
            await harness.Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        if (seed is not null)
        {
            await seed(harness.Store);
        }

        return harness;
    }

    /// <summary>Starts a stream of an order placed and shipped, under the session's tenant.</summary>
    public static async Task AppendAsync(
        IDocumentSession session,
        Guid stream,
        string customer,
        string? correlation = null,
        string? causation = null,
        string? user = null)
    {
        session.CorrelationId = correlation;
        session.CausationId = causation;
        session.LastModifiedBy = user;
        session.Events.StartStream(stream, new LinqLiveOrderPlaced(customer), new LinqLiveOrderShipped("track-" + customer));
        await session.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        scope.Dispose();
        await provider.DisposeAsync();
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ops")], "test"))));
    }
}

/// <summary>
/// LINQ over events against a real database (1.1), conjoined events with correlation, causation and user name recorded:
/// the tenant and archive filters are Marten's, the reads are the studio's, and nothing creates a table.
/// </summary>
public class LinqEventQueryLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Schema = "linqeventlive";

    private static readonly Guid AcmeStream = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid AcmeArchived = Guid.Parse("a0000000-0000-0000-0000-00000000000b");
    private static readonly Guid GlobexStream = Guid.Parse("b0000000-0000-0000-0000-00000000000a");

    private LinqEventHarness harness = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await fixture.CreateSchemaAsync(Schema, Token);

        harness = await LinqEventHarness.CreateAsync(
            ConfigureStore,
            seed: static async store =>
            {
                await using (IDocumentSession acme = store.LightweightSession(LinqEventHarness.Acme))
                {
                    await LinqEventHarness.AppendAsync(acme, AcmeStream, "ada", "corr-1", "cause-1", "ada");
                }

                await using (IDocumentSession archived = store.LightweightSession(LinqEventHarness.Acme))
                {
                    await LinqEventHarness.AppendAsync(archived, AcmeArchived, "bob", "corr-2", "cause-2", "bob");
                    archived.Events.ArchiveStream(AcmeArchived);
                    await archived.SaveChangesAsync();
                }

                await using IDocumentSession globex = store.LightweightSession(LinqEventHarness.Globex);
                await LinqEventHarness.AppendAsync(globex, GlobexStream, "gina", "corr-3", "cause-3", "gina");
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private void ConfigureStore(StoreOptions options)
    {
        options.Connection(fixture.ConnectionString);
        options.DatabaseSchemaName = Schema;
        options.Events.TenancyStyle = TenancyStyle.Conjoined;
        options.Events.MetadataConfig.CorrelationIdEnabled = true;
        options.Events.MetadataConfig.CausationIdEnabled = true;
        options.Events.MetadataConfig.UserNameEnabled = true;
        options.Policies.AllDocumentsAreMultiTenanted();
    }

    private static LinqQueryRequest Events(
        string? where,
        string? orderBy = null,
        ArchivedFilter archived = ArchivedFilter.Exclude,
        int offset = 0,
        int pageSize = 50,
        bool plan = true) =>
        new(LinqQueryService.EventsAlias, where, orderBy, pageSize, offset, IncludePlan: plan, Target: LinqQueryTarget.Events, Archived: archived);

    // ------------------------------------------------------------------------------------------------
    // Tenancy: Marten's
    // ------------------------------------------------------------------------------------------------

    [LinqEventLiveFact]
    public async Task A_tenants_query_never_returns_another_tenants_events_and_TenantId_is_not_offered()
    {
        LinqQueryResult acme = await harness.Linq.RunAsync(LinqEventHarness.For(LinqEventHarness.Acme), Events("Version >= 1"), Token);
        LinqQueryResult globex = await harness.Linq.RunAsync(LinqEventHarness.For(LinqEventHarness.Globex), Events("Version >= 1"), Token);

        acme.Error.Should().BeNull();
        acme.EventRows.Select(static x => x.TenantId).Should().OnlyContain(static x => x == LinqEventHarness.Acme);
        acme.EventRows.Select(static x => x.StreamId).Distinct().Should().Equal(AcmeStream.ToString());
        globex.EventRows.Select(static x => x.TenantId).Should().OnlyContain(static x => x == LinqEventHarness.Globex);
        acme.EventScope.Should().Be(new LinqEventScope(LinqEventHarness.Acme, CrossTenant: false, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: false));

        LinqQueryResult widening = await harness.Linq.RunAsync(LinqEventHarness.For(LinqEventHarness.Acme), Events("TenantId == \"globex\""), Token);

        widening.Refusal!.Stage.Should().Be(LinqStage.Screen);
        widening.EventRows.Should().BeEmpty();

        LinqModeStatus status = await harness.Linq.DescribeAsync(LinqEventHarness.For(LinqEventHarness.Acme), Token);
        status.Events!.Members.Should().NotContain("TenantId");
        status.Events.Withheld.Should().Contain(static x => x.Name == "TenantId");
    }

    [LinqEventLiveFact]
    public async Task With_no_tenant_selected_Marten_is_told_to_read_every_tenant_and_the_page_says_so()
    {
        LinqQueryResult all = await harness.Linq.RunAsync(LinqEventHarness.AllTenants, Events("Version >= 1"), Token);

        all.Error.Should().BeNull();
        all.EventScope!.CrossTenant.Should().BeTrue();
        all.EventRows.Select(static x => x.TenantId).Distinct().Should().BeEquivalentTo([LinqEventHarness.Acme, LinqEventHarness.Globex]);

        LinqQueryResult one = await harness.Linq.RunAsync(LinqEventHarness.AllTenants, Events("TenantId == \"globex\""), Token);

        one.EventRows.Select(static x => x.TenantId).Should().OnlyContain(static x => x == LinqEventHarness.Globex, "TenantId is offered where no tenant narrows the read");
    }

    // ------------------------------------------------------------------------------------------------
    // Archived: Marten's filters
    // ------------------------------------------------------------------------------------------------

    [LinqEventLiveFact]
    public async Task Archived_events_are_left_out_unless_asked_for_and_the_selector_is_Martens_own_filter()
    {
        StudioScope acme = LinqEventHarness.For(LinqEventHarness.Acme);

        LinqQueryResult live = await harness.Linq.RunAsync(acme, Events(null), Token);
        LinqQueryResult both = await harness.Linq.RunAsync(acme, Events(null, archived: ArchivedFilter.Include), Token);
        LinqQueryResult only = await harness.Linq.RunAsync(acme, Events(null, archived: ArchivedFilter.Only), Token);

        live.EventRows.Select(static x => x.StreamId).Distinct().Should().Equal(AcmeStream.ToString());
        live.EventRows.Should().OnlyContain(static x => !x.IsArchived);
        both.EventRows.Select(static x => x.StreamId).Distinct().Should().BeEquivalentTo([AcmeStream.ToString(), AcmeArchived.ToString()]);
        both.EventRows.Should().Contain(static x => x.IsArchived);
        only.EventRows.Select(static x => x.StreamId).Distinct().Should().Equal(AcmeArchived.ToString());
        only.EventRows.Should().OnlyContain(static x => x.IsArchived);
    }

    [LinqEventLiveFact]
    public async Task A_predicate_cannot_name_IsArchived_to_replace_Martens_filter()
    {
        LinqQueryResult result = await harness.Linq.RunAsync(LinqEventHarness.For(LinqEventHarness.Acme), Events("IsArchived == true"), Token);

        result.Refusal!.Stage.Should().Be(LinqStage.Screen);
        result.EventRows.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // The dialect, on real rows
    // ------------------------------------------------------------------------------------------------

    [LinqEventLiveFact]
    public async Task A_metadata_predicate_and_a_type_predicate_filter_on_the_columns()
    {
        StudioScope acme = LinqEventHarness.For(LinqEventHarness.Acme);

        LinqQueryResult byUser = await harness.Linq.RunAsync(acme, Events("UserName == \"ada\" and CorrelationId == \"corr-1\" and CausationId == \"cause-1\""), Token);
        LinqQueryResult byType = await harness.Linq.RunAsync(acme, Events("EventTypeName.StartsWith(\"linq_live_order_s\")"), Token);
        LinqQueryResult byStream = await harness.Linq.RunAsync(acme, Events("StreamId == \"" + AcmeStream + "\" and Version == 2"), Token);
        LinqQueryResult byList = await harness.Linq.RunAsync(acme, Events("EventTypeName in (\"nothing\", \"linq_live_order_placed\")"), Token);

        byUser.Error.Should().BeNull();
        byUser.EventRows.Should().NotBeEmpty().And.OnlyContain(static x => x.UserName == "ada" && x.CorrelationId == "corr-1" && x.CausationId == "cause-1");
        byType.Error.Should().BeNull();
        byType.EventRows.Should().ContainSingle("a type prefix is a LIKE on the type column").Which.EventTypeName.Should().Be("linq_live_order_shipped");
        byStream.EventRows.Should().ContainSingle().Which.Version.Should().Be(2);
        byList.EventRows.Should().ContainSingle("an in-list on text is = ANY").Which.EventTypeName.Should().Be("linq_live_order_placed");
    }

    [LinqEventLiveFact]
    public async Task Newest_first_by_default_and_pages_are_deterministic_and_disjoint()
    {
        StudioScope all = LinqEventHarness.AllTenants;

        LinqQueryResult first = await harness.Linq.RunAsync(all, Events(null, archived: ArchivedFilter.Include, pageSize: 2), Token);
        LinqQueryResult second = await harness.Linq.RunAsync(all, Events(null, archived: ArchivedFilter.Include, offset: 2, pageSize: 2), Token);

        first.EventRows.Select(static x => x.Sequence).Should().BeInDescendingOrder();
        first.HasMore.Should().BeTrue();
        second.EventRows.Select(static x => x.Sequence).Should().BeInDescendingOrder();
        first.EventRows.Select(static x => x.Sequence).Should().NotIntersectWith(second.EventRows.Select(static x => x.Sequence));
        first.EventRows[^1].Sequence.Should().BeGreaterThan(second.EventRows[0].Sequence);

        LinqQueryResult ordered = await harness.Linq.RunAsync(all, Events(null, "Version, Sequence desc", ArchivedFilter.Include), Token);

        ordered.EventRows.Select(static x => x.Version).Should().BeInAscendingOrder();
    }

    [LinqEventLiveFact]
    public async Task The_body_is_text_the_binary_payload_is_not_read_and_the_statement_is_shown()
    {
        LinqQueryResult result = await harness.Linq.RunAsync(LinqEventHarness.For(LinqEventHarness.Acme), Events("Version == 1"), Token);

        result.Error.Should().BeNull();
        result.EventRows.Should().ContainSingle().Which.Should().Match<LinqEventRow>(static x =>
            x.Json != null && x.Json.Contains("ada", StringComparison.Ordinal) && !x.HasBinary && x.DotNetTypeName != null);
        result.Sql.Should().Contain("mt_events");
        result.Parameters.Should().NotBeEmpty();
        result.Plan.Should().NotBeNull();
        result.Plan!.HasPlan.Should().BeTrue();
    }

    // ------------------------------------------------------------------------------------------------
    // The gate and the audit
    // ------------------------------------------------------------------------------------------------

    [LinqEventLiveFact]
    public async Task A_success_is_audited_once_per_signature_and_a_refusal_every_time()
    {
        StudioScope acme = LinqEventHarness.For(LinqEventHarness.Acme);

        await harness.Linq.RunAsync(acme, Events("Version == 1"), Token);
        await harness.Linq.RunAsync(acme, Events("Version == 1", plan: false), Token);
        await harness.Linq.RunAsync(acme, Events("Version == 1", offset: 1), Token);
        await harness.Linq.RunAsync(acme, Events("Version == 1", archived: ArchivedFilter.Include), Token);
        await harness.Linq.RunAsync(acme, Events("IsArchived"), Token);
        await harness.Linq.RunAsync(acme, Events("IsArchived"), Token);

        StudioActionLogEntry[] entries = [.. harness.Ring.GetLatest().Where(static x => x.Action == LinqQueryService.LinqEventQueryAction)];

        entries.Count(static x => x.Succeeded).Should().Be(2, "one per signature: the archived selector is part of it, a page turn is not");
        entries.Count(static x => !x.Succeeded).Should().Be(2, "every refusal, at its stage");
        entries.Should().OnlyContain(static x => x.Target.StartsWith("events: ", StringComparison.Ordinal));
    }

    [LinqEventLiveFact]
    public async Task A_tenant_only_visitor_reads_the_tenants_events_and_is_refused_the_plan_which_is_audited_as_an_event_explain()
    {
        await using LinqEventHarness acmeOnly = await LinqEventHarness.CreateAsync(
            ConfigureStore,
            options =>
            {
                options.StoreAuthorizationPolicy = LinqEventHarness.AcmeOnlyPolicy;
                options.WriteAuthorizationPolicy = LinqEventHarness.AcmeOnlyPolicy;
            },
            apply: false);

        StudioScope acme = LinqEventHarness.For(LinqEventHarness.Acme);

        LinqQueryResult result = await acmeOnly.Linq.RunAsync(acme, Events("Version >= 1"), Token);

        result.Error.Should().BeNull();
        result.EventRows.Should().NotBeEmpty().And.OnlyContain(static x => x.TenantId == LinqEventHarness.Acme);
        result.Plan!.UnavailableReason.Should().Be(LinqQueryService.PlanRefusedMessage);
        acmeOnly.Ring.GetLatest().Should().Contain(static x =>
            x.Action == LinqQueryService.LinqEventExplainAction && !x.Succeeded && x.TenantId == null);
    }

    [LinqEventLiveFact]
    public async Task A_what_if_over_the_events_is_declined()
    {
        PostgresStudio.Services.Query.HypotheticalIndex index = PostgresStudio.Services.Query.HypotheticalIndexCandidates.FromPlan(
            PostgresStudio.Services.Query.QueryPlanReader.Read(
                """[{"Plan":{"Node Type":"Seq Scan","Relation Name":"mt_events","Schema":"public","Alias":"d","Filter":"(d.version = 1)","Total Cost":10}}]""").Plan!)[0];

        PostgresStudio.Services.Query.WhatIfResult result = await harness.Linq.WhatIfAsync(
            LinqEventHarness.For(LinqEventHarness.Acme), Events("Version == 1"), index, Token);

        result.Reason.Should().Be(LinqQueryService.EventsWhatIfMessage);
        harness.Ring.GetLatest().Should().Contain(static x => x.Action == LinqQueryService.LinqWhatIfAction && !x.Succeeded);
    }
}

/// <summary>
/// Single-tenanted events in a store whose documents are conjoined (1.1): nothing narrows the event log by tenant, so a
/// tenant scope reaches the whole database's log - and is asked, authorized and audited as exactly that.
/// </summary>
public class LinqEventQuerySingleTenantedLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Schema = "linqeventsingle";

    private LinqEventHarness harness = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await fixture.CreateSchemaAsync(Schema, Token);

        harness = await LinqEventHarness.CreateAsync(
            Configure,
            options =>
            {
                options.StoreAuthorizationPolicy = LinqEventHarness.AcmeOnlyPolicy;
                options.WriteAuthorizationPolicy = LinqEventHarness.AcmeOnlyPolicy;
            },
            static async store =>
            {
                await using IDocumentSession session = store.LightweightSession(LinqEventHarness.Acme);
                await LinqEventHarness.AppendAsync(session, Guid.NewGuid(), "ada");
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private void Configure(StoreOptions options)
    {
        options.Connection(fixture.ConnectionString);
        options.DatabaseSchemaName = Schema;
        options.Policies.AllDocumentsAreMultiTenanted();
    }

    [LinqEventLiveFact]
    public async Task A_tenant_only_visitor_is_refused_the_whole_databases_event_log_and_it_is_audited_with_no_tenant()
    {
        Func<Task> run = () => harness.Linq.RunAsync(
            LinqEventHarness.For(LinqEventHarness.Acme),
            new LinqQueryRequest("x", "Version >= 1", Target: LinqQueryTarget.Events),
            Token);

        (await run.Should().ThrowAsync<StudioNotAuthorizedException>()).Which.Scope.TenantId.Should().BeNull();

        harness.Ring.GetLatest().Should().Contain(static x =>
            x.Action == LinqQueryService.LinqEventQueryAction && !x.Succeeded && x.TenantId == null);
    }

    [LinqEventLiveFact]
    public async Task A_visitor_allowed_the_whole_database_reads_it_and_the_audit_says_whole_database()
    {
        await using LinqEventHarness open = await LinqEventHarness.CreateAsync(Configure, apply: false);

        LinqQueryResult result = await open.Linq.RunAsync(
            LinqEventHarness.For(LinqEventHarness.Acme),
            new LinqQueryRequest("x", "Version >= 1", Target: LinqQueryTarget.Events),
            Token);

        result.Error.Should().BeNull();
        result.EventRows.Should().NotBeEmpty();
        result.EventScope!.WholeDatabase.Should().BeTrue();
        result.EventScope.TenantId.Should().BeNull("Marten narrowed nothing");
        open.Ring.GetLatest().Should().Contain(static x =>
            x.Action == LinqQueryService.LinqEventQueryAction && x.Succeeded && x.TenantId == null);
    }
}

/// <summary>
/// Separate databases per tenant (1.1): the session is pinned to the scope's database, so a tenant's query reads that
/// database's events and never the default database's - and a visitor who may address only their own tenant reads their own
/// database with no whole-database question.
/// </summary>
public class LinqEventQuerySeparateDatabasesLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Prefix = "linqeventsep";

    private LinqEventHarness harness = null!;
    private StudioScope acmeScope = null!;
    private StudioScope globexScope = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        acmeConnection = await CreateDatabaseAsync(Prefix + "_acme");
        globexConnection = await CreateDatabaseAsync(Prefix + "_globex");

        harness = await LinqEventHarness.CreateAsync(
            ConfigureStore,
            seed: static async store =>
            {
                await using (IDocumentSession acme = store.LightweightSession(LinqEventHarness.Acme))
                {
                    await LinqEventHarness.AppendAsync(acme, Guid.NewGuid(), "ada");
                }

                await using IDocumentSession globex = store.LightweightSession(LinqEventHarness.Globex);
                await LinqEventHarness.AppendAsync(globex, Guid.NewGuid(), "gina");
            });

        IReadOnlyList<Marten.Storage.IMartenDatabase> databases = await harness.Store.Storage.AllDatabases();

        acmeScope = ScopeIn(databases, Prefix + "_acme", LinqEventHarness.Acme);
        globexScope = ScopeIn(databases, Prefix + "_globex", LinqEventHarness.Globex);
    }

    private string acmeConnection = string.Empty;
    private string globexConnection = string.Empty;

    /// <summary>
    /// Globex is registered first, so its database is the store's default - which is where an unpinned session would go and
    /// which is never <em>exclusively</em> one tenant's. Acme's is the second: exclusively acme's.
    /// </summary>
    private void ConfigureStore(StoreOptions options)
    {
        options.MultiTenantedDatabases(tenancy =>
        {
            tenancy.AddSingleTenantDatabase(globexConnection, LinqEventHarness.Globex);
            tenancy.AddSingleTenantDatabase(acmeConnection, LinqEventHarness.Acme);
        });

        options.DatabaseSchemaName = "linqeventsep";
    }

    public async ValueTask DisposeAsync()
    {
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private static StudioScope ScopeIn(IReadOnlyList<Marten.Storage.IMartenDatabase> databases, string name, string tenant) =>
        new(MartenStoreRegistry.DefaultStoreKey, databases.Single(x => string.Equals(x.Id.Name, name, StringComparison.OrdinalIgnoreCase)).Id.Identity, tenant);

    private async Task<string> CreateDatabaseAsync(string name)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);

        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync(Token);

            string quoted = PostgresStudio.Internal.Sql.SqlIdentifier.Quote(name);

            await using (var drop = new NpgsqlCommand($"drop database if exists {quoted} with (force)", admin))
            {
                await drop.ExecuteNonQueryAsync(Token);
            }

            await using var create = new NpgsqlCommand($"create database {quoted}", admin);
            await create.ExecuteNonQueryAsync(Token);
        }

        builder.Database = name;
        return builder.ConnectionString;
    }

    /// <summary>
    /// The reach exception, live: events nothing divides by tenant, but a database that is exclusively one tenant's - so the
    /// whole-database question is the tenant's own and is not asked again. The session is pinned to that database, so the
    /// default database's events (globex's) are not what comes back.
    /// </summary>
    [LinqEventLiveFact]
    public async Task A_tenant_only_visitor_reads_their_own_database_with_no_whole_database_question_and_never_the_defaults()
    {
        await using LinqEventHarness acmeOnly = await LinqEventHarness.CreateAsync(
            ConfigureStore,
            options =>
            {
                options.StoreAuthorizationPolicy = LinqEventHarness.AcmeOnlyPolicy;
                options.WriteAuthorizationPolicy = LinqEventHarness.AcmeOnlyPolicy;
            },
            apply: false);

        LinqQueryResult result = await acmeOnly.Linq.RunAsync(
            acmeScope, new LinqQueryRequest("x", "Version >= 1", Target: LinqQueryTarget.Events, PageSize: 50), Token);

        result.Error.Should().BeNull();
        result.EventRows.Should().NotBeEmpty().And.OnlyContain(static x => x.Json!.Contains("ada", StringComparison.Ordinal),
            "the session is pinned to acme's database: gina's events are in the other one");
        result.EventScope!.WholeDatabase.Should().BeTrue("the events are not divided by tenant; the database is simply acme's alone");
    }

    [LinqEventLiveFact]
    public async Task Each_tenants_database_answers_for_itself_including_the_default_one()
    {
        LinqQueryResult globex = await harness.Linq.RunAsync(
            globexScope, new LinqQueryRequest("x", null, Target: LinqQueryTarget.Events), Token);
        LinqQueryResult acme = await harness.Linq.RunAsync(
            acmeScope, new LinqQueryRequest("x", null, Target: LinqQueryTarget.Events), Token);

        globex.Error.Should().BeNull();
        globex.EventRows.Should().NotBeEmpty().And.OnlyContain(static x => x.Json!.Contains("gina", StringComparison.Ordinal));
        acme.EventRows.Should().NotBeEmpty().And.OnlyContain(static x => x.Json!.Contains("ada", StringComparison.Ordinal));
    }
}

/// <summary>
/// Per-tenant sequences (1.1): <c>seq_id</c> repeats across tenants, so the default order across tenants is by timestamp, the
/// pages are disjoint all the same, and nothing is keyed by sequence.
/// </summary>
public class LinqEventQueryPartitionedLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Schema = "linqeventpart";

    private LinqEventHarness harness = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await fixture.CreateSchemaAsync(Schema, Token);

        harness = await LinqEventHarness.CreateAsync(
            options =>
            {
                options.Connection(fixture.ConnectionString);
                options.DatabaseSchemaName = Schema;
                options.Events.TenancyStyle = TenancyStyle.Conjoined;
                options.Events.UseTenantPartitionedEvents = true;
                options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
                options.Policies.AllDocumentsAreMultiTenanted();
            },
            seed: static async store =>
            {
                await store.Advanced.AddMartenManagedTenantsAsync(Token, LinqEventHarness.Acme, LinqEventHarness.Globex);

                foreach (string tenant in new[] { LinqEventHarness.Acme, LinqEventHarness.Globex })
                {
                    await using IDocumentSession session = store.LightweightSession(tenant);
                    await LinqEventHarness.AppendAsync(session, Guid.NewGuid(), tenant + "-1");
                    await LinqEventHarness.AppendAsync(session, Guid.NewGuid(), tenant + "-2");
                }
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [LinqEventLiveFact]
    public async Task Across_tenants_the_default_order_is_the_timestamp_and_pages_are_disjoint_while_sequences_repeat()
    {
        LinqQueryResult all = await harness.Linq.RunAsync(
            LinqEventHarness.AllTenants, new LinqQueryRequest("x", null, PageSize: 100, Target: LinqQueryTarget.Events), Token);

        all.Error.Should().BeNull();
        all.EventScope!.PerTenantSequences.Should().BeTrue();
        all.Sql.Should().Contain("order by d.timestamp desc");
        all.EventRows.Should().NotBeEmpty();

        // The premise: two tenants draw the same sequence number from their own counters.
        all.EventRows.GroupBy(static x => x.Sequence).Should().Contain(static group => group.Count() > 1);
        all.EventRows.Select(static x => x.Timestamp).Should().BeInDescendingOrder("the default order across tenants is the timestamp");

        List<(string? Tenant, long Sequence)> seen = [];

        for (int offset = 0; offset < all.EventRows.Count; offset += 2)
        {
            LinqQueryResult page = await harness.Linq.RunAsync(
                LinqEventHarness.AllTenants, new LinqQueryRequest("x", null, PageSize: 2, Offset: offset, Target: LinqQueryTarget.Events), Token);

            page.Error.Should().BeNull();
            seen.AddRange(page.EventRows.Select(static x => (x.TenantId, x.Sequence)));
        }

        seen.Should().OnlyHaveUniqueItems("pages are disjoint by (tenant, sequence)");
        seen.Should().HaveCount(all.EventRows.Count);
    }
}

/// <summary>String-identified streams (1.1): <c>StreamKey</c> is the column, <c>StreamId</c> is not offered.</summary>
public class LinqEventQueryStringIdentityLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Schema = "linqeventstring";

    private LinqEventHarness harness = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await fixture.CreateSchemaAsync(Schema, Token);

        harness = await LinqEventHarness.CreateAsync(
            options =>
            {
                options.Connection(fixture.ConnectionString);
                options.DatabaseSchemaName = Schema;
                options.Events.StreamIdentity = StreamIdentity.AsString;
            },
            seed: static async store =>
            {
                await using IDocumentSession session = store.LightweightSession();
                session.Events.StartStream("order/1", new LinqLiveOrderPlaced("ada"));
                session.Events.StartStream("order/2", new LinqLiveOrderPlaced("bob"));
                await session.SaveChangesAsync();
            });
    }

    public async ValueTask DisposeAsync()
    {
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [LinqEventLiveFact]
    public async Task StreamKey_filters_on_the_stream_column_and_StreamId_is_refused()
    {
        LinqQueryResult byKey = await harness.Linq.RunAsync(
            LinqEventHarness.AllTenants, new LinqQueryRequest("x", "StreamKey == \"order/2\"", Target: LinqQueryTarget.Events), Token);
        LinqQueryResult byId = await harness.Linq.RunAsync(
            LinqEventHarness.AllTenants, new LinqQueryRequest("x", "StreamId == \"aaaaaaaa-0000-0000-0000-000000000001\"", Target: LinqQueryTarget.Events), Token);

        byKey.Error.Should().BeNull();
        byKey.EventRows.Should().ContainSingle().Which.StreamId.Should().Be("order/2");
        byId.Refusal!.Stage.Should().Be(LinqStage.Screen);
    }
}

/// <summary>
/// The studio creates nothing (1.1, hard rule 14): a database whose event tables were never created is answered with a
/// sentence, and is still without them afterwards.
/// </summary>
public class LinqEventQueryNoDdlLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Schema = "linqeventnoddl";

    private LinqEventHarness harness = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await fixture.CreateSchemaAsync(Schema, Token);

        harness = await LinqEventHarness.CreateAsync(
            options =>
            {
                options.Connection(fixture.ConnectionString);
                options.DatabaseSchemaName = Schema;
            },
            apply: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [LinqEventLiveFact]
    public async Task A_database_with_no_event_tables_says_so_and_still_has_none()
    {
        LinqQueryResult result = await harness.Linq.RunAsync(
            LinqEventHarness.AllTenants, new LinqQueryRequest("x", "Version >= 1", Target: LinqQueryTarget.Events), Token);

        result.Error.Should().NotBeNull();
        result.Error!.SqlState.Should().Be("42P01");
        result.Error.Message.Should().Be(LinqQueryService.NoEventTablesMessage(Schema));
        result.Plan.Should().BeNull("the plan would fail the same way");
        harness.Ring.GetLatest().Should().Contain(static x => x.Action == LinqQueryService.LinqEventQueryAction && !x.Succeeded);

        await using NpgsqlConnection connection = await fixture.OpenAsync(Token);
        await using var command = new NpgsqlCommand($"select to_regclass('{Schema}.mt_events')::text", connection);

        (await command.ExecuteScalarAsync(Token)).Should().Be(DBNull.Value, "LINQ mode never creates the table");

        await using var schemaObjects = new NpgsqlCommand(
            "select count(*) from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = @schema", connection);
        schemaObjects.Parameters.AddWithValue("schema", Schema);

        Convert.ToInt64(await schemaObjects.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture).Should().Be(0, "nothing at all was created in the events schema");
    }
}
