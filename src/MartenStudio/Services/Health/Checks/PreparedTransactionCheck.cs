using System.Globalization;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What C07 reads.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="MaxPrepared"><c>max_prepared_transactions</c>; zero means none can exist.</param>
/// <param name="Prepared">This database's prepared transactions, oldest first.</param>
internal sealed record PreparedTransactionFacts(DateTimeOffset ReadAt, int MaxPrepared, IReadOnlyList<PreparedRow> Prepared);

/// <summary>C07: a prepared transaction - half of a two-phase commit - left waiting for five minutes or more.</summary>
/// <remarks>
/// <para>
/// A prepared transaction outlives the session that prepared it and a restart of the server, and nothing ends it
/// but <c>COMMIT PREPARED</c> or <c>ROLLBACK PREPARED</c>: no timeout reaches it. One whose coordinator went away
/// keeps its locks and its row versions for good, holds back vacuum for the whole database, and - having a
/// transaction id - can hold Marten's high-water mark still. So an old one is Critical.
/// </para>
/// <para>
/// <c>pg_prepared_xacts</c> is readable by every role; its <c>gid</c> and owner are details, behind the activity
/// gate. A server with <c>max_prepared_transactions = 0</c>, Postgres' default, cannot have any, and says so.
/// </para>
/// </remarks>
[HealthCheck("C07", HealthArea.Connections, HealthCostTier.Catalog, "Orphaned prepared transaction")]
internal sealed class PreparedTransactionCheck : HealthCheck<PreparedTransactionFacts>
{
    /// <inheritdoc />
    public override async Task<PreparedTransactionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<(int MaxPrepared, PreparedRow? Row)> rows = await context.Sql
            .QueryAsync(HealthConnectionQueries.PreparedTransactions(), SessionReads.Prepared, cancellationToken)
            .ConfigureAwait(false);

        return new PreparedTransactionFacts(
            context.Now,
            rows.Count > 0 ? rows[0].MaxPrepared : 0,
            [.. rows.Where(static x => x.Row is not null).Select(static x => x.Row!)]);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PreparedTransactionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.MaxPrepared == 0 && facts.Prepared.Count == 0)
        {
            return HealthVerdict.Quiet("max_prepared_transactions is 0 on this server, so no prepared transaction can exist.");
        }

        List<(PreparedRow Row, TimeSpan Age)> old =
        [
            .. facts.Prepared
                .Select(x => (Row: x, Age: SnapshotAge.At(x.Age, facts.ReadAt, evaluation)))
                .Where(static x => x.Age >= ConnectionThresholds.PreparedTransactionCritical)
                .OrderByDescending(static x => x.Age),
        ];

        if (old.Count == 0)
        {
            return HealthVerdict.Quiet(
                facts.Prepared.Count == 0
                    ? "No prepared transaction is waiting in this database."
                    : SessionText.Count(facts.Prepared.Count, "prepared transaction") + " in this database, none older than "
                      + SessionText.Duration(ConnectionThresholds.PreparedTransactionCritical) + ".");
        }

        TimeSpan oldest = old[0].Age;

        List<HealthEvidence> evidence =
        [
            new("Prepared transactions older than " + SessionText.Duration(ConnectionThresholds.PreparedTransactionCritical), SessionText.Number(old.Count)),
            new("Oldest", SessionText.Duration(oldest) + ", prepared at " + old[0].Row.Prepared.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)),
            new("max_prepared_transactions", facts.MaxPrepared.ToString(CultureInfo.InvariantCulture)),
        ];

        for (int i = 0; i < Math.Min(old.Count, ConnectionThresholds.SessionsNamed); i++)
        {
            (PreparedRow row, TimeSpan age) = old[i];

            evidence.Add(new HealthEvidence(
                SessionEvidence.Label("Prepared transaction", i, "oldest"),
                "gid '" + row.Gid + "', owner " + row.Owner + ", transaction " + row.TransactionId + ", prepared "
                + SessionText.Duration(age) + " ago",
                HealthGate.Activity));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C07",
            Severity = HealthSeverity.Critical,
            Title = old.Count == 1
                ? "A prepared transaction has been waiting for its commit or rollback for " + SessionText.Duration(oldest)
                : SessionText.Number(old.Count) + " prepared transactions have been waiting for their commit or rollback, the oldest for "
                  + SessionText.Duration(oldest),
            WhyItMatters =
                "A prepared transaction is the first half of a two-phase commit. It outlives the session and the connection that made "
                + "it, survives a restart of the server, and keeps every lock and every row version it holds until something says "
                + "COMMIT PREPARED or ROLLBACK PREPARED - no timeout ends it. One whose transaction manager went away holds back vacuum "
                + "for the whole database for good, blocks whatever needs its locks, and, having a transaction id, can hold Marten's "
                + "high-water mark still so that async projections stop. Resolve it from the transaction manager that prepared it; "
                + "by hand only after finding out whether it should commit or roll back.",
            Evidence = evidence,
            Caveat =
                "Read in pg_prepared_xacts, for this database, at " + facts.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
                + ". Its global identifier and owner are shown only with MartenStudioOptions.Capabilities.ViewDatabaseActivity.",
            Remedy = null,
            Link = SessionEvidence.SessionsLink,
        });
    }
}
