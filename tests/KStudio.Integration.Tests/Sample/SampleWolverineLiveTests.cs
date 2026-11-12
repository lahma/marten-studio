using System.Security.Claims;
using System.Text;

using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Correlation;
using KStudio.Core.Scanning;
using KStudio.Internal;
using KStudio.Sample.Auth;
using KStudio.Sample.Kafka;
using KStudio.Sample.Wolverine;
using KStudio.Services;

using Marten;

using MartenStudio;
using MartenStudio.SampleDomain;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.Settings;

using Wolverine;

namespace KStudio.Integration.Tests.Sample;

/// <summary>
/// D-CORR decision 11, SAMPLE-M4: the KStudio sample with its Wolverine application, started once as a plain <c>dotnet run</c> would. The host's
/// Wolverine listener handles 25 checkout commands the startup sender publishes, stores a <c>CheckoutOrder</c> in the sample's Marten store in each
/// command's tenant and confirms it on another topic; KStudio discovers the application's cluster and topics from its Wolverine configuration, and the
/// README's six-step walk-through is followed here through KStudio's and Marten Studio's real services (the browser is UI-VERIFY's).
/// </summary>
/// <remarks>
/// The services are the sample's own registrations (<see cref="SampleKafkaRegistration" />, <see cref="SampleWolverine" />, <see cref="SampleStore" />) in a
/// container of the test's, pointed at the host's broker and database; the test's container starts nothing: the host's Wolverine is the one running.
/// </remarks>
public class SampleWolverineLiveTests(SampleHostFixture fixture) : IClassFixture<SampleHostFixture>
{
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(2);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The walk-through's outcomes, as xunit diagnostics and, when <c>KSTUDIO_WALK_LOG</c> names a file, appended to it (the packet's report pastes them).
    /// </summary>
    private static void Say(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);

        if (Environment.GetEnvironmentVariable("KSTUDIO_WALK_LOG") is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    [KafkaFact]
    public async Task The_wolverine_cluster_is_discovered_beside_the_host_cluster_with_its_roles_group_and_dead_letter_topic()
    {
        await using WolverineEngine engine = WolverineEngine.Create(fixture.Host, "admin");

        KafkaClusterRegistry registry = engine.Services.GetRequiredService<KafkaClusterRegistry>();
        registry.Clusters.Select(static x => x.Key).Should().Equal("local", "wolverine");
        registry.Notes.Should().BeEmpty();

        KafkaClusterDefinition own = registry.Get("wolverine");
        (own.DisplayName, own.Source).Should().Be(("Sample Kafka (Wolverine)", "Wolverine"));

        KafkaDiscoveredTopic listener = own.Descriptor(SampleWolverine.CheckoutTopic).Discovered!;
        (listener.Role, listener.ConsumerGroup).Should().Be((KafkaTopicRole.Listener, SampleWolverine.ServiceName));
        own.Descriptor(SampleWolverine.ConfirmedTopic).Discovered!.Role.Should().Be(KafkaTopicRole.Sender);

        // The broker's topics, as the page lists them: the three of the application beside the seed's.
        KafkaTopicListing topics = (await engine.Catalog.ListTopicsAsync(engine.Entry("wolverine"), null, Token))!;
        topics.Rows.Select(static x => x.Name).Should().Contain([SampleWolverine.CheckoutTopic, SampleWolverine.ConfirmedTopic, SampleWolverine.DeadLetterTopic, "orders.v1"]);
    }

    [KafkaFact]
    public async Task The_25_commands_produce_25_documents_in_their_tenants_and_a_confirmation_for_every_delivery_with_the_headers_wolverine_writes()
    {
        await using WolverineEngine engine = WolverineEngine.Create(fixture.Host, "admin");
        await engine.SettledAsync();

        // 25 commands + the one delivered twice; 25 documents, because the second delivery stores the same document again.
        engine.Count(SampleWolverine.CheckoutTopic).Should().Be(SampleWolverine.Commands + 1);
        engine.Count(SampleWolverine.ConfirmedTopic).Should().Be(SampleWolverine.Commands + 1);

        IDocumentStore store = engine.Services.GetRequiredService<IDocumentStore>();
        int acme = await CountDocumentsAsync(store, "acme");
        int globex = await CountDocumentsAsync(store, "globex");
        (acme, globex).Should().Be((13, 12), "odd commands are acme's, even ones globex's");

        await using (IQuerySession inAcme = store.QuerySession("acme"))
        await using (IQuerySession inGlobex = store.QuerySession("globex"))
        {
            (await inAcme.LoadAsync<CheckoutOrder>("chk-0007", Token)).Should().NotBeNull("chk-0007 is acme's");
            (await inGlobex.LoadAsync<CheckoutOrder>("chk-0007", Token)).Should().BeNull("a conjoined document is invisible to the other tenant");
        }

        // The records are Wolverine's: the key is the order id, and the envelope's headers are on the record.
        List<Message<byte[], byte[]>> commands = engine.ReadAll(SampleWolverine.CheckoutTopic);
        commands.Select(static x => Encoding.UTF8.GetString(x.Key)).Distinct().Should().HaveCount(SampleWolverine.Commands);

        string[] names = [.. commands[0].Headers.Select(static x => x.Key)];
        names.Should().Contain(["id", "correlation-id", "conversation-id", "message-type", "tenant-id", "parent-id"], "parent-id is the W3C trace context, written because the sample listens to Wolverine's activities");
        Say("Headers of the first command: " + string.Join(", ", names));
        Say("Headers of the first confirmation: " + string.Join(", ", engine.ReadAll(SampleWolverine.ConfirmedTopic)[0].Headers.Select(static x => x.Key)));
    }

    [KafkaFact]
    public async Task Support_sees_the_checkout_topics_of_the_wolverine_cluster_and_not_its_dead_letter_topic()
    {
        await using WolverineEngine support = WolverineEngine.Create(fixture.Host, "support");

        KafkaTopicListing topics = (await support.Catalog.ListTopicsAsync(support.Entry("wolverine"), null, Token))!;

        // The same broker as the host's cluster, so support also sees the seed's orders.* topics; what it does not see is anything else.
        topics.Rows.Select(static x => x.Name).Should().BeEquivalentTo(SampleWolverine.CheckoutTopic, SampleWolverine.ConfirmedTopic, "orders.v1", "orders.v1.dlq");
    }

    [KafkaFact]
    public async Task The_walk_through_of_the_readme_holds_step_by_step_through_the_services()
    {
        await using WolverineEngine engine = WolverineEngine.Create(fixture.Host, "admin");
        await engine.SettledAsync();
        KafkaClusterEntry entry = engine.Entry("wolverine");

        // 1. Sign in as admin. Kafka > Topics, cluster "Sample Kafka (Wolverine)": checkout.v1 (listener, group KStudio.Sample), checkout.confirmed.v1
        //    (sender), wolverine-dead-letter-queue (dead letters).
        KafkaClusterDefinition cluster = engine.Services.GetRequiredService<KafkaClusterRegistry>().Get("wolverine");
        entry.DisplayName.Should().Be("Sample Kafka (Wolverine)");
        KafkaTopicListing topics = (await engine.Catalog.ListTopicsAsync(entry, null, Token))!;
        string[] roles =
        [
            .. new[] { SampleWolverine.CheckoutTopic, SampleWolverine.ConfirmedTopic }.Select(topic =>
                $"{topic} ({cluster.Descriptor(topic)?.Discovered?.Role.ToString() ?? "not discovered"}{(cluster.Descriptor(topic)?.Discovered?.ConsumerGroup is { } group ? ", group " + group : string.Empty)})"),
        ];
        topics.Rows.Select(static x => x.Name).Should().Contain(SampleWolverine.CheckoutTopic).And.Contain(SampleWolverine.ConfirmedTopic).And.Contain(SampleWolverine.DeadLetterTopic);
        Say("1. Topics of 'Sample Kafka (Wolverine)': " + string.Join("; ", roles) + "; wolverine-dead-letter-queue (on the broker, in the listing)");

        // Wolverine adds its dead-letter topic to the transport as it starts, which this container never does; the topic is on the broker (it is in
        // the listing above), and the role it is discovered with is WolverineDiscoveryLiveTests' on a started host and UI-VERIFY-M4's in the browser.

        // 2. Search checkout.v1 over the last hour, with no predicate, and open any record.
        (IReadOnlyList<KafkaMatch> found, KafkaScanResult result) = await engine.SearchAsync(entry, SampleWolverine.CheckoutTopic);
        result.StopReason.Should().Be(KafkaScanStopReason.Completed);
        found.Should().HaveCount(SampleWolverine.Commands + 1);
        KafkaMatch command = found.Where(static x => x.Key.Text == "chk-0007").OrderBy(static x => x.Offset).First();
        Say($"2. Search checkout.v1, last hour, no predicate: {found.Count} records ({result.StopReason}); opened {command.Topic}/{command.Partition}/{command.Offset}, key {command.Key.Text}");

        // The Traces link of the record page (a placeholder per value the visitor sees): the trace id of the command's parent-id header, escaped.
        IReadOnlyList<KafkaExternalLink> links = KafkaMatchView.ExternalLinks(engine.Services.GetRequiredService<IOptions<KafkaStudioOptions>>().Value.ExternalLinks, command);
        KafkaExternalLink traces = links.Should().ContainSingle(static x => x.Label == "Traces").Subject;
        traces.Href.Should().StartWith("https://tracing.example.invalid/trace/").And.MatchRegex("/trace/[0-9a-f]{32}$");
        Say($"   record page, external link: {traces.Label} -> {traces.Href}");

        // 3. Related records: the timeline shows the command on checkout.v1 and its CheckoutConfirmed on checkout.confirmed.v1 on one screen.
        KafkaRelatedAnchorResult.Opened anchor = (KafkaRelatedAnchorResult.Opened) await engine.Correlation.OpenAnchorAsync(entry, command.Topic, command.Partition, command.Offset, Token);
        Say("3. Anchor keys: " + string.Join(", ", anchor.Keys.Values.Select(static value => $"{value.Kind}={value.Value}")));

        KafkaRelatedInput input = new(
            entry,
            KafkaRelatedSubject.Anchor(command.Topic, command.Partition, command.Offset),
            anchor.Keys,
            [SampleWolverine.CheckoutTopic, SampleWolverine.ConfirmedTopic],
            command.Timestamp - KafkaCorrelationService.DefaultHalfWindow,
            command.Timestamp + KafkaCorrelationService.DefaultHalfWindow);

        KafkaRelatedPlanned planned = ((KafkaRelatedPlanResult.Planned) await engine.Correlation.PlanAsync(input, Token)).Value;
        await using KafkaSearchRun run = ((KafkaRelatedStartResult.Running) await engine.Correlation.StartAsync(planned, confirmed: true, Token)).Run;
        (List<KafkaMatch> related, KafkaScanResult relatedResult) = await ReadAsync(run);

        Say($"   related: {related.Count} records, {relatedResult.StopReason}");
        relatedResult.StopReason.Should().Be(KafkaScanStopReason.Completed);
        related.Select(static x => x.Topic).Distinct().Should().BeEquivalentTo([SampleWolverine.CheckoutTopic, SampleWolverine.ConfirmedTopic], "the command and its confirmation, on one screen");
        related.Should().Contain(x => x.Topic == SampleWolverine.ConfirmedTopic && x.Key.Text == "chk-0007");

        foreach (KafkaMatch match in KafkaRelatedView.InOrder(related))
        {
            IReadOnlyList<string> by = KafkaRelatedView.MatchedBy(match, engine.Correlation.FieldsOf(entry, match.Topic), anchor.Keys);
            Say($"   {match.Timestamp:HH:mm:ss.fff} {match.Topic}/{match.Partition}/{match.Offset} key {match.Key.Text}: matched by {string.Join(" + ", by)}");
        }

        IReadOnlyList<string> confirmedBy = KafkaRelatedView.MatchedBy(
            related.First(static x => x.Topic == SampleWolverine.ConfirmedTopic),
            engine.Correlation.FieldsOf(entry, SampleWolverine.ConfirmedTopic),
            anchor.Keys);
        confirmedBy.Should().Contain(static x => x.StartsWith("correlation", StringComparison.Ordinal), "the confirmation shares the command's correlation id");
        confirmedBy.Should().Contain(static x => x.StartsWith("message", StringComparison.Ordinal), "the confirmation's conversation id is the command's message id");

        // 4. In the record detail, "Document CheckoutOrder chk-0007" opens Marten Studio's document, in tenant acme.
        IStudioRouteLinks routes = engine.Services.GetRequiredService<IStudioRouteLinks>();
        string tenant = Header(command, "tenant-id");
        tenant.Should().Be("acme");
        string? documentLink = routes.ToDocument(new StudioLinkScope(null, tenant), SampleWolverine.DocumentType, "chk-0007");
        documentLink.Should().NotBeNull().And.Contain("tenant=acme").And.Contain("chk-0007");
        Say("4. Document link: " + documentLink);

        // 5. On the document page, "Find in Kafka" returns to the timeline over both checkout topics.
        IStudioRecordSearchLinks search = engine.Services.GetRequiredService<IStudioRecordSearchLinks>();
        string? findInKafka = search.ToSearch(new StudioRecordSearchRequest(
            null,
            "acme",
            new StudioRecordReference(StudioRecordKind.Document, [SampleWolverine.DocumentType, typeof(CheckoutOrder).FullName!], "chk-0007"),
            null,
            null));
        findInKafka.Should().NotBeNull().And.StartWith("kafka/related#", "the timeline, with everything the page needs in the fragment");
        findInKafka.Should().Contain("type=CheckoutOrder").And.Contain("v=chk-0007");
        findInKafka.Split('#')[0].Should().NotContain("chk-0007", "the value travels in the fragment, which a browser never sends to a server");
        Say("5. Find in Kafka: " + findInKafka);

        // 6. Possible duplicates: 1 group under the search results of checkout.v1.
        IReadOnlyList<KafkaDuplicateGroup> groups = KafkaDuplicateFinder.Find(found);
        KafkaDuplicateGroup group = groups.Should().ContainSingle().Subject;
        (group.Topic, group.Count, group.Copies[0].Key.Text).Should().Be((SampleWolverine.CheckoutTopic, 2, "chk-0007"));
        Say($"6. Possible duplicates: {groups.Count} group ({group.Basis}, {group.Count} copies of {group.Copies[0].Key.Text} on {group.Topic})");
    }

    private static string Header(KafkaMatch match, string name) =>
        match.Headers.Single(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).Value.Text
        ?? throw new InvalidOperationException($"The header {name} is not shown.");

    private static async Task<(List<KafkaMatch> Matches, KafkaScanResult Result)> ReadAsync(KafkaSearchRun run)
    {
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

        result.Should().NotBeNull("the last batch carries the result");
        return (matches, result);
    }

    private static async Task<int> CountDocumentsAsync(IDocumentStore store, string tenant)
    {
        await using IQuerySession session = store.QuerySession(tenant);
        return await session.Query<CheckoutOrder>().CountAsync(Token);
    }

    /// <summary>
    /// The sample's registrations in a container of the test's: Marten (the sample's store), Marten Studio, Wolverine (configured, never started) and
    /// KStudio, over the host's broker and database, with the sample's own policies and a signed-in user.
    /// </summary>
    private sealed class WolverineEngine : IAsyncDisposable
    {
        private readonly KStudioSampleProcess host;
        private readonly string bootstrap;
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private WolverineEngine(ServiceProvider provider, KStudioSampleProcess host, string bootstrap)
        {
            this.host = host;
            this.provider = provider;
            this.bootstrap = bootstrap;
            scope = provider.CreateScope();
        }

        public IServiceProvider Services => scope.ServiceProvider;

        public KafkaCatalogService Catalog => scope.ServiceProvider.GetRequiredService<KafkaCatalogService>();

        public KafkaCorrelationService Correlation => scope.ServiceProvider.GetRequiredService<KafkaCorrelationService>();

        public static WolverineEngine Create(KStudioSampleProcess host, string user)
        {
            string bootstrap = host.KafkaBootstrap;
            string postgres = host.PostgresConnectionString;

            ServiceCollection services = new();
            services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddHttpContextAccessor();
            services.AddAuthorization(SamplePolicies.Configure);
            SampleKafkaPolicies.AddHandlers(services);

            services.AddMarten(options =>
            {
                SampleStore.Configure(options, postgres);
                options.Schema.For<CheckoutOrder>().MultiTenanted();
            }).UseLightweightSessions();

            services.AddMartenStudio(options =>
            {
                options.AuthorizationPolicy = SamplePolicies.Studio;
                options.WriteAuthorizationPolicy = SamplePolicies.StudioWrite;

                foreach (string tenantId in SampleStore.TenantIds)
                {
                    options.KnownTenantIds.Add(tenantId);
                }
            });

            // Configured and never started: KStudio reads the options, and the host's own Wolverine is the one that runs.
            services.AddWolverine(options => SampleWolverine.Configure(options, bootstrap));
            services.AddKafkaStudio(options =>
            {
                SampleKafkaRegistration.Configure(options, bootstrap);
                SampleKafkaRegistration.ConfigureWolverine(options);
            });

            // After the studio's own: the circuit's visitor, signed in as the sample's user.
            ClaimsPrincipal principal = DevUsers.Authenticate(user, user)!;
            services.AddScoped<AuthenticationStateProvider>(_ => new SignedIn(principal));

            return new WolverineEngine(services.BuildServiceProvider(), host, bootstrap);
        }

        public KafkaClusterEntry Entry(string key) =>
            provider.GetRequiredService<IKafkaClusterEntrySource>().Entries.Single(x => string.Equals(x.Key, key, StringComparison.Ordinal));

        /// <summary>The records on a topic: high minus low watermark, summed over its partitions.</summary>
        public long Count(string topic)
        {
            using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrap }).Build();
            using IConsumer<Ignore, Ignore> consumer = new ConsumerBuilder<Ignore, Ignore>(
                new ConsumerConfig { BootstrapServers = bootstrap, GroupId = "sample-wolverine-live-test-probe", EnableAutoCommit = false }).Build();

            TopicMetadata? found = admin.GetMetadata(topic, TimeSpan.FromSeconds(30)).Topics.FirstOrDefault(x => x.Topic == topic && !x.Error.IsError);

            return found is null
                ? 0
                : found.Partitions.Sum(partition =>
                {
                    WatermarkOffsets marks = consumer.QueryWatermarkOffsets(new TopicPartition(topic, partition.PartitionId), TimeSpan.FromSeconds(30));
                    return marks.High.Value - marks.Low.Value;
                });
        }

        /// <summary>Waits until the host's sender and listener are done: both topics hold 26 records and the store holds 25 documents.</summary>
        public async Task SettledAsync()
        {
            IDocumentStore store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            DateTime deadline = DateTime.UtcNow + Patience;

            while (true)
            {
                long commands = Count(SampleWolverine.CheckoutTopic);
                long confirmations = Count(SampleWolverine.ConfirmedTopic);
                int documents = await ExistingDocumentsAsync(store);

                if (commands == SampleWolverine.Commands + 1 && confirmations == SampleWolverine.Commands + 1 && documents == SampleWolverine.Commands)
                {
                    return;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"The sample did not settle: {commands} commands, {confirmations} confirmations, {documents} documents. Host log:{Environment.NewLine}{Tail(host.Log)}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), Token);
            }
        }

        private static async Task<int> ExistingDocumentsAsync(IDocumentStore store)
        {
            try
            {
                return await CountDocumentsAsync(store, "acme") + await CountDocumentsAsync(store, "globex");
            }
            catch (Npgsql.PostgresException)
            {
                // The table does not exist until the first command is handled.
                return 0;
            }
        }

        private static string Tail(string log) => log.Length <= 4_000 ? log : log[^4_000..];

        /// <summary>Every record of a topic, from the beginning.</summary>
        public List<Message<byte[], byte[]>> ReadAll(string topic)
        {
            long expected = Count(topic);
            List<Message<byte[], byte[]>> messages = [];

            using IAdminClient admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = bootstrap }).Build();
            using IConsumer<byte[], byte[]> consumer = new ConsumerBuilder<byte[], byte[]>(
                new ConsumerConfig { BootstrapServers = bootstrap, GroupId = "sample-wolverine-live-test-probe", EnableAutoCommit = false }).Build();

            consumer.Assign(admin.GetMetadata(topic, TimeSpan.FromSeconds(30)).Topics.Single(x => x.Topic == topic).Partitions
                .Select(partition => new TopicPartitionOffset(topic, partition.PartitionId, Offset.Beginning)));

            DateTime deadline = DateTime.UtcNow + Patience;

            while (messages.Count < expected && DateTime.UtcNow < deadline)
            {
                if (consumer.Consume(TimeSpan.FromSeconds(1)) is { IsPartitionEOF: false } result)
                {
                    messages.Add(result.Message);
                }
            }

            return messages;
        }

        /// <summary>A search of one topic over the last hour with no predicate, run to the end.</summary>
        public async Task<(IReadOnlyList<KafkaMatch> Matches, KafkaScanResult Result)> SearchAsync(KafkaClusterEntry entry, string topic)
        {
            KafkaSearchService search = scope.ServiceProvider.GetRequiredService<KafkaSearchService>();
            KafkaSearchInput input = new(entry, [topic], DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMinutes(5), null, IncludeUncommitted: false);

            KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(input, Token)).Value;
            await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(planned, confirmed: true, Token)).Run;

            (List<KafkaMatch> matches, KafkaScanResult result) = await ReadAsync(run);
            return (matches, result);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }

        private sealed class SignedIn(ClaimsPrincipal principal) : AuthenticationStateProvider
        {
            public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
        }
    }
}
