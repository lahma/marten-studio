using MartenStudio.Services.Events;

using Npgsql;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// The Overview's stream and event tiles over a store Postgres has never analysed.
/// </summary>
/// <remarks>
/// <para>
/// The tiles are estimates by design (D8): they are read on every refresh interval by every circuit, and
/// an exact <c>count(*)</c> of <c>mt_events</c> on a timer is the denial of service the whole rule exists
/// to prevent. But <c>pg_class.reltuples</c> is <c>-1</c> until the first <c>ANALYZE</c>, which is the
/// state of every freshly seeded store — the zero-config sample host included — so estimating alone made
/// the tiles say "unknown" over an event store that plainly has events in it, until autovacuum next ran.
/// </para>
/// <para>
/// The upgrade is the bounded one the documents rail already uses: <c>relpages</c> first, so a heap that
/// is already enormous is declined without a statement, then <c>select 1 … offset N limit 1</c> against
/// <c>ExactCountThreshold</c>. A store big enough for the count to be expensive fails one of those and
/// goes on reporting nothing.
/// </para>
/// <para>
/// <b>The never-analysed state is constructed rather than waited for.</b> Autovacuum is turned off on the
/// two tables and <c>pg_class</c> is then set back to <c>-1</c>, which is the same doctoring
/// <c>DocumentCountThresholdLiveTests</c> does and for the same reason: what is under test is what the
/// studio does with the state, and a test that waited for the state would be a test of autovacuum's
/// naptime.
/// </para>
/// </remarks>
/// <param name="fixture">The class's store.</param>
public class EventStoreCountsLiveTests(NeverAnalysedEventStoreFixture fixture)
    : IClassFixture<NeverAnalysedEventStoreFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private EventsFixture Events => fixture.Events;

    /// <summary>
    /// A never-analysed event store gets counted, so the tiles show a number rather than "unknown".
    /// </summary>
    [PostgresFact]
    public async Task The_tiles_count_a_never_analysed_store_rather_than_reporting_unknown()
    {
        // The anti-vacuity half: if either table had an estimate, this would be a test of the other
        // branch - passing, and measuring nothing.
        (await ReltuplesAsync("mt_events")).Should().Be(-1);
        (await ReltuplesAsync("mt_streams")).Should().Be(-1);

        EventStoreCounts counts = await Events.Service.GetEventStoreCountsAsync(Events.Scope, Token);

        counts.Error.Should().BeNull();
        counts.TablesExist.Should().BeTrue();

        counts.Streams.Value.Should().Be(
            EventsFixture.SeededStreamCount,
            "there was no estimate to be had and the store is small, so the tile paid for the truth");

        counts.Streams.IsEstimate.Should().BeFalse("a count(*) is not an estimate, and the tile drops the tilde");
        counts.Streams.Display.Should().NotContain("~").And.NotBe("unknown");

        counts.Events.Value.Should().BeGreaterThan(0);
        counts.Events.IsEstimate.Should().BeFalse();
    }

    private async Task<long> ReltuplesAsync(string table)
    {
        await using NpgsqlConnection connection = await Events.OpenAsync(Token);
        await using var command = new NpgsqlCommand(
            "select c.reltuples::bigint from pg_catalog.pg_class c where c.oid = to_regclass(@qualified)",
            connection);

        command.Parameters.AddWithValue("qualified", $"\"{Events.Schema}\".\"{table}\"");

        return (long) (await command.ExecuteScalarAsync(Token))!;
    }
}

/// <summary>
/// The same seed as every other events class, with <c>pg_class</c> put back to "never analysed".
/// </summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
public sealed class NeverAnalysedEventStoreFixture(PostgresFixture postgres) : EventsStoreFixtureBase(postgres)
{
    /// <inheritdoc />
    public override string Schema => "events_counts";

    /// <inheritdoc />
    protected override async Task AfterSeedAsync()
    {
        await using NpgsqlConnection connection = await Events.OpenAsync();

        string[] tables = ["mt_events", "mt_streams"];

        // Autovacuum first, so that nothing re-analyses the tables between the doctoring and the test.
        foreach (string table in tables)
        {
            await using var off = new NpgsqlCommand(
                $"alter table \"{Schema}\".\"{table}\" set (autovacuum_enabled = off)", connection);

            await off.ExecuteNonQueryAsync();
        }

        await using var doctor = new NpgsqlCommand(
            $"""
             update pg_catalog.pg_class
             set reltuples = -1, relpages = 0
             where oid in ('"{Schema}"."mt_events"'::regclass, '"{Schema}"."mt_streams"'::regclass)
             """,
            connection);

        await doctor.ExecuteNonQueryAsync();
    }
}
