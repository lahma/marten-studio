using Microsoft.Extensions.Options;

using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.ConnectionHistory;

/// <summary>
/// Issue #51 without a database: the reader keeps each server's series and asks the store only for what is new - the whole window the
/// first time, after an eviction and every ten minutes; the newest sample less five minutes otherwise - de-duplicated, trimmed to the
/// window, kept through a failed refresh, bounded in servers and in idle time, and single-flight.
/// </summary>
public class ConnectionHistoryReaderTests
{
    private const string Server = "db:5432";

    private static readonly DateTimeOffset Start = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PostgresConnectionSample Sample(DateTimeOffset at, long connections = 1, string server = Server) =>
        new() { Server = server, At = at, Connections = connections };

    private sealed class Rig
    {
        public Rig(TimeSpan? retention = null)
        {
            Clock = new ManualTimeProvider(Start);
            Retention = retention ?? TimeSpan.FromDays(7);
            Reader = new ConnectionHistoryReader(
                Store,
                Options.Create(new PostgresConnectionHistoryOptions { Retention = Retention }),
                PostgresStudioSettings.Of(new PostgresStudioOptions()),
                Clock);
        }

        public ManualTimeProvider Clock { get; }

        public TimeSpan Retention { get; }

        public SeriesStore Store { get; } = new();

        public ConnectionHistoryReader Reader { get; }

        public DateTimeOffset Now => Clock.GetUtcNow();

        public Task<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server = Server) => Reader.ReadAsync(server, Token);

