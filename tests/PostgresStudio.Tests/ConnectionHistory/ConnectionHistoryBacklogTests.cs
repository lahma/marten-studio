using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.ConnectionHistory;

/// <summary>
/// Issue #46 without a database: the samples a store refused are held, per server, and stored oldest first before the next one; the
/// first failure stops the flush; an hour at the interval is the bound; and a page reads the held samples beside the store's.
/// </summary>
public class ConnectionHistoryBacklogTests
{
    private const string Server = "db:5432";

    private static readonly DateTimeOffset Start = new(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static PostgresConnectionSample Sample(int minute, string server = Server) =>
        new() { Server = server, At = Start.AddMinutes(minute), Connections = minute };

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IStudioSourceProvider>(new NoSources())
            .BuildServiceProvider();

        public Rig(TimeSpan? interval = null, Func<CancellationToken, ValueTask<bool>>? shouldSample = null)
        {
            Clock = new ManualTimeProvider(Start);
            Options = Microsoft.Extensions.Options.Options.Create(new PostgresConnectionHistoryOptions
            {
                Interval = interval ?? TimeSpan.FromMinutes(1),
                ShouldSample = shouldSample,
            });
            Store = new FlakyStore();
            Sampler = new ConnectionHistorySampler(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options,
                Store,
                PostgresStudioSettings.Of(new PostgresStudioOptions()),
                Logs,
                new StudioLogThrottle(Clock),
                Clock);
        }

        public ManualTimeProvider Clock { get; }

        public IOptions<PostgresConnectionHistoryOptions> Options { get; }

        public FlakyStore Store { get; }

        public RecordingLogger<ConnectionHistorySampler> Logs { get; } = new();

        public ConnectionHistorySampler Sampler { get; }

        public async ValueTask DisposeAsync()
        {
            Sampler.Dispose();
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_failed_append_is_held_and_the_next_round_stores_it_before_the_new_sample_in_order()
    {
        await using Rig rig = new();

        rig.Store.Refusing = true;
        (await rig.Sampler.StoreAsync(Server, Sample(1), Token)).Should().Be(0);
        (await rig.Sampler.StoreAsync(Server, null, Token)).Should().Be(0, "a round with no new sample still offers the backlog, and the store still refuses");
        rig.Sampler.Pending(Server).Should().Be(1);

        rig.Store.Refusing = false;
        (await rig.Sampler.StoreAsync(Server, Sample(2), Token)).Should().Be(2, "the held sample and the new one");

        rig.Store.Stored.Select(static x => x.Connections).Should().Equal([1L, 2L], "oldest first, the new sample last");
        rig.Sampler.Pending(Server).Should().Be(0);
    }

    [Fact]
    public async Task The_first_failure_stops_the_flush_and_keeps_that_sample_and_every_later_one_in_order()
    {
        await using Rig rig = new();

        rig.Store.Refusing = true;
        await rig.Sampler.StoreAsync(Server, Sample(1), Token);
        await rig.Sampler.StoreAsync(Server, Sample(2), Token);
        await rig.Sampler.StoreAsync(Server, Sample(3), Token);
        rig.Store.Attempts = 0;

        rig.Store.AcceptNext = 1;
        (await rig.Sampler.StoreAsync(Server, Sample(4), Token)).Should().Be(1, "the first of the backlog was stored, the second refused");

        rig.Store.Attempts.Should().Be(2, "the flush stopped at the first failure: nothing after it, the new sample included, was tried");
        rig.Store.Stored.Select(static x => x.Connections).Should().Equal([1L]);
        rig.Sampler.PendingSamples(Server).Select(static x => x.Connections).Should().Equal([2L, 3L, 4L], "in order, the new sample last");
    }

    [Fact]
    public async Task Past_its_bound_the_backlog_drops_the_oldest_and_says_so_once_per_throttle_window()
    {
        await using Rig rig = new(interval: TimeSpan.FromMinutes(30));

        ConnectionHistorySampler.PendingBound(TimeSpan.FromMinutes(30)).Should().Be(2);
        rig.Store.Refusing = true;

        await rig.Sampler.StoreAsync(Server, Sample(1), Token);
        await rig.Sampler.StoreAsync(Server, Sample(2), Token);
        rig.Logs.Of(9464).Should().BeEmpty("nothing was dropped yet");

        await rig.Sampler.StoreAsync(Server, Sample(3), Token);
        await rig.Sampler.StoreAsync(Server, Sample(4), Token);

        rig.Sampler.PendingSamples(Server).Select(static x => x.Connections).Should().Equal([3L, 4L], "the oldest went first");
        rig.Logs.Of(9464).Select(static x => x.Level).Should().Equal([LogLevel.Warning, LogLevel.Debug], "a Warning once per throttle window, Debug in between");
        rig.Logs.Of(9464)[0].Message.Should().Contain("dropped 1 of the oldest samples of server db:5432");
    }

    [Fact]
    public async Task When_the_backlog_is_written_the_sampler_says_so_once_with_how_many_it_held()
    {
        await using Rig rig = new();

        rig.Store.Refusing = true;
        await rig.Sampler.StoreAsync(Server, Sample(1), Token);
        await rig.Sampler.StoreAsync(Server, Sample(2), Token);
        rig.Store.Refusing = false;

        await rig.Sampler.StoreAsync(Server, Sample(3), Token);
        await rig.Sampler.StoreAsync(Server, Sample(4), Token);

        rig.Logs.Of(9463).Should().ContainSingle().Which.Message.Should().Contain("stored 2 samples of server db:5432 it had held since");
        rig.Logs.Of(9463)[0].Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task A_sample_that_could_not_be_read_is_lost_but_the_backlog_is_still_flushed()
    {
        await using Rig rig = new();

        rig.Store.Refusing = true;
        await rig.Sampler.StoreAsync(Server, Sample(1), Token);
        rig.Store.Refusing = false;

        (await rig.Sampler.StoreAsync(Server, null, Token)).Should().Be(1);

        rig.Store.Stored.Select(static x => x.Connections).Should().Equal([1L]);
        rig.Sampler.Pending(Server).Should().Be(0);
    }

    [Fact]
    public async Task While_the_host_says_another_process_samples_the_backlog_is_kept_and_not_flushed()
    {
        bool sample = true;
        await using Rig rig = new(shouldSample: _ => ValueTask.FromResult(sample));

        rig.Store.Refusing = true;
        await rig.Sampler.StoreAsync(Server, Sample(1), Token);
        rig.Store.Refusing = false;
        rig.Store.Attempts = 0;

        sample = false;
        (await rig.Sampler.SampleOnceAsync(Token)).Should().Be(0);

        rig.Store.Attempts.Should().Be(0, "the sampler does nothing in such a round");
        rig.Sampler.Pending(Server).Should().Be(1);
    }

    [Fact]
    public async Task A_server_that_left_the_list_still_has_its_backlog_offered_to_the_store_at_the_next_round()
    {
        await using Rig rig = new();

        rig.Store.Refusing = true;
        await rig.Sampler.StoreAsync(Server, Sample(1), Token);
        await rig.Sampler.StoreAsync(Server, Sample(2), Token);
        rig.Store.Refusing = false;

        // The round's list is empty: the server is not in it, and the backlog is written all the same.
        (await rig.Sampler.SampleOnceAsync(Token)).Should().Be(2);

        rig.Store.Stored.Select(static x => x.Connections).Should().Equal([1L, 2L]);
        rig.Sampler.Pending(Server).Should().Be(0);
        rig.Logs.Of(9463).Should().ContainSingle();
    }

    [Fact]
    public async Task Servers_are_held_apart_and_matched_ignoring_case()
    {
        await using Rig rig = new();

        rig.Store.Refusing = true;
        await rig.Sampler.StoreAsync("db:5432", Sample(1), Token);
        await rig.Sampler.StoreAsync("other:5432", Sample(2, "other:5432"), Token);

        rig.Sampler.Pending("DB:5432").Should().Be(1);
        rig.Sampler.PendingSamples("other:5432").Select(static x => x.Connections).Should().Equal([2L]);
        rig.Sampler.Pending("nowhere:5432").Should().Be(0);
    }

    [Theory]
    [InlineData(15, 240)]
    [InlineData(60, 60)]
    [InlineData(3600, 1)]
    [InlineData(7200, 1)]
    public void The_bound_is_an_hour_of_samples_at_the_interval(int seconds, int bound) =>
        ConnectionHistorySampler.PendingBound(TimeSpan.FromSeconds(seconds)).Should().Be(bound);

    [Fact]
    public async Task A_page_reading_the_backlog_while_a_round_changes_it_never_throws()
    {
        await using Rig rig = new(interval: TimeSpan.FromSeconds(15));
        rig.Store.Refusing = true;
        using CancellationTokenSource stop = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> reader = Task.Run(
            () =>
            {
                int reads = 0;

                while (!stop.IsCancellationRequested)
                {
                    rig.Sampler.PendingSamples(Server).Count.Should().BeLessThanOrEqualTo(240);
                    rig.Sampler.Pending(Server);
                    reads++;
                    started.TrySetResult();
                    Thread.Yield();
                }

                return reads;
            },
            Token);

        // The writer starts only once the reader has made its first pass, so the two really overlap.
        await started.Task.WaitAsync(Token);

        for (int i = 0; i < 400; i++)
        {
            rig.Store.Refusing = i % 50 < 40;
            await rig.Sampler.StoreAsync(Server, Sample(i), Token);
        }

        await stop.CancelAsync();
        (await reader).Should().BePositive("the reader ran while the rounds changed the backlog");
    }

    [Fact]
    public async Task A_page_reads_what_the_store_holds_merged_with_what_this_process_holds_in_time_order()
    {
        ConnectionHistoryReader reader = Reader(out FlakyStore store);
        await store.AppendAsync(Sample(1), Token);
        await store.AppendAsync(Sample(4), Token);

        IReadOnlyList<PostgresConnectionSample> read = await reader.ReadWithPendingAsync(Server, [Sample(2), Sample(3), Sample(4)], Token);

        read.Select(static x => x.Connections).Should().Equal([1L, 2L, 3L, 4L], "ordered by time, and a sample the store holds too is counted once");
    }

    [Fact]
    public async Task A_page_whose_store_cannot_be_read_gets_the_held_samples_alone()
    {
        ConnectionHistoryReader reader = Reader(out FlakyStore store);
        store.FailReads = true;

        IReadOnlyList<PostgresConnectionSample> read = await reader.ReadWithPendingAsync(Server, [Sample(3), Sample(2)], Token);

        read.Select(static x => x.Connections).Should().Equal([2L, 3L]);
        ConnectionHistoryView.Of(read, names: false)!.Samples.Should().Be(2);

        Func<Task> none = () => reader.ReadWithPendingAsync(Server, [], Token);
        await none.Should().ThrowAsync<InvalidOperationException>("with nothing held there is nothing to show in the store's place");
    }

    /// <summary>
    /// The one lookup Holders, the diagnostics API and C15 read through merges the sampler's held samples (the 1.0.1 integration of
    /// #46 and #47): the peak of a burst whose appends all failed is in the view, and the section's pending count is theirs.
    /// </summary>
    [Fact]
    public async Task The_shared_lookup_sees_the_samples_the_sampler_holds()
    {
        await using Rig rig = new();

        rig.Store.AcceptNext = 1;
        (await rig.Sampler.StoreAsync(Server, Sample(1), Token)).Should().Be(1);
        (await rig.Sampler.StoreAsync(Server, Sample(7), Token)).Should().Be(0, "the store refuses from here on");
        (await rig.Sampler.StoreAsync(Server, Sample(2), Token)).Should().Be(0);

        ConnectionHistoryReader reader = new(
            rig.Store,
            rig.Options,
            PostgresStudioSettings.Of(new PostgresStudioOptions()),
            rig.Clock);

        ConnectionHistoryRead read = await ConnectionHistoryLookup.ReadAsync(reader, rig.Sampler, Server, names: false, since: null, Token);

        read.Outcome.Should().Be(PostgresReadOutcome.Read);
        read.PendingSamples.Should().Be(2);
        read.View!.Samples.Should().Be(3);
        read.View.Peak.Should().Be(7, "the peak was in a sample the store refused");

        rig.Store.FailReads = true;
        ConnectionHistoryRead down = await ConnectionHistoryLookup.ReadAsync(
            new ConnectionHistoryReader(rig.Store, rig.Options, PostgresStudioSettings.Of(new PostgresStudioOptions()), rig.Clock),
            rig.Sampler,
            Server,
            names: false,
            since: null,
            Token);

        down.Outcome.Should().Be(PostgresReadOutcome.Read, "the held samples stand in for a store that cannot be read");
        down.View!.Peak.Should().Be(7);
    }

    /// <summary>A source provider that lists no source: every round's list of servers is empty.</summary>
    private sealed class NoSources : IStudioSourceProvider
    {
        public long Generation => 0;

        public IReadOnlyList<StudioSourceRegistration> Registrations() => [];

        public StudioSourceRegistration? Find(string? key) => null;

        public StoreAvailability TryResolve(StudioSourceRegistration registration, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStudioSource? source) =>
            throw new NotSupportedException("There is no source to resolve.");

        public void Invalidate()
        {
        }

        public IAsyncEnumerable<StudioListedDatabase> ListUnattendedAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("The sampler lists through the sources, never unattended.");

        public ValueTask<StudioUnattendedResolution> ResolveUnattendedAsync(string sourceKey, string databaseId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The sampler lists through the sources, never unattended.");
    }

    private static ConnectionHistoryReader Reader(out FlakyStore store)
    {
        store = new FlakyStore();
        return new ConnectionHistoryReader(
            store,
            Microsoft.Extensions.Options.Options.Create(new PostgresConnectionHistoryOptions()),
            PostgresStudioSettings.Of(new PostgresStudioOptions()),
            new ManualTimeProvider(Start));
    }

    /// <summary>A store that refuses on demand, refuses after a number of accepts, and remembers what it kept.</summary>
    private sealed class FlakyStore : IPostgresConnectionHistoryStore
    {
        private readonly List<PostgresConnectionSample> stored = [];

        public bool Refusing { get; set; }

        public bool FailReads { get; set; }

        /// <summary>When above zero, that many appends are accepted before the next one is refused.</summary>
        public int AcceptNext { get; set; }

        public int Attempts { get; set; }

        public IReadOnlyList<PostgresConnectionSample> Stored => stored;

        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken)
        {
            Attempts++;

            if (AcceptNext > 0)
            {
                AcceptNext--;
                stored.Add(sample);

                if (AcceptNext == 0)
                {
                    Refusing = true;
                }

                return ValueTask.CompletedTask;
            }

            if (Refusing)
            {
                throw new InvalidOperationException("The pool is exhausted.");
            }

            stored.Add(sample);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken) =>
            FailReads
                ? throw new InvalidOperationException("The store is down.")
                : ValueTask.FromResult<IReadOnlyList<PostgresConnectionSample>>([.. stored.Where(x => x.At >= since)]);
    }

    [Fact]
    public async Task Stop_after_Dispose_does_not_throw()
    {
        await using Rig rig = new();
        await rig.Sampler.SampleOnceAsync(Token);

        rig.Sampler.Dispose();

        Func<Task> stop = () => rig.Sampler.StopAsync(Token);
        await stop.Should().NotThrowAsync("the semaphore is never disposed (issue #50)");
        await rig.Sampler.StopAsync(Token);
    }

    [Fact]
    public async Task DisposeAsync_twice_does_not_throw_and_a_stop_after_it_does_not_either()
    {
        await using Rig rig = new();
        rig.Store.Refusing = true;
        await rig.Sampler.StoreAsync(Server, Sample(1), Token);
        rig.Sampler.Pending(Server).Should().Be(1);

        await rig.Sampler.DisposeAsync();
        await rig.Sampler.DisposeAsync();

        rig.Sampler.Pending(Server).Should().Be(0, "a disposed sampler has no next round to store the backlog in");
        Func<Task> stop = () => rig.Sampler.StopAsync(Token);
        await stop.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_round_in_flight_when_Dispose_runs_finishes_without_throwing()
    {
        TaskCompletionSource inRound = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource proceed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // ShouldSample is asked inside the round, after it took its semaphore: the round is in flight until the test lets it go.
        await using Rig rig = new(shouldSample: async _ =>
        {
            inRound.TrySetResult();
            await proceed.Task;
            return true;
        });

        Task<int> round = rig.Sampler.SampleOnceAsync(Token);
        await inRound.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        rig.Sampler.Dispose();
        proceed.SetResult();

        Func<Task> finished = () => round;
        await finished.Should().NotThrowAsync("the round's release of its semaphore does not meet a disposed one (issue #50)");
    }

    /// <summary>A logger that keeps what it was asked to write.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(int Id, LogLevel Level, string Message)> entries = [];

        public IReadOnlyList<(int Id, LogLevel Level, string Message)> Of(int id)
        {
            lock (entries)
            {
                return [.. entries.Where(x => x.Id == id)];
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add((eventId.Id, logLevel, formatter(state, exception)));
            }
        }
    }
}
