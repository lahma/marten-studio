using System.Reflection;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;
using MartenStudio.Services.Query;
using MartenStudio.Services.Relationships;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// One place that fails when a mutating service method is added without gating it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this adds to the per-area tests.</b> Every area already proves its own refusals —
/// <c>DocumentWriteServiceTests</c>, <c>ProjectionsWritePolicyTests</c>, <c>SchemaWritePolicyTests</c>
/// and the live suites. What none of them can do is fail when a <em>new</em> mutating method appears:
/// a method nobody wrote a test for is a method with no test, and the gap is silent. So the roster below
/// names every member of every data seam interface with a verdict, and
/// <see cref="Every_member_of_every_data_service_is_classified" /> fails when the interfaces and the
/// roster disagree in either direction. Adding a method without classifying it is a red build.
/// </para>
/// <para>
/// <b>The three refusals, for every mutating member.</b> AGENTS.md hard rule 5 says a mutating operation
/// is capability-gated in the service layer and audited. That is three separate refusals — the
/// capability is off, <c>ReadOnly</c> is on, the write policy refused this visitor for this scope — and
/// each of them has to reach the audit whether or not anything is rendered. Each is asserted here for
/// all twenty mutating members at once.
/// </para>
/// <para>
/// <b>No database.</b> The store is pointed at a host that does not resolve, exactly as
/// <c>DocumentWriteServiceTests</c> does. Every refusal asserted here must happen <em>before</em> a
/// connection is opened, so a gate that ran too late would hang or throw a Npgsql error rather than
/// quietly passing.
/// </para>
/// </remarks>
public class CapabilityGatingMatrixTests
{
    private const string DummyConnectionString =
        "Host=marten-studio-gating-test.invalid;Database=none;Username=none;Password=none";

    private const string StorePolicy = "store-policy";
    private const string WritePolicy = "write-policy";

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    // -------------------------------------------------------------------------------------------------
    // The roster
    // -------------------------------------------------------------------------------------------------

    /// <summary>One mutating member: what it is called, and which capability it needs.</summary>
    /// <param name="Service">The seam interface it lives on.</param>
    /// <param name="Method">Its name.</param>
    /// <param name="Capability">The capability the service requires before it does anything.</param>
    private sealed record GatedCall(Type Service, string Method, StudioCapability Capability);

    /// <summary>
    /// Every mutating member of every data seam interface, and the capability it is gated on.
    /// </summary>
    /// <remarks>
    /// Curated deliberately rather than discovered: "which of these methods changes something" is a
    /// design decision, and a heuristic that guessed it would be a test that agrees with whatever the
    /// code happens to do. What reflection checks is that the roster and the interfaces say the same
    /// thing.
    /// </remarks>
    private static readonly GatedCall[] Mutating =
    [
        new(typeof(IDocumentWriteService), nameof(IDocumentWriteService.PreviewAsync), StudioCapability.EditDocuments),
        new(typeof(IDocumentWriteService), nameof(IDocumentWriteService.SaveAsync), StudioCapability.EditDocuments),
        new(typeof(IDocumentWriteService), nameof(IDocumentWriteService.DeleteAsync), StudioCapability.DeleteDocuments),
        new(typeof(IDocumentWriteService), nameof(IDocumentWriteService.UndeleteAsync), StudioCapability.DeleteDocuments),
        new(typeof(IDocumentWriteService), nameof(IDocumentWriteService.BulkDeleteAsync), StudioCapability.DeleteDocuments),

        new(typeof(IEventDataService), nameof(IEventDataService.ArchiveStreamAsync), StudioCapability.ArchiveStreams),
        new(typeof(IEventDataService), nameof(IEventDataService.DiscardDeadLetterAsync), StudioCapability.ManageDeadLetters),
        new(typeof(IEventDataService), nameof(IEventDataService.SkipEventAsync), StudioCapability.ManageDeadLetters),
        new(typeof(IEventDataService), nameof(IEventDataService.RewindSubscriptionAsync), StudioCapability.ManageDeadLetters),

        new(typeof(IProjectionDataService), nameof(IProjectionDataService.StartAgentAsync), StudioCapability.ControlDaemon),
        new(typeof(IProjectionDataService), nameof(IProjectionDataService.StopAgentAsync), StudioCapability.ControlDaemon),
        new(typeof(IProjectionDataService), nameof(IProjectionDataService.PauseDaemonAsync), StudioCapability.ControlDaemon),
        new(typeof(IProjectionDataService), nameof(IProjectionDataService.ResumeDaemonAsync), StudioCapability.ControlDaemon),
        new(typeof(IProjectionDataService), nameof(IProjectionDataService.RestartHighWaterAgentAsync), StudioCapability.ControlDaemon),
        new(typeof(IProjectionDataService), nameof(IProjectionDataService.RebuildAsync), StudioCapability.RebuildProjections),
        new(typeof(IProjectionDataService), nameof(IProjectionDataService.CancelOperationAsync), StudioCapability.RebuildProjections),
        new(typeof(IProjectionDataService), nameof(IProjectionDataService.AdvanceHighWaterMarkAsync), StudioCapability.CorrectProgression),
        new(typeof(IProjectionDataService), nameof(IProjectionDataService.CorrectProgressionAsync), StudioCapability.CorrectProgression),

        new(typeof(ISchemaDataService), nameof(ISchemaDataService.ApplyAsync), StudioCapability.ApplySchemaChanges),

        new(typeof(IQueryService), nameof(IQueryService.RunSqlAsync), StudioCapability.RunSql),
    ];

