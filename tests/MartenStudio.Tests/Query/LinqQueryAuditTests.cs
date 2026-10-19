using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Services;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Query;

/// <summary>
/// SEC1 L3 and L4: what LINQ mode writes down, and that every refusal is written down.
/// </summary>
/// <remarks>
/// <para>
/// <b>L3.</b> Events 9260 and 9261 carried the predicate and the ordering whole, at Information - up to a thousand
/// characters of the visitor's own text per line, and on the capability-off refusal, which only a scripted circuit
/// reaches, text nothing had authorized. They now carry each of the alias, the predicate and the ordering cut by
/// <see cref="QueryService.Target" />, exactly as the ring's target is cut, and a capability refusal carries none.
/// </para>
/// <para>
/// <b>L4.</b> A scope the policies passed that names no store or database, a store that will not build, and a host's
/// <c>IsDocumentTypeVisible</c> that throws all escaped the service without an entry in the ring. Each is now a refusal,
/// recorded like the rest, and still fails closed.
/// </para>
/// <para>
/// No database: the store's host does not resolve, and every refusal here happens before a connection would open.
/// The texts are long enough that every Marten this runs at refuses them - at the floor because LINQ mode is absent,
/// at the latest because the screen caps a predicate at a thousand characters - so 9261 is written at both.
/// </para>
/// </remarks>
public class LinqQueryAuditTests
{
    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // L3: capped as the audit target is
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refusal_logs_the_predicate_and_the_ordering_capped_exactly_as_the_audit_target_is()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        string where = "Name == \"" + new string('w', 4_000) + "\"";
        string orderBy = string.Join(", ", Enumerable.Repeat("Total desc", 60));

        LinqQueryResult result = await harness.Linq.RunAsync(Scope, new LinqQueryRequest("linqorder", where, orderBy), Token);

        result.Refusal.Should().NotBeNull("the premise: refused at either Marten, before any connection");

