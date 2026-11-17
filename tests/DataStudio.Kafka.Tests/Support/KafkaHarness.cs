using System.Security.Claims;

using Confluent.Kafka;

using DataStudio.Services.Settings;
using DataStudio.Services;
using DataStudio.Kafka.Core.Clusters;
using DataStudio.Kafka.Services;
using DataStudio.Kafka.Tests.Contract;
using DataStudio.Kafka.Tests.Support.FakeKafka;

using DataStudio.Marten.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using DataStudio.Postgres.Tests.Support;

namespace DataStudio.Kafka.Tests.Support;

/// <summary>
/// An <see cref="IAuthorizationService" /> that answers for both resources a host with the Kafka module and another module is asked about -
/// <see cref="FakeHostResource" /> (the other module's, from <see cref="FakeHostModule" />) and <see cref="KafkaTopicResource" /> (the Kafka module's) -
/// and records every question, so a test can say which resource reached which policy. A question about any other resource is refused.
/// </summary>
public sealed class RecordingAuthorizationService : IAuthorizationService
{
    private Func<KafkaTopicResource, bool> kafka = static _ => true;
    private Func<FakeHostResource, bool> host = static _ => true;
    private TaskCompletionSource<bool>? hostGate;
    private TaskCompletionSource<bool>? kafkaGate;
    private Func<KafkaTopicResource, bool> kafkaHeld = static _ => false;

    /// <summary>Every question asked, in order: the policy's name and the resource.</summary>
    public List<(string Policy, object? Resource)> Calls { get; } = [];

    /// <summary>The questions about KStudio's resource.</summary>
    public IEnumerable<(string Policy, KafkaTopicResource Resource)> KafkaCalls =>
        Calls.Where(static x => x.Resource is KafkaTopicResource).Select(static x => (x.Policy, (KafkaTopicResource) x.Resource!));

    /// <summary>The questions about the other module's resource.</summary>
    public IEnumerable<(string Policy, FakeHostResource Resource)> HostCalls =>
        Calls.Where(static x => x.Resource is FakeHostResource).Select(static x => (x.Policy, (FakeHostResource) x.Resource!));

    /// <summary>What the other module's policies allow; everything by default.</summary>
    public void AllowHost(Func<FakeHostResource, bool> predicate) => host = predicate;

    /// <summary>
    /// Holds every question about the other module's resource until the returned source is completed - with whether its handler then allows it -
    /// so a test can look at the shell while that module's scope gate is pending.
    /// </summary>
    public TaskCompletionSource<bool> BlockHost()
    {
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref hostGate, gate);
        return gate;
    }

    /// <summary>What KStudio's policies allow; everything by default.</summary>
    public void AllowKafka(Func<KafkaTopicResource, bool> predicate) => kafka = predicate;

    /// <summary>
    /// Holds every question about KStudio's resource that <paramref name="held" /> picks until the returned source is completed - with whether the
    /// handler then allows it - so a test can look at a page while a check is pending.
    /// </summary>
    /// <param name="held">Which questions wait.</param>
    public TaskCompletionSource<bool> HoldKafka(Func<KafkaTopicResource, bool> held)
    {
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        kafkaHeld = held;
        Volatile.Write(ref kafkaGate, gate);
        return gate;
    }

    /// <inheritdoc />
    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
        Task.FromResult(AuthorizationResult.Failed());

    /// <inheritdoc />
    public async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
    {
        lock (Calls)
        {
            Calls.Add((policyName, resource));
        }

        bool allowed = resource switch
        {
            KafkaTopicResource topic => kafka(topic) && await KafkaOpenAsync(topic).ConfigureAwait(false),
            FakeHostResource scope => host(scope) && await HostOpenAsync().ConfigureAwait(false),
            _ => false,
        };

        return allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed();
    }

    private async Task<bool> KafkaOpenAsync(KafkaTopicResource topic) =>
        Volatile.Read(ref kafkaGate) is { } gate && kafkaHeld(topic) ? await gate.Task.Bounded() : true;

    private async Task<bool> HostOpenAsync() => Volatile.Read(ref hostGate) is { } gate ? await gate.Task.Bounded() : true;
}

/// <summary>The cluster entries a test lists, in place of the engine's catalog.</summary>
internal sealed class FakeClusterEntries(IReadOnlyList<KafkaClusterEntry> entries) : IKafkaClusterEntrySource
{
    /// <inheritdoc />
    public IReadOnlyList<KafkaClusterEntry> Entries { get; set; } = entries;
}

/// <summary>
/// A bUnit context for a host that added the shell and the Kafka module (<see cref="KafkaShellContext" />): the shell, the rail, the header and the audit
/// are the real registration's, over the engine's fake broker and a visitor the test names. No database module is composed, because a Kafka-only host
/// has none; a test about the shell between two modules asks for <see cref="FakeHostModule" /> beside the Kafka module instead.
/// </summary>
internal sealed class KafkaHarness : IDisposable
{
    /// <summary>The cluster most tests open.</summary>
    public static readonly KafkaClusterEntry Primary = new("primary", null, "Primary", "production");

    /// <summary>A second cluster, so the selector draws its picker.</summary>
    public static readonly KafkaClusterEntry Secondary = new("secondary", null, "Secondary", null);

    public KafkaHarness(Action<KafkaOptions>? configure = null, params KafkaClusterEntry[] clusters)
        : this(configure, null, clusters)
    {
    }

