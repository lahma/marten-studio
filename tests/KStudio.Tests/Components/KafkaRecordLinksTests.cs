using Bunit;

using KStudio.Components.Shared;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Settings;

namespace KStudio.Tests.Components;

/// <summary>
/// D44, decisions 7.3 and 9 of D-CORR, drawn: the record detail's Links section and its "Open in" line, and Wolverine's envelope lines. The seam is the
/// studio's own, so a host without it draws none of the first, and every address is the seam's or the template's, never built here.
/// </summary>
public class KafkaRecordLinksTests
{
    private const string Secret = "S3NT1NEL-tenant";

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static KafkaSearchRig Rig(FakeRouteLinks? routes, Action<KafkaStudioOptions>? studio = null, Action<KafkaEngineOptions>? engine = null) =>
        new(
            engine: engine ?? (static o => o.Topic("orders", static t => t.Correlation(static c =>
            {
                c.Header("order-id").Document("Order");
                c.Header("stream-id").Stream();
            }))),
            studio: studio,
            services: routes is null ? null : services => services.AddSingleton<IStudioRouteLinks>(routes));

    private static async Task<KafkaMatch> ReadAsync(KafkaSearchRig rig)
    {
        KafkaRecordPageResult read = await rig.Records().ReadAsync(rig.Entry, "orders", 0, 0, Ct);
        return read.Should().BeOfType<KafkaRecordPageResult.Read>().Subject.Value.Record!;
    }

    private static IRenderedComponent<KafkaRecordDetail> Render(KafkaSearchRig rig, KafkaMatch match) =>
        rig.Studio.Context.Render<KafkaRecordDetail>(p => p.Add(x => x.Model, match).Add(x => x.Cluster, rig.Entry.Key));