        CapturedLogEntry refused = harness.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9261).Subject;
        refused.Message.Should().Contain(QueryService.Target(where), "the predicate, cut where the ring's target is cut");
        refused.Message.Should().Contain(QueryService.Target(orderBy), "and the ordering, the same way");
        refused.Message.Length.Should().BeLessThan(where.Length, "never the text whole");

        harness.Logs.Entries.Should().NotContain(x => x.Message.Contains(where, StringComparison.Ordinal), "no line carries the predicate whole");
        harness.Logs.Entries.Should().NotContain(x => x.Message.Contains(orderBy, StringComparison.Ordinal), "nor the ordering");

        StudioActionLogEntry entry = harness.Ring.GetLatest()[0];
        entry.Succeeded.Should().BeFalse();
        entry.Target.Length.Should().BeLessThanOrEqualTo(QueryService.AuditTargetLength + 1, "the ring's target: 200 characters and an ellipsis");
        entry.Target.Should().StartWith("linqorder: Name == \"www");
    }

    [Fact]
    public async Task A_short_predicate_is_logged_as_it_was_typed()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        await harness.Linq.RunAsync(Scope, new LinqQueryRequest("nosuchtype", "Name == \"ada\"", "Total desc"), Token);

        harness.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9261)
            .Which.Message.Should().Contain("Where Name == \"ada\" order by Total desc", "the cap cuts only what is longer than it");
    }

    [Fact]
    public async Task An_alias_is_visitor_text_too_and_is_capped_in_the_refusal_and_the_log()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        string alias = new('a', 3_000);

        LinqQueryResult result = await harness.Linq.RunAsync(Scope, new LinqQueryRequest(alias, "Name == \"x\""), Token);

        result.Refusal!.Message.Should().Contain("no visible document type").And.Contain(QueryService.Target(alias));
        result.Refusal.Message.Should().NotContain(alias);
        harness.Logs.Entries.Should().NotContain(x => x.Message.Contains(alias, StringComparison.Ordinal));
        harness.Ring.GetLatest()[0].Message.Should().NotContain(alias);
    }

    [Fact]
    public async Task A_capability_refusal_writes_none_of_the_text_to_the_ring_or_the_log()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities());

        const string Secret = "sec1-capability-predicate";
        const string SecretOrder = "Sec1CapabilityOrdering";

        Func<Task> run = () => harness.Linq.RunAsync(
            Scope, new LinqQueryRequest("linqorder", "Name == \"" + Secret + "\"", SecretOrder), Token);

        await run.Should().ThrowAsync<StudioCapabilityDeniedException>();

        harness.Logs.Entries.Should().NotBeEmpty("the premise: the refusal was logged");
        harness.Logs.Entries.Should().NotContain(x => x.Message.Contains(Secret, StringComparison.Ordinal) || x.Message.Contains(SecretOrder, StringComparison.Ordinal),
            "nothing authorized the text, and only a scripted circuit sends it with the capability off");

        harness.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9261)
            .Which.Message.Should().Contain("capability").And.Contain(LinqQueryService.TextNotRecorded);
        harness.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9202, "the capability refusal itself, as every other one is");

        StudioActionLogEntry entry = harness.Ring.GetLatest()[0];
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be(LinqQueryService.LinqQueryAction);
        entry.Target.Should().Be("linqorder: " + LinqQueryService.TextNotRecorded);
    }

    // ------------------------------------------------------------------------------------------------
    // L4: every refusal is recorded
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("nosuchstore", "", "No Marten store is registered under 'nosuchstore'")]
    [InlineData("default", "nosuchdb", "has no database with identity 'nosuchdb'")]
    public async Task A_scope_the_policies_passed_that_names_nothing_here_is_a_recorded_refusal(string storeKey, string databaseId, string said)
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        LinqQueryResult result = await harness.Linq.RunAsync(
            new StudioScope(storeKey, databaseId, null), new LinqQueryRequest("linqorder", "Name == \"a\""), Token);

        harness.Policies.Calls.Should().NotBeEmpty("the premise: the policies were asked, and allowed it");
        result.Refusal.Should().NotBeNull("refused, failing closed - and returned rather than thrown past the audit");
        result.Refusal!.Stage.Should().Be(LinqStage.Unavailable);
        result.Refusal.Message.Should().Contain(said, "the resolver's own sentence, which the page showed before");
        result.Sql.Should().BeEmpty("nothing reached Marten");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(LinqQueryService.LinqQueryAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Contain(said);
        entry.Capability.Should().Be(nameof(StudioCapability.RunLinqQueries));
        harness.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9261);
    }

    [Fact]
    public async Task A_store_that_will_not_build_is_a_recorded_refusal()
    {
        await using Harness harness = Harness.Create(
            static options => options.Capabilities = MartenStudioCapabilities.All(),
            static services => services.AddSingleton<MartenStoreRegistryTest.IBrokenStore>(
                static _ => throw new InvalidOperationException("no connection string was configured")));

        LinqQueryResult result = await harness.Linq.RunAsync(
            new StudioScope(nameof(MartenStoreRegistryTest.IBrokenStore), string.Empty, null),
            new LinqQueryRequest("linqorder", "Name == \"a\""),
            Token);

        result.Refusal!.Stage.Should().Be(LinqStage.Unavailable);
        result.Refusal.Message.Should().Contain("could not be built").And.Contain("no connection string was configured");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Contain("could not be built");
    }

    /// <summary>
    /// A delegate that throws for the queried type, and one that throws only for another type - which the hidden set
    /// asks about. Both fail closed with the answer a type that is not there gets, are recorded, and are logged with
    /// the exception for whoever has to fix the delegate.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_visibility_delegate_that_throws_is_a_recorded_refusal_with_the_answer_a_missing_type_gets(bool forTheQueriedType)
    {
        await using Harness harness = Harness.Create(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.IsDocumentTypeVisible = type => forTheQueriedType || type == typeof(LinqSecret)
                ? throw new InvalidOperationException("the host's delegate is broken")
                : true;
        });

        LinqQueryResult thrown = await harness.Linq.RunAsync(Scope, new LinqQueryRequest("linqorder", "Name == \"a\""), Token);
        LinqQueryResult missing = await harness.Linq.RunAsync(Scope, new LinqQueryRequest("nosuchtype", "Name == \"a\""), Token);

        thrown.Refusal!.Stage.Should().Be(LinqStage.Unavailable);
        thrown.Refusal.Message.Should().Be(missing.Refusal!.Message.Replace("nosuchtype", "linqorder", StringComparison.Ordinal),
            "a delegate that throws is no different, to the visitor, from a type that is not there");
        thrown.Sql.Should().BeEmpty();

        harness.Ring.GetLatest().Should().HaveCount(2).And.OnlyContain(static x => !x.Succeeded && x.Action == LinqQueryService.LinqQueryAction);
        harness.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9263, "the delegate's exception, for the operator");
    }

    /// <summary>A studio over a store that never connects, with a policy on both axes that allows everything.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, IServiceScope scope, TestStoreAuthorizationService policies, CapturingLoggerProvider logs)
        {
            this.provider = provider;
            this.scope = scope;
            Policies = policies;
            Logs = logs;
        }

        public TestStoreAuthorizationService Policies { get; }

        public CapturingLoggerProvider Logs { get; }

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public ILinqQueryService Linq => scope.ServiceProvider.GetRequiredService<ILinqQueryService>();

        public static Harness Create(Action<MartenStudioOptions> configure, Action<IServiceCollection>? services = null)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();
            var logs = new CapturingLoggerProvider();

            var collection = new ServiceCollection();
            collection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            collection.AddMarten(static options =>
            {
                options.Connection(LinqTestStore.Unreachable);
                options.DatabaseSchemaName = LinqTestStore.Schema;
                options.Schema.For<LinqOrder>();
                options.Schema.For<LinqSecret>();
            });
            services?.Invoke(collection);
            collection.AddMartenStudio(options =>
            {
                options.StoreAuthorizationPolicy = "store-policy";
                options.WriteAuthorizationPolicy = "write-policy";
                configure(options);
            });

            collection.AddSingleton<IAuthorizationService>(policies);
            collection.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = collection.BuildServiceProvider();

            return new Harness(provider, provider.CreateScope(), policies, logs);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
