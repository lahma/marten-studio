using Marten;

using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// An exact count that runs out of time leaves the visitor somewhere they can act from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by the adversarial review of P8b.</b> <c>CountExactAsync</c> caught every non-cancellation
/// failure and answered <see cref="DocumentCount.Unavailable" />, while its sibling on the list header
/// mapped a timeout to <see cref="DocumentCount.Unknown" />. On the rail that is a trap: a never-analysed
/// collection shows "?" with the "=" button beside it, the visitor presses it, the <c>count(*)</c> runs
/// past <c>QueryTimeout</c> — and the answer that comes back removes both the badge and the button,
/// leaving a row that says the table could not be read and offers nothing to try. Nothing short of
/// reloading the page gets it back, and nothing on screen says the count was merely slow.
/// </para>
/// <para>
/// <b>How the timeout is forced, and why not the other ways.</b> An <c>ACCESS EXCLUSIVE</c> lock held on
/// the collection by another transaction, with this fixture's <c>QueryTimeout</c> at the one second the
/// options validator allows as a floor. It is deterministic and costs a second. The alternatives were
/// worse: <c>pg_sleep</c> cannot be attached to a <c>count(*)</c> of a real table; generating a table big
/// enough to take more than a second to count is slow on a fast machine and flaky on a slow one; and
/// putting <c>statement_timeout</c> on the connection string would have bounded the fixture's own schema
/// apply and seeding as well, which is a fixture that fails for reasons the test is not about.
/// </para>
/// <para>
/// Which exception the timeout arrives as is a race — Postgres' <c>57014</c> if the backend answers
/// Npgsql's cancellation request in time, an <c>NpgsqlException</c> wrapping a <c>TimeoutException</c> if
/// it does not — so this asserts the answer rather than the exception. Both shapes are pinned, without a
/// database, by <c>DocumentCountFailureTests</c>.
/// </para>
/// </remarks>
public class DocumentCountTimeoutLiveTests(DocumentCountTimeoutLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentCountTimeoutLiveTests.Fixture>
{
    /// <summary>Small enough that the count is instant when nothing is in its way.</summary>
    public const int Rows = 50;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>One small, analysed collection, under the shortest query timeout the options allow.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options) => options.Schema.For<SlowCountThing>();

        /// <inheritdoc />
        protected override void ConfigureStudio(MartenStudioOptions options) =>
            options.QueryTimeout = TimeSpan.FromSeconds(1);

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            await using (IDocumentSession session = Marten.Store.LightweightSession())
            {
                for (var i = 0; i < Rows; i++)
                {
                    session.Store(new SlowCountThing { Id = Guid.NewGuid() });
                }

                await session.SaveChangesAsync();
            }

            // Analysed, so the "=" path goes straight to count(*): with an estimate in hand there is no
            // threshold probe in front of it, and the statement that blocks is the one under test.
            await using NpgsqlConnection connection = await Postgres.OpenAsync();
            await using var analyze = new NpgsqlCommand(
                $"analyze \"{Schema}\".\"mt_doc_slowcountthing\"", connection);

            await analyze.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// A blocked exact count answers "unknown", not "could not read this table".
    /// </summary>
    [PostgresFact]
    public async Task An_exact_count_that_times_out_is_unknown_rather_than_unavailable()
    {
        using var documents = Documents();

        await using NpgsqlConnection blocker = await Postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await blocker.BeginTransactionAsync(Token);

        await using (var take = new NpgsqlCommand(
            $"lock table \"{Schema}\".\"mt_doc_slowcountthing\" in access exclusive mode", blocker, holding))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        DocumentCount blocked = await documents.Service.CountExactAsync(Scope, "slowcountthing", Token);

        blocked.IsUnknown.Should().BeTrue(
            "a count that ran out of time is a collection nobody has measured, and the rail keeps the " +
            "'=' button beside one of those");
        blocked.IsEstimate.Should().BeFalse();

        await holding.RollbackAsync(Token);

        // The anti-vacuity half: with the lock gone the same call is exact and instant, so the assertion
        // above is about the timeout rather than about a collection that never answers.
        DocumentCount afterwards = await documents.Service.CountExactAsync(Scope, "slowcountthing", Token);

        afterwards.Value.Should().Be(Rows);
        afterwards.IsUnknown.Should().BeFalse();
        afterwards.IsEstimate.Should().BeFalse();
    }

    /// <summary>
    /// A collection that genuinely cannot be read is still <see cref="DocumentCount.Unavailable" />.
    /// </summary>
    /// <remarks>
    /// The other half of the same decision, and the reason the timeout arm is a narrow one: a studio that
    /// answered "nobody has measured this" to a table that is not there would be hiding a real fault
    /// behind a reassuring badge.
    /// </remarks>
    [PostgresFact]
    public async Task A_collection_that_is_not_there_is_still_unavailable()
    {
        using var documents = Documents();

        DocumentCount missing = await documents.Service.CountExactAsync(Scope, "nosuchcollection", Token);

        missing.IsUnavailable.Should().BeTrue();
        missing.IsUnknown.Should().BeFalse();
    }
}

/// <summary>A document whose only job is to have its table locked while the studio counts it.</summary>
public sealed class SlowCountThing
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }
}
