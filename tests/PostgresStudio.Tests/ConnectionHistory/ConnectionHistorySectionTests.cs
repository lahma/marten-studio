using System.Text.Json;

using Microsoft.Extensions.Options;

using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Postgres;

namespace PostgresStudio.Tests.ConnectionHistory;

/// <summary>
/// 1.0.1 (issue #47) without a database: the one lookup Holders and the diagnostics API read a server's connection history
/// through, the snapshot section built from it, its JSON key and the request that asks for it.
/// </summary>
public class ConnectionHistorySectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PostgresConnectionSample Sample(int minutesAgo, long connections, int limit = 100, string instance = "a", params (string? Application, int Total)[] applications) => new()
    {
        At = Now.AddMinutes(-minutesAgo),
        Server = "db:5432",
        Instance = instance,
        Connections = connections,
        ClientLimit = limit,
        ByApplication = [.. applications.Select(static x => new PostgresConnectionCounts { ApplicationName = x.Application, Total = x.Total })],
    };

    /// <summary>Oldest first, as a store returns them.</summary>
    private static PostgresConnectionSample[] Day() =>
    [
        Sample(120, 10, 100, "a", ("api", 8)),
        Sample(90, 90, 100, "a", ("api", 70), (null, 20)),
        Sample(60, 90, 100, "b", ("api", 70)),
        Sample(30, 5, 200, "b"),
    ];

    private static ConnectionHistoryReader Reader(IPostgresConnectionHistoryStore store) =>
        new(store, Options.Create(new PostgresConnectionHistoryOptions()), PostgresStudioSettings.Of(new PostgresStudioOptions { QueryTimeout = TimeSpan.FromMilliseconds(300) }));

    // ----------------------------------------------------------------------------------------------
    // The lookup
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_lookup_summarizes_the_window_with_the_peak_the_limit_now_and_the_instance_breaks()
    {
        ConnectionHistoryRead read = await ConnectionHistoryLookup.ReadAsync(Reader(new FixedStore(Day())), "db:5432", names: true, since: null, Token);

        read.Outcome.Should().Be(PostgresReadOutcome.Read);
        read.Server.Should().Be("db:5432");
        read.Window.Should().Be(new PostgresConnectionHistoryOptions().Retention);
        read.PendingSamples.Should().Be(0, "no backlog is wired in yet");
        read.Failure.Should().BeNull();

        ConnectionHistoryView view = read.View!;
        view.Samples.Should().Be(4);
        view.Peak.Should().Be(90);
        view.PeakAt.Should().Be(Now.AddMinutes(-90), "the first time it was reached");
        view.ClientLimit.Should().Be(100, "the budget at the peak");
        view.ClientLimitNow.Should().Be(200, "the budget at the latest sample: the cluster was resized after the peak");
        view.InstanceChanges.Should().Be(1);
        view.Applications.Select(static x => (x.Application, x.Peak)).Should().Equal(("api", 70), (null, 20));
    }

    [Fact]
    public async Task A_since_drops_the_older_samples_before_the_view_is_made()
    {
        PostgresConnectionSample[] samples = [Sample(26 * 60, 99), .. Day()];

        ConnectionHistoryRead whole = await ConnectionHistoryLookup.ReadAsync(Reader(new FixedStore(samples)), "db:5432", names: true, since: null, Token);
        ConnectionHistoryRead lastDay = await ConnectionHistoryLookup.ReadAsync(Reader(new FixedStore(samples)), "db:5432", names: true, Now.AddHours(-24), Token);

        whole.View!.Peak.Should().Be(99);
        whole.View.Samples.Should().Be(5);
        lastDay.View!.Peak.Should().Be(90, "the 99 was taken 26 hours ago");
        lastDay.View.Samples.Should().Be(4);
    }

    [Fact]
    public async Task A_window_without_samples_is_unavailable_with_no_view_and_so_is_one_whose_samples_are_all_too_old()
    {
        ConnectionHistoryRead none = await ConnectionHistoryLookup.ReadAsync(Reader(new FixedStore([])), "db:5432", names: true, since: null, Token);
        ConnectionHistoryRead old = await ConnectionHistoryLookup.ReadAsync(Reader(new FixedStore(Day())), "db:5432", names: true, Now, Token);

        none.Outcome.Should().Be(PostgresReadOutcome.Unavailable);
        none.View.Should().BeNull();
        old.Outcome.Should().Be(PostgresReadOutcome.Unavailable);
        old.View.Should().BeNull();
    }

    [Fact]
    public async Task A_store_that_fails_is_a_value_a_store_that_is_cut_off_is_timed_out_and_the_callers_own_cancellation_is_thrown()
    {
        ConnectionHistoryRead failed = await ConnectionHistoryLookup.ReadAsync(Reader(new ThrowingStore(new InvalidOperationException("SENTINEL"))), "db:5432", true, null, Token);
        ConnectionHistoryRead hung = await ConnectionHistoryLookup.ReadAsync(Reader(new HangingStore()), "db:5432", true, null, Token);

        failed.Outcome.Should().Be(PostgresReadOutcome.Failed);
        failed.Failure.Should().BeOfType<InvalidOperationException>();
        failed.View.Should().BeNull();
        hung.Outcome.Should().Be(PostgresReadOutcome.TimedOut, "the reader cuts a store off at QueryTimeout");

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        Func<Task> caller = () => ConnectionHistoryLookup.ReadAsync(Reader(new HangingStore()), "db:5432", true, null, cancelled.Token);
        await caller.Should().ThrowAsync<OperationCanceledException>("the caller's cancellation is theirs, never a value");
    }

    // ----------------------------------------------------------------------------------------------
    // The section
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_section_carries_the_view_and_names_applications_only_with_IncludeActivity()
    {
        ConnectionHistoryRead read = await ConnectionHistoryLookup.ReadAsync(Reader(new FixedStore(Day())), "db:5432", names: true, since: null, Token);

        PostgresConnectionHistorySection without = DiagnosticsSnapshotBuilder.ConnectionHistory(read, includeActivity: false);
        PostgresConnectionHistorySection named = DiagnosticsSnapshotBuilder.ConnectionHistory(read, includeActivity: true);

        without.Outcome.Should().Be(PostgresReadOutcome.Read);
        without.Server.Should().Be("db:5432");
        without.Window.Should().Be(read.Window);
        without.Since.Should().Be(Now.AddMinutes(-120));
        without.Until.Should().Be(Now.AddMinutes(-30));
        without.Samples.Should().Be(4);
        without.PendingSamples.Should().Be(0);
        without.InstanceChanges.Should().Be(1);
        without.Peak.Should().Be(90);
        without.PeakAt.Should().Be(Now.AddMinutes(-90));
        without.ClientLimit.Should().Be(100);
        without.ClientLimitNow.Should().Be(200);
        without.ByApplication.Should().BeEmpty("another application's name is Activity-grade");

        named.ByApplication.Select(static x => (x.ApplicationName, x.Peak)).Should().Equal(("api", 70), (null, 20));
        named.ByApplication[0].At.Should().Be(Now.AddMinutes(-90));
        (named with { ByApplication = [] }).Should().Be(without, "nothing else differs");
    }

    [Fact]
    public void A_section_that_was_not_read_says_why_and_keeps_every_number_at_its_default()
    {
        PostgresConnectionHistorySection none = DiagnosticsSnapshotBuilder.ConnectionHistory(
            new ConnectionHistoryRead(PostgresReadOutcome.Unavailable, "db:5432", TimeSpan.FromDays(7), null, 0, null), false);
        PostgresConnectionHistorySection timedOut = DiagnosticsSnapshotBuilder.ConnectionHistory(
            new ConnectionHistoryRead(PostgresReadOutcome.TimedOut, "db:5432", TimeSpan.FromDays(7), null, 0, new OperationCanceledException()), false);
        PostgresConnectionHistorySection failed = DiagnosticsSnapshotBuilder.ConnectionHistory(
            new ConnectionHistoryRead(PostgresReadOutcome.Failed, "db:5432", TimeSpan.FromDays(7), null, 0, new InvalidOperationException("SENTINEL-STORE-WORDS")), false);

        none.Outcome.Should().Be(PostgresReadOutcome.Unavailable);
        none.Reason.Should().Be(DiagnosticsSnapshotBuilder.NoSamplesYet);
        timedOut.Outcome.Should().Be(PostgresReadOutcome.TimedOut);
        timedOut.Reason.Should().Contain("QueryTimeout");
        failed.Outcome.Should().Be(PostgresReadOutcome.Failed);
        failed.Reason.Should().Contain("InvalidOperationException").And.NotContain("SENTINEL", "the store's own words stay in the server log");

        foreach (PostgresConnectionHistorySection section in new[] { none, timedOut, failed })
        {
            section.Server.Should().Be("db:5432");
            section.Window.Should().Be(TimeSpan.FromDays(7));
            section.Samples.Should().Be(0);
            section.Peak.Should().Be(0);
            section.PeakAt.Should().BeNull();
            section.ByApplication.Should().BeEmpty();
        }
    }

    [Fact]
    public void An_empty_window_says_the_first_sample_is_coming_here_and_in_reader_mode_that_another_process_fills_it()
    {
        ConnectionHistoryRead none = new(PostgresReadOutcome.Unavailable, "db:5432", TimeSpan.FromDays(7), null, 0, null);

        PostgresConnectionHistorySection sampling = DiagnosticsSnapshotBuilder.ConnectionHistory(none, false);
        PostgresConnectionHistorySection explicitSampling = DiagnosticsSnapshotBuilder.ConnectionHistory(none, false, readerOnly: false);
        PostgresConnectionHistorySection reading = DiagnosticsSnapshotBuilder.ConnectionHistory(none, false, readerOnly: true);

        sampling.Reason.Should().Be(DiagnosticsSnapshotBuilder.NoSamplesYet).And.Contain("is taken a moment after the application starts");
        explicitSampling.Reason.Should().Be(sampling.Reason);
        reading.Reason.Should().Be(DiagnosticsSnapshotBuilder.NoSamplesYetReaderOnly)
            .And.Contain("This process only reads it: another process fills it, and has sampled nothing of this server within the window.")
            .And.NotContain("moment after the application starts");
        reading.Outcome.Should().Be(PostgresReadOutcome.Unavailable);
    }

    [Fact]
    public void Under_ShouldSample_an_empty_window_says_whether_this_process_was_chosen_and_never_that_the_first_is_taken_a_moment_after_start()
    {
        ConnectionHistoryRead none = new(PostgresReadOutcome.Unavailable, "db:5432", TimeSpan.FromDays(7), null, 0, null);

        PostgresConnectionHistorySection chosen = DiagnosticsSnapshotBuilder.ConnectionHistory(none, false, sampling: ConnectionHistorySampling.Chosen);
        PostgresConnectionHistorySection notChosen = DiagnosticsSnapshotBuilder.ConnectionHistory(none, false, sampling: ConnectionHistorySampling.NotChosen);
        PostgresConnectionHistorySection always = DiagnosticsSnapshotBuilder.ConnectionHistory(none, false, sampling: ConnectionHistorySampling.Always);
        PostgresConnectionHistorySection readerOnly = DiagnosticsSnapshotBuilder.ConnectionHistory(none, false, readerOnly: true, sampling: ConnectionHistorySampling.NotChosen);

        chosen.Reason.Should().Be("The connection history holds no sample of this server yet: the first is taken at the next interval, now that ShouldSample has chosen this process to sample.");
        notChosen.Reason.Should().Be("The connection history holds no sample of this server yet: another process samples (ShouldSample chooses it), and its samples appear here once stored.");
        always.Reason.Should().Be(DiagnosticsSnapshotBuilder.NoSamplesYet, "without ShouldSample the sentence is unchanged");
        readerOnly.Reason.Should().Be(DiagnosticsSnapshotBuilder.NoSamplesYetReaderOnly, "the reader-only registration keeps its own sentence (#48)");

        foreach (PostgresConnectionHistorySection section in new[] { chosen, notChosen })
        {
            section.Outcome.Should().Be(PostgresReadOutcome.Unavailable);
            section.Reason.Should().NotContain("moment after the application starts");
        }
    }

    [Fact]
    public async Task Holders_and_the_API_read_one_sample_set_through_one_cache_and_cannot_disagree_on_a_peak()
    {
        CountingStore store = new(Day());
        ConnectionHistoryReader reader = Reader(store);

        // Holders: the view as the page shows it. The API: the section. The same reader, so the same read.
        ConnectionHistoryRead holders = await ConnectionHistoryLookup.ReadAsync(reader, "db:5432", names: true, since: null, Token);
        ConnectionHistoryRead api = await ConnectionHistoryLookup.ReadAsync(reader, "db:5432", names: true, since: null, Token);
        PostgresConnectionHistorySection section = DiagnosticsSnapshotBuilder.ConnectionHistory(api, includeActivity: true);

        store.Reads.Should().Be(1, "two callers within the cache's time to live share one read of the store");

        ConnectionHistoryView expected = ConnectionHistoryView.Of(Day(), names: true)!;
        holders.View!.Peak.Should().Be(expected.Peak).And.Be(section.Peak);
        holders.View.PeakAt.Should().Be(expected.PeakAt).And.Be(section.PeakAt!.Value);
        holders.View.ClientLimit.Should().Be(expected.ClientLimit).And.Be(section.ClientLimit);
        holders.View.ClientLimitNow.Should().Be(expected.ClientLimitNow).And.Be(section.ClientLimitNow);
        holders.View.Samples.Should().Be(expected.Samples).And.Be(section.Samples);
        holders.View.InstanceChanges.Should().Be(expected.InstanceChanges).And.Be(section.InstanceChanges);
        holders.View.Applications.Select(static x => x.Peak).Should().Equal(section.ByApplication.Select(static x => x.Peak));
    }

    // ----------------------------------------------------------------------------------------------
    // The request and the JSON
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void AllSections_asks_for_the_history_and_FindingsOnly_does_not()
    {
        PostgresDiagnosticsRequest.AllSections.ConnectionHistory.Should().BeTrue();
        PostgresDiagnosticsRequest.FindingsOnly.ConnectionHistory.Should().BeFalse();
        new PostgresDiagnosticsRequest().ConnectionHistory.Should().BeFalse("a section not asked for is not read");
    }

    [Fact]
    public async Task The_json_has_a_connectionHistory_key_always_null_when_not_asked_and_the_version_stays_1()
    {
        PostgresDiagnosticsSnapshot asked = new()
        {
            SchemaVersion = DiagnosticsJson.SchemaVersion,
            Database = new PostgresDatabaseRef("default", "app"),
            ConnectionHistory = DiagnosticsSnapshotBuilder.ConnectionHistory(
                await ConnectionHistoryLookup.ReadAsync(Reader(new FixedStore(Day())), "db:5432", names: true, since: null, Token), includeActivity: true),
        };

        using JsonDocument notAsked = JsonDocument.Parse((asked with { ConnectionHistory = null }).ToJson());
        using JsonDocument json = JsonDocument.Parse(asked.ToJson());

        notAsked.RootElement.GetProperty("connectionHistory").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1, "a key was added, none removed or changed");

        JsonElement section = json.RootElement.GetProperty("connectionHistory");
        section.GetProperty("outcome").GetString().Should().Be("read");
        section.GetProperty("server").GetString().Should().Be("db:5432");
        section.GetProperty("windowSeconds").GetInt64().Should().Be(7 * 24 * 3600);
        section.GetProperty("samples").GetInt32().Should().Be(4);
        section.GetProperty("pendingSamples").GetInt32().Should().Be(0);
        section.GetProperty("instanceChanges").GetInt32().Should().Be(1);
        section.GetProperty("peak").GetInt64().Should().Be(90);
        section.GetProperty("peakAt").GetString().Should().Be("2026-11-02T04:30:00.0000000+00:00");
        section.GetProperty("clientLimit").GetInt32().Should().Be(100);
        section.GetProperty("clientLimitNow").GetInt32().Should().Be(200);
        section.GetProperty("byApplication")[0].GetProperty("applicationName").GetString().Should().Be("api");
        section.GetProperty("byApplication")[1].GetProperty("applicationName").ValueKind.Should().Be(JsonValueKind.Null, "sessions that set no name");
    }

    // ----------------------------------------------------------------------------------------------
    // Stores
    // ----------------------------------------------------------------------------------------------

    private class FixedStore(IReadOnlyList<PostgresConnectionSample> samples) : IPostgresConnectionHistoryStore
    {
        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public virtual ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken) =>
            ValueTask.FromResult(samples);
    }

    private sealed class CountingStore(IReadOnlyList<PostgresConnectionSample> samples) : FixedStore(samples)
    {
        public int Reads { get; private set; }

        public override ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken)
        {
            Reads++;
            return base.ReadAsync(server, since, cancellationToken);
        }
    }

    private sealed class ThrowingStore(Exception exception) : IPostgresConnectionHistoryStore
    {
        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class HangingStore : IPostgresConnectionHistoryStore
    {
        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }
}
