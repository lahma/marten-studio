using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;

using Confluent.Kafka;

using KStudio.Core.Scanning;
using KStudio.Sample.Auth;
using KStudio.Sample.Kafka;
using KStudio.Services;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio;

namespace KStudio.Integration.Tests.Sample;

/// <summary>
/// The KStudio sample, started once as a plain <c>dotnet run</c> would (D-UI decision 18, SAMPLE's acceptance criteria 1 and 3): it starts both
/// throwaway containers, prints the banner, seeds the cluster deterministically and serves <c>/marten/kafka</c> - and the engine, configured by the
/// sample's own registration and asked through its own policies, lets support find the needle and see only <c>orders.*</c>.
/// </summary>
/// <remarks>
/// The browser is UI-VERIFY's. What this class proves is what can be proved without one: startup, the banner, the seed on the broker, the pages' HTTP
/// answers for each user, and the services the pages call, against the broker the host started.
/// </remarks>
public class SampleHostLiveTests(SampleHostFixture fixture) : IClassFixture<SampleHostFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [KafkaFact]
    public void The_host_prints_the_banner_at_warning_naming_both_containers()
    {
        string log = fixture.Host.Log;

        Regex.IsMatch(
                log,
                @"warn: [^\r\n]*\r?\n\s*KStudio sample: started a throwaway Kafka at \S+ and a throwaway Postgres at \S+; both are deleted when this process stops\. Set ConnectionStrings:Kafka and ConnectionStrings:Postgres to use your own\.",
                RegexOptions.None,
                TimeSpan.FromSeconds(5))
            .Should().BeTrue("the banner is a Warning, once, on start. Log: " + log);

        log.Should().NotContain("localhost:9092", "the sample never falls back to a default cluster");
    }

    [KafkaFact]
    public void The_cluster_is_seeded_deterministically_and_the_judge_recognises_it()
    {
        using IAdminClient admin = new AdminClientBuilder(new ClientConfig { BootstrapServers = fixture.Host.KafkaBootstrap }).Build();
        Metadata metadata = admin.GetMetadata(TimeSpan.FromSeconds(30));

        metadata.Topics.Where(static x => !x.Topic.StartsWith("__", StringComparison.Ordinal)).ToDictionary(static x => x.Topic, static x => x.Partitions.Count).Should().BeEquivalentTo(
            new Dictionary<string, int>
            {
                ["orders.v1"] = 6,
                ["payments.v1"] = 3,
                ["customers.v1"] = 1,
                ["orders.v1.dlq"] = 1,
                ["secrets.v1"] = 1,
                ["kstudio-sample.seed-v2"] = 1,
            });

        SampleKafkaJudge.Judge(metadata.Topics.Select(static x => x.Topic)).Verdict.Should().Be(SampleKafkaVerdict.AlreadySeeded);

        // About 1,000 records in each partition of orders.v1 (where murmur2 puts the keys), 6,000 in all, written up to the marker's own offset.
        using IConsumer<Ignore, Ignore> consumer = new ConsumerBuilder<Ignore, Ignore>(
            new ConsumerConfig { BootstrapServers = fixture.Host.KafkaBootstrap, GroupId = "sample-live-test-probe", EnableAutoCommit = false }).Build();

        long total = 0;

        for (int partition = 0; partition < 6; partition++)
        {
            WatermarkOffsets marks = consumer.QueryWatermarkOffsets(new TopicPartition("orders.v1", partition), TimeSpan.FromSeconds(30));
            long records = marks.High.Value - marks.Low.Value;
            records.Should().BeGreaterThanOrEqualTo(900, $"orders.v1 partition {partition}");
            total += records;
        }

        total.Should().Be(6000);
    }

    [KafkaFact]
    public async Task The_pages_answer_for_each_user_and_send_a_visitor_with_no_session_to_the_sign_in()
    {
        using HttpClient anonymous = fixture.NewClient(followRedirects: false);
        using HttpResponseMessage redirected = await anonymous.GetAsync(new Uri("/marten/kafka", UriKind.Relative), Token);
        redirected.StatusCode.Should().Be(HttpStatusCode.Redirect);
        redirected.Headers.Location!.OriginalString.Should().Contain("/login");

        foreach (string user in new[] { "admin", "support" })
        {
            using HttpClient client = await fixture.SignedInAsync(user, Token);

            foreach (string page in new[] { "/marten/kafka", "/marten/kafka/search", "/marten/kafka/settings", "/marten/kafka/topic?cluster=local&topic=orders.v1" })
            {
                using HttpResponseMessage response = await client.GetAsync(new Uri(page, UriKind.Relative), Token);
                response.StatusCode.Should().Be(HttpStatusCode.OK, $"{user} opening {page}");
            }
        }
    }

    [KafkaFact]
    public async Task Support_sees_only_the_orders_topics_and_admin_sees_them_all()
    {
        await using SampleEngine support = SampleEngine.Create(fixture.Host.KafkaBootstrap, "support");
        await using SampleEngine admin = SampleEngine.Create(fixture.Host.KafkaBootstrap, "admin");

        KafkaTopicListing supportTopics = (await support.Catalog.ListTopicsAsync(support.Entry, null, Token))!;
        KafkaTopicListing adminTopics = (await admin.Catalog.ListTopicsAsync(admin.Entry, null, Token))!;

        supportTopics.Rows.Select(static x => x.Name).Should().BeEquivalentTo("orders.v1", "orders.v1.dlq");
        adminTopics.Rows.Select(static x => x.Name).Should().Contain(["orders.v1", "payments.v1", "customers.v1", "secrets.v1", "orders.v1.dlq"]);
    }

    [KafkaFact]
    public async Task Support_finds_the_needle_by_correlation_id_and_is_refused_a_topic_the_policy_hides()
    {
        await using SampleEngine support = SampleEngine.Create(fixture.Host.KafkaBootstrap, "support");

        KafkaSearchInput input = new(
            support.Entry,
            ["orders.v1"],
            DateTimeOffset.UtcNow.AddHours(-6),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "header.correlation-id = 'c-0042'",
            IncludeUncommitted: false);

        KafkaSearchPlanResult.Planned planned = (KafkaSearchPlanResult.Planned) await support.Search.PlanAsync(input, Token);
        planned.Value.Plan.IsValid.Should().BeTrue(string.Join("; ", planned.Value.Plan.Errors.Select(static x => x.Message)));

        // 12,000 records in the window is above the demo's 5,000, so the confirmation is what the page would have shown first.
        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await support.Search.StartAsync(planned.Value, confirmed: true, Token)).Run;

        List<KafkaMatch> matches = [];
        KafkaScanResult? result = null;

        await foreach (KafkaScanBatch batch in run.ReadBatchesAsync(Token))
        {
            matches.AddRange(batch.NewMatches);

            if (batch.Final is { } final)
            {
                result = final;
                run.Complete(final);
            }
        }

        result.Should().NotBeNull();
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        KafkaMatch found = matches.Should().ContainSingle().Subject;
        (found.Topic, found.Key.Text).Should().Be(("orders.v1", "order-0042"));

        // The topic the policy hides is refused whole, never narrowed.
        KafkaSearchPlanResult hidden = await support.Search.PlanAsync(input with { Topics = ["orders.v1", "payments.v1"] }, Token);
        hidden.Should().BeOfType<KafkaSearchPlanResult.Refused>();
    }

    /// <summary>KStudio's real services over the host's broker, configured by the sample's own registration and asked through the sample's own policies.</summary>
    private sealed class SampleEngine : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private SampleEngine(ServiceProvider provider)
        {
            this.provider = provider;
            scope = provider.CreateScope();
        }

        public KafkaClusterEntry Entry { get; } = new(SampleKafkaRegistration.ClusterKey, null, "Sample Kafka", "Development");

        public KafkaSearchService Search => scope.ServiceProvider.GetRequiredService<KafkaSearchService>();

        public KafkaCatalogService Catalog => scope.ServiceProvider.GetRequiredService<KafkaCatalogService>();

        public static SampleEngine Create(string bootstrap, string user)
        {
            ServiceCollection services = new();
            services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddHttpContextAccessor();
            services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none"));
            services.AddPostgresStudio();
            services.AddAuthorization(SamplePolicies.Configure);
            SampleKafkaPolicies.AddHandlers(services);
            services.AddKafkaStudio(options => SampleKafkaRegistration.Configure(options, bootstrap));

            // After the studio's own: the circuit's visitor, signed in as the sample's user.
            ClaimsPrincipal principal = DevUsers.Authenticate(user, user)!;
            services.AddScoped<AuthenticationStateProvider>(_ => new SignedIn(principal));

            return new SampleEngine(services.BuildServiceProvider());
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }

    private sealed class SignedIn(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}