    /// <summary>
    /// Every member that reads, by interface.
    /// </summary>
    /// <remarks>
    /// Named rather than assumed, because "not in the mutating list" is not the same claim as "somebody
    /// decided this one reads". Three of these do touch the database in ways worth knowing about —
    /// <c>ISchemaDataService.CheckAsync</c>, <c>PreviewAsync</c> and <c>DdlAsync</c> are behind buttons
    /// that say what they may create (hard rule 14) — and they are still reads as far as the capability
    /// model is concerned.
    /// </remarks>
    private static readonly Dictionary<Type, string[]> Reads = new()
    {
        [typeof(IStoreInfoService)] = ["GetOverviewAsync"],
        [typeof(IConfigurationService)] = ["DescribeAsync"],
        [typeof(IRelationshipDataService)] = ["GetGraphAsync", "GetReferencedByAsync"],
        [typeof(IDocumentWriteService)] = [],
        [typeof(IDocumentDataService)] =
        [
            "CountExactAsync", "GetCollectionsAsync", "GetDocumentAsync", "GetRelatedAsync",
            "ListAsync", "ListRecentAsync", "ProbeIdAsync", "StreamExistsAsync",
        ],
        [typeof(IEventDataService)] =
        [
            "AggregateAtVersionAsync", "CountDeadLettersAsync", "DescribeAsync", "GetEventBySequenceAsync",
            "GetEventStoreCountsAsync", "GetFeedAsync", "GetHighestSequenceAsync", "GetRecentStreamsAsync",
            "GetStreamAsync", "GetStreamEventsAsync", "ListAggregateCandidatesAsync", "ListDeadLettersAsync",
            "ListEventTypesAsync", "ListStreamsAsync",
        ],
        [typeof(IProjectionDataService)] =
        [
            // FindOperation is the one synchronous member on any of these interfaces: an in-memory
            // lookup of a running operation by its handle, which reads the tracker and touches nothing.
            "DescribeRebuildAsync", "FindOperation", "GetProjectionsAsync", "GetSummaryAsync",
            "RunningOperationsAsync", "SubscribeToLiveStateAsync",
        ],
        [typeof(ISchemaDataService)] =
        [
            "CheckAsync", "DatabaseIdentityAsync", "DdlAsync", "FunctionsAsync", "IndexesAsync",
            "PreviewAsync", "TablesAsync",
        ],
        [typeof(IQueryService)] = ["BuildExamplesAsync", "ListDocumentTypesAsync", "RunMartenQueryAsync"],
    };