        /// <summary>Moves the clock, so the reader's cache has expired for the next read.</summary>
        public void Pass(TimeSpan by) => Clock.Advance(by);
    }

    [Fact]
    public async Task The_first_read_asks_for_the_whole_window()
    {
        Rig rig = new();
        rig.Store.Held.Add(Sample(Start.AddMinutes(-10)));

        (await rig.ReadAsync()).Should().HaveCount(1);

        rig.Store.Reads.Should().ContainSingle().Which.Since.Should().Be(Start - rig.Retention);
    }

    [Fact]
    public async Task A_refresh_asks_since_the_newest_sample_less_five_minutes_and_adds_only_the_new_ones()
    {
        Rig rig = new();
        PostgresConnectionSample first = Sample(Start.AddMinutes(-10), 1);
        PostgresConnectionSample newest = Sample(Start.AddMinutes(-1), 2);
        rig.Store.Held.AddRange([first, newest]);
        await rig.ReadAsync();

        rig.Pass(ConnectionHistoryReader.CacheFor);
        PostgresConnectionSample added = Sample(Start.AddSeconds(25), 3);
        rig.Store.Held.Add(added);

        // The store re-reads the overlap and hands back its own copy of the newest sample: the copy held wins.
        rig.Store.Held[1] = Sample(newest.At, 99);
        IReadOnlyList<PostgresConnectionSample> samples = await rig.ReadAsync();

        rig.Store.Reads.Should().HaveCount(2);
        rig.Store.Reads[1].Since.Should().Be(newest.At - ConnectionHistoryReader.Overlap);
        samples.Select(static x => x.Connections).Should().Equal([1L, 2L, 3L], "the newest sample is counted once, and the copy held wins");
        samples[1].Should().BeSameAs(newest);
        samples[2].Should().BeSameAs(added);
    }

    [Fact]
    public async Task A_read_within_the_cache_time_makes_no_store_call_and_samples_older_than_the_window_are_trimmed()
    {
        Rig rig = new(retention: TimeSpan.FromHours(1));
        rig.Store.Held.AddRange([Sample(Start.AddMinutes(-59)), Sample(Start.AddMinutes(-30), 2)]);
        await rig.ReadAsync();
        await rig.ReadAsync();
        rig.Store.Reads.Should().HaveCount(1);

        rig.Pass(TimeSpan.FromMinutes(2));
        rig.Store.Held.Add(Sample(rig.Now, 3));
        IReadOnlyList<PostgresConnectionSample> samples = await rig.ReadAsync();

        samples.Select(static x => x.Connections).Should().Equal([2L, 3L], "the sample of 61 minutes ago left the window");
    }

    [Fact]
    public async Task A_full_read_every_ten_minutes_finds_a_sample_that_landed_late_and_keeps_the_series_ordered()
    {
        Rig rig = new();
        rig.Store.Held.Add(Sample(Start.AddMinutes(-1), 2));
        await rig.ReadAsync();

        // Another process's backlog, flushed after a handover: older than the overlap reaches.
        rig.Store.Held.Add(Sample(Start.AddMinutes(-30), 1));
        rig.Pass(ConnectionHistoryReader.CacheFor);
        (await rig.ReadAsync()).Should().HaveCount(1, "a refresh does not look that far back");

        rig.Pass(ConnectionHistoryReader.FullReadEvery);
        IReadOnlyList<PostgresConnectionSample> samples = await rig.ReadAsync();

        rig.Store.Reads[^1].Since.Should().Be(rig.Now - rig.Retention, "ten minutes after the last whole read, the whole window again");
        samples.Select(static x => x.Connections).Should().Equal([1L, 2L]);
    }

    [Fact]
    public async Task A_late_sample_inside_the_overlap_is_added_in_order()
    {
        Rig rig = new();
        rig.Store.Held.Add(Sample(Start.AddMinutes(-1), 2));
        await rig.ReadAsync();

        rig.Store.Held.Add(Sample(Start.AddMinutes(-3), 1));
        rig.Pass(ConnectionHistoryReader.CacheFor);

        (await rig.ReadAsync()).Select(static x => x.Connections).Should().Equal([1L, 2L]);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_series_and_the_next_one_retries_from_the_same_point()
    {
        Rig rig = new();
        PostgresConnectionSample newest = Sample(Start.AddMinutes(-1), 1);
        rig.Store.Held.Add(newest);
        await rig.ReadAsync();

        rig.Pass(ConnectionHistoryReader.CacheFor);
        rig.Store.Failing = true;
        Func<Task> failing = () => rig.ReadAsync();
        await failing.Should().ThrowAsync<InvalidOperationException>("the caller decides what a failed refresh means");

        rig.Store.Failing = false;
        rig.Store.Held.Add(Sample(rig.Now, 2));
        IReadOnlyList<PostgresConnectionSample> samples = await rig.ReadAsync();

        rig.Store.Reads.Select(static x => x.Since).Should().Equal(
            Start - rig.Retention,
            newest.At - ConnectionHistoryReader.Overlap,
            newest.At - ConnectionHistoryReader.Overlap);
        samples.Select(static x => x.Connections).Should().Equal([1L, 2L], "the series survived the failure");
    }

    [Fact]
    public async Task A_series_not_read_for_ten_minutes_is_dropped_at_the_next_read_of_any_server()
    {
        Rig rig = new();
        await rig.ReadAsync("a:5432");
        rig.Pass(ConnectionHistoryReader.IdleFor - TimeSpan.FromSeconds(1));
        await rig.ReadAsync("b:5432");
        rig.Reader.Servers.Should().Be(2, "a was read just under ten minutes ago");

        rig.Pass(TimeSpan.FromSeconds(1));
        await rig.ReadAsync("b:5432");
        rig.Reader.Servers.Should().Be(1, "a was not read for ten minutes, and b's read dropped it");
    }

    [Fact]
    public async Task Past_sixty_four_servers_the_least_recently_read_is_dropped_and_read_whole_again()
    {
        Rig rig = new();
        rig.Store.Held.Add(Sample(Start.AddMinutes(-1), 1, "s0:5432"));

        for (int i = 0; i < ConnectionHistoryReader.MaxServers + 1; i++)
        {
            await rig.ReadAsync($"s{i}:5432");
            rig.Pass(TimeSpan.FromSeconds(1));
        }

        rig.Reader.Servers.Should().Be(ConnectionHistoryReader.MaxServers);

        await rig.ReadAsync("s0:5432");

        rig.Store.Reads[^1].Server.Should().Be("s0:5432");
        rig.Store.Reads[^1].Since.Should().Be(rig.Now - rig.Retention, "its series was dropped, so this is a first read again");
    }

    [Fact]
    public async Task Concurrent_readers_of_one_server_make_one_store_call()
    {
        Rig rig = new();
        rig.Store.Held.Add(Sample(Start.AddMinutes(-1)));
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Store.Gate = gate;

        Task<IReadOnlyList<PostgresConnectionSample>>[] readers = [.. Enumerable.Range(0, 12).Select(_ => rig.ReadAsync())];
        gate.SetResult();
        IReadOnlyList<PostgresConnectionSample>[] results = await Task.WhenAll(readers);

        rig.Store.Reads.Should().ContainSingle();
        results.Should().OnlyContain(static x => x.Count == 1);
    }

    private sealed class SeriesStore : IPostgresConnectionHistoryStore
    {
        private readonly Lock gate = new();
        private readonly List<(string Server, DateTimeOffset Since)> reads = [];

        public List<PostgresConnectionSample> Held { get; } = [];

        public bool Failing { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public IReadOnlyList<(string Server, DateTimeOffset Since)> Reads
        {
            get
            {
                lock (gate)
                {
                    return [.. reads];
                }
            }
        }

        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken)
        {
            Held.Add(sample);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                reads.Add((server, since));
            }

            if (Failing)
            {
                throw new InvalidOperationException("The store is down.");
            }

            if (Gate is { } waiting)
            {
                await waiting.Task.Bounded();
            }

            return [.. Held.Where(x => x.Server == server && x.At >= since).OrderBy(static x => x.At)];
        }
    }
}
