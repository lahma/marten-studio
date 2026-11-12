using System.Security.Claims;

using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Services;
using KStudio.Tests.Contract;
using KStudio.Tests.Support.FakeKafka;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using PostgresStudio;
using PostgresStudio.Services;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Tests.Components;
using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Support;

/// <summary>
/// An <see cref="IAuthorizationService" /> that answers for both resources a Postgres Studio host with KStudio is asked about -
/// <see cref="PostgresStudioResource" /> (the host's) and <see cref="KafkaTopicResource" /> (KStudio's) - and records every question, so a test can
/// say which resource reached which policy.
/// </summary>
public sealed class RecordingAuthorizationService : IAuthorizationService
{
    private Func<KafkaTopicResource, bool> kafka = static _ => true;
    private Func<PostgresStudioResource, bool> host = static _ => true;
    private TaskCompletionSource<bool>? hostGate;

    /// <summary>Every question asked, in order: the policy's name and the resource.</summary>
    public List<(string Policy, object? Resource)> Calls { get; } = [];

    /// <summary>The questions about KStudio's resource.</summary>
    public IEnumerable<(string Policy, KafkaTopicResource Resource)> KafkaCalls =>
        Calls.Where(static x => x.Resource is KafkaTopicResource).Select(static x => (x.Policy, (KafkaTopicResource) x.Resource!));

    /// <summary>The questions about the host studio's resource.</summary>
    public IEnumerable<(string Policy, PostgresStudioResource Resource)> HostCalls =>
        Calls.Where(static x => x.Resource is PostgresStudioResource).Select(static x => (x.Policy, (PostgresStudioResource) x.Resource!));

    /// <summary>What KStudio's policies allow; everything by default.</summary>
    public void AllowKafka(Func<KafkaTopicResource, bool> predicate) => kafka = predicate;

    /// <summary>What the host studio's policies allow; everything by default.</summary>
    public void AllowHost(Func<PostgresStudioResource, bool> predicate) => host = predicate;

    /// <inheritdoc />
    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
        Task.FromResult(AuthorizationResult.Failed());

    /// <summary>
    /// Holds every question about the host studio's resource until the returned source is completed - with whether the host's handler then
    /// allows it - so a test can look at the shell while the host's scope gate is pending.
    /// </summary>
    public TaskCompletionSource<bool> BlockHost()
    {
        TaskCompletionSource<bool> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref hostGate, gate);
        return gate;
    }

    /// <inheritdoc />
    public async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
    {
        lock (Calls)
        {
            Calls.Add((policyName, resource));
        }

        bool allowed = resource switch
        {
            KafkaTopicResource topic => kafka(topic),
            PostgresStudioResource source => host(source) && await HostOpenAsync().ConfigureAwait(false),
            _ => false,
        };

        return allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed();
    }

    private async Task<bool> HostOpenAsync() => Volatile.Read(ref hostGate) is { } gate ? await gate.Task.Bounded() : true;
}

/// <summary>The cluster entries a test lists, in place of the engine's catalog.</summary>
internal sealed class FakeClusterEntries(IReadOnlyList<KafkaClusterEntry> entries) : IKafkaClusterEntrySource
{
    /// <inheritdoc />
    public IReadOnlyList<KafkaClusterEntry> Entries { get; set; } = entries;
}

/// <summary>
/// A Postgres Studio bUnit context with KStudio's module composed into its descriptor and KStudio's services beside it, as
/// <c>AddPostgresStudio()</c> and <c>AddKafkaStudio()</c> leave a circuit: the shell, the rail, the header and the audit are the real ones, over the
/// fakes <see cref="PostgresStudioComponentContext" /> brings, and the engine is not involved at all.
/// </summary>
internal sealed class KafkaStudioHarness : IDisposable
{
    /// <summary>The cluster most tests open.</summary>
    public static readonly KafkaClusterEntry Primary = new("primary", null, "Primary", "production");

    /// <summary>A second cluster, so the selector draws its picker.</summary>
    public static readonly KafkaClusterEntry Secondary = new("secondary", null, "Secondary", null);

    public KafkaStudioHarness(Action<KafkaStudioOptions>? configure = null, params KafkaClusterEntry[] clusters)
        : this(configure, null, clusters)
    {
    }

