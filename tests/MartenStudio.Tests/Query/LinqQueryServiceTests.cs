using Marten;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The service's gate and its refusals, through a real studio container over a store that never connects. Every
/// refusal here happens before a connection is opened - a gate that ran too late would answer 08006 instead.
/// </summary>
public class LinqQueryServiceTests
{
    private const string StorePolicy = "store-policy";
    private const string WritePolicy = "write-policy";

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // The three refusals, in the service
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_capability_that_is_off_refuses_before_anything_is_resolved_or_parsed()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities());

        Func<Task> run = () => harness.Linq.RunAsync(Scope, new LinqQueryRequest("linqorder", "Name == \"a\""), Token);

        (await run.Should().ThrowAsync<StudioCapabilityDeniedException>()).Which.Capability.Should().Be(StudioCapability.RunLinqQueries);

        harness.Policies.Calls.Should().BeEmpty("a capability that is off is answered before any policy is asked");
        harness.Ring.GetLatest()[0].Should().Match<StudioActionLogEntry>(static x =>
            !x.Succeeded && x.Action == LinqQueryService.LinqQueryAction && x.Capability == nameof(StudioCapability.RunLinqQueries));
    }

    [Fact]
    public async Task ReadOnly_refuses_whatever_the_capabilities_say()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.ReadOnly = true;
        });

        Func<Task> run = () => harness.Linq.RunAsync(Scope, new LinqQueryRequest("linqorder", "Name == \"a\""), Token);

        (await run.Should().ThrowAsync<StudioCapabilityDeniedException>()).Which.Reason.Should().Be(CapabilityDenialReason.ReadOnly);
        harness.Ring.GetLatest()[0].Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task The_write_policy_is_asked_with_RunLinqQueries_and_its_refusal_is_audited()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());
        harness.Policies.Allow(static resource => resource.Capability is null);

        Func<Task> run = () => harness.Linq.RunAsync(Scope, new LinqQueryRequest("linqorder", "Name == \"a\""), Token);

        await run.Should().ThrowAsync<StudioNotAuthorizedException>();

        harness.Policies.Calls.Should().Contain(static x => x.Policy == WritePolicy && x.Resource.Capability == nameof(StudioCapability.RunLinqQueries));
        harness.Ring.GetLatest()[0].Succeeded.Should().BeFalse();
    }

    // ------------------------------------------------------------------------------------------------
    // The version line, at the floor
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task At_a_Marten_older_than_9_48_the_mode_is_absent_and_says_which_Marten_it_needs()
    {
        Assert.SkipWhen(DynamicLinqAvailability.IsAvailable, "This run resolved a Marten with DynamicQuery; the absent case is the floor's.");

        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqModeStatus status = await harness.Linq.DescribeAsync(Scope, Token);

        status.Availability.Should().Be(LinqAvailability.MartenTooOld);
        status.Types.Should().BeEmpty();
        status.Message.Should().Contain("Marten 9.48+").And.Contain("this host runs " + MartenRuntime.Current.Describe(RuntimePackage.Marten));

        LinqQueryResult result = await harness.Linq.RunAsync(Scope, new LinqQueryRequest("linqorder", "Name == \"a\""), Token);

        result.Refusal!.Stage.Should().Be(LinqStage.Unavailable);
        result.Refusal.Message.Should().Contain("Marten 9.48+");
        harness.Ring.GetLatest()[0].Succeeded.Should().BeFalse("an unavailable mode is still a refused request, and recorded as one");
    }

    [Fact]
    public async Task A_capability_that_is_off_is_what_the_page_is_told_before_the_version()
    {
        await using Harness off = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities());
        (await off.Linq.DescribeAsync(Scope, Token)).Availability.Should().Be(LinqAvailability.CapabilityOff);

        await using Harness readOnly = Harness.Create(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.ReadOnly = true;
        });
        (await readOnly.Linq.DescribeAsync(Scope, Token)).Availability.Should().Be(LinqAvailability.ReadOnly);
    }

    // ------------------------------------------------------------------------------------------------
    // Types
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_hidden_type_is_the_same_answer_as_no_type()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.IsDocumentTypeVisible = static type => type != typeof(LinqSecret);
        });

        LinqQueryResult hidden = await harness.Linq.RunAsync(Scope, new LinqQueryRequest("linqsecret", "Code == \"a\""), Token);
        LinqQueryResult missing = await harness.Linq.RunAsync(Scope, new LinqQueryRequest("nosuchtype", "Code == \"a\""), Token);

        hidden.Refusal!.Message.Should().Contain("no visible document type");
        missing.Refusal!.Message.Should().Contain("no visible document type");
        harness.Ring.GetLatest().Take(2).Should().OnlyContain(static x => !x.Succeeded);
    }

    // ------------------------------------------------------------------------------------------------
    // At the latest: the screen, the page cap and the gate's tenant
    // ------------------------------------------------------------------------------------------------

    [DynamicLinqFact]
    public async Task A_screened_predicate_is_a_value_with_a_caret_and_an_audit_entry()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqQueryResult result = await harness.Linq.RunAsync(
            Scope, new LinqQueryRequest("linqorder", "Name == System.IO.File.ReadAllText(\"c:/x\")"), Token);

        result.Refusal.Should().NotBeNull();
        result.Refusal!.Stage.Should().Be(LinqStage.Screen);
        result.Refusal.Clause.Should().Be(LinqClause.Where);
        result.Refusal.Position.Should().Be(8);
        result.Sql.Should().BeEmpty("nothing reached Marten");
        harness.Ring.GetLatest()[0].Should().Match<StudioActionLogEntry>(static x => !x.Succeeded && x.Target.Contains("ReadAllText"));
    }

    [DynamicLinqFact]
    public async Task A_page_past_the_offset_cap_is_refused()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqQueryResult result = await harness.Linq.RunAsync(
            Scope, new LinqQueryRequest("linqorder", "Rush", Offset: LinqQueryLimits.MaxOffset + 1), Token);

        result.Refusal!.Message.Should().Contain("OFFSET");
    }

    [DynamicLinqFact]
    public async Task An_accepted_query_is_built_by_Marten_and_only_then_reaches_for_the_database()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqQueryResult result = await harness.Linq.RunAsync(
            Scope, new LinqQueryRequest("linqorder", "Name == \"a\" and Total > 100", "Total desc"), Token);

        result.Sql.Should().Contain("mt_doc_linqorder", "Marten's statement is shown whether or not it could run");
        result.Parameters.Should().NotBeEmpty().And.OnlyContain(static x => x.Name.StartsWith(':') || x.Name.StartsWith('$'));
        result.Error!.SqlState.Should().Be("08006", "the store points at a host that does not resolve");
        result.Plan.Should().BeNull("nothing runs on a connection that never opened");
        harness.Ring.GetLatest()[0].Succeeded.Should().BeFalse();
    }

    [DynamicLinqFact]
    public async Task At_the_latest_the_mode_is_described_with_the_visible_types_only()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.IsDocumentTypeVisible = static type => type != typeof(LinqSecret);
        });

        LinqModeStatus status = await harness.Linq.DescribeAsync(Scope, Token);

        status.IsAvailable.Should().BeTrue();
        status.MayRun.Should().BeTrue();
        status.MayExplain.Should().BeTrue();
        status.Types.Select(static x => x.Alias).Should().Contain("linqorder").And.NotContain("linqsecret");
        status.Types.Single(static x => x.Alias == "linqorder").Members.Should().Contain("Name");
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

        public static Harness Create(Action<MartenStudioOptions> configure)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(static options =>
            {
                options.Connection(LinqTestStore.Unreachable);
                options.DatabaseSchemaName = LinqTestStore.Schema;
                options.Schema.For<LinqOrder>();
                options.Schema.For<LinqSecret>();
            });
            services.AddMartenStudio(options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
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
