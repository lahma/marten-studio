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

        result.Should().BeOfType<KafkaRecordPageResult.CapabilityOff>().Which.Text.Should().Be("Searching records is not available here.");
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

    private const string ValueSentinel = "S3NT1NEL-9d4e2f";

    [Fact]
    public async Task A_value_view_is_audited_once_per_circuit_as_9605()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        for (int i = 0; i < 12; i++)
        {
            rig.Produce("orders", 0, i, "k" + i, "{\"v\":" + i + "}");
        }

        KafkaRecordService records = rig.Records();

        KafkaRecordPageResult.Read first = (KafkaRecordPageResult.Read) await records.ReadAsync(rig.Entry, "orders", 0, 5, Ct);
        await records.ReadAsync(rig.Entry, "orders", 0, 5, Ct);
        (await records.RecordValueViewAsync(rig.Entry, first.Value.Record!, Ct)).Should().BeTrue("the page's own view of the value covers the inline one");
        (await records.RecordValueViewAsync(rig.Entry, first.Value.Before[0], Ct)).Should().BeTrue("a neighbour's value is a view of its own");
        (await records.RecordValueViewAsync(rig.Entry, first.Value.Before[0], Ct)).Should().BeTrue();

        first.Grant.HasPayload.Should().BeTrue();
        first.Value.Record!.ValueText.Should().Be("{\"v\":5}");
        rig.Ring.Where(static x => x.Action == "ViewRecord").Select(static x => x.Target).Should().BeEquivalentTo(["orders/0/5", "orders/0/0"]);
        rig.Ring.Should().OnlyContain(static x => x.Capability == "ReadPayloads");
        rig.Ring.Single(static x => x.Target == "orders/0/5").Message.Should().StartWith("key, headers and value (JSON, ");
        rig.Logs.Entries.Where(static x => x.EventId.Id == 9605).Should().HaveCount(2);
        rig.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9604, "a page that shows the value writes 9605 instead of 9604");
    }

    [Fact]
    public async Task A_key_view_then_a_value_view_records_both()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k1", "{\"a\":1}");
        KafkaRecordService records = rig.Records();

        // The topic's capability policy refuses values: the page opens the record with its key and headers only (9604).
        rig.Studio.Authorization.AllowKafka(static r => r.Capability != "ReadPayloads");
        KafkaRecordPageResult.Read key = (KafkaRecordPageResult.Read) await records.ReadAsync(rig.Entry, "orders", 0, 0, Ct);
        key.Value.Record!.ReadWithPayload.Should().BeFalse();
        key.Grant.AccessFor("orders").Should().Be(KafkaValueAccess.PolicyRefused);

        // A match read under the envelope holds no value, so showing it is no view of one, whatever the policy says now: nothing is recorded as read.
        rig.Studio.Authorization.AllowKafka(static _ => true);
        (await records.RecordValueViewAsync(rig.Entry, key.Value.Record!, Ct)).Should().BeFalse();
        rig.Ring.Should().ContainSingle().Which.Capability.Should().Be("SearchRecords");
        rig.Logs.Entries.Select(static x => x.EventId.Id).Should().NotContain(9605);

        // The record is read again with values (a new read): its value is a view of its own, recorded once.
        KafkaRecordPageResult.Read value = (KafkaRecordPageResult.Read) await records.ReadAsync(rig.Entry, "orders", 0, 0, Ct);
        value.Value.Record!.ReadWithPayload.Should().BeTrue();
        (await records.RecordValueViewAsync(rig.Entry, value.Value.Record!, Ct)).Should().BeTrue();

        rig.Ring.Where(static x => x.Succeeded).Select(static x => x.Capability).Should().BeEquivalentTo(["SearchRecords", "ReadPayloads"]);
        rig.Logs.Entries.Select(static x => x.EventId.Id).Should().Contain([9604, 9605]);
        rig.Logs.Entries.Count(static x => x.EventId.Id == 9605).Should().Be(1);
    }

    [Fact]
    public async Task A_value_view_asks_SearchRecords_too()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k1", "{\"a\":1}");
        KafkaRecordService records = rig.Records();
        KafkaMatch match = ((KafkaRecordPageResult.Read) await records.ReadAsync(rig.Entry, "orders", 0, 0, Ct)).Value.Record!;
        rig.Studio.Authorization.Calls.Clear();

        // The capability policy passes ReadPayloads and refuses SearchRecords: the prerequisite is judged where the view is recorded.
        rig.Studio.Authorization.AllowKafka(static r => r.Capability != "SearchRecords");

        (await records.RecordValueViewAsync(rig.Entry, match, Ct)).Should().BeFalse();
        rig.Studio.Authorization.KafkaCalls.Should().Contain(static x => x.Resource.Capability == "SearchRecords" && x.Resource.Topic == "orders");

        // And the capability itself: SearchRecords off with ReadPayloads on is refused naming SearchRecords.
        rig.Studio.Authorization.AllowKafka(static _ => true);
        rig.Studio.Options.Capabilities.SearchRecords = false;
        (await records.RecordValueViewAsync(rig.Entry, match, Ct)).Should().BeFalse();
        rig.Ring[0].Message.Should().Contain("SearchRecords");
    }

    [Fact]
    public async Task The_9605_entry_holds_no_value()
    {
        await using KafkaSearchRig rig = new(payloads: true);
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k1", "{\"secret\":\"" + ValueSentinel + "\"}");
        KafkaRecordService records = rig.Records();

        KafkaRecordPageResult.Read read = (KafkaRecordPageResult.Read) await records.ReadAsync(rig.Entry, "orders", 0, 0, Ct);
        await records.RecordValueViewAsync(rig.Entry, read.Value.Record!, Ct);

        read.Value.Record!.ValueText.Should().Contain(ValueSentinel, "the visitor is shown the value");
        string everything = string.Join('\n', rig.Ring.Select(static x => string.Join('|', x.User, x.Action, x.Target, x.Message, x.Capability, x.ScopeLabel)))
            + string.Join('\n', rig.Logs.Entries.Select(static x => x.Message));
        everything.Should().NotContain(ValueSentinel);
        rig.Logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9605);
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