    /// <summary>
    /// The same, with <paramref name="extra" /> replacing or adding services in the circuit's container (a Find contributor, say). The engine behind the
    /// catalog service is <paramref name="engine" />, or - for a test that is about the shell and not about what a page reads - an empty fake cluster
    /// that lists every entry as a registered cluster.
    /// </summary>
    /// <param name="configure">Configures the module's options.</param>
    /// <param name="extra">Replaces or adds services after the real registration.</param>
    /// <param name="clusters">The cluster entries the catalog lists; the primary one when none.</param>
    /// <param name="engine">The engine the catalog reads through, or none for an empty fake cluster.</param>
    /// <param name="withHostModule">Whether <see cref="FakeHostModule" /> is composed before the Kafka module, as the host's first module.</param>
    public KafkaHarness(
        Action<KafkaOptions>? configure,
        Action<IServiceCollection>? extra,
        KafkaClusterEntry[] clusters,
        EngineHarness? engine = null,
        bool withHostModule = false)
    {
        Options = new KafkaOptions();
        configure?.Invoke(Options);
        KafkaClusterEntry[] entries = clusters.Length == 0 ? [Primary] : clusters;

        ownedEngine = engine is null;
        Engine = engine ?? EngineHarness.ForWire(
            new FakeKafkaWire(new FakeKafkaCluster(TimeProvider.System), EngineHarness.ClusterKey),
            configure: options =>
            {
                // A tenant entry ({cluster}/{tenant}, D43) is not a cluster a host registers: the engine's registry would refuse its key.
                foreach (KafkaClusterEntry entry in entries.Where(static x => x.Key != EngineHarness.ClusterKey && !x.Key.Contains('/', StringComparison.Ordinal)))
                {
                    options.AddCluster(entry.Key, new ClientConfig { BootstrapServers = "fake:9092" }, entry.Profile is { } profile ? c => c.Profile(profile) : null);
                }
            });

        Authorization = new RecordingAuthorizationService();
        Entries = new FakeClusterEntries(entries);
        Navigation = new RecordingNavigationManager();

        Context = new KafkaShellContext(Options, withHostModule, services =>
        {
            // The address the circuit is on, and the browser's two navigation services a StudioRoutes render asks for: the tests that render
            // through the router (as the studio does) say what the browser did with Navigation.Simulate.
            services.AddSingleton<NavigationManager>(Navigation);
            services.AddSingleton<INavigationInterception>(new RecordingNavigationInterception());
            services.AddSingleton<IScrollToLocationHash>(new RecordingScrollToLocationHash());

            services.RemoveAll<IAuthorizationService>();
            services.AddSingleton<IAuthorizationService>(Authorization);

            // The module's own services, singletons here (the context's one root is the one circuit), over the cluster entries a test lists.
            services.AddSingleton<IKafkaClusterEntrySource>(Entries);
            services.RemoveAll<IStudioAuditScopeGate>();
            services.AddSingleton<IStudioAuditScopeGate, KafkaAuditGate>();
            services.AddSingleton<KafkaCapabilityGuard>();
            services.AddSingleton<KafkaAuthorization>();
            services.AddSingleton<KafkaState>();
            services.AddSingleton<KafkaActionLog>();
            services.AddSingleton<KafkaCatalogService>();

            // The engine behind them: the fake broker's.
            services.AddSingleton(Engine.Catalog);
            services.AddSingleton(Engine.Provider.GetRequiredService<KafkaClusterRegistry>());
            services.AddSingleton(Engine.Provider.GetRequiredService<KafkaSettingsReader>());

            // Search and the record page (UI-B): the engine's scanner and its owner budget over the same fake broker, and the services around them.
            services.AddSingleton<TimeProvider>(Engine.Clock);
            services.AddSingleton(Engine.Provider.GetRequiredService<DataStudio.Kafka.Core.Scanning.IKafkaScanner>());
            services.AddSingleton(Engine.Provider.GetRequiredService<DataStudio.Kafka.Core.Scanning.OwnerByteBudget>());
            services.AddSingleton(Engine.Provider.GetRequiredService<DataStudio.Kafka.Core.Scanning.KafkaClusterCostControls>());
            services.AddSingleton(Engine.Provider.GetRequiredService<DataStudio.Kafka.Core.Index.KafkaRecordIndexResolver>());
            services.AddSingleton(Engine.Provider.GetRequiredService<DataStudio.Kafka.Core.Clusters.KafkaEngineSnapshot>());
            services.AddSingleton<KafkaOwnerKey>();
            services.AddSingleton<KafkaReadGrants>();
            services.AddSingleton<KafkaSearchService>();
            services.AddSingleton<KafkaRecordService>();
            services.AddSingleton<KafkaCorrelationService>();
            services.AddSingleton<KafkaExportGate>();
            services.AddSingleton<KafkaExportService>();
            services.AddSingleton<KafkaTailService>();
            services.AddSingleton<KafkaTailCircuitRegistry>();
            extra?.Invoke(services);
        });
    }

    private readonly bool ownedEngine;

    public KafkaOptions Options { get; }

    /// <summary>The engine the catalog service reads through.</summary>
    public EngineHarness Engine { get; }

    public RecordingAuthorizationService Authorization { get; }

    public FakeClusterEntries Entries { get; }

    /// <summary>The circuit's address; <see cref="RecordingNavigationManager.Simulate" /> is a link click, Back or Forward.</summary>
    public RecordingNavigationManager Navigation { get; }

    public KafkaShellContext Context { get; }

    public KafkaState State => Context.Services.GetRequiredService<KafkaState>();

    public KafkaAuthorization KafkaAuthorization => Context.Services.GetRequiredService<KafkaAuthorization>();

    public KafkaActionLog ActionLog => Context.Services.GetRequiredService<KafkaActionLog>();

    public StudioShellDescriptor Descriptor => Context.Services.GetRequiredService<StudioShellDescriptor>();

    public TestAuthenticationStateProvider Visitor => Context.AuthenticationState;

    public void Dispose()
    {
        Context.Dispose();

        if (ownedEngine)
        {
            Engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