    /// <summary>
    /// The same, with <paramref name="extra" /> adding services to the circuit's container (a Find contributor, say). The engine behind the catalog
    /// service is <paramref name="engine" />, or - for a test that is about the shell and not about what a page reads - an empty fake cluster
    /// that lists every entry as a registered cluster.
    /// </summary>
    public KafkaStudioHarness(Action<KafkaStudioOptions>? configure, Action<IServiceCollection>? extra, KafkaClusterEntry[] clusters, EngineHarness? engine = null)
    {
        Options = new KafkaStudioOptions();
        configure?.Invoke(Options);
        KafkaClusterEntry[] entries = clusters.Length == 0 ? [Primary] : clusters;

        ownedEngine = engine is null;
        Engine = engine ?? EngineHarness.ForWire(
            new FakeKafkaWire(new FakeKafkaCluster(TimeProvider.System), EngineHarness.ClusterKey),
            configure: options =>
            {
                foreach (KafkaClusterEntry entry in entries.Where(static x => x.Key != EngineHarness.ClusterKey))
                {
                    options.AddCluster(entry.Key, new ClientConfig { BootstrapServers = "fake:9092" });
                }
            });

        Authorization = new RecordingAuthorizationService();
        Entries = new FakeClusterEntries(entries);
        Navigation = new RecordingNavigationManager();

        Context = new PostgresStudioComponentContext(services =>
        {
            // The address the circuit is on, and the browser's two navigation services a StudioRoutes render asks for: the tests that render
            // through the router (as the studio does) say what the browser did with Navigation.Simulate.
            services.AddSingleton<NavigationManager>(Navigation);
            services.AddSingleton<INavigationInterception>(new RecordingNavigationInterception());
            services.AddSingleton<IScrollToLocationHash>(new RecordingScrollToLocationHash());

            services.RemoveAll<IAuthorizationService>();
            services.AddSingleton<IAuthorizationService>(Authorization);

            services.AddSingleton<IOptions<KafkaStudioOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
            services.AddSingleton<IStudioModule, KafkaStudioModule>();
            services.RemoveAll<StudioDescriptor>();
            services.AddSingleton(static provider =>
                StudioModuleComposition.Compose(PostgresStudioDescriptor.Instance, provider.GetServices<IStudioModule>()));

            services.AddSingleton<IKafkaClusterEntrySource>(Entries);
            services.AddSingleton<KafkaCapabilityGuard>();
            services.AddSingleton<KafkaAuthorization>();
            services.AddSingleton<KafkaState>();
            services.AddSingleton<KafkaActionLog>();
            services.AddSingleton<IStudioAuditScopeGate, KafkaAuditGate>();

            services.AddSingleton(Engine.Catalog);
            services.AddSingleton(Engine.Provider.GetRequiredService<KafkaClusterRegistry>());
            services.AddSingleton(Engine.Provider.GetRequiredService<KafkaSettingsReader>());
            services.AddSingleton<KafkaCatalogService>();

            // Search and the record page (UI-B): the engine's scanner and its owner budget over the same fake broker, and the services around them.
            services.TryAddSingleton(Engine.Clock);
            services.AddSingleton(Engine.Provider.GetRequiredService<KStudio.Core.Scanning.IKafkaScanner>());
            services.AddSingleton(Engine.Provider.GetRequiredService<KStudio.Core.Scanning.OwnerByteBudget>());
            services.AddSingleton<KafkaOwnerKey>();
            services.AddSingleton<KafkaSearchService>();
            services.AddSingleton<KafkaRecordService>();
            extra?.Invoke(services);
        });
    }

    private readonly bool ownedEngine;

    public KafkaStudioOptions Options { get; }

    /// <summary>The engine the catalog service reads through.</summary>
    public EngineHarness Engine { get; }

    public RecordingAuthorizationService Authorization { get; }

    public FakeClusterEntries Entries { get; }

    /// <summary>The circuit's address; <see cref="RecordingNavigationManager.Simulate" /> is a link click, Back or Forward.</summary>
    public RecordingNavigationManager Navigation { get; }

    public PostgresStudioComponentContext Context { get; }

    public KafkaState State => Context.Services.GetRequiredService<KafkaState>();

    public KafkaAuthorization KafkaAuthorization => Context.Services.GetRequiredService<KafkaAuthorization>();

    public KafkaActionLog ActionLog => Context.Services.GetRequiredService<KafkaActionLog>();

    public StudioDescriptor Descriptor => Context.Services.GetRequiredService<StudioDescriptor>();

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