    /// <summary>
    /// Verbs a method name cannot start with and be a read.
    /// </summary>
    /// <remarks>
    /// The second net, and the one that catches the more likely mistake: classifying a new mutating
    /// method as a read. The roster above fails when a method is <em>missing</em>; this fails when it is
    /// present and filed under the wrong heading. Deliberately unambiguous prefixes — <c>Run</c> is not
    /// one of them, because <c>RunMartenQueryAsync</c> reads and <c>RunSqlAsync</c> does not.
    /// </remarks>
    private static readonly string[] MutatingVerbs =
    [
        "Advance", "Apply", "Archive", "BulkDelete", "Cancel", "Correct", "Delete", "Discard", "Pause",
        "Rebuild", "Restart", "Resume", "Rewind", "RunSql", "Save", "Skip", "Start", "Stop", "Undelete",
    ];

    private static IEnumerable<Type> Services => Reads.Keys;

    // -------------------------------------------------------------------------------------------------
    // The roster agrees with the interfaces
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_member_of_every_data_service_is_classified()
    {
        List<string> problems = [];

        foreach (Type service in Services)
        {
            HashSet<string> classified =
            [
                .. Reads[service],
                .. Mutating.Where(x => x.Service == service).Select(static x => x.Method),
            ];

            HashSet<string> declared =
            [
                .. service.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Select(static x => x.Name),
            ];

            foreach (string missing in declared.Except(classified).Order(StringComparer.Ordinal))
            {
                problems.Add(
                    service.Name + "." + missing + " is not classified. Add it to Mutating (with the "
                    + "capability its implementation requires) or to Reads.");
            }

            foreach (string stale in classified.Except(declared).Order(StringComparer.Ordinal))
            {
                problems.Add(service.Name + "." + stale + " is in the roster but no longer exists.");
            }
        }

        problems.Should().BeEmpty(
            "a mutating method added to a data service without being gated is the failure this test "
            + "exists to catch, and it can only catch it if the roster is complete: "
            + string.Join(" | ", problems));
    }

    [Fact]
    public void A_method_that_reads_like_a_write_is_classified_as_one()
    {
        List<string> problems = [];

        foreach (Type service in Services)
        {
            foreach (string read in Reads[service])
            {
                string? verb = MutatingVerbs.FirstOrDefault(x => read.StartsWith(x, StringComparison.Ordinal));

                if (verb is not null)
                {
                    problems.Add(service.Name + "." + read + " starts with '" + verb + "' and is filed as a read.");
                }
            }
        }

        problems.Should().BeEmpty(
            "a mutating method filed under Reads is gated by nothing and audited by nothing: "
            + string.Join(" | ", problems));
    }

    [Fact]
    public void The_roster_covers_every_capability()
    {
        Mutating.Select(static x => x.Capability).Distinct().Order().Should().Equal(
            Enum.GetValues<StudioCapability>().Order(),
            "a capability no method requires is a capability nothing enforces, and one this roster has "
            + "forgotten is a set of methods nothing here tests");
    }