    [Fact]
    public async Task A_host_without_the_seam_draws_no_links_section()
    {
        await using KafkaSearchRig rig = Rig(null);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("order-id", "4711"));

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        detail.FindAll(".ms-k-record-links").Should().BeEmpty();
        detail.Markup.Should().NotContain("Document Order");
    }

    [Fact]
    public async Task The_links_section_draws_the_seams_addresses_verbatim()
    {
        FakeRouteLinks routes = new() { DocumentAnswer = static (_, _, _) => "marten/documents/detail?store=main&tenant=acme&type=Order&id=a%2Fb", StreamAnswer = static (_, _) => null };
        await using KafkaSearchRig rig = Rig(routes);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("order-id", "a/b"), ("stream-id", "xyz"), ("tenant-id", "acme"));

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        detail.Find(".ms-k-record-links h3").TextContent.Should().Be("Links");
        detail.Find("a.ms-k-marten-link").GetAttribute("href").Should().Be("marten/documents/detail?store=main&tenant=acme&type=Order&id=a%2Fb");
        detail.Find("a.ms-k-marten-link").TextContent.Should().Be("Document Order a/b →");
        detail.Find(".ms-k-marten-nolink").TextContent.Should().Be("Stream xyz (not a stream this studio shows)");
        routes.Documents.Should().ContainSingle().Which.Scope.TenantId.Should().Be("acme");
    }

    [Fact]
    public async Task A_masked_tenant_id_draws_no_link_and_says_why_without_the_value()
    {
        FakeRouteLinks routes = new();
        await using KafkaSearchRig rig = Rig(
            routes,
            engine: static o => o.Topic("orders", static t =>
            {
                t.Correlation(static c => c.Header("order-id").Document("Order"));
                t.Redact(static r => r.Header("tenant-id"));
            }));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("order-id", "4711"), ("tenant-id", Secret));

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        detail.FindAll("a.ms-k-marten-link").Should().BeEmpty();
        detail.Find(".ms-k-record-links").TextContent.Should().Contain(KafkaStudioText.LinksTenantHidden);
        detail.Markup.Should().NotContain(Secret);
        routes.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_timeline_row_with_a_hidden_tenant_says_so_briefly_and_keeps_the_sentence_as_its_title()
    {
        FakeRouteLinks routes = new();
        await using KafkaSearchRig rig = Rig(
            routes,
            engine: static o => o.Topic("orders", static t =>
            {
                t.Correlation(static c => c.Header("order-id").Document("Order"));
                t.Redact(static r => r.Header("tenant-id"));
            }));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("order-id", "4711"), ("tenant-id", Secret));
        KafkaMatch match = await ReadAsync(rig);

        IRenderedComponent<KafkaRecordLinks> links = rig.Studio.Context.Render<KafkaRecordLinks>(p => p
            .Add(x => x.Model, match)
            .Add(x => x.Cluster, rig.Entry.Key)
            .Add(x => x.Compact, true));

        var note = links.Find(".ms-k-record-links-compact .ms-muted");
        note.TextContent.Should().Be("no links (tenant hidden)");
        note.GetAttribute("title").Should().Be(KafkaStudioText.LinksTenantHidden);
        links.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public async Task A_masked_key_draws_no_document_link()
    {
        FakeRouteLinks routes = new();
        await using KafkaSearchRig rig = Rig(
            routes,
            engine: static o => o.Topic("orders", static t =>
            {
                t.Correlation(static c => c.Key().Document("Order"));
                t.Redact(static r => r.Key());
            }));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "S3NT1NEL-key", "v");

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        detail.FindAll(".ms-k-record-links").Should().BeEmpty();
        routes.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_timeline_row_draws_the_same_links_compactly()
    {
        FakeRouteLinks routes = new();
        await using KafkaSearchRig rig = Rig(routes);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("order-id", "4711"));
        KafkaMatch match = await ReadAsync(rig);

        IRenderedComponent<KafkaRecordLinks> links = rig.Studio.Context.Render<KafkaRecordLinks>(p => p
            .Add(x => x.Model, match)
            .Add(x => x.Cluster, rig.Entry.Key)
            .Add(x => x.Compact, true));

        links.FindAll(".ms-k-record-links-compact a.ms-k-marten-link").Should().ContainSingle();
        links.FindAll("h3").Should().BeEmpty("a row has no section heading");
    }

    [Fact]
    public async Task The_envelope_lines_read_the_wolverine_headers_as_they_are()
    {
        await using KafkaSearchRig rig = Rig(null);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("id", "0194b7a2-env"), ("sent-at", "2026-11-08 12:00:01:123456 Z"), ("partition-key", "order-4711"));

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        detail.Find("dd.ms-k-envelope-id").TextContent.Should().Be("0194b7a2-env");
        detail.Find("dd.ms-k-sent-at").TextContent.Should().Be("2026-11-08 12:00:01:123456 Z");
        detail.Find("dd.ms-k-partition-key").TextContent.Should().Be("order-4711");
    }

    [Fact]
    public async Task A_topic_without_the_envelope_headers_draws_no_envelope_line()
    {
        await using KafkaSearchRig rig = Rig(null);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v");

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        detail.Markup.Should().NotContain("Envelope id").And.NotContain("Sent at").And.NotContain("Partition key");
    }

    [Fact]
    public async Task A_masked_envelope_header_reads_redacted()
    {
        await using KafkaSearchRig rig = Rig(null, engine: static o => o.Topic("orders", static t => t.Redact(static r => r.Header("partition-key"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("partition-key", Secret));

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        detail.Find("dd.ms-k-partition-key").TextContent.Should().Be("••• (redacted by header partition-key)");
        detail.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public async Task The_open_in_line_opens_a_new_tab_and_sends_no_referrer()
    {
        await using KafkaSearchRig rig = Rig(null, studio: static o => o.ExternalLink("Traces", "https://tempo.example.com/trace/{traceId}?c={correlationId}"));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("correlation-id", "c-1"), ("traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"));

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        var link = detail.Find("a.ms-k-external-link");
        link.GetAttribute("href").Should().Be("https://tempo.example.com/trace/4bf92f3577b34da6a3ce929d0e0e4736?c=c-1");
        link.GetAttribute("rel").Should().Be("noopener noreferrer");
        link.GetAttribute("target").Should().Be("_blank");
        link.TextContent.Should().Be("Traces");
        detail.Find(".ms-k-external-links").TextContent.Should().StartWith("Open in");
    }

    [Fact]
    public async Task A_masked_correlation_id_draws_no_link_that_names_it()
    {
        await using KafkaSearchRig rig = Rig(
            null,
            studio: static o =>
            {
                o.ExternalLink("Logs", "https://logs.example.com/?c={correlationId}");
                o.ExternalLink("Window", "https://logs.example.com/?from={fromMs}&to={toMs}");
            },
            engine: static o => o.Topic("orders", static t => t.Redact(static r => r.Header("correlation-id"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("correlation-id", Secret));

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        detail.FindAll("a.ms-k-external-link").Select(static x => x.TextContent).Should().Equal("Window");
        detail.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public async Task A_host_with_no_external_link_draws_no_open_in_line()
    {
        await using KafkaSearchRig rig = Rig(null);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("correlation-id", "c-1"));

        IRenderedComponent<KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig));

        detail.FindAll(".ms-k-external-links").Should().BeEmpty();
    }
}
