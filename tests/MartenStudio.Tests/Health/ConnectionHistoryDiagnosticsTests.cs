using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio;
using PostgresStudio.Services.ConnectionHistory;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The connection history through the diagnostics API and check C15, over the health harness (1.0.1, issue #47): a snapshot
/// that asks for it, whatever the history's state, with the applications behind <c>IncludeActivity</c>; C15 not applicable
/// without a history; and a store that fails or hangs a value, never an exception. The harness's store refuses every connection, so
/// what C15 finds from samples is proved live (<c>ConnectionHistoryLiveTests</c>) and over stated facts (<c>ConnectionNearMissCheckTests</c>).
/// </summary>
public class ConnectionHistoryDiagnosticsTests
{
    private const string Application = "SENTINEL-APP-billing";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static IPostgresDiagnostics Diagnostics(HealthHarness harness) => harness.Services.GetRequiredService<IPostgresDiagnostics>();

    private static PostgresDatabaseRef Database(HealthHarness harness) => new("default", harness.Scope.DatabaseId);

    private static PostgresDiagnosticsRequest Asked(bool activity) =>
        PostgresDiagnosticsRequest.FindingsOnly with { ConnectionHistory = true, IncludeActivity = activity };

    private static HealthHarness Harness(IPostgresConnectionHistoryStore? store) => HealthHarness.Create(
        // A second catalog check that finds nothing: with C15 alone, its own failure would be every catalog check failing - a database
        // the sweep could not reach - which a failing history store is not.
        checks: [typeof(ConnectionNearMissCheck), typeof(ScriptedCatalogCheck)],
        before: store is null
            ? null
            : services =>
            {
                services.AddPostgresConnectionHistory();
                services.AddSingleton<IPostgresConnectionHistoryStore>(store);
            });

    private static PostgresConnectionSample Sample(HealthHarness harness, TimeSpan ago, long connections, int limit = 100, params (string? Application, int Total)[] applications) => new()
    {
        At = harness.Clock.GetUtcNow() - ago,
        Server = ConnectionHistoryLookup.ServerOf(harness.Resolved)!,
        Instance = "a",
        Connections = connections,
        ClientLimit = limit,
        ByApplication = [.. applications.Select(static x => new PostgresConnectionCounts { ApplicationName = x.Application, Total = x.Total })],
    };

    private static PostgresCheck C15(PostgresDiagnosticsSnapshot snapshot) => snapshot.Checks.Single(static x => x.Code == "C15");

    [Fact]
    public async Task Without_a_history_registration_the_section_and_C15_are_not_applicable()
    {
        await using HealthHarness harness = Harness(null);

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Asked(activity: false), Token);

        snapshot.Outcome.Should().Be(PostgresReadOutcome.Read, snapshot.Reason);
        snapshot.ConnectionHistory.Should().NotBeNull();
        snapshot.ConnectionHistory!.Outcome.Should().Be(PostgresReadOutcome.NotApplicable);
        snapshot.ConnectionHistory.Reason.Should().Contain("AddPostgresConnectionHistory");
        C15(snapshot).Outcome.Should().Be(PostgresReadOutcome.NotApplicable, "no history is kept, so there is no peak to judge");
        snapshot.Findings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_section_not_asked_for_is_null_and_FindingsOnly_never_reads_the_history()
    {
        CountingStore store = new();
        await using HealthHarness harness = Harness(store);

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(Database(harness), PostgresDiagnosticsRequest.FindingsOnly, Token);

        snapshot.ConnectionHistory.Should().BeNull();
        snapshot.Withheld.Should().NotContainKey("connectionHistory.byApplication");
    }

    [Fact]
    public async Task The_section_summarizes_the_window_and_the_applications_need_IncludeActivity()
    {
        CountingStore store = new();
        await using HealthHarness harness = Harness(store);
        store.Samples =
        [
            Sample(harness, TimeSpan.FromHours(3), 12, 100, (Application, 8)),
            Sample(harness, TimeSpan.FromHours(2), 85, 100, (Application, 60), ("reports", 20), (null, 5)),
            Sample(harness, TimeSpan.FromHours(1), 30, 100, (Application, 20)),
        ];

        PostgresDiagnosticsSnapshot without = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Asked(activity: false), Token);
        PostgresDiagnosticsSnapshot with = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Asked(activity: true), Token);

        PostgresConnectionHistorySection section = without.ConnectionHistory!;
        section.Outcome.Should().Be(PostgresReadOutcome.Read, section.Reason);
        section.Server.Should().Be(ConnectionHistoryLookup.ServerOf(harness.Resolved));
        section.Samples.Should().Be(3);
        section.Peak.Should().Be(85);
        section.PeakAt.Should().Be(harness.Clock.GetUtcNow().AddHours(-2));
        section.ClientLimit.Should().Be(100);
        section.ClientLimitNow.Should().Be(100);
        section.ByApplication.Should().BeEmpty();
        without.Withheld.Should().ContainKey("connectionHistory.byApplication");
        without.ToJson().Should().NotContain(Application);

        with.ConnectionHistory!.ByApplication.Select(static x => (x.ApplicationName, x.Peak)).Should().Equal((Application, 60), ("reports", 20), (null, 5));
        with.Withheld.Should().NotContainKey("connectionHistory.byApplication");
        with.ToJson().Should().Contain("\"connectionHistory\"").And.Contain(Application);

        store.Reads.Should().Be(1, "both calls read the history through one shared cache");
    }

    [Fact]
    public async Task The_section_summarizes_the_whole_window_old_samples_included()
    {
        CountingStore store = new();
        await using HealthHarness harness = Harness(store);
        store.Samples =
        [
            Sample(harness, TimeSpan.FromHours(26), 99, 100),
            Sample(harness, TimeSpan.FromHours(2), 50, 100),
        ];

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Asked(activity: false), Token);

        snapshot.ConnectionHistory!.Peak.Should().Be(99, "the window holds the 26-hour-old sample; only C15 judges the last day");
        snapshot.ConnectionHistory.Samples.Should().Be(2);
    }

    [Fact]
    public async Task A_window_without_samples_is_unavailable_with_its_reason()
    {
        CountingStore store = new();
        await using HealthHarness harness = Harness(store);

        PostgresDiagnosticsSnapshot none = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Asked(activity: false), Token);

        none.ConnectionHistory!.Outcome.Should().Be(PostgresReadOutcome.Unavailable);
        none.ConnectionHistory.Reason.Should().NotBeNullOrWhiteSpace();
        none.ConnectionHistory.Server.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_store_that_fails_is_the_sections_outcome_and_never_an_exception_and_its_words_stay_in_the_log()
    {
        ThrowingStore store = new(new InvalidOperationException("SENTINEL-STORE-WORDS"));
        await using HealthHarness harness = Harness(store);

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Asked(activity: true), Token);

        snapshot.Outcome.Should().Be(PostgresReadOutcome.Read, "the database is fine; only the history is not");
        snapshot.ConnectionHistory!.Outcome.Should().Be(PostgresReadOutcome.Failed);
        snapshot.ConnectionHistory.Reason.Should().Contain("InvalidOperationException");
        snapshot.ToJson().Should().NotContain("SENTINEL-STORE-WORDS");
        harness.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9251, "an unexpected failure of the history's store is logged once, as any section's");
    }

    [Fact]
    public async Task A_store_that_is_cut_off_is_timed_out_in_the_section()
    {
        ThrowingStore store = new(new OperationCanceledException());
        await using HealthHarness harness = Harness(store);

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(Database(harness), Asked(activity: false), Token);

        snapshot.ConnectionHistory!.Outcome.Should().Be(PostgresReadOutcome.TimedOut);
        snapshot.ConnectionHistory.Reason.Should().Contain("QueryTimeout");
        harness.Warnings.Should().NotContain(static x => x.EventId.Id == 9251, "a timeout is an expected state, Debug only");
    }

    [Fact]
    public async Task A_call_that_asks_for_the_history_is_logged_with_its_section_name_and_at_Debug_only()
    {
        CountingStore store = new();
        await using HealthHarness harness = Harness(store);
        store.Samples = [Sample(harness, TimeSpan.FromHours(2), 85, 100, (Application, 60))];

        await Diagnostics(harness).GetSnapshotAsync(Database(harness), Asked(activity: true), Token);

        harness.Logs.Entries.Where(static x => x.EventId.Id == 9250).Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Debug);
        harness.Logs.Entries.Where(static x => x.Level > LogLevel.Debug && x.Message.Contains(Application, StringComparison.Ordinal))
            .Should().BeEmpty("D31: no result of the API is logged above Debug");
    }

    /// <summary>
    /// C15 is a catalog-tier check that reads no catalog without a history: its standing down must not make a database the sweep
    /// could not reach look reached (<c>PostgresDiagnosticsService.Reached</c>), and with a history its failure with C03 is the same.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_database_the_sweep_could_not_reach_is_still_unreached_beside_C15_and_the_history_is_still_read(bool withHistory)
    {
        CountingStore store = new();
        await using HealthHarness harness = HealthHarness.Create(
            checks: [typeof(ConnectionNearMissCheck), typeof(ScriptedCatalogCheck)],
            before: withHistory ? services => { services.AddPostgresConnectionHistory(); services.AddSingleton<IPostgresConnectionHistoryStore>(store); } : null);
        harness.Script.Throws("T90", new Npgsql.NpgsqlException("refused", new System.Net.Sockets.SocketException(10061)));
        store.Samples = [Sample(harness, TimeSpan.FromHours(2), 85, 100)];

        PostgresDiagnosticsSnapshot snapshot = await Diagnostics(harness).GetSnapshotAsync(
            Database(harness), PostgresDiagnosticsRequest.AllSections with { IncludeActivity = true }, Token);

        snapshot.Outcome.Should().Be(PostgresReadOutcome.Failed, snapshot.Reason);
        snapshot.Connections!.Reason.Should().Be(PostgresDiagnosticsService.NotReachedSection);
        C15(snapshot).Outcome.Should().Be(withHistory ? PostgresReadOutcome.Failed : PostgresReadOutcome.NotApplicable);
        snapshot.ConnectionHistory!.Outcome.Should().Be(withHistory ? PostgresReadOutcome.Read : PostgresReadOutcome.NotApplicable,
            "the history is the store's, not the database's: it is there when the database is not");

        if (withHistory)
        {
            snapshot.ConnectionHistory.Peak.Should().Be(85);
        }
    }

    private class CountingStore : IPostgresConnectionHistoryStore
    {
        public IReadOnlyList<PostgresConnectionSample> Samples { get; set; } = [];

        public int Reads { get; private set; }

        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken)
        {
            Reads++;
            return ValueTask.FromResult<IReadOnlyList<PostgresConnectionSample>>([.. Samples.Where(x => x.At >= since)]);
        }
    }

    private sealed class ThrowingStore(Exception exception) : IPostgresConnectionHistoryStore
    {
        public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken) =>
            throw exception;
    }
}
