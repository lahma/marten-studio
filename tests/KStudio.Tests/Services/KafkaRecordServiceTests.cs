using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

namespace KStudio.Tests.Services;

/// <summary>The record page's read (D-UI decision 14): the same gates as a search, a refusal that reads like a miss, and a view audited once per circuit.</summary>
public class KafkaRecordServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Seed(KafkaSearchRig rig, string topic, int count)
    {
        rig.Topic(topic);

        for (int i = 0; i < count; i++)
        {
            rig.Produce(topic, 0, i, "k" + i, "v" + i, ("correlation-id", "c-" + i));
        }
    }

    [Fact]
    public async Task A_refused_topic_reads_as_missing_and_is_recorded()
    {
        await using KafkaSearchRig rig = new();
        rig.Studio.Authorization.AllowKafka(static resource => resource.Topic != "secret.v1");
        Seed(rig, "secret.v1", 3);
        int callsBefore = rig.BrokerCalls;

        KafkaRecordPageResult result = await rig.Records().ReadAsync(rig.Entry, "secret.v1", 0, 1, Ct);

        result.Should().BeOfType<KafkaRecordPageResult.NotAvailable>();
        rig.BrokerCalls.Should().Be(callsBefore, "the policy refused the topic before any broker call was made for it");
        rig.Ring.Should().ContainSingle().Which.Should().Match<PostgresStudio.Services.StudioActionLogEntry>(
            static x => !x.Succeeded && x.Action == "ViewRecord" && x.Message == KafkaStudioText.RefusedEntry);
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9603);
    }

    [Fact]
    public async Task The_capability_is_asked_where_the_read_is()
    {
        await using KafkaSearchRig rig = new(searchOn: false);
        Seed(rig, "orders", 3);
        int callsBefore = rig.BrokerCalls;

        KafkaRecordPageResult result = await rig.Records().ReadAsync(rig.Entry, "orders", 0, 1, Ct);

        result.Should().BeOfType<KafkaRecordPageResult.CapabilityOff>().Which.Text.Should().Contain("KafkaStudioOptions.Capabilities.SearchRecords");
        rig.BrokerCalls.Should().Be(callsBefore);
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9602);
    }

    [Theory]
    [InlineData("not a topic", 0, 0)]
    [InlineData("orders", -1, 0)]
    [InlineData("orders", 0, -1)]
    public async Task An_address_that_is_not_a_record_makes_no_broker_call(string topic, int partition, long offset)
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 3);
        int callsBefore = rig.BrokerCalls;

        KafkaRecordPageResult result = await rig.Records().ReadAsync(rig.Entry, topic, partition, offset, Ct);

        result.Should().BeOfType<KafkaRecordPageResult.NotAvailable>();
        rig.BrokerCalls.Should().Be(callsBefore);
    }

    [Fact]
    public async Task A_view_is_audited_once_per_circuit_per_record()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 20);
        KafkaRecordService records = rig.Records();

        KafkaRecordPageResult first = await records.ReadAsync(rig.Entry, "orders", 0, 10, Ct);
        await records.ReadAsync(rig.Entry, "orders", 0, 10, Ct);
        await records.ReadAsync(rig.Entry, "orders", 0, 11, Ct);

        KafkaRecordRead read = first.Should().BeOfType<KafkaRecordPageResult.Read>().Subject.Value;
        read.Status.Should().Be(KafkaRecordReadStatus.Found);
        read.Before.Should().HaveCount(5);
        read.After.Should().HaveCount(5);

        rig.Ring.Where(static x => x.Action == "ViewRecord").Select(static x => x.Target).Should().BeEquivalentTo(["orders/0/10", "orders/0/11"]);
        rig.Ring.First(static x => x.Target == "orders/0/10").Message.Should().StartWith("key and headers, with 10 neighbours (11 records and ");
        rig.Logs.Entries.Where(static x => x.EventId.Id == 9604).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_view_of_an_offset_with_no_record_is_logged_as_9604_too()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 3);
        long open = rig.Cluster.BeginTransaction();
        rig.Cluster.Produce("orders", 0, [1], [2], null, KafkaSearchRig.T0.ToUnixTimeMilliseconds() + 10_000, open);
        rig.Cluster.EndTransaction("orders", 0, open, commit: false);

        KafkaRecordPageResult result = await rig.Records().ReadAsync(rig.Entry, "orders", 0, 3, Ct);

        result.Should().BeOfType<KafkaRecordPageResult.Read>().Which.Value.Status.Should().Be(KafkaRecordReadStatus.NoRecordAtOffset);
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9604);
        rig.Ring.Should().ContainSingle(static x => x.Action == "ViewRecord" && x.Succeeded);
    }

    [Fact]
    public async Task A_read_that_found_nothing_is_always_recorded()
    {
        await using KafkaSearchRig rig = new();
        Seed(rig, "orders", 3);
        KafkaRecordService records = rig.Records();

        await records.ReadAsync(rig.Entry, "orders", 0, 99, Ct);
        await records.ReadAsync(rig.Entry, "orders", 0, 99, Ct);

        rig.Ring.Where(static x => x.Action == "ViewRecord").Should().HaveCount(2).And.OnlyContain(static x => !x.Succeeded);
        rig.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9604);
    }
}
