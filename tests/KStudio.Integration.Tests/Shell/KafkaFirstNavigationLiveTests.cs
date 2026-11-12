using System.Diagnostics;
using System.Net;

using Confluent.Kafka;
using Confluent.Kafka.Admin;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using KStudio.Core.Clusters;
using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;
using KStudio.Integration.Tests.Support;

using Marten;

using MartenStudio;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio;

namespace KStudio.Integration.Tests.Shell;

/// <summary>
/// Hard rule 17 and Postgres Studio's read-never-writes rule, for the shell (D34): opening every Kafka page of a Postgres Studio host that also
/// called <c>AddKafkaStudio()</c> - over a real, super-user, auto-creating broker and a real Postgres - creates no topic, commits no offset, leaves no
/// group, writes no row, and makes no broker call while it prerenders.
/// </summary>
/// <remarks>
/// <para>
/// <b>What each proof is against.</b> (a) The broker auto-creates topics (<c>KAFKA_AUTO_CREATE_TOPICS_ENABLE=true</c>) for a principal allowed to create
/// them, so a request that named a topic would have created it; the topic list is compared before and after, with a page opened on
/// <c>?topic=does-not-exist</c>. (b) Every consumer group, with its committed offsets, is compared before and after. (c) Postgres: every object and the
/// tuple counters of every user table, before and after. (d) The engine's wire factory is wrapped by one that counts: a wire is created by the first
/// broker call of a cluster, so a count of zero is a prerender that touched no broker - the second cluster, <c>dark</c>, points at a port nothing listens
/// on, so a call to it would also have been slow and logged.
/// </para>
/// <para>
/// <b>A prerendering request reads nothing from a broker</b> (the page frame releases reads only to a live circuit), so the interactive half of the
/// walk is the component suite's, against the fake broker; what this class holds is the part only a real broker and a real database can: that nothing
/// the shell and the catalog pages do on a first navigation writes, and that no GET calls a broker. UI-A added Topics, the topic overview and Cluster
/// settings to <see cref="Pages" />; UI-B adds its pages.
/// </para>
/// </remarks>
public abstract class KafkaFirstNavigationLiveTests(KafkaFixture kafka)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The Kafka pages the shell serves, below the studio's mount, as a visitor opens them; later packets add theirs.</summary>
    private static readonly string[] Pages =
    [
        "/kafka",
        "/kafka?cluster=live",
        "/kafka?cluster=dark",
        "/kafka?cluster=nope",
        "/kafka?cluster=live&topic=does-not-exist",
        "/kafka/topic?cluster=live&topic=orders.v1",
        "/kafka/topic?cluster=live&topic=does-not-exist",
        "/kafka/settings",
        "/kafka/settings?cluster=live",
        "/kafka/search",
        "/kafka/search?cluster=live&topic=orders.v1",
        "/kafka/search?cluster=live&topic=orders.v1&topic=does-not-exist&from=2026-11-12T10:00:00Z&to=2026-11-12T11:00:00Z&iso=uncommitted",
        "/kafka/record?cluster=live&topic=orders.v1&partition=0&offset=0",
        "/kafka/record?cluster=live&topic=does-not-exist&partition=0&offset=0",
        "/kafka/record?cluster=live&topic=orders.v1&partition=x&offset=-1",
    ];

    /// <summary>Counts the wires the engine creates, which is the engine's first contact with a cluster.</summary>
    private sealed class CountingWireFactory(IKafkaWireFactory inner) : IKafkaWireFactory
    {
        private int created;

        public int Created => Volatile.Read(ref created);

        public IKafkaWire Create(KafkaClusterDefinition cluster)
        {
            Interlocked.Increment(ref created);
            return inner.Create(cluster);
        }
    }

    [KafkaFact]
    public Task Opening_every_kafka_page_creates_no_topic_commits_no_offset_leaves_no_group_writes_no_row_and_calls_no_broker_while_prerendering() =>
        WalkAsync(
            "/postgres",
            static (builder, postgres) =>
            {
                builder.Services.AddSingleton(NpgsqlDataSource.Create(postgres.ConnectionString));
                builder.Services.AddPostgresStudio();
            },
            static app => app.MapPostgresStudio().AllowAnonymous());

    /// <summary>
    /// The same walk on a Marten Studio host (D-SHELL 12.1): a Marten store over the same database, with Marten's own default of
    /// <c>CreateOrUpdate</c> said by leaving it alone, so a shell that ensured storage on a Kafka page's first navigation would have created
    /// <c>mt_</c> objects the snapshot compares.
    /// </summary>
    [KafkaFact]
    public Task Opening_every_kafka_page_of_a_marten_studio_host_creates_no_topic_commits_no_offset_leaves_no_group_writes_no_row_and_calls_no_broker_while_prerendering() =>
        WalkAsync(
            "/marten",
            static (builder, postgres) =>
            {
                builder.Services.AddMarten(options =>
                {
                    options.Connection(postgres.ConnectionString);
                    options.DatabaseSchemaName = "kshell";
                });
                builder.Services.AddMartenStudio();
            },
            static app => app.MapMartenStudio().AllowAnonymous());

    private async Task WalkAsync(string mount, Action<WebApplicationBuilder, PostgresContainer> addHostStudio, Func<WebApplication, object> mapStudio)
    {
        await using PostgresContainer postgres = await PostgresContainer.StartAsync(Token);
        await postgres.ExecuteAsync(
            """
            create table public.customers (id int primary key, name text not null);
            insert into public.customers select g, 'customer ' || g from generate_series(1, 20) g;
            create table public.mt_doc_order (id uuid primary key, data jsonb not null);
            analyze;
            """,
            Token);

        // The control: this broker, for this principal, creates a topic on a metadata request, so the proofs below mean something.
        string control = "shell-control-" + kafka.RunId;
        using (IAdminClient plain = new AdminClientBuilder(kafka.Client("admin")).Build())
        {
            plain.GetMetadata(control, TimeSpan.FromSeconds(10));
        }

        Topics(kafka).Should().Contain(control, "the premise: a default admin client's single-topic metadata request creates the topic here");

        string[] topicsBefore = Topics(kafka);
        string[] groupsBefore = await GroupsAsync(kafka);
        string postgresBefore = await postgres.SnapshotAsync(Token);
        postgresBefore.Should().Contain("customers rows=20", "the premise: there are rows to write to");

        CountingWireFactory? counting = null;

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        addHostStudio(builder, postgres);
        builder.Services.AddSingleton<IKafkaWireFactory>(provider =>
            counting = new CountingWireFactory(ActivatorUtilities.CreateInstance<ConfluentWireFactory>(provider)));
        builder.Services.AddKafkaStudio(options =>
        {
            options.AddCluster("live", kafka.Client("admin"), static cluster => cluster.DisplayName("Live"));
            options.AddCluster("dark", new ClientConfig { BootstrapServers = "127.0.0.1:1" }, static cluster => cluster.DisplayName("Dark"));
        });

        await using WebApplication app = builder.Build();
        mapStudio(app);
        await app.StartAsync(Token);

        try
        {
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using HttpClient client = new() { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };

            foreach (string page in Pages.Select(x => string.Concat(mount, x)))
            {
                Stopwatch watch = Stopwatch.StartNew();
                using HttpResponseMessage response = await client.GetAsync(new Uri(page, UriKind.Relative), Token);

                response.StatusCode.Should().Be(HttpStatusCode.OK, page);
                (await response.Content.ReadAsStringAsync(Token)).Should().Contain("ms-k-trademark", page);
                watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "{0} made no call to the unreachable cluster, which would have waited for its request timeout", page);
            }

            counting.Should().NotBeNull();
            counting.Created.Should().Be(0, "(d) a prerendered page makes no broker call: no cluster's wire was ever created");
        }
        finally
        {
            await app.StopAsync(Token);
        }

        Topics(kafka).Where(static x => !x.StartsWith("__", StringComparison.Ordinal)).Should().BeEquivalentTo(
            topicsBefore.Where(static x => !x.StartsWith("__", StringComparison.Ordinal)),
            "(a) no topic was created, including by the page opened on ?topic=does-not-exist");
        Topics(kafka).Should().NotContain("does-not-exist");
        (await GroupsAsync(kafka)).Should().BeEquivalentTo(groupsBefore, "(b) no group was joined and no offset committed");
        (await postgres.SnapshotAsync(Token)).Should().Be(postgresBefore, "(c) nothing was created or written in Postgres");
    }

    private static string[] Topics(KafkaFixture kafka)
    {
        using IAdminClient admin = new AdminClientBuilder(kafka.Client("admin")).Build();
        return [.. admin.GetMetadata(TimeSpan.FromSeconds(10)).Topics.Select(static x => x.Topic).Order(StringComparer.Ordinal)];
    }

    /// <summary>Every group with its committed offsets, as text, so two readings compare by value.</summary>
    private static async Task<string[]> GroupsAsync(KafkaFixture kafka)
    {
        using IAdminClient admin = new AdminClientBuilder(kafka.Client("admin")).Build();
        ListConsumerGroupsResult groups = await admin.ListConsumerGroupsAsync();
        List<string> lines = [];

        foreach (ConsumerGroupListing group in groups.Valid.OrderBy(static x => x.GroupId, StringComparer.Ordinal))
        {
            List<ListConsumerGroupOffsetsResult> offsets = await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group.GroupId, null)]);
            lines.Add(group.GroupId + ": " + string.Join(
                ", ",
                offsets.SelectMany(static x => x.Partitions).OrderBy(static x => x.Topic, StringComparer.Ordinal).ThenBy(static x => x.Partition.Value)
                    .Select(static x => $"{x.Topic}[{x.Partition.Value}]={x.Offset}")));
        }

        return [.. lines];
    }

    /// <summary>A throwaway <c>postgres:17-alpine</c>, started for one test and removed with it, labelled for the packet that started it.</summary>
    internal sealed class PostgresContainer : IAsyncDisposable
    {
        private const string Password = "kstudio-live";

        private readonly IContainer container;

        private PostgresContainer(IContainer container) => this.container = container;

        public string ConnectionString =>
            $"Host=127.0.0.1;Port={container.GetMappedPublicPort(5432)};Database=postgres;Username=postgres;Password={Password};Timeout=15;Pooling=false";

        public static async Task<PostgresContainer> StartAsync(CancellationToken cancellationToken)
        {
            ContainerBuilder builder = new ContainerBuilder("postgres:17-alpine")
                .WithEnvironment("POSTGRES_PASSWORD", Password)
                .WithPortBinding(5432, true)
                .WithLabel("kstudio.live", "KafkaFirstNavigationLiveTests")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "127.0.0.1", "-U", "postgres"));

            if (Environment.GetEnvironmentVariable(KafkaFixture.PacketEnvironmentVariable) is { Length: > 0 } packet)
            {
                builder = builder.WithLabel("kstudio.packet", packet);
            }

            IContainer started = builder.Build();
            await started.StartAsync(cancellationToken);
            return new PostgresContainer(started);
        }

        public async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>Every object of every user schema, and each user table's exact row count and a hash of its rows, as text.</summary>
        public async Task<string> SnapshotAsync(CancellationToken cancellationToken)
        {
            await using NpgsqlConnection connection = new(ConnectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql =
                """
                select string_agg(line, ';' order by line) from (
                    select c.relnamespace::regnamespace::text || '.' || c.relname || ' kind=' || c.relkind::text as line
                    from pg_class c where c.relnamespace::regnamespace::text not in ('pg_catalog', 'information_schema', 'pg_toast')
                    union all
                    select 'customers rows=' || count(*) || ' hash=' || coalesce(md5(string_agg(t::text, ',' order by t.id)), '') from public.customers t
                    union all
                    select 'mt_doc_order rows=' || count(*) from public.mt_doc_order
                ) s
                """;

            await using NpgsqlCommand command = new(sql, connection);
            return (string?) await command.ExecuteScalarAsync(cancellationToken) ?? string.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            await container.DisposeAsync();
        }
    }
}

/// <summary><see cref="KafkaFirstNavigationLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class KafkaFirstNavigationLiveTests39(Kafka39Fixture fixture) : KafkaFirstNavigationLiveTests(fixture);

/// <summary><see cref="KafkaFirstNavigationLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class KafkaFirstNavigationLiveTests4(Kafka4Fixture fixture) : KafkaFirstNavigationLiveTests(fixture);