    // -------------------------------------------------------------------------------------------------
    // The three refusals
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_capability_that_is_off_refuses_every_mutating_call_and_audits_it()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = new MartenStudioCapabilities());

        await AssertEveryCallRefusedAsync<StudioCapabilityDeniedException>(harness, checkCapabilityOnEntry: true);

        harness.Policies.Calls.Should().BeEmpty(
            "a capability that is off is answered before anybody is asked who is asking, so no policy "
            + "handler runs and nothing about this process leaks to a caller who may not write");
    }

    [Fact]
    public async Task ReadOnly_refuses_every_mutating_call_and_audits_it()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.ReadOnly = true;
        });

        await AssertEveryCallRefusedAsync<StudioCapabilityDeniedException>(harness, checkCapabilityOnEntry: true);
    }

    [Fact]
    public async Task A_write_policy_that_refuses_the_visitor_refuses_every_mutating_call_and_audits_it()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities = MartenStudioCapabilities.All());

        // The store policy passes and the write policy does not, which is the shape that matters: the
        // visitor may look at this store and may not change it. The capability travels on the resource,
        // so a null capability is the store question and a named one is the write question.
        harness.Policies.Allow(static resource => resource.Capability is null);

        await AssertEveryCallRefusedAsync<StudioNotAuthorizedException>(harness, checkCapabilityOnEntry: false);
    }

    private static async Task AssertEveryCallRefusedAsync<TException>(Harness harness, bool checkCapabilityOnEntry)
        where TException : Exception
    {
        List<string> problems = [];

        foreach (GatedCall call in Mutating)
        {
            int before = harness.Ring.GetLatest().Count;
            string what = call.Service.Name + "." + call.Method;

            object service = harness.Resolve(call.Service);
            MethodInfo method = call.Service.GetMethod(call.Method)
                ?? throw new InvalidOperationException(what + " does not exist.");

            Exception? thrown = await InvokeAsync(service, method);

            if (thrown is not TException)
            {
                problems.Add(what + " threw " + (thrown?.GetType().Name ?? "nothing")
                    + " where " + typeof(TException).Name + " was required"
                    + (thrown is null ? string.Empty : ": " + thrown.Message));
                continue;
            }

            if (checkCapabilityOnEntry
                && thrown is StudioCapabilityDeniedException denial
                && denial.Capability != call.Capability)
            {
                problems.Add(what + " was refused for " + denial.Capability + " rather than " + call.Capability);
            }

            IReadOnlyList<StudioActionLogEntry> after = harness.Ring.GetLatest();

            if (after.Count <= before)
            {
                problems.Add(what + " was refused and wrote nothing to the audit");
                continue;
            }

            if (after[0].Succeeded)
            {
                problems.Add(what + " audited its refusal as a success");
            }
        }

        problems.Should().BeEmpty(
            "every mutating operation is refused in the service layer and audited, whatever a page "
            + "chose to render (AGENTS.md hard rule 5): " + string.Join(" | ", problems));
    }

    /// <summary>Invokes one service method with plausible arguments and returns what it threw.</summary>
    /// <remarks>
    /// Plausible, not empty: several of these validate their arguments before they reach the gate, and an
    /// <c>ArgumentException</c> from a blank id would be a test that proved nothing about gating.
    /// </remarks>
    private static async Task<Exception?> InvokeAsync(object service, MethodInfo method)
    {
        object?[] arguments = [.. method.GetParameters().Select(Argument)];

        try
        {
            object? returned = method.Invoke(service, arguments);

            if (returned is Task task)
            {
                await task;
            }

            return null;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return exception.InnerException;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static object? Argument(ParameterInfo parameter)
    {
        Type type = parameter.ParameterType;

        if (type == typeof(StudioScope))
        {
            return Scope;
        }

        if (type == typeof(CancellationToken))
        {
            return Token;
        }

        if (type == typeof(Guid))
        {
            return Guid.Parse("8f1d5a6e-1a2b-4c3d-9e8f-000000000001");
        }

        if (type == typeof(long))
        {
            return 1L;
        }

        if (type == typeof(bool))
        {
            return false;
        }

        if (type == typeof(string))
        {
            return parameter.Name switch
            {
                "alias" => "customer",
                "editedJson" => "{}",
                "confirmation" => "not-the-database-identity",
                _ => "8f1d5a6e-1a2b-4c3d-9e8f-000000000001",
            };
        }

        if (type == typeof(IReadOnlyList<string>))
        {
            return new[] { "8f1d5a6e-1a2b-4c3d-9e8f-000000000001" };
        }

        if (type == typeof(DocumentConcurrencyToken))
        {
            return DocumentConcurrencyToken.None;
        }

        if (type == typeof(SqlConsoleRequest))
        {
            return new SqlConsoleRequest("select 1");
        }

        throw new NotSupportedException(
            "No argument is defined for " + type.Name + " " + parameter.Name
            + ". Add one here: a mutating member this test cannot call is a mutating member it cannot gate-check.");
    }

    // -------------------------------------------------------------------------------------------------
    // The harness
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A real studio container over a store that will never connect.
    /// </summary>
    /// <remarks>
    /// The whole container rather than five hand-built services, because what is being asserted is what
    /// a circuit resolves: a service constructed by hand in a test could be wired differently from the
    /// one a page gets, and the difference would be invisible.
    /// </remarks>
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

        public object Resolve(Type service) => scope.ServiceProvider.GetRequiredService(service);

        public static Harness Create(Action<MartenStudioOptions> configure)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(x => x.Connection(DummyConnectionString));
            services.AddMartenStudio(options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                configure(options);
            });

            // After AddMartenStudio, so these win over anything the framework registrations contributed.
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
