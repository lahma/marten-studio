using KStudio.Core.Clusters;
using KStudio.Core.Logging;
using KStudio.Services;
using KStudio.Tests.Support;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services.Settings;

namespace KStudio.Tests.Services;

/// <summary>
/// D44, decision 8.3 of D-CORR: "Find in Kafka" is a pure link. The value travels only in the fragment, every part is escaped, a link is offered only when
/// some cluster has a topic that refers to such a record, and a registry that cannot be read costs the Marten page nothing.
/// </summary>
public class KafkaRecordSearchLinksTests
{
    private static readonly DateTimeOffset From = new(2026, 11, 8, 11, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset To = new(2026, 11, 8, 13, 0, 0, TimeSpan.Zero);

    private static StudioRecordSearchRequest Document(string id = "4711", string? store = "main", string? tenant = null, string[]? names = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(store, tenant, new StudioRecordReference(StudioRecordKind.Document, names ?? ["order", "Shop.Order", "Order"], id), from, to);

    private static StudioRecordSearchRequest Stream(string id = "abc", string? store = "main", string? tenant = null) =>
        new(store, tenant, new StudioRecordReference(StudioRecordKind.Stream, [], id), null, null);

    private static KafkaSearchRig Rig(Action<KafkaEngineOptions>? engine = null) =>
        new(engine: engine ?? (static o =>
        {
            o.Topic("orders", static t => t.Correlation(static c => c.Header("order-id").Document("Order")));
            o.Topic("shipments", static t => t.Correlation(static c => c.Header("stream-id").Stream()));
        }));

    private static KafkaRecordSearchLinks Links(KafkaSearchRig rig) =>
        new(rig.Engine.Provider, rig.Studio.Context.Services.GetRequiredService<IStudioSettings>());

    [Fact]
    public async Task The_label_is_the_modules()
    {
        await using KafkaSearchRig rig = Rig();

        Links(rig).Label.Should().Be("Find in Kafka");
    }

    [Fact]
    public async Task The_value_travels_only_in_the_fragment_and_the_query_has_no_v()
    {
        await using KafkaSearchRig rig = Rig();

        string href = Links(rig).ToSearch(Document("a/b#c?d%e&f=g h"))!;
        int hash = href.IndexOf('#', StringComparison.Ordinal);
        string beforeFragment = href[..hash];
        string fragment = href[(hash + 1)..];

        beforeFragment.Should().StartWith("kafka/related?cluster=test").And.NotContain("v=").And.NotContain("a/b").And.NotContain("%2F");
        fragment.Should().Be("link=document&type=Order&store=main&v=a%2Fb%23c%3Fd%25e%26f%3Dg%20h");
    }

    [Fact]
    public async Task A_stream_link_has_no_type()
    {
        await using KafkaSearchRig rig = Rig();

        string href = Links(rig).ToSearch(Stream("b7a1c0de-0000-4000-8000-000000000001"))!;

        href.Should().EndWith("#link=stream&store=main&v=b7a1c0de-0000-4000-8000-000000000001");
    }

    [Fact]
    public async Task The_window_travels_in_the_query_when_both_ends_are_known()
    {
        await using KafkaSearchRig rig = Rig();

        Links(rig).ToSearch(Document(from: From, to: To))!.Should().Contain("&from=2026-11-08T11%3A00%3A00Z&to=2026-11-08T13%3A00%3A00Z");
        Links(rig).ToSearch(Document(from: From))!.Should().NotContain("from=").And.NotContain("to=");
        Links(rig).ToSearch(Document())!.Should().NotContain("from=");
    }

    [Fact]
    public async Task Nothing_is_offered_when_no_topic_refers_to_such_a_record()
    {
        await using KafkaSearchRig rig = Rig(static o => o.Topic("orders", static t => t.Correlation(static c => c.Header("order-id").Document("Order"))));

        Links(rig).ToSearch(Stream()).Should().BeNull("no declared topic has a stream field");
        Links(rig).ToSearch(Document(names: ["invoice", "Shop.Invoice", "Invoice"])).Should().BeNull("no field names that type");
    }

    [Fact]
    public async Task A_host_with_no_correlation_field_at_all_offers_nothing()
    {
        await using KafkaSearchRig rig = Rig(static o => o.Topic("orders", static t => t.Description("orders")));

        Links(rig).ToSearch(Document()).Should().BeNull();
        Links(rig).ToSearch(Stream()).Should().BeNull();
    }

    [Fact]
    public async Task A_type_goes_by_any_of_its_names_ignoring_case_and_the_link_carries_the_fields_spelling()
    {
        await using KafkaSearchRig rig = Rig(static o => o.Topic("orders", static t => t.Correlation(static c => c.Header("order-id").Document("ORDER"))));

        Links(rig).ToSearch(Document(names: ["order", "Shop.Order", "Order"]))!.Should().Contain("type=ORDER&");
        Links(rig).ToSearch(Document(names: ["x", "Shop.Order", "ORDER"]))!.Should().Contain("type=ORDER&");
    }

    [Fact]
    public async Task A_field_that_names_a_store_applies_only_to_a_screen_on_that_store()
    {
        await using KafkaSearchRig rig = Rig(static o => o.Topic("orders", static t => t.Correlation(static c => c.Header("order-id").Document("Order", "billing"))));

        Links(rig).ToSearch(Document(store: "main")).Should().BeNull();
        Links(rig).ToSearch(Document(store: null)).Should().BeNull();
        Links(rig).ToSearch(Document(store: "billing")).Should().NotBeNull();
        Links(rig).ToSearch(Document(store: "BILLING")).Should().NotBeNull();
    }

    [Fact]
    public async Task A_field_that_names_no_store_applies_to_any_screen_and_the_screens_store_is_carried()
    {
        await using KafkaSearchRig rig = Rig();

        Links(rig).ToSearch(Document(store: "billing"))!.Should().Contain("store=billing");
        Links(rig).ToSearch(Document(store: null))!.Should().NotContain("store=");
    }

    [Fact]
    public async Task Several_clusters_with_such_topics_choose_the_requests_tenant_and_otherwise_none()
    {
        await using KafkaSearchRig rig = Rig(o =>
        {
            o.Topic("orders", static t => t.Correlation(static c => c.Header("order-id").Document("Order")));
            o.ClusterSources.Add(_ => new FakeClusterSource(new KafkaClusterContribution(
                [FakeClusterSource.Cluster("wolverine"), FakeClusterSource.Cluster("wolverine", "acme"), FakeClusterSource.Cluster("wolverine", "globex")],
                [])));
        });

        Links(rig).ToSearch(Document(tenant: "acme"))!.Should().StartWith("kafka/related?cluster=wolverine%2Facme#");
        Links(rig).ToSearch(Document(tenant: "globex"))!.Should().StartWith("kafka/related?cluster=wolverine%2Fglobex#");
        Links(rig).ToSearch(Document(tenant: "initech"))!.Should().NotContain("cluster=");
        Links(rig).ToSearch(Document())!.Should().NotContain("cluster=");
    }

    [Fact]
    public async Task One_cluster_with_such_topics_is_named()
    {
        await using KafkaSearchRig rig = Rig();

        Links(rig).ToSearch(Document())!.Should().StartWith("kafka/related?cluster=test");
    }

    [Fact]
    public async Task An_empty_id_is_offered_nothing()
    {
        await using KafkaSearchRig rig = Rig();

        Links(rig).ToSearch(Document(id: string.Empty)).Should().BeNull();
    }

    [Fact]
    public async Task A_registry_that_cannot_be_built_gives_null_and_event_9678_once_at_debug_without_the_exception_text()
    {
        CapturingLoggerProvider logs = new();
        ServiceCollection services = new();
        services.AddSingleton(logs.CreateFactory());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<KStudioLoggers>();
        services.AddSingleton(static provider => new KafkaLogOnce(provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<KafkaClusterRegistry>(static _ => throw new InvalidOperationException("S3NT1NEL host options are not valid"));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using KafkaSearchRig rig = Rig();

        KafkaRecordSearchLinks links = new(provider, rig.Studio.Context.Services.GetRequiredService<IStudioSettings>());

        links.ToSearch(Document()).Should().BeNull();
        links.ToSearch(Document()).Should().BeNull();

        CapturedLogEntry entry = logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9678, "said once").Subject;
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Message.Should().Be("KStudio's Find in Kafka link could not read the clusters: InvalidOperationException").And.NotContain("S3NT1NEL");
    }
}
