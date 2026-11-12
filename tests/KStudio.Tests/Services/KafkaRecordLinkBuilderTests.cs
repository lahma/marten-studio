using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Decoding;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Correlation;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Settings;

namespace KStudio.Tests.Services;

/// <summary>
/// D44, decision 7.3 of D-CORR: a record links to the Marten documents and streams its topic's correlation fields name, in the tenant it shows, and never
/// from a value it hides.
/// </summary>
public class KafkaRecordLinkBuilderTests
{
    private const KafkaReadGrant Envelope = KafkaReadGrant.Envelope;

    private static readonly Action<KafkaTopicBuilder> OrderFields = static t => t.Correlation(c =>
    {
        c.Header("order-id").Document("Order");
        c.Header("stream-id").Stream();
    });

    private static KafkaClusterDefinition Cluster(Action<KafkaTopicBuilder>? topic = null, string? tenant = null)
    {
        KafkaEngineOptions options = new();
        options.AddCluster("main", new ClientConfig { BootstrapServers = "broker:9092" });
        options.Topic("orders", topic ?? OrderFields);

        if (tenant is null)
        {
            return new KafkaClusterRegistry(options).Get("main");
        }

        options.ClusterSources.Add(_ => new FakeClusterSource(new KafkaClusterContribution([FakeClusterSource.Cluster("wolverine"), FakeClusterSource.Cluster("wolverine", tenant)], [])));
        return new KafkaClusterRegistry(options, new ServiceCollection().BuildServiceProvider()).Get("wolverine/" + tenant);
    }

    private static KafkaMatch Match(KafkaClusterDefinition cluster, string? key = "k-1", params (string Name, string? Value)[] headers) =>
        CorrelationRig.Match(cluster.Descriptor("orders").Settings, Envelope, key, null, headers);

