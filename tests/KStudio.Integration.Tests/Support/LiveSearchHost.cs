using System.Security.Claims;

using KStudio.Services;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio;
using PostgresStudio.Services;

namespace KStudio.Integration.Tests.Support;

/// <summary>
/// KStudio's real services over a real broker, composed by the real registrations (<c>AddPostgresStudio()</c> and <c>AddKafkaStudio()</c>) and used the way
/// a circuit uses them - from one scope - for the tests of the search service (UI-B). The authorization is KStudio's own <c>KafkaAuthorization</c> over the
/// application's real <c>IAuthorizationService</c> and a real policy that is asked about the <c>KafkaTopicResource</c>; the ring is the studio's own. Nothing
/// here connects to Postgres: the data source is only registered, and no hosted service is started.
/// </summary>
internal sealed class LiveSearchHost : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;
    private readonly HashSet<string> refused = new(StringComparer.Ordinal);

    private LiveSearchHost(ServiceProvider provider, CapturingLoggerProvider logs, HashSet<string> refused)
    {
        this.provider = provider;
        this.refused = refused;
        Logs = logs;
        scope = provider.CreateScope();
    }

    /// <summary>Everything KStudio and the studio logged.</summary>
    public CapturingLoggerProvider Logs { get; }

    /// <summary>The cluster entry the host registered.</summary>
    public KafkaClusterEntry Entry { get; } = new("live", null, "Live", null);

    public KafkaSearchService Search => scope.ServiceProvider.GetRequiredService<KafkaSearchService>();

    /// <summary>The studio's audit ring.</summary>
    public IReadOnlyList<StudioActionLogEntry> Ring => provider.GetRequiredService<StudioActionLogService>().GetLatest();

    /// <summary>Topics the cluster policy refuses from now on.</summary>
    /// <param name="topics">The topic names.</param>
    public void Refuse(params string[] topics)
    {
        foreach (string topic in topics)
        {
            refused.Add(topic);
        }
    }

    /// <summary>Builds the host for a principal of the fixture's broker.</summary>
    /// <param name="fixture">The broker.</param>
    /// <param name="user">The Kafka principal the engine reads as.</param>
    /// <param name="configure">Sets KStudio's options (topics, limits).</param>
    public static LiveSearchHost Create(KafkaFixture fixture, string user, Action<KafkaStudioOptions>? configure = null)
    {
        Assert.SkipUnless(DockerAvailability.IsAvailable, DockerAvailability.SkipReason);
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        CapturingLoggerProvider logs = new();
        HashSet<string> refused = new(StringComparer.Ordinal);

        ServiceCollection services = new();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddHttpContextAccessor();
        services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none"));
        services.AddPostgresStudio();
        services.AddAuthorization(options => options.AddPolicy(
            "kafka-topics",
            policy => policy.RequireAssertion(context => context.Resource is KafkaTopicResource resource && (resource.Topic is null || !refused.Contains(resource.Topic)))));
        services.AddKafkaStudio(options =>
        {
            options.AddCluster("live", fixture.Client(user), static cluster => cluster.DisplayName("Live"));
            options.Capabilities.SearchRecords = true;
            options.ClusterAuthorizationPolicy = "kafka-topics";
            configure?.Invoke(options);
        });

        // After the studio's own: the circuit's visitor, signed in.
        services.AddScoped<AuthenticationStateProvider, SignedIn>();

        return new LiveSearchHost(services.BuildServiceProvider(), logs, refused);
    }

    public async ValueTask DisposeAsync()
    {
        scope.Dispose();
        await provider.DisposeAsync();
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "live-visitor")], "test"))));
    }
}
