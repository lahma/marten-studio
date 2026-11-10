using System.Diagnostics;
using System.Text;

using KStudio.Core.Clusters;
using KStudio.Core.Wire;

namespace KStudio.Tests.Contract;

/// <summary>
/// What the wire must do, on any cluster (decision 3.5): run on the fake in the fast suite and on <c>apache/kafka</c> 3.9 and 4.x in the
/// live suite, so the fake cannot drift from Kafka. A fast test may rely on a Kafka behaviour only if a test here proves it on both
/// images. Every seeded timestamp is in the past (F7).
/// </summary>
public abstract class WireContract
{
    private const long Second = 1000;

    private static readonly long T0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal abstract Task<IContractCluster> OpenAsync();

    [Fact]
    public async Task Earliest_and_latest_are_offsets_that_carry_no_timestamp()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("els");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Sequence(5));
        WirePartition partition = new(topic, 0);

        WireOffset earliest = (await Offsets(cluster, [partition], WireOffsetSpec.Earliest)).Single();
        WireOffset latest = (await Offsets(cluster, [partition], WireOffsetSpec.Latest)).Single();

        earliest.Offset.Should().Be(0);
        earliest.TimestampMs.Should().BeNull("Earliest answers an offset only (SPIKE S3a)");
        latest.Offset.Should().Be(5);
        latest.TimestampMs.Should().BeNull("Latest answers an offset only (SPIKE S3a)");
    }

    [Fact]
    public async Task For_timestamp_is_the_earliest_record_at_or_after_the_time_even_when_stamps_are_out_of_order()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("fts");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Stamped([10, 30, 20, 50, 15]));
        WirePartition partition = new(topic, 0);

        WireOffset a = (await Offsets(cluster, [partition], WireOffsetSpec.ForTimestamp(T0 + (25 * Second)))).Single();
        WireOffset b = (await Offsets(cluster, [partition], WireOffsetSpec.ForTimestamp(T0))).Single();
        WireOffset c = (await Offsets(cluster, [partition], WireOffsetSpec.ForTimestamp(T0 + (31 * Second)))).Single();
        WireOffset d = (await Offsets(cluster, [partition], WireOffsetSpec.ForTimestamp(T0 + (60 * Second)))).Single();

        (a.Offset, a.TimestampMs).Should().Be((1L, T0 + (30 * Second)), "offset 1 (30 s) is the first record at or after 25 s, though offset 2 is stamped 20 s");
        (b.Offset, b.TimestampMs).Should().Be((0L, T0 + (10 * Second)));
        (c.Offset, c.TimestampMs).Should().Be((3L, T0 + (50 * Second)));
        d.Found.Should().BeFalse("no record is stamped at or after 60 s");
        d.Offset.Should().Be(-1);
        d.TimestampMs.Should().BeNull();
    }

    [Fact]
    public async Task Max_timestamp_is_the_highest_stamp_not_the_last_records()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("max");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Stamped([10, 90, 30]));

        WireOffset max = (await Offsets(cluster, [new WirePartition(topic, 0)], WireOffsetSpec.MaxTimestamp)).Single();

        (max.Offset, max.TimestampMs).Should().Be((1L, T0 + (90 * Second)), "the record with the highest stamp, with its own offset (SPIKE S3b)");
    }

    [Fact]
    public async Task An_empty_partition_answers_minus_one_and_no_error()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("empty");
        await cluster.CreateTopicAsync(topic, 1);
        WirePartition partition = new(topic, 0);

        WireOffset forTimestamp = (await Offsets(cluster, [partition], WireOffsetSpec.ForTimestamp(0))).Single();
        WireOffset max = (await Offsets(cluster, [partition], WireOffsetSpec.MaxTimestamp)).Single();
        WireOffset earliest = (await Offsets(cluster, [partition], WireOffsetSpec.Earliest)).Single();
        WireOffset latest = (await Offsets(cluster, [partition], WireOffsetSpec.Latest)).Single();

        forTimestamp.Found.Should().BeFalse();
        forTimestamp.Error.Should().BeNull();
        max.Found.Should().BeFalse();
        max.Error.Should().BeNull();
        (earliest.Offset, latest.Offset).Should().Be((0L, 0L));
    }

    [Fact]
    public async Task A_partition_twice_in_one_call_is_refused()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("dup");
        await cluster.CreateTopicAsync(topic, 1);
        WirePartition partition = new(topic, 0);

        WireResult<IReadOnlyList<WireOffset>> result = await cluster.Wire.ListOffsetsAsync(
            [partition, partition], WireOffsetSpec.Earliest, WireIsolation.ReadUncommitted, Token);

        result.IsOk.Should().BeFalse("librdkafka refuses duplicates: 'Partitions must not contain duplicates' (SPIKE S3e)");
        result.Error.Should().BeOfType<WireError.Failed>();
    }

    [Fact]
    public async Task While_a_transaction_is_open_the_last_stable_offset_is_below_the_high_watermark()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("lso");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Sequence(3));
        WirePartition partition = new(topic, 0);

        await using IContractTransaction transaction = await cluster.BeginTransactionAsync(topic, 0, Sequence(2));

        long committed = (await Offsets(cluster, [partition], WireOffsetSpec.Latest, WireIsolation.ReadCommitted)).Single().Offset;
        long uncommitted = (await Offsets(cluster, [partition], WireOffsetSpec.Latest, WireIsolation.ReadUncommitted)).Single().Offset;

        committed.Should().Be(3, "read_committed Latest is the last stable offset (SPIKE S3c)");
        uncommitted.Should().Be(5, "read_uncommitted Latest is the high watermark");

        await transaction.AbortAsync();

        long afterCommitted = (await Offsets(cluster, [partition], WireOffsetSpec.Latest, WireIsolation.ReadCommitted)).Single().Offset;
        long afterUncommitted = (await Offsets(cluster, [partition], WireOffsetSpec.Latest, WireIsolation.ReadUncommitted)).Single().Offset;

        afterCommitted.Should().Be(6, "the abort marker occupies offset 5, and nothing is held back any more");
        afterUncommitted.Should().Be(6);
    }

    [Fact]
    public async Task Metadata_lists_the_topic_with_its_partitions_and_describe_adds_the_authorized_operations()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("meta");
        await cluster.CreateTopicAsync(topic, 3);

        WireMetadata metadata = Ok(await cluster.Wire.GetMetadataAsync(Token));
        WireTopicInfo listed = metadata.Topics.Single(x => x.Name == topic);

        listed.Partitions.Select(static x => x.Partition).Order().Should().Equal(0, 1, 2);
        listed.Partitions.Should().OnlyContain(static x => x.Replicas.Count >= 1 && x.InSyncReplicas.Count >= 1);

        IReadOnlyList<WireTopicDescription> described = Ok(await cluster.Wire.DescribeTopicsAsync([new KafkaTopicName(topic)], Token));
        WireTopicDescription description = described.Single();

        description.Error.Should().BeNull();
        description.Partitions.Should().HaveCount(3);
        description.AuthorizedOperations.Should().Contain(["Describe", "Read"]);

        IReadOnlyList<WireTopicDescription> missing = Ok(await cluster.Wire.DescribeTopicsAsync([new KafkaTopicName(cluster.NewTopic("nope"))], Token));
        missing.Single().Error.Should().BeOfType<WireError.NotFound>("an unknown topic is a per-topic value, not a failed call");
    }

    [Fact]
    public async Task The_cluster_description_names_the_cluster_a_controller_and_the_brokers()
    {
        await using IContractCluster cluster = await OpenAsync();

        WireClusterDescription description = Ok(await cluster.Wire.DescribeClusterAsync(Token));

        description.ClusterId.Should().NotBeNullOrWhiteSpace();
        description.ControllerId.Should().NotBeNull();
        description.Nodes.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_topics_config_returns_the_known_entries_that_were_set_and_no_others()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("cfg");
        await cluster.CreateTopicAsync(
            topic,
            1,
            new Dictionary<string, string>
            {
                ["retention.ms"] = "86400000",
                ["cleanup.policy"] = "compact",
                ["message.timestamp.type"] = "LogAppendTime",
                ["min.cleanable.dirty.ratio"] = "0.9",
            });

        WireTopicConfig config = Ok(await cluster.Wire.DescribeTopicConfigAsync(new KafkaTopicName(topic), Token));

        config.Get("retention.ms").Should().Be("86400000");
        config.Get("cleanup.policy").Should().Be("compact");
        config.Get("message.timestamp.type").Should().Be("LogAppendTime");
        config.Entries.Keys.Should().BeSubsetOf(WireTopicConfig.KnownKeys, "no entry outside the list the engine reads is carried");
    }

    [Fact]
    public async Task A_reader_assigned_explicit_offsets_reads_every_record_intact_and_reports_the_end()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("read");
        await cluster.CreateTopicAsync(topic, 1);

        await cluster.ProduceAsync(
            topic,
            0,
            [
                new ContractRecord("k0", "v0", T0, [new("h", "x0"), new("h", "dup")]),
                new ContractRecord("k1", "v1", T0 + Second),
                new ContractRecord("k2", null, T0 + (2 * Second)),
                new ContractRecord(null, "v3", T0 + (3 * Second)),
                new ContractRecord("k4", string.Empty, T0 + (4 * Second)),
                new ContractRecord("k5", "v5", T0 + (5 * Second)),
            ]);

        List<WireReadOutcome> outcomes = await OnReaderThread(cluster, WireIsolation.ReadCommitted, reader =>
        {
            reader.Assign([new WirePartitionOffset(topic, 0, 0)]);
            return DrainUntil(reader, list => list.OfType<WireReadOutcome.PartitionEnd>().Any());
        });

        List<WireRecord> records = [.. outcomes.OfType<WireReadOutcome.Record>().Select(static x => x.Value)];
        records.Select(static x => x.Offset).Should().Equal(0, 1, 2, 3, 4, 5);
        records[0].Topic.Should().Be(topic);
        Text(records[0].Key).Should().Be("k0");
        Text(records[0].Value).Should().Be("v0");
        records[0].Headers.Select(static x => (x.Name, Text(x.Value))).Should().Equal(("h", "x0"), ("h", "dup"));
        (records[0].TimestampMs, records[0].TimestampType).Should().Be((T0, WireTimestampType.CreateTime));
        records[2].Value.Should().BeNull("a tombstone stays null, never an empty array");
        records[3].Key.Should().BeNull("a null key stays null");
        records[4].Value.Should().NotBeNull().And.BeEmpty("an empty value is empty, not a tombstone");
        records[0].Size.Should().BeGreaterThan(0);

        WireReadOutcome.PartitionEnd end = outcomes.OfType<WireReadOutcome.PartitionEnd>().Single();
        (end.Partition, end.Offset).Should().Be((new WirePartition(topic, 0), 6L));
    }

    [Fact]
    public async Task A_reader_starts_at_the_offset_it_is_assigned()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("from");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Sequence(6));

        List<WireReadOutcome> outcomes = await OnReaderThread(cluster, WireIsolation.ReadCommitted, reader =>
        {
            reader.Assign([new WirePartitionOffset(topic, 0, 3)]);
            return DrainUntil(reader, list => list.OfType<WireReadOutcome.PartitionEnd>().Any());
        });

        outcomes.OfType<WireReadOutcome.Record>().Select(static x => x.Value.Offset).Should().Equal(3, 4, 5);
    }

    [Fact]
    public async Task An_empty_partition_reports_its_end_at_zero()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("none");
        await cluster.CreateTopicAsync(topic, 1);

        List<WireReadOutcome> outcomes = await OnReaderThread(cluster, WireIsolation.ReadCommitted, reader =>
        {
            reader.Assign([new WirePartitionOffset(topic, 0, 0)]);
            return DrainUntil(reader, list => list.OfType<WireReadOutcome.PartitionEnd>().Any());
        });

        outcomes.OfType<WireReadOutcome.Record>().Should().BeEmpty();
        outcomes.OfType<WireReadOutcome.PartitionEnd>().Single().Offset.Should().Be(0);
    }

    [Fact]
    public async Task Under_read_committed_a_reader_stops_at_the_last_stable_offset()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("rc");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Sequence(3));
        await using IContractTransaction transaction = await cluster.BeginTransactionAsync(topic, 0, Sequence(2));

        List<WireReadOutcome> outcomes = await OnReaderThread(cluster, WireIsolation.ReadCommitted, reader =>
        {
            reader.Assign([new WirePartitionOffset(topic, 0, 0)]);
            List<WireReadOutcome> first = DrainUntil(reader, list => list.OfType<WireReadOutcome.PartitionEnd>().Any());
            first.AddRange(ReadFor(reader, TimeSpan.FromMilliseconds(700)));
            return first;
        });

        outcomes.OfType<WireReadOutcome.Record>().Select(static x => x.Value.Offset).Should().Equal(0, 1, 2);
        outcomes.OfType<WireReadOutcome.PartitionEnd>().Select(static x => x.Offset).Should().Equal(
            [3L],
            "the end arrives at the last stable offset (SPIKE S3c), and nothing is delivered past it");

        await transaction.AbortAsync();
    }

    [Fact]
    public async Task Aborted_records_are_invisible_under_read_committed_and_delivered_under_read_uncommitted()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("abort");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Sequence(3));
        IContractTransaction transaction = await cluster.BeginTransactionAsync(topic, 0, Sequence(2));
        await transaction.AbortAsync();

        List<WireReadOutcome> committed = await OnReaderThread(cluster, WireIsolation.ReadCommitted, reader =>
        {
            reader.Assign([new WirePartitionOffset(topic, 0, 0)]);
            return DrainUntil(reader, list => list.OfType<WireReadOutcome.PartitionEnd>().Any());
        });

        List<WireReadOutcome> uncommitted = await OnReaderThread(cluster, WireIsolation.ReadUncommitted, reader =>
        {
            reader.Assign([new WirePartitionOffset(topic, 0, 0)]);
            return DrainUntil(reader, list => list.OfType<WireReadOutcome.PartitionEnd>().Any());
        });

        committed.OfType<WireReadOutcome.Record>().Select(static x => x.Value.Offset).Should().Equal(0, 1, 2);
        committed.OfType<WireReadOutcome.PartitionEnd>().Single().Offset.Should().Be(6, "the aborted records and the marker occupy offsets 3 to 5");
        uncommitted.OfType<WireReadOutcome.Record>().Select(static x => x.Value.Offset).Should().Equal(0, 1, 2, 3, 4);
        uncommitted.OfType<WireReadOutcome.PartitionEnd>().Single().Offset.Should().Be(6, "the marker is never delivered");
    }

    [Fact]
    public async Task A_paused_partition_delivers_nothing_and_resuming_it_loses_and_repeats_nothing()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("pause");
        await cluster.CreateTopicAsync(topic, 2);
        await cluster.ProduceAsync(topic, 0, Sequence(4));
        await cluster.ProduceAsync(topic, 1, Sequence(4));
        WirePartition zero = new(topic, 0);

        List<WireReadOutcome> outcomes = await OnReaderThread(cluster, WireIsolation.ReadCommitted, reader =>
        {
            reader.Assign([new WirePartitionOffset(topic, 0, 0), new WirePartitionOffset(topic, 1, 0)]);
            List<WireReadOutcome> all = DrainUntil(reader, list => list.OfType<WireReadOutcome.Record>().Any());

            reader.Pause([zero]);
            List<WireReadOutcome> whilePaused = ReadFor(reader, TimeSpan.FromMilliseconds(700));
            whilePaused.OfType<WireReadOutcome.Record>().Should().NotContain(
                x => x.Value.Partition == 0,
                "a paused partition delivers nothing (records prefetched before the pause are discarded and fetched again on resume)");
            all.AddRange(whilePaused);

            reader.Resume([zero]);
            all.AddRange(DrainUntil(reader, list => EveryPartitionEnded(all.Concat(list).ToList(), topic, 2)));
            return all;
        });

        foreach (int partition in new[] { 0, 1 })
        {
            outcomes.OfType<WireReadOutcome.Record>()
                .Where(x => x.Value.Partition == partition)
                .Select(static x => x.Value.Offset)
                .Order()
                .Should().Equal([0L, 1L, 2L, 3L], $"partition {partition} loses and repeats nothing across a pause and a resume");
        }
    }

    [Fact]
    public async Task A_position_below_the_log_start_is_reported_and_repositioning_resumes_there()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("gone");
        await cluster.CreateTopicAsync(topic, 1);
        await cluster.ProduceAsync(topic, 0, Sequence(10));
        await cluster.DeleteRecordsAsync(topic, 0, 5);

        List<WireReadOutcome> outcomes = await OnReaderThread(cluster, WireIsolation.ReadCommitted, reader =>
        {
            reader.Assign([new WirePartitionOffset(topic, 0, 0)]);
            List<WireReadOutcome> first = DrainUntil(reader, list => list.OfType<WireReadOutcome.Failure>().Any());

            // Not a seek: a seek on a partition in this state throws (SPIKE S3d).
            reader.Reposition(new WirePartitionOffset(topic, 0, 5));
            first.AddRange(DrainUntil(reader, list => list.OfType<WireReadOutcome.PartitionEnd>().Any()));
            return first;
        });

        WireReadOutcome.Failure failure = outcomes.OfType<WireReadOutcome.Failure>().First();
        failure.Error.Should().BeOfType<WireError.OffsetOutOfRange>().Which.Position.Should().BeNull("librdkafka's error does not carry the position; the fake must not invent one");
        failure.Partition.Should().Be(new WirePartition(topic, 0));
        outcomes.OfType<WireReadOutcome.Record>().Select(static x => x.Value.Offset).Should().Equal(5, 6, 7, 8, 9);
        outcomes.OfType<WireReadOutcome.PartitionEnd>().Last().Offset.Should().Be(10);
    }

    [Fact]
    public async Task Repositioning_a_partition_that_is_not_assigned_throws_the_engines_exception_with_a_mapped_error()
    {
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("unassigned");
        await cluster.CreateTopicAsync(topic, 2);
        await cluster.ProduceAsync(topic, 0, Sequence(3));

        WireException? thrown = null;

        await OnReaderThread(cluster, WireIsolation.ReadCommitted, reader =>
        {
            reader.Assign([new WirePartitionOffset(topic, 0, 0)]);

            try
            {
                reader.Reposition(new WirePartitionOffset(topic, 1, 0));
            }
            catch (WireException exception)
            {
                thrown = exception;
            }

            return [];
        });

        thrown.Should().NotBeNull("a Confluent exception must not cross the seam, and the call must not pass silently");
        WireError.Failed failed = thrown!.Error.Should().BeOfType<WireError.Failed>().Subject;
        failed.IsFatal.Should().BeFalse();
        failed.FailedCode.Should().Be("Local_InvalidArg", "measured on Kafka 3.9.2 and 4.4.0: librdkafka's incremental unassign refuses a partition that is not assigned");
    }

    [Fact]
    public async Task Every_wire_call_about_a_missing_topic_answers_not_found_and_creates_nothing()
    {
        // Run as a principal that may create topics, on a broker that auto-creates them (REVIEW-ENG-A 2): nothing the engine asks may create one.
        await using IContractCluster cluster = await OpenAsync();
        string topic = cluster.NewTopic("absent");
        WireError notFound = new WireError.NotFound(KafkaResourceType.Topic, topic);

        WireResult<IReadOnlyList<WireTopicDescription>> described = await cluster.Wire.DescribeTopicsAsync([new KafkaTopicName(topic)], Token);
        described.Error.Should().BeNull();
        described.Value.Should().ContainSingle().Which.Error.Should().Be(notFound);

        (await cluster.Wire.DescribeTopicConfigAsync(new KafkaTopicName(topic), Token)).Error.Should().Be(notFound);

        foreach (WireIsolation isolation in new[] { WireIsolation.ReadUncommitted, WireIsolation.ReadCommitted })
        {
            foreach (WireOffsetSpec spec in new[] { WireOffsetSpec.Earliest, WireOffsetSpec.Latest, WireOffsetSpec.MaxTimestamp, WireOffsetSpec.ForTimestamp(T0) })
            {
                WireResult<IReadOnlyList<WireOffset>> offsets = await cluster.Wire.ListOffsetsAsync([new WirePartition(topic, 0)], spec, isolation, Token);
                offsets.Error.Should().Be(notFound, $"ListOffsets {spec.Kind} at {isolation}");
            }
        }

        // A group's offsets for a partition that does not exist are simply not committed: no error, no offset.
        WireResult<IReadOnlyList<WireGroupOffset>> committed = await cluster.Wire.ListGroupOffsetsAsync("kstudio-absent-group", [new WirePartition(topic, 0)], Token);
        committed.Error.Should().BeNull();
        committed.Value.Should().ContainSingle().Which.Committed.Should().BeNull();

        (await cluster.TopicExistsAsync(topic)).Should().BeFalse("no read the engine makes creates a topic");
    }

    // ---- helpers ----

    protected static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    protected static string? Text(byte[]? bytes) => bytes is null ? null : Encoding.UTF8.GetString(bytes);

    /// <summary>The offsets a call answered, for the partitions asked, failing with the error when it failed.</summary>
    private static async Task<IReadOnlyList<WireOffset>> Offsets(
        IContractCluster cluster,
        IReadOnlyList<WirePartition> partitions,
        WireOffsetSpec spec,
        WireIsolation isolation = WireIsolation.ReadUncommitted) =>
        Ok(await cluster.Wire.ListOffsetsAsync(partitions, spec, isolation, Token));

    private static T Ok<T>(WireResult<T> result)
    {
        result.Error.Should().BeNull();
        return result.Value;
    }

    private static List<ContractRecord> Sequence(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ContractRecord("k" + i, "v" + i, T0 + (i * Second)))];

    /// <summary>Records stamped at the given seconds after the base time, in the order given.</summary>
    private static List<ContractRecord> Stamped(params int[] seconds) =>
        [.. seconds.Select((s, i) => new ContractRecord("k" + i, "v" + i, T0 + (s * Second)))];

    private static bool EveryPartitionEnded(List<WireReadOutcome> outcomes, string topic, int partitions) =>
        Enumerable.Range(0, partitions).All(p => outcomes.OfType<WireReadOutcome.PartitionEnd>().Any(x => x.Partition == new WirePartition(topic, p)));

    /// <summary>
    /// Builds a reader and works with it on one thread of its own, as a scan does, then closes it (a wait on a condition, not a delay).
    /// </summary>
    private static Task<List<WireReadOutcome>> OnReaderThread(
        IContractCluster cluster, WireIsolation isolation, Func<IWireScanReader, List<WireReadOutcome>> work) =>
        Task.Factory.StartNew(
            () =>
            {
                using IWireScanReader reader = cluster.Wire.OpenReader(new WireReaderOptions(Guid.NewGuid(), isolation, new KStudio.KafkaScanLimits()));

                try
                {
                    return work(reader);
                }
                finally
                {
                    reader.Close();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

    /// <summary>Reads until <paramref name="done" /> says so; a reader that never gets there fails the test after 30 seconds.</summary>
    private static List<WireReadOutcome> DrainUntil(IWireScanReader reader, Func<List<WireReadOutcome>, bool> done)
    {
        List<WireReadOutcome> outcomes = [];
        Stopwatch clock = Stopwatch.StartNew();

        while (!done(outcomes))
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("The reader did not reach the expected state within 30 s; it read: " + Describe(outcomes));
            }

            WireReadOutcome outcome = reader.Read(TimeSpan.FromMilliseconds(100));

            if (outcome != WireReadOutcome.Idle)
            {
                outcomes.Add(outcome);
            }
        }

        return outcomes;
    }

    /// <summary>Reads for a fixed time, to see that something does NOT arrive.</summary>
    private static List<WireReadOutcome> ReadFor(IWireScanReader reader, TimeSpan duration)
    {
        List<WireReadOutcome> outcomes = [];
        Stopwatch clock = Stopwatch.StartNew();

        while (clock.Elapsed < duration)
        {
            WireReadOutcome outcome = reader.Read(TimeSpan.FromMilliseconds(100));

            if (outcome != WireReadOutcome.Idle)
            {
                outcomes.Add(outcome);
            }

            Thread.Yield();
        }

        return outcomes;
    }

    private static string Describe(List<WireReadOutcome> outcomes) =>
        string.Join(", ", outcomes.Select(static x => x switch
        {
            WireReadOutcome.Record record => "record@" + record.Value.Offset,
            WireReadOutcome.PartitionEnd end => "end@" + end.Offset,
            WireReadOutcome.Failure failure => "failure:" + failure.Error.Code,
            _ => x.GetType().Name,
        }));
}
