using System.Text;

using Bunit;

using Confluent.Kafka;

using KStudio.Core.Clusters;
using KStudio.Core.Predicates;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Components;

/// <summary>The inline record detail and the helpers behind it (D-UI decisions 14 to 16): a masked field reads redacted, nothing copied is wrong, and no credential is ever in a command.</summary>
public class KafkaRecordDetailTests
{
    private const string Secret = "hunter2-S3NT1NEL";

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static async Task<KafkaMatch> ReadAsync(KafkaSearchRig rig, string topic, long offset)
    {
        KafkaRecordPageResult read = await rig.Records().ReadAsync(rig.Entry, topic, 0, offset, Ct);
        return read.Should().BeOfType<KafkaRecordPageResult.Read>().Subject.Value.Record!;
    }

    private static IRenderedComponent<KStudio.Components.Shared.KafkaRecordDetail> Render(KafkaSearchRig rig, KafkaMatch match, string? kcat = null) =>
        rig.Studio.Context.Render<KStudio.Components.Shared.KafkaRecordDetail>(p => p
            .Add(x => x.Model, match)
            .Add(x => x.Cluster, rig.Entry.Key)
            .Add(x => x.Kcat, kcat));

    [Fact]
    public async Task A_masked_header_reads_redacted_by_its_rule()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("orders", t => t.Redact(r => r.Header("customer-email"))));
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k1", "v1", ("customer-email", Secret), ("correlation-id", "c-1"));

        IRenderedComponent<KStudio.Components.Shared.KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig, "orders", 0));

        detail.Markup.Should().Contain("••• (redacted by header customer-email)").And.NotContain(Secret);
        detail.Find("span.ms-k-redacted").TextContent.Should().Be("••• (redacted by header customer-email)");
        detail.Markup.Should().Contain("c-1");
        detail.Markup.Should().Contain(KafkaStudioText.ValueNotShown);
    }

    [Fact]
    public async Task A_null_key_reads_null_and_a_tombstone_reads_tombstone()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, null, null);

        IRenderedComponent<KStudio.Components.Shared.KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig, "orders", 0));

        detail.Find("span.ms-k-null").TextContent.Should().Be("null");
        detail.Find(".ms-k-tombstone").TextContent.Should().Be("tombstone");
    }

    [Fact]
    public async Task Omitted_headers_are_counted()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", [.. Enumerable.Range(0, 70).Select(static i => ("h" + i, (string?) "x"))]);

        IRenderedComponent<KStudio.Components.Shared.KafkaRecordDetail> detail = Render(rig, await ReadAsync(rig, "orders", 0));

        detail.Markup.Should().Contain("6 more headers were not kept.");
        detail.FindAll("table[aria-label='Headers'] tbody tr, [aria-label='Headers'] tbody tr").Should().HaveCount(64);
    }

    [Fact]
    public async Task Copy_as_key_is_absent_for_a_redacted_or_cut_key()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("masked", t => t.Redact(r => r.Key())));
        rig.Topic("masked");
        rig.Topic("plain");
        rig.Topic("long");
        rig.Produce("masked", 0, 1, "k-secret", "v");
        rig.Produce("plain", 0, 1, "order-42", "v");
        rig.Produce("long", 0, 1, new string('k', 2_000), "v");

        KafkaMatch masked = await ReadAsync(rig, "masked", 0);
        KafkaMatch plain = await ReadAsync(rig, "plain", 0);
        KafkaMatch cut = await ReadAsync(rig, "long", 0);

        KafkaMatchView.KeyPredicate(masked).Should().BeNull();
        KafkaMatchView.KeyPredicate(cut).Should().BeNull("a cut key would copy a different value");
        cut.Key.Truncated.Should().BeTrue();
        KafkaMatchView.KeyPredicate(plain).Should().Be("key = 'order-42'");
        PredicateParser.Parse(KafkaMatchView.KeyPredicate(plain)).IsValid.Should().BeTrue("a copied predicate parses");

        Render(rig, masked).Markup.Should().NotContain("Predicate for this key");
        IRenderedComponent<KStudio.Components.Shared.KafkaRecordDetail> menu = Render(rig, plain);
        menu.Markup.Should().Contain("Predicate for this key");
    }

    [Fact]
    public async Task A_key_longer_than_a_search_literal_is_refused_with_its_reason()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("long");
        rig.Produce("long", 0, 1, new string('k', 1_001), "v");

        KafkaMatch match = await ReadAsync(rig, "long", 0);

        match.Key.Truncated.Should().BeFalse("the match keeps up to 1 KB of key");
        KafkaMatchView.KeyPredicate(match).Should().BeNull();
        KafkaMatchView.KeyTooLongForPredicate(match).Should().BeTrue();
        Render(rig, match).Markup.Should().Contain(KafkaStudioText.CopyLiteralTooLong);
    }

    [Fact]
    public async Task A_correlation_header_longer_than_a_search_literal_is_refused_with_its_reason()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v", ("correlation-id", new string('c', 1_001)));

        KafkaMatch match = await ReadAsync(rig, "orders", 0);

        KafkaMatchView.Chips(match).Should().BeEmpty("no chip would be a search the parser reads");
        KafkaMatchView.HasLiteralTooLong(match).Should().BeTrue();
        Render(rig, match).Markup.Should().Contain(KafkaStudioText.CopyLiteralTooLong);
    }

    [Fact]
    public async Task The_kcat_line_has_no_credential()
    {
        await using KafkaSearchRig rig = new(engine: o => o.AddCluster(
            "secured",
            new ClientConfig
            {
                BootstrapServers = "broker-1.example:9093,broker-2.example:9093",
                SecurityProtocol = SecurityProtocol.SaslSsl,
                SaslMechanism = SaslMechanism.Plain,
                SaslUsername = "svc-orders",
                SaslPassword = Secret,
            }));

        KafkaClusterSettingsView view = rig.Engine.Provider.GetRequiredService<KafkaSettingsReader>()
            .Read(rig.Engine.Provider.GetRequiredService<KafkaClusterRegistry>().Get("secured"));

        string command = KafkaMatchView.Kcat("orders.v1", 3, 1200, view);

        command.Should().StartWith("kcat -C -b 'broker-1.example:9093,broker-2.example:9093' -t 'orders.v1' -p 3 -o 1200 -c 1 -J");
        command.Should().Contain("-X security.protocol=").And.Contain("-X sasl.mechanism=");
        command.Should().NotContain(Secret).And.NotContain("svc-orders");
        command.Should().EndWith(KafkaStudioText.KcatCredentialsHint);
    }

    [Fact]
    public void Without_settings_the_bootstrap_is_a_variable_and_a_quote_is_escaped()
    {
        KafkaMatchView.Kcat("o'rders", 0, 5, null).Should().Contain("-b '$BOOTSTRAP'").And.Contain("-t 'o'\\''rders'");
    }

    [Fact]
    public async Task Chips_skip_redacted_fields()
    {
        await using KafkaSearchRig rig = new(engine: o => o.Topic("orders", t => t.Redact(r => r.Header("tenant-id"))));
        rig.Topic("orders");
        rig.Produce(
            "orders",
            0,
            1,
            "k",
            "v",
            ("Correlation-Id", "0123456789012345678901234567890123456789"),
            ("tenant-id", Secret),
            ("saga-id", null));

        KafkaMatch match = await ReadAsync(rig, "orders", 0);
        IReadOnlyList<KafkaChip> chips = KafkaMatchView.Chips(match);

        chips.Should().ContainSingle("a masked header and a null one never become a chip");
        KafkaChip chip = chips[0];
        chip.Display.Should().Be("Correlation-Id 012345678901234567890123…");
        chip.Value.Should().Be("0123456789012345678901234567890123456789", "the whole value is the chip's title");
        chip.Predicate.Should().Be("header.Correlation-Id = '0123456789012345678901234567890123456789'");
        PredicateParser.Parse(chip.Predicate).IsValid.Should().BeTrue();
        chips.Select(static x => x.Predicate).Should().NotContain(static x => x.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_binary_header_is_not_a_chip()
    {
        await using KafkaSearchRig rig = new();
        rig.Topic("orders");
        rig.Cluster.Produce("orders", 0, Encoding.UTF8.GetBytes("k"), Encoding.UTF8.GetBytes("v"), [new KStudio.Core.Wire.WireHeader("correlation-id", [0xFF, 0xFE, 0x01])], KafkaSearchRig.T0.ToUnixTimeMilliseconds());

        KafkaMatch match = await ReadAsync(rig, "orders", 0);

        KafkaMatchView.Chips(match).Should().BeEmpty("a hex rendering of bytes is not a literal that finds them");
    }
}