/// <summary>The sample host, started once for the class; it is skipped (not failed) when there is no Docker, as every live fact is.</summary>
public sealed class SampleHostFixture : IAsyncLifetime
{
    private static readonly Regex Token = new(@"name=""__RequestVerificationToken""[^>]*value=""(?<token>[^""]+)""", RegexOptions.None, TimeSpan.FromSeconds(5));

    private KStudioSampleProcess? host;

    internal KStudioSampleProcess Host => host ?? throw new InvalidOperationException("The sample host was not started (no Docker).");

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (DockerAvailability.IsAvailable)
        {
            host = await KStudioSampleProcess.StartAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    public HttpClient NewClient(bool followRedirects)
    {
        HttpClientHandler handler = new() { AllowAutoRedirect = followRedirects, UseCookies = true, CookieContainer = new CookieContainer() };
        return new HttpClient(handler) { BaseAddress = Host.BaseAddress };
    }

    /// <summary>A client holding the session cookie of <paramref name="user" />, signed in through the sample's real form.</summary>
    public async Task<HttpClient> SignedInAsync(string user, CancellationToken cancellationToken)
    {
        HttpClient client = NewClient(followRedirects: false);

        string page = await client.GetStringAsync(new Uri("/login", UriKind.Relative), cancellationToken);
        string token = WebUtility.HtmlDecode(Token.Match(page).Groups["token"].Value);

        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["username"] = user,
            ["password"] = user,
            ["returnUrl"] = "/",
        });

