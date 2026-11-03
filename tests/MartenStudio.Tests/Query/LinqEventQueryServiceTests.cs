using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Services.Query;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The service's gate, audit and refusals for the event log (1.1), through a real studio container over a store that never
/// connects. Every refusal here happens before a connection is opened; an accepted query is built by Marten and only then
/// fails at the host that does not resolve - with the statement shown.
/// </summary>
public class LinqEventQueryServiceTests
{
    private const string StorePolicy = "store-policy";
    private const string WritePolicy = "write-policy";
    private const string Acme = "acme";

    private static readonly StudioScope NoTenant = new("default", string.Empty, null);
    private static readonly StudioScope AcmeScope = new("default", string.Empty, Acme);

    /// <summary>An index a plan would suggest, for the what-if the events refuse.</summary>
    private static readonly HypotheticalIndex Imagined = HypotheticalIndexCandidates.FromPlan(QueryPlanReader.Read(
        """[{"Plan":{"Node Type":"Seq Scan","Relation Name":"orders","Schema":"public","Alias":"o","Filter":"(o.customer_id = 42)","Total Cost":10}}]""").Plan!)[0];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static LinqQueryRequest Events(string? where, string? orderBy = null, ArchivedFilter archived = ArchivedFilter.Exclude, int offset = 0) =>
        new("whatever the visitor sent", where, orderBy, Offset: offset, Target: LinqQueryTarget.Events, Archived: archived);

