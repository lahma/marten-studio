using MartenStudio.Services;
using MartenStudio.Services.Events;

using Npgsql;

namespace MartenStudio.Tests.Services;

/// <summary>
/// DB-0-fix, items 2 and 4: which failed reads are an expected answer, as the event-store reads and the
/// recent-documents region now decide it through one helper.
/// </summary>
/// <remarks>
/// <para>
/// The event reads treated only <c>57014</c> as a timeout while the documents browser had long known the
/// second spelling - an <c>NpgsqlException</c> wrapping a <c>TimeoutException</c>, which is what the same
/// expired budget produces when the backend is too slow to answer the cancel. So one expired budget was
/// Debug on one screen and a throttled Warning on the next. And a missing table or schema (42P01, 3F000)
/// was Debug there too, although no read reaches Postgres without asking the catalog first - so it means
/// the catalog and the database disagree, which is worth a line.
/// </para>
/// <para>
/// Npgsql spells two <em>real</em> anomalies the same way as a statement timeout: a connect that timed
/// out and a pool that ran dry (verified in Npgsql 9.0.4's <c>NpgsqlConnector</c> and
/// <c>PoolingDataSource</c>). The helper does not read those as timeouts when the connection was opened
/// through it, and these tests are the anti-vacuity half of "both spellings are a timeout".
/// </para>
/// </remarks>
public class PostgresFailureTests
{
    /// <summary>A connection string nothing answers at, bounded so a slow resolver cannot stall the suite.</summary>
    private const string Nowhere =
        "Host=marten-studio-postgres-failure.invalid;Database=none;Username=none;Password=none;Timeout=2";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(StatementTimeouts))]
    public void A_statement_timeout_is_expected_in_either_spelling(Exception timeout)
    {
        PostgresFailure.IsTimeout(timeout).Should().BeTrue();
        EventDataService.IsExpectedReadFailure(timeout).Should().BeTrue("the page already shows it as timed out, and the budget is the studio's own");
    }

    [Fact]
    public void A_stale_link_is_expected()
    {
        EventDataService.IsExpectedReadFailure(new KeyNotFoundException("no such database")).Should().BeTrue();
    }

    /// <summary>
    /// Item 4: back on the throttled-Warning path. Every event read asks the column catalog - or
    /// <c>DeadLetterTableExistsAsync</c> - before it touches a table, so an event store that has not been
    /// created yet is an empty answer and never one of these.
    /// </summary>
    [Theory]
    [InlineData("42P01")] // undefined_table
    [InlineData("3F000")] // invalid_schema_name
    [InlineData("42501")] // insufficient_privilege
    [InlineData("53300")] // too_many_connections
    [InlineData("40P01")] // deadlock_detected
    public void A_missing_table_and_every_other_SQLSTATE_are_anomalies(string sqlState)
    {
        EventDataService.IsExpectedReadFailure(Postgres(sqlState)).Should().BeFalse();
    }

    /// <summary>
    /// A pool that ran dry and a connect that timed out wrap a <c>TimeoutException</c> exactly as a
    /// statement timeout does; opened through the helper, they are marked and never read as one.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConnectionFailuresSpelledLikeTimeouts))]
    public void A_connection_that_could_not_be_opened_is_never_a_timeout(Exception failure)
    {
        PostgresFailure.IsTimeout(failure).Should().BeTrue("unmarked, Npgsql's spelling is indistinguishable - which is why the mark exists");

        PostgresFailure.MarkAsConnectionFailure(failure);

        PostgresFailure.IsConnectionFailure(failure).Should().BeTrue();
        PostgresFailure.IsTimeout(failure).Should().BeFalse("a pool that ran dry is not a statement that ran out of its budget");
        EventDataService.IsExpectedReadFailure(failure).Should().BeFalse("and it is an anomaly an operator has to see");
    }

    /// <summary>What <see cref="PostgresFailure.OpenAsync" /> throws is what the connection threw, marked.</summary>
    [Fact]
    public async Task OpenAsync_marks_whatever_opening_throws_and_changes_nothing_else()
    {
        await using var connection = new NpgsqlConnection(Nowhere);

        Func<Task> opening = () => PostgresFailure.OpenAsync(connection, Token);

        Exception thrown = (await opening.Should().ThrowAsync<Exception>("nothing answers at an .invalid host")).Which;

        // A name that does not resolve surfaces as the resolver's own SocketException; a server that does
        // not answer, as Npgsql's. Either way it is what the connection threw, not a wrapper of the studio's.
        thrown.GetType().Namespace.Should().NotStartWith("MartenStudio");
        PostgresFailure.IsConnectionFailure(thrown).Should().BeTrue();
        PostgresFailure.IsTimeout(thrown).Should().BeFalse();
    }

    [Fact]
    public void An_exception_nothing_marked_is_not_a_connection_failure()
    {
        PostgresFailure.IsConnectionFailure(Postgres("57014")).Should().BeFalse();
        PostgresFailure.IsConnectionFailure(new InvalidOperationException()).Should().BeFalse();
    }

    public static TheoryData<Exception> StatementTimeouts =>
    [
        Postgres(PostgresFailure.QueryCanceled),
        new NpgsqlException("Exception while reading from stream", new TimeoutException("Timeout during reading attempt")),
        new TimeoutException(),
    ];

    public static TheoryData<Exception> ConnectionFailuresSpelledLikeTimeouts =>
    [
        new NpgsqlException(
            "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 100) or 'Timeout' (currently 15 seconds) in your connection string.",
            new TimeoutException()),
        new NpgsqlException("Failed to connect to 10.0.0.1:5432", new TimeoutException("Timeout during connection attempt")),
    ];

    private static PostgresException Postgres(string sqlState) =>
        new("something went wrong", "ERROR", "ERROR", sqlState);
}