        using HttpResponseMessage response = await client.PostAsync(new Uri("/login", UriKind.Relative), form, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "a good sign-in redirects to the landing page");
        response.Headers.Location!.OriginalString.Should().NotContain("error");
        return client;
    }
}

/// <summary>The sample's refusal when Docker is absent, without a Docker: the container start is injected and fails.</summary>
public class SampleRefusalTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_nothing_configured_and_no_docker_the_start_throws_the_sentence_and_returns_no_cluster_to_build_a_client_for()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Kafka"] = "  " }).Build();

        Func<Task> start = () => SampleKafka.StartAsync(
            configuration,
            static (_, _) => throw new HttpRequestException("Docker is not running"),
            Token);

        InvalidOperationException refusal = (await start.Should().ThrowAsync<InvalidOperationException>()).Which;

        refusal.Message.Should().Be(SampleKafka.NoDockerMessage);
        refusal.Message.Should().NotContain("9092", "the sample never falls back to a default cluster; no SampleKafka exists, so nothing can build a client for one");
        refusal.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task A_configured_cluster_is_taken_as_it_is_and_no_container_is_started()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Kafka"] = "broker.example:9093" }).Build();

        await using SampleKafka kafka = await SampleKafka.StartAsync(
            configuration,
            static (_, _) => throw new InvalidOperationException("a configured cluster starts no container"),
            Token);

        kafka.BootstrapServers.Should().Be("broker.example:9093");
        kafka.IsThrowaway.Should().BeFalse();
    }
}
