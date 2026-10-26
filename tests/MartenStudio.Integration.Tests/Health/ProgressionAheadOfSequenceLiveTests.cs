using Marten;

using MartenStudio.SampleDomain;
using MartenStudio.Services.Health.Checks;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>An event the D09 tests append, so the event store holds something the sequence can be reset below.</summary>
/// <param name="Number">Which one it is.</param>
public sealed record SequenceProbeEvent(int Number);

/// <summary>
/// D09 against a real Postgres: a sequence reset below the high-water mark and the committed events is both kinds of
/// finding at once, a shard ahead of a mark that is not is the third, and Marten's own correction and the repair statement
/// the remedy shows do what the remedy says they do - read at the floor and at the latest-tested Marten, which run the
/// same <c>HighWaterDetector</c>.
/// </summary>
/// <remarks>
/// Everything here is the test's own DML and DDL in the test's own schema: the progression rows a daemon would have
/// written, the <c>ALTER SEQUENCE ... RESTART</c> a restore or a cut-over leaves behind, and the remedy's statement pasted
/// as a DBA would paste it. The studio runs none of it.
/// </remarks>
/// <param name="postgres">The assembly's container.</param>
public class ProgressionAheadOfSequenceLiveTests(PostgresFixture postgres)
{
    private const int Events = 30;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthFires("D09")]
    public async Task A_sequence_reset_below_the_mark_and_the_events_is_D09_and_Martens_correction_does_what_it_says()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = await HostAsync(postgres, "health_d09_fires", clock);
        await ApplyAsync(host.Store);

        string events = host.Schema + "_events";
        string shard = DaemonCheckConfiguration.AsyncShardsOf(host.Store)[0].Identity;
        await AppendAsync(host.Store);

        await WriteProgressionAsync(events, ("HighWaterMark", Events), (shard, Events));

        HealthReport healthy = await ReportAsync(host);
        HealthLiveHost.Status(healthy, "D09").Outcome.Should().Be(HealthCheckOutcome.Checked);
        Of(healthy, "D09").Should().BeEmpty("the precondition: a store whose sequence is past everything is quiet");

        await ExecuteAsync(postgres, "alter sequence " + SqlIdentifier.Qualify(events, "mt_events_sequence") + " restart");

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport reset = await ReportAsync(host);

        HealthReportFinding behind = Of(reset, "D09").Should().ContainSingle(x => x.Finding.Subject == events + ".mt_events_sequence").Subject;
        behind.Severity.Should().Be(HealthSeverity.Critical);
        behind.Finding.Title.Should().Be("The event sequence is behind the events: it hands out 1 next, and mt_events already holds events up to "
            + Events);

        HealthReportFinding ahead = Of(reset, "D09").Should().ContainSingle(x => x.Finding.Subject == "HighWaterMark").Subject;
        ahead.Severity.Should().Be(HealthSeverity.Critical);
        ahead.Evidence.Should().Contain(new HealthEvidence("Shards ahead of the sequence", shard + " at " + Events));
        ahead.Finding.Remedy!.Code.Should().Contain("TryCorrectProgressInDatabaseAsync");

        // Marten's correction, as the remedy describes it: every row but its own bookkeeping to the sequence's last_value,
        // and only because the mark is above it. Right after the RESTART that is 1, the number handed out next - so the
        // mark ends one past what the sequence has handed out, and D09 goes on saying so.
        long lastValue = await ScalarAsync<long>("select last_value from " + SqlIdentifier.Qualify(events, "mt_events_sequence"));
        lastValue.Should().Be(1);
        await host.Store.Advanced.TryCorrectProgressInDatabaseAsync(Token);

        (await ProgressionAsync(events, "HighWaterMark")).Should().Be(lastValue);
        (await ProgressionAsync(events, shard)).Should().Be(lastValue);

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport corrected = await ReportAsync(host);
        HealthReportFinding exhausted = Of(corrected, "D09").Should().ContainSingle(x => x.Finding.Variant == "mark-ahead").Subject;
        exhausted.Finding.Title.Should().StartWith("The high-water mark (1) is ahead of the event sequence, which hands out 1 next");
        HealthReportFinding stillBehind = Of(corrected, "D09").Should().ContainSingle(x => x.Finding.Variant == "sequence-behind").Subject;

        // The correction has done all it can, and running it again changes nothing - so it is not offered again. The events
        // reach past the mark, so the sequence-behind repair is the one repair for both: the same statement, which reads
        // its target - the greater of the mark and max(seq_id) - when it runs (the G1M-fix delta review's N1 and N2).
        exhausted.Finding.Remedy!.Code.Should().NotContain("TryCorrectProgressInDatabaseAsync")
            .And.StartWith("-- One repair for both of D09's findings");
        exhausted.Finding.Remedy.Explanation.Should().StartWith("Correct progression has done what it can")
            .And.Contain("The events already reach " + Events + ", at or past the mark").And.Contain("one repair, not two");
        string repair = StatementOf(exhausted.Finding.Remedy);
        StatementOf(stillBehind.Finding.Remedy!).Should().Be(repair);
        repair.Should().NotContain("not q.is_called");

        await host.Store.Advanced.TryCorrectProgressInDatabaseAsync(Token);
        (await ProgressionAsync(events, "HighWaterMark")).Should().Be(1, "Marten corrects only while the mark is above last_value");

        // The appends really fail, as the finding says: the next number is one mt_events holds.
        Func<Task> append = () => AppendAsync(host.Store, 1);
        (await append.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("23505");

        // That append drew 1, which leaves the sequence exactly where this remedy's old text put it - setval it to the mark,
        // 1 - and the next append, taking 2, fails just the same: 2 is committed too.
        (await SequenceAsync(events)).Should().Be((1L, true));
        (await append.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("23505");

        // The remedy's own statement, pasted as it is shown, after that failed append drew a number: it moves the sequence
        // past every event and the mark, and the next append takes the number after the highest event.
        (await RepairAsync(repair)).Should().Be(Events, "the greater of the mark (1) and max(seq_id), read when it ran");
        (await SequenceAsync(events)).Should().Be(((long) Events, true));
        await AppendAsync(host.Store, 1);
        (await ScalarAsync<long>("select pg_catalog.max(seq_id) from " + SqlIdentifier.Qualify(events, "mt_events"))).Should().Be(Events + 1);
        (await RepairAsync(repair)).Should().BeNull("it never moves the sequence back, nor sideways");

        clock.Advance(TimeSpan.FromMinutes(2));
        Of(await ReportAsync(host), "D09").Should().BeEmpty("one repair fixed both findings");

        // The shard alone ahead, the mark not: the third kind, whose remedy is a rebuild or a rewind, not the correction -
        // to the mark, which no shard is rightly past, or to the highest number handed out where that is lower (N4).
        await WriteProgressionAsync(events, ("HighWaterMark", Events + 1), (shard, Events + 50));

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport shardAhead = await ReportAsync(host);

        HealthReportFinding lone = Of(shardAhead, "D09").Should().ContainSingle().Subject;
        lone.Finding.Subject.Should().Be(shard);
        lone.Finding.Title.Should().StartWith("Shard " + shard + " (" + (Events + 50) + ") is ahead of the event sequence, which hands out "
            + (Events + 2) + " next");
        lone.Finding.Remedy!.Explanation.Should().StartWith("Correct progression does nothing here");
        lone.Finding.Remedy.Code.Should().EndWith("rewind the subscription to " + (Events + 1) + ".");

        await WriteProgressionAsync(events, ("HighWaterMark", 20));

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReportFinding trailing = Of(await ReportAsync(host), "D09").Should().ContainSingle().Subject;
        trailing.Finding.Remedy!.Code.Should().EndWith("rewind the subscription to 20.", "the mark trails the sequence");
        trailing.Finding.Remedy.Explanation.Should().Contain("events appended since the reset at or below 20 are not replayed by it");

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>
    /// The other way out of a reset: <c>mt_events</c> emptied and its sequence restarted, as a <c>TRUNCATE ... RESTART
    /// IDENTITY</c> leaves it. Once Marten's correction has done what it can, the remedy is the repair statement with its
    /// guard - only while nothing has drawn from the sequence - and pasting it does what it says, once.
    /// </summary>
    [PostgresFact]
    [HealthFires("D09")]
    public async Task After_a_reset_with_no_events_left_the_guarded_repair_moves_the_sequence_past_the_mark_once()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = await HostAsync(postgres, "health_d09_emptied", clock);
        await ApplyAsync(host.Store);

        string events = host.Schema + "_events";
        string shard = DaemonCheckConfiguration.AsyncShardsOf(host.Store)[0].Identity;
        await AppendAsync(host.Store);
        await WriteProgressionAsync(events, ("HighWaterMark", Events), (shard, Events));

        await ExecuteAsync(postgres, "delete from " + SqlIdentifier.Qualify(events, "mt_events"));
        await ExecuteAsync(postgres, "delete from " + SqlIdentifier.Qualify(events, "mt_streams"));
        await ExecuteAsync(postgres, "alter sequence " + SqlIdentifier.Qualify(events, "mt_events_sequence") + " restart");

        HealthReport reset = await ReportAsync(host);
        Of(reset, "D09").Should().ContainSingle().Which.Finding.Remedy!.Code.Should().Contain("TryCorrectProgressInDatabaseAsync",
            "the mark is above last_value, so Marten's correction acts");
        await host.Store.Advanced.TryCorrectProgressInDatabaseAsync(Token);

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReportFinding done = Of(await ReportAsync(host), "D09").Should().ContainSingle().Subject;
        done.Finding.Variant.Should().Be("mark-ahead");
        done.Finding.Remedy!.Code.Should().StartWith(
            "-- A DBA's repair, with appends stopped, and only while mt_events_sequence still reads is_called = false:");

        string repair = StatementOf(done.Finding.Remedy);
        repair.Should().EndWith("\n  and not q.is_called;");

        (await RepairAsync(repair)).Should().Be(1, "the greater of the mark (1) and an empty mt_events");
        (await SequenceAsync(events)).Should().Be((1L, true));
        (await RepairAsync(repair)).Should().BeNull("the sequence has been called now, so the guarded repair does nothing");

        clock.Advance(TimeSpan.FromMinutes(2));
        Of(await ReportAsync(host), "D09").Should().BeEmpty();

        await AppendAsync(host.Store, 1);
        (await ScalarAsync<long>("select pg_catalog.max(seq_id) from " + SqlIdentifier.Qualify(events, "mt_events")))
            .Should().Be(2, "the next append takes a number above the mark, which the daemon has not passed");

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    [HealthQuiet("D09")]
    public async Task The_seeded_sample_store_with_its_progression_where_the_daemon_leaves_it_is_quiet()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = await HostAsync(postgres, "health_d09_quiet", clock);

        await new SampleDataSeeder().Populate(host.Store, Token);

        string events = host.Schema + "_events";
        long highest = await ScalarAsync<long>("select pg_catalog.max(seq_id) from " + SqlIdentifier.Qualify(events, "mt_events"));
        highest.Should().BePositive("the sample appends events");

        // Where a daemon caught up leaves them: the mark and every shard at the highest committed event.
        await WriteProgressionAsync(
            events,
            [("HighWaterMark", highest), .. DaemonCheckConfiguration.AsyncShardsOf(host.Store).Select(x => (x.Identity, highest))]);

        HealthReport report = await ReportAsync(host);

        HealthCheckStatus status = HealthLiveHost.Status(report, "D09");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Statements.Should().Contain(static x => x.Sql.Contains("as highest_committed", StringComparison.Ordinal),
            "D09 read the sequence and the highest event, so its silence means something");
        Of(report, "D09").Should().BeEmpty();
    }

    private static async Task AppendAsync(IDocumentStore store, int count = Events)
    {
        await using IDocumentSession session = store.LightweightSession();

        for (int i = 0; i < count; i++)
        {
            session.Events.StartStream(Guid.NewGuid(), new SequenceProbeEvent(i));
        }

        await session.SaveChangesAsync(Token);
    }

    /// <summary>Writes progression rows as a daemon would have left them - the test's own DML.</summary>
    private async Task WriteProgressionAsync(string schema, params (string Name, long Sequence)[] rows)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        foreach ((string name, long sequence) in rows)
        {
            await using NpgsqlCommand upsert = new(
                "insert into " + SqlIdentifier.Qualify(schema, "mt_event_progression") + " (name, last_seq_id, last_updated) "
                + "values (@name, @sequence, now()) on conflict (name) do update set last_seq_id = excluded.last_seq_id, last_updated = now()",
                connection);
            upsert.Parameters.AddWithValue("name", name);
            upsert.Parameters.AddWithValue("sequence", sequence);
            await upsert.ExecuteNonQueryAsync(Token);
        }
    }

    private async Task<long?> ProgressionAsync(string schema, string name)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand read = new(
            "select last_seq_id from " + SqlIdentifier.Qualify(schema, "mt_event_progression") + " where name = @name", connection);
        read.Parameters.AddWithValue("name", name);

        return await read.ExecuteScalarAsync(Token) is long value ? value : null;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        return (T) (await command.ExecuteScalarAsync(Token))!;
    }

    /// <summary>The statement a remedy shows, without the comment lines around it - what a DBA pastes.</summary>
    private static string StatementOf(HealthRemedy remedy) => string.Join('\n', remedy.Code.Split('\n')
        .SkipWhile(static x => x.StartsWith("--", StringComparison.Ordinal))
        .TakeWhile(static x => !x.StartsWith("--", StringComparison.Ordinal)));

    /// <summary>Runs a remedy's statement as a DBA would: what <c>setval</c> returned, or nothing when it moved nothing.</summary>
    private async Task<long?> RepairAsync(string sql)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        return await command.ExecuteScalarAsync(Token) is long value ? value : null;
    }

    private async Task<(long LastValue, bool IsCalled)> SequenceAsync(string schema)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select last_value, is_called from " + SqlIdentifier.Qualify(schema, "mt_events_sequence"), connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).Should().BeTrue();
        return (reader.GetInt64(0), reader.GetBoolean(1));
    }
}
