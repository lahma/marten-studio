using PostgresStudio.Services.Database;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Database;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The D01 Explain's shared read (H2 review, plan §8): single-flight per store and database, kept for a minute across
/// circuits, a failure never kept - and a session's statement masked per visitor before it reaches a page.
/// </summary>
public class HighWaterExplainCacheTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now = now.Add(by);
    }

    private static HighWaterExplainRead Read(long hidden) => new(null, null, [], [], hidden, []);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Presses_from_many_circuits_within_a_minute_share_one_read()
    {
        ManualClock clock = new(DateTimeOffset.UnixEpoch);
        HighWaterExplainCache cache = new(clock);
        int reads = 0;
        TaskCompletionSource<HighWaterExplainRead> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<HighWaterExplainRead> Press() => cache.GetAsync("default|db", _ =>
        {
            Interlocked.Increment(ref reads);
            return gate.Task;
        }, Token);

        Task<HighWaterExplainRead>[] presses = [Press(), Press(), Press()];
        gate.SetResult(Read(7));
        HighWaterExplainRead[] answers = await Task.WhenAll(presses);

        reads.Should().Be(1, "three circuits pressing while the read is in flight cost one read");
        answers.Should().OnlyContain(x => ReferenceEquals(x, answers[0]));

        clock.Advance(TimeSpan.FromSeconds(59));
        (await Press()).Should().BeSameAs(answers[0], "a minute is one read");
        reads.Should().Be(1);
    }

    [Fact]
    public async Task After_a_minute_the_next_press_reads_again()
    {
        ManualClock clock = new(DateTimeOffset.UnixEpoch);
        HighWaterExplainCache cache = new(clock);
        int reads = 0;

        Task<HighWaterExplainRead> Press() => cache.GetAsync("default|db", _ => Task.FromResult(Read(Interlocked.Increment(ref reads))), Token);

        (await Press()).HiddenWriters.Should().Be(1);
        clock.Advance(HighWaterExplainCache.TimeToLive);
        (await Press()).HiddenWriters.Should().Be(2);
    }

    [Fact]
    public async Task Each_database_has_a_read_of_its_own()
    {
        HighWaterExplainCache cache = new(new ManualClock(DateTimeOffset.UnixEpoch));

        (await cache.GetAsync("default|a", _ => Task.FromResult(Read(1)), Token)).HiddenWriters.Should().Be(1);
        (await cache.GetAsync("default|b", _ => Task.FromResult(Read(2)), Token)).HiddenWriters.Should().Be(2);
    }

    [Fact]
    public async Task A_read_that_failed_is_not_kept()
    {
        HighWaterExplainCache cache = new(new ManualClock(DateTimeOffset.UnixEpoch));

        Func<Task> failing = () => cache.GetAsync("default|db", _ => Task.FromException<HighWaterExplainRead>(new InvalidOperationException("boom")), Token);
        await failing.Should().ThrowAsync<InvalidOperationException>();

        (await cache.GetAsync("default|db", _ => Task.FromResult(Read(3)), Token)).HiddenWriters.Should().Be(3, "the next press asks again");
    }

    /// <summary>One tab navigating away gives up its wait, not the read the others share.</summary>
    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_shared_read()
    {
        HighWaterExplainCache cache = new(new ManualClock(DateTimeOffset.UnixEpoch));
        TaskCompletionSource<HighWaterExplainRead> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken shared = new(canceled: true);

        using CancellationTokenSource leaving = new();
        Task<HighWaterExplainRead> left = cache.GetAsync("default|db", token =>
        {
            shared = token;
            return gate.Task;
        }, leaving.Token);
        Task<HighWaterExplainRead> staying = cache.GetAsync("default|db", _ => throw new InvalidOperationException("never run"), Token);

        await leaving.CancelAsync();
        Func<Task> wait = () => left;
        await wait.Should().ThrowAsync<OperationCanceledException>();

        shared.CanBeCanceled.Should().BeFalse("the shared read runs on no caller's token");
        gate.SetResult(Read(9));
        (await staying).HiddenWriters.Should().Be(9);
    }

    // ---- The statement a visitor is shown ---------------------------------------------------------------------------

    private static readonly StatementScreen HrWithheld = new(new HashSet<string>(StringComparer.Ordinal) { "hr" }, null, false, []);

    [Fact]
    public void A_withheld_schema_is_masked_in_a_statement()
    {
        ExplainQueryText.Present("select * from hr.payroll where id = 1", truncated: false, HrWithheld)
            .Should().Be("select * from " + WithheldNames.Token + ".payroll where id = 1");
    }

    /// <summary>A cut statement loses the name the cut left half-written: <c>from hr_payr</c> is the start of a withheld name.</summary>
    [Fact]
    public void A_cut_statement_drops_its_half_written_tail_before_it_is_masked()
    {
        ExplainQueryText.Present("select * from hr.payroll p join hr_payr", truncated: true, HrWithheld)
            .Should().Be("select * from " + WithheldNames.Token + ".payroll p join" + ExplainQueryText.Cut);

        ExplainQueryText.Present("select 'half a lit", truncated: true, HrWithheld).Should().Be("select" + ExplainQueryText.Cut);
    }

    /// <summary>When the visitor's withheld schemas cannot be read, there is no text: never one shown unmasked.</summary>
    [Fact]
    public void Without_the_withheld_schemas_the_statement_is_withheld()
    {
        ExplainQueryText.Present("select * from hr.payroll", truncated: false, StatementScreen.Unchecked(false)).Should().Be(ExplainQueryText.Withheld);
        ExplainQueryText.Present(null, truncated: false, StatementScreen.Unchecked(false)).Should().BeNull();
    }

    /// <summary>
    /// One rule with Database › Sessions (<see cref="SessionQueryText" />): a statement naming a hidden type's table, one
    /// no store declares while hiding is configured, or a table of a store whose policy refuses the visitor is withheld,
    /// and the Explain says why in brackets.
    /// </summary>
    [Fact]
    public void The_Explain_withholds_a_statement_by_the_Sessions_pages_own_rules()
    {
        StatementScreen screen = new(new HashSet<string>(), DatabaseTestStores.Classifier(), true, ["mt_doc_dbinvoice"]);

        ExplainQueryText.Present("select d.data from studio_db.mt_doc_dbsecret as d", truncated: false, screen)
            .Should().Be("(" + SessionQueryText.HiddenTypeNote + ")");
        ExplainQueryText.Present("select d.data from studio_db.mt_doc_dbpayroll as d", truncated: false, screen)
            .Should().Be("(" + SessionQueryText.UndeclaredTableNote + ")");
        ExplainQueryText.Present("select d.data from other_db.mt_doc_dbinvoice as d", truncated: false, screen)
            .Should().Be("(" + SessionQueryText.RefusedStoreNote + ")");
        ExplainQueryText.Present("select d.data from studio_db.mt_doc_dbcustomer as d", truncated: false, screen)
            .Should().Be("select d.data from studio_db.mt_doc_dbcustomer as d", "a statement naming only what the visitor may see is shown");
    }
}
