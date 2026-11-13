using System.Text;
using System.Text.Json;

using Confluent.Kafka;

using KStudio.Sample.Kafka;

using Microsoft.Extensions.Configuration;

namespace KStudio.Integration.Tests.Sample;

/// <summary>
/// The sample's heartbeat and its two settings, without Docker (D-M5 decision 10): the record the live tail follows carries the deny-listed header and
/// a JSON value whose email the topic's rule masks, the seed creates the topic empty, and the settings the verifier uses lower the tail's pause and
/// leave only search on.
/// </summary>
/// <remarks>These live in the live suite's project for the reason <c>SampleSeedTests</c> does: the sample hosts Marten Studio. They start no container.</remarks>
public class SampleTickerTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 9, 30, 15, 250, TimeSpan.Zero);

    [Fact]
    public void A_heartbeat_has_the_deny_listed_header_a_numbered_value_and_a_customer_email()
    {
        Message<string?, byte[]?> heartbeat = SampleTicker.Heartbeat(7, At);

        heartbeat.Key.Should().Be("heartbeat");
        heartbeat.Timestamp.UtcDateTime.Should().Be(At.UtcDateTime);

        IHeader header = heartbeat.Headers.Should().ContainSingle().Subject;
        header.Key.Should().Be("x-api-key", "the built-in deny-list masks it whatever a topic declares");
        Encoding.UTF8.GetString(header.GetValueBytes()).Should().Be("sample-secret");

        using JsonDocument value = JsonDocument.Parse(heartbeat.Value!);
        value.RootElement.GetProperty("seq").GetInt64().Should().Be(7);
        value.RootElement.GetProperty("at").GetString().Should().Be("2026-11-13T09:30:15.250Z");
        value.RootElement.GetProperty("customer").GetProperty("email").GetString().Should().Be("heartbeat7@example.com");
    }

    [Fact]
    public void The_seed_creates_the_heartbeat_topic_empty_and_the_ticker_writes_every_two_seconds()
    {
        KafkaSeedPlan plan = KafkaSeedPlan.Create(At);

        plan.Topics.Should().ContainSingle(static x => x.Name == KafkaSeedPlan.HeartbeatsTopic).Which.Partitions.Should().Be(1);
        plan.Records.Should().NotContain(static x => x.Topic == KafkaSeedPlan.HeartbeatsTopic, "the ticker writes them, as the application would");
        SampleTicker.Interval.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_judge_does_not_mind_the_heartbeat_topic()
    {
        SampleKafkaJudge.Judge(["orders.v1", "heartbeats.v1", "kstudio-sample.seed-v2"]).Verdict.Should().Be(SampleKafkaVerdict.AlreadySeeded);
        SampleKafkaJudge.Judge(["heartbeats.v1"]).Verdict.Should().Be(SampleKafkaVerdict.Empty, "a cluster with only the ticker's topic is not somebody's orders.v1");
    }

    [Fact]
    public void The_settings_lower_the_tail_pause_and_leave_only_search_on()
    {
        KafkaStudioOptions defaults = new();
        SampleKafkaRegistration.Configure(defaults, "localhost:9");
        defaults.Capabilities.LiveTail.Should().BeTrue("the sample turns everything on by default");
        defaults.Capabilities.ExportRecords.Should().BeTrue();
        defaults.Limits.TailPauseAfter.Should().Be(TimeSpan.FromMinutes(5));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SampleKafkaRegistration.TailPauseAfterSetting] = "00:00:10",
                [SampleKafkaRegistration.CapabilitiesSetting] = "search",
            })
            .Build();

        KafkaStudioOptions demo = new();
        SampleKafkaRegistration.Configure(demo, "localhost:9", configuration);

        demo.Limits.TailPauseAfter.Should().Be(TimeSpan.FromSeconds(10));
        demo.Capabilities.SearchRecords.Should().BeTrue();
        demo.Capabilities.ExportRecords.Should().BeFalse();
        demo.Capabilities.LiveTail.Should().BeFalse();
        demo.Capabilities.ReadPayloads.Should().BeFalse();
    }
}