    // ------------------------------------------------------------------------------------------------
    // The gate, in order
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_capability_that_is_off_refuses_an_event_run_before_anything_is_resolved_and_records_none_of_the_text()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities());

        Func<Task> run = () => harness.Linq.RunAsync(NoTenant, Events("EventTypeName == \"secret-literal\""), Token);

        (await run.Should().ThrowAsync<StudioCapabilityDeniedException>()).Which.Capability.Should().Be(StudioCapability.RunLinqQueries);

        harness.Policies.Calls.Should().BeEmpty("a capability that is off is answered before any policy is asked");
        StudioActionLogEntry entry = harness.Ring.GetLatest()[0];
        entry.Action.Should().Be(LinqQueryService.LinqEventQueryAction);
        entry.Succeeded.Should().BeFalse();
        entry.Target.Should().Be("(events): " + LinqQueryService.TextNotRecorded);
        entry.Target.Should().NotContain("secret-literal");
    }

    [Fact]
    public async Task ReadOnly_refuses_an_event_run_whatever_the_capabilities_say()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.ReadOnly = true;
        });

        Func<Task> run = () => harness.Linq.RunAsync(NoTenant, Events("Version > 1"), Token);

        (await run.Should().ThrowAsync<StudioCapabilityDeniedException>()).Which.Reason.Should().Be(CapabilityDenialReason.ReadOnly);
    }

    [Fact]
    public async Task The_write_policy_is_asked_with_RunLinqQueries_and_its_refusal_is_audited_as_an_event_query()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());
        harness.Policies.Allow(static resource => resource.Capability is null);

        Func<Task> run = () => harness.Linq.RunAsync(NoTenant, Events("Version > 1"), Token);

        await run.Should().ThrowAsync<StudioNotAuthorizedException>();

        harness.Policies.Calls.Should().Contain(static x => x.Policy == WritePolicy && x.Resource.Capability == nameof(StudioCapability.RunLinqQueries));
        StudioActionLogEntry entry = harness.Ring.GetLatest()[0];
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be(LinqQueryService.LinqEventQueryAction);
        entry.Target.Should().StartWith("events: Version > 1");
    }

    // ------------------------------------------------------------------------------------------------
    // The target
    // ------------------------------------------------------------------------------------------------

    [DynamicLinqFact]
    public async Task The_events_are_offered_beside_the_documents_and_with_no_document_type_at_all()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.IsDocumentTypeVisible = static _ => false;
        });

        LinqModeStatus status = await harness.Linq.DescribeAsync(NoTenant, Token);

        status.IsAvailable.Should().BeTrue();
        status.Types.Should().BeEmpty("the host hid every document type");
        status.Events.Should().NotBeNull("the event log is always there to query");
        status.Events!.Members.Should().Contain(["Sequence", "Timestamp", "EventTypeName", "StreamId", "Version"]);
        status.Events.EventsSchema.Should().Be(LinqTestStore.Schema);
    }

    [DynamicLinqFact]
    public async Task Describing_the_events_connects_to_nothing_and_follows_the_scopes_tenant()
    {
        await using Harness harness = Harness.Create(
            static options => options.Capabilities = MartenStudioCapabilities.All(),
            static marten => marten.Events.TenancyStyle = TenancyStyle.Conjoined);

        LinqModeStatus all = await harness.Linq.DescribeAsync(NoTenant, Token);
        LinqModeStatus one = await harness.Linq.DescribeAsync(AcmeScope, Token);

        all.Events!.Conjoined.Should().BeTrue();
        all.Events.Members.Should().Contain("TenantId");
        one.Events!.Members.Should().NotContain("TenantId");
        one.Events.Withheld.Should().Contain(static x => x.Name == "TenantId" && x.Reason.Contains(Acme, StringComparison.Ordinal));
    }

    [DynamicLinqFact]
    public async Task The_target_decides_and_whatever_alias_came_with_it_is_dropped()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        const string Hostile = "no\r\nsuch\u001b[2J";

        LinqQueryResult result = await harness.Linq.RunAsync(
            NoTenant, new LinqQueryRequest(Hostile, "Version > 1", Target: LinqQueryTarget.Events), Token);

        result.Alias.Should().Be(LinqQueryService.EventsAlias);
        result.Sql.Should().Contain("mt_events");
        harness.Ring.GetLatest()[0].Target.Should().NotContain("no\r\nsuch");
    }

    // ------------------------------------------------------------------------------------------------
    // Refusals
    // ------------------------------------------------------------------------------------------------

    [DynamicLinqTheory]
    [InlineData("IsArchived", 0)]
    [InlineData("StreamKey == \"a\"", 0)]
    [InlineData("CorrelationId == \"c\"", 0)]
    [InlineData("Data == null", 0)]
    public async Task A_withheld_member_is_a_screen_refusal_with_a_caret_and_an_event_audit_entry(string predicate, int position)
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqQueryResult result = await harness.Linq.RunAsync(NoTenant, Events(predicate), Token);

        result.Refusal!.Stage.Should().Be(LinqStage.Screen);
        result.Refusal.Clause.Should().Be(LinqClause.Where);
        result.Refusal.Position.Should().Be(position);
        result.Sql.Should().BeEmpty("nothing reached Marten");
        harness.Ring.GetLatest()[0].Should().Match<StudioActionLogEntry>(x => !x.Succeeded && x.Action == LinqQueryService.LinqEventQueryAction);
    }

    [DynamicLinqFact]
    public async Task A_page_past_the_offset_cap_is_refused_in_the_events_words()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqQueryResult result = await harness.Linq.RunAsync(NoTenant, Events(null, offset: LinqQueryLimits.MaxOffset + 1), Token);

        result.Refusal!.Message.Should().Contain("OFFSET").And.Contain("events you want come first");
    }

    [DynamicLinqFact]
    public async Task Marten_refusing_a_shape_is_shown_as_Martens_refusal()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqQueryResult result = await harness.Linq.RunAsync(NoTenant, Events("EventTypeName.Length > 3"), Token);

        result.Refusal!.Stage.Should().Be(LinqStage.Translate);
        result.Refusal.Message.Should().StartWith("Marten cannot translate this query");
    }

    // ------------------------------------------------------------------------------------------------
    // An accepted query
    // ------------------------------------------------------------------------------------------------

    [DynamicLinqFact]
    public async Task An_accepted_query_is_built_by_Marten_and_only_then_reaches_for_the_database()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqQueryResult result = await harness.Linq.RunAsync(
            NoTenant, Events("EventTypeName == \"order_placed\" and Version > 1", "Timestamp desc"), Token);

        result.Sql.Should().Contain("mt_events").And.Contain("order by d.timestamp desc, d.seq_id desc");
        result.Parameters.Should().NotBeEmpty();
        result.Error!.SqlState.Should().Be("08006", "the store points at a host that does not resolve");
        result.Plan.Should().BeNull("nothing runs on a connection that never opened");
        result.EventScope.Should().NotBeNull();
        result.EventRows.Should().BeEmpty();

        StudioActionLogEntry entry = harness.Ring.GetLatest()[0];
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be(LinqQueryService.LinqEventQueryAction);
        entry.Target.Should().Be("events: EventTypeName == \"order_placed\" and Version > 1 order by Timestamp desc");
    }

    [DynamicLinqFact]
    public async Task The_archived_selector_is_in_the_audit_target()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        await harness.Linq.RunAsync(NoTenant, Events(null, archived: ArchivedFilter.Only), Token);

        harness.Ring.GetLatest()[0].Target.Should().Be("events: (all) (archived: only)");
    }

    // ------------------------------------------------------------------------------------------------
    // Reach, not scope
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Conjoined events from a tenant scope: Marten narrows to the tenant, so the question is the tenant's and the
    /// tenant-only visitor passes the gate (and fails at the connection that is not there).
    /// </summary>
    [DynamicLinqFact]
    public async Task Conjoined_events_from_a_tenant_scope_ask_only_the_tenant_and_a_tenant_only_visitor_passes()
    {
        await using Harness harness = Harness.Create(
            static options => options.Capabilities = MartenStudioCapabilities.All(),
            static marten => marten.Events.TenancyStyle = TenancyStyle.Conjoined);
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        LinqQueryResult result = await harness.Linq.RunAsync(AcmeScope, Events("Version > 1"), Token);

        result.Error!.SqlState.Should().Be("08006", "the gate let it through");
        result.EventScope.Should().Be(new LinqEventScope(Acme, CrossTenant: false, ArchivedFilter.Exclude, WholeDatabase: false, PerTenantSequences: false));
        harness.Policies.Calls.Should().NotContain(static x => x.Resource.TenantId == null && x.Resource.Capability == nameof(StudioCapability.RunLinqQueries),
            "the whole database is never asked when Marten narrows the read");
        harness.Ring.GetLatest()[0].TenantId.Should().Be(Acme);
    }

    /// <summary>
    /// Single-tenanted events from a tenant scope: nothing narrows the read, so it is the whole database's event log, asked
    /// as <c>(store, database, null)</c> and audited against that scope.
    /// </summary>
    [DynamicLinqFact]
    public async Task Single_events_from_a_tenant_scope_are_asked_as_the_whole_database_and_a_tenant_only_visitor_is_refused()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        Func<Task> run = () => harness.Linq.RunAsync(AcmeScope, Events("Version > 1"), Token);

        StudioNotAuthorizedException refused = (await run.Should().ThrowAsync<StudioNotAuthorizedException>()).Which;

        refused.Scope.TenantId.Should().BeNull("the question asked was the database's as a whole");
        harness.Policies.Calls.Should().Contain(static x => x.Resource.TenantId == null,
            "the database as a whole was asked, and the store policy refused it before the write policy was reached");

        StudioActionLogEntry entry = harness.Ring.GetLatest()[0];
        entry.Succeeded.Should().BeFalse();
        entry.TenantId.Should().BeNull();
        entry.Action.Should().Be(LinqQueryService.LinqEventQueryAction);
    }

    [DynamicLinqFact]
    public async Task Single_events_from_a_tenant_scope_pass_for_a_visitor_allowed_the_whole_database_and_say_so()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqQueryResult result = await harness.Linq.RunAsync(AcmeScope, Events("Version > 1"), Token);

        result.Error!.SqlState.Should().Be("08006");
        result.EventScope!.WholeDatabase.Should().BeTrue();
        result.EventScope.TenantId.Should().BeNull("Marten narrowed nothing");
        result.Sql.Should().NotContain("d.tenant_id");
        harness.Ring.GetLatest()[0].TenantId.Should().BeNull("audited against the scope that was asked");
    }

    [DynamicLinqFact]
    public async Task Conjoined_events_with_no_tenant_read_every_tenant_and_say_so()
    {
        await using Harness harness = Harness.Create(
            static options => options.Capabilities = MartenStudioCapabilities.All(),
            static marten => marten.Events.TenancyStyle = TenancyStyle.Conjoined);

        LinqQueryResult result = await harness.Linq.RunAsync(NoTenant, Events(null), Token);

        result.EventScope!.CrossTenant.Should().BeTrue();
        result.Sql.Should().Contain("1=1").And.Contain("d.tenant_id");
    }

    // ------------------------------------------------------------------------------------------------
    // What if?
    // ------------------------------------------------------------------------------------------------

    [DynamicLinqFact]
    public async Task A_what_if_over_the_events_is_not_compared_and_audited_after_the_same_gate()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        WhatIfResult result = await harness.Linq.WhatIfAsync(NoTenant, Events("Version > 1"), Imagined, Token);

        result.Before.Should().BeNull();
        result.Reason.Should().Be(LinqQueryService.EventsWhatIfMessage);

        StudioActionLogEntry entry = harness.Ring.GetLatest()[0];
        entry.Action.Should().Be(LinqQueryService.LinqWhatIfAction);
        entry.Succeeded.Should().BeFalse();
        entry.Target.Should().StartWith("events: Version > 1");
        harness.Policies.Calls.Should().Contain(static x => x.Resource.Capability == nameof(StudioCapability.RunLinqQueries),
            "the run's own gate was asked first");
    }

    [DynamicLinqFact]
    public async Task A_what_if_over_the_events_is_refused_at_the_gate_before_it_is_declined()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities());

        WhatIfResult result = await harness.Linq.WhatIfAsync(NoTenant, Events("Version > 1"), Imagined, Token);

        result.Refusal.Should().Be(PostgresStudio.Services.Database.DatabaseRefusal.CapabilityOff);
    }

    // ------------------------------------------------------------------------------------------------
    // The harness
    // ------------------------------------------------------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, IServiceScope scope, TestStoreAuthorizationService policies)
        {
            this.provider = provider;
            this.scope = scope;
            Policies = policies;
        }

        public TestStoreAuthorizationService Policies { get; }

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public ILinqQueryService Linq => scope.ServiceProvider.GetRequiredService<ILinqQueryService>();

        public static Harness Create(Action<MartenStudioOptions> configure, Action<StoreOptions>? marten = null)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(options =>
            {
                options.Connection(LinqTestStore.Unreachable);
                options.DatabaseSchemaName = LinqTestStore.Schema;
                options.Schema.For<LinqOrder>();
                options.Schema.For<LinqSecret>();
                marten?.Invoke(options);
            });
            services.AddMartenStudio(options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                options.KnownTenantIds.Add(Acme);
                configure(options);
            });

            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            return new Harness(provider, provider.CreateScope(), policies);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