    [Fact]
    public void A_host_with_no_seam_gets_no_link()
    {
        KafkaClusterDefinition cluster = Cluster();

        KafkaRecordLinkBuilder.Build(null, cluster, Match(cluster, "k", ("order-id", "4711"))).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_topic_with_no_link_field_gets_no_link_and_the_seam_is_never_asked()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster(static t => t.Correlation(c => c.Header("order-id").As("order")));

        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "4711"))).IsEmpty.Should().BeTrue();

        routes.Documents.Should().BeEmpty();
    }

    [Fact]
    public void A_document_and_a_stream_link_come_from_the_seam_verbatim()
    {
        FakeRouteLinks routes = new() { DocumentAnswer = static (_, _, _) => "marten/x?a=1&b=%2F", StreamAnswer = static (_, _) => "marten/y" };
        KafkaClusterDefinition cluster = Cluster();

        KafkaRecordLinkSet set = KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "4711"), ("stream-id", "b7a1c0de-0000-4000-8000-000000000001")));

        set.TenantHidden.Should().BeFalse();
        set.Links.Select(static x => (x.Kind, x.Href, x.Reason)).Should().Equal(
            ("document", "marten/x?a=1&b=%2F", null),
            ("stream", "marten/y", null));
        set.Links.Select(static x => x.Text).Should().Equal("Document Order 4711 →", "Stream b7a1c0de-0000-4000-8000-… →");
        routes.Documents.Should().ContainSingle().Which.Should().Be((new StudioLinkScope(null, null), "Order", "4711"));
    }

    [Fact]
    public void An_answer_of_null_draws_the_words_and_the_reason_and_no_address()
    {
        FakeRouteLinks routes = new() { DocumentAnswer = static (_, _, _) => null, StreamAnswer = static (_, _) => null };
        KafkaClusterDefinition cluster = Cluster();

        KafkaRecordLinkSet set = KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "4711"), ("stream-id", "abc")));

        set.Links.Should().HaveCount(2).And.OnlyContain(static x => x.Href == null);
        set.Links[0].Should().Be(new KafkaRecordLink("document", "Document Order 4711", null, "not a document this studio shows"));
        set.Links[1].Reason.Should().Be("not a stream this studio shows");
    }

    [Fact]
    public void The_store_a_field_names_is_the_scope_the_seam_is_asked_with()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster(static t => t.Correlation(c => c.Header("order-id").Document("Order", "billing")));

        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "4711")));

        routes.Documents.Should().ContainSingle().Which.Scope.StoreKey.Should().Be("billing");
    }

    [Fact]
    public void The_tenant_is_the_literal_tenant_id_header()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster();

        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "4711"), ("tenant-id", "acme")));

        routes.Documents.Should().ContainSingle().Which.Scope.TenantId.Should().Be("acme");
    }

    [Fact]
    public void A_header_that_names_a_tenant_wins_over_the_entrys_own()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster(tenant: "globex");

        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "4711"), ("tenant-id", "acme")));

        routes.Documents.Should().ContainSingle().Which.Scope.TenantId.Should().Be("acme");
    }

    [Fact]
    public void With_no_header_the_tenant_is_the_cluster_entrys()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster(tenant: "globex");

        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "4711")));

        routes.Documents.Should().ContainSingle().Which.Scope.TenantId.Should().Be("globex");
    }

    [Fact]
    public void With_neither_there_is_no_tenant()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster();

        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "4711")));

        routes.Documents.Should().ContainSingle().Which.Scope.TenantId.Should().BeNull();
    }

    [Fact]
    public void A_masked_tenant_id_gives_no_link_at_all_and_the_seam_is_never_asked()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster(static t =>
        {
            t.Correlation(c => c.Header("order-id").Document("Order"));
            t.Redact(r => r.Header("tenant-id"));
        });

        KafkaRecordLinkSet set = KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "4711"), ("tenant-id", "S3NT1NEL-acme")));

        set.Links.Should().BeEmpty();
        set.TenantHidden.Should().BeTrue();
        routes.Documents.Should().BeEmpty("a link would carry the hidden tenant");
    }

    [Fact]
    public void A_masked_tenant_id_is_not_replaced_by_the_entrys_own_tenant()
    {
        KafkaClusterDefinition cluster = Cluster(static t => t.Redact(r => r.Header("tenant-id")), tenant: "globex");

        KafkaRecordLinkBuilder.TryTenant(Match(cluster, "k", ("tenant-id", "acme")), cluster, out string? tenant).Should().BeFalse();

        tenant.Should().BeNull();
    }

    [Fact]
    public void A_masked_tenant_id_says_nothing_when_there_is_nothing_to_link()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster(static t =>
        {
            t.Correlation(c => c.Header("order-id").Document("Order"));
            t.Redact(r => r.Header("tenant-id"));
        });

        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("tenant-id", "acme"))).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_masked_key_gives_no_document_link()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster(static t =>
        {
            t.Correlation(c => c.Key().Document("Order"));
            t.Redact(r => r.Key());
        });

        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "S3NT1NEL-order")).IsEmpty.Should().BeTrue();

        routes.Documents.Should().BeEmpty();
    }

    [Fact]
    public void A_masked_header_gives_no_link()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster(static t =>
        {
            t.Correlation(c => c.Header("order-id").Document("Order"));
            t.Redact(r => r.Header("order-id"));
        });

        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", "S3NT1NEL"))).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void A_cut_value_gives_no_link()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster();

        // A header longer than the engine keeps is cut, and a cut value is never an id.
        KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", new string('7', 10_000)))).IsEmpty.Should().BeTrue();

        routes.Documents.Should().BeEmpty();
    }

    [Fact]
    public void The_same_document_named_by_two_fields_is_linked_once_and_the_list_is_bounded()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition twice = Cluster(static t => t.Correlation(c =>
        {
            c.Header("order-id").Document("Order");
            c.Header("also-order-id").Document("Order");
        }));

        KafkaRecordLinkBuilder.Build(routes, twice, Match(twice, "k", ("order-id", "4711"), ("also-order-id", "4711"))).Links.Should().ContainSingle();

        KafkaClusterDefinition many = Cluster(static t => t.Correlation(c => c.Header("order-id").Document("Order")));
        KafkaRecordLinkSet set = KafkaRecordLinkBuilder.Build(routes, many, Match(many, "k", [.. Enumerable.Range(0, 20).Select(static i => ("order-id", (string?) ("o" + i)))]));

        set.Links.Count.Should().BeLessThanOrEqualTo(KafkaRecordLinkBuilder.MaxLinks);
    }

    [Fact]
    public void An_id_is_cut_at_24_characters_in_the_words_and_never_in_the_address()
    {
        FakeRouteLinks routes = new();
        KafkaClusterDefinition cluster = Cluster();
        string id = "0123456789012345678901234567890123456789";

        KafkaRecordLink link = KafkaRecordLinkBuilder.Build(routes, cluster, Match(cluster, "k", ("order-id", id))).Links.Single();

        link.Text.Should().Be("Document Order 012345678901234567890123… →");
        routes.Documents.Single().Id.Should().Be(id);
        link.Href.Should().Contain(id);
    }
}
