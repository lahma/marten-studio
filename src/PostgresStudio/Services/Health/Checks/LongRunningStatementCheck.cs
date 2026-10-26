using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

using Npgsql;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>One statement still running, as C05 read it.</summary>
/// <param name="ProcessId">Its session's process id. A detail.</param>
/// <param name="User">Its role. A detail.</param>
/// <param name="Application">Its application name. A detail.</param>
/// <param name="ClientAddress">Its client address. A detail.</param>
/// <param name="BackendStart">When its backend started. A detail.</param>
/// <param name="WaitEventType">The class of what it waits for, if it waits.</param>
/// <param name="RunningAtRead">How long it had been running when read.</param>
internal sealed record RunningStatement(
    int ProcessId,
    string? User,
    string? Application,
    string? ClientAddress,
    DateTimeOffset? BackendStart,
    string? WaitEventType,
    TimeSpan RunningAtRead);

/// <summary>What C05 reads.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Visibility">How much of <c>pg_stat_activity</c> the role could see.</param>
/// <param name="Total">How many statements have run for at least the floor, beyond the ones read.</param>
/// <param name="Statements">The longest of them, at most <see cref="HealthConnectionQueries.MaxOffenders" />.</param>
internal sealed record LongRunningFacts(
    DateTimeOffset ReadAt,
    SessionVisibility Visibility,
    long Total,
    IReadOnlyList<RunningStatement> Statements);

/// <summary>C05: statements in this database that have been running for five minutes or more.</summary>
/// <remarks>
/// Client sessions only - autovacuum, replication and background workers run as long as they need to and are not
/// statements anybody sent. The statement's text is never part of the finding: another application's statement
/// carries its literals and its schemas, and what a visitor may read of it depends on the schemas they may see,
/// which a finding shared by every visitor cannot know. It is on Database › Sessions, behind
/// <c>ViewDatabaseActivity</c> and masked for that visitor.
/// </remarks>
[HealthCheck("C05", HealthArea.Connections, HealthCostTier.Catalog, "Long-running statement")]
internal sealed class LongRunningStatementCheck : HealthCheck<LongRunningFacts>
{
    /// <inheritdoc />
    public override async Task<LongRunningFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<(SessionVisibility Visibility, RunningStatement? Statement, long Total)> rows = await context.Sql
            .QueryAsync(HealthConnectionQueries.LongRunningStatements(ConnectionThresholds.CandidateFloor), Map, cancellationToken)
            .ConfigureAwait(false);

        return new LongRunningFacts(
            context.Now,
            rows.Count > 0 ? rows[0].Visibility : SessionVisibility.Everything,
            rows.Count > 0 ? rows[0].Total : 0,
            [.. rows.Where(static x => x.Statement is not null).Select(static x => x.Statement!)]);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(LongRunningFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<(RunningStatement Statement, TimeSpan Running)> running =
        [
            .. facts.Statements
                .Select(x => (Statement: x, Running: SnapshotAge.At(x.RunningAtRead, facts.ReadAt, evaluation)))
                .Where(static x => x.Running >= ConnectionThresholds.LongRunningWarning)
                .OrderByDescending(static x => x.Running),
        ];

        if (running.Count == 0)
        {
            return HealthVerdict.Quiet(
                "No statement in this database has been running for " + SessionText.Duration(ConnectionThresholds.LongRunningWarning) + " or more.");
        }

        TimeSpan longest = running[0].Running;
        int waiting = running.Count(static x => x.Statement.WaitEventType is { Length: > 0 });

        List<HealthEvidence> evidence =
        [
            new("Statements running " + SessionText.Duration(ConnectionThresholds.LongRunningWarning) + " or more", SessionText.Number(running.Count)),
            new("Longest", SessionText.Duration(longest)),
            new("Waiting rather than working", SessionText.Number(waiting)
                + (waiting == 0
                    ? string.Empty
                    : " (" + string.Join(", ", running
                        .Where(static x => x.Statement.WaitEventType is { Length: > 0 })
                        .GroupBy(static x => x.Statement.WaitEventType!, StringComparer.Ordinal)
                        .OrderByDescending(static x => x.Count())
                        .Select(static x => x.Key + " " + SessionText.Number(x.Count()))) + ")")),
        ];

        for (int i = 0; i < Math.Min(running.Count, ConnectionThresholds.SessionsNamed); i++)
        {
            (RunningStatement statement, TimeSpan runningFor) = running[i];

            evidence.Add(SessionEvidence.Session(
                SessionEvidence.Label("Statement", i),
                statement.ProcessId,
                statement.User,
                statement.Application,
                statement.ClientAddress,
                statement.BackendStart,
                "running for " + SessionText.Duration(runningFor)
                + (statement.WaitEventType is { Length: > 0 } wait ? ", waiting on " + wait : string.Empty)));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C05",
            Severity = HealthSeverity.Warning,
            Title = running.Count == 1
                ? "A statement has been running for " + SessionText.Duration(longest)
                : SessionText.Number(running.Count) + " statements have been running for more than "
                  + SessionText.Duration(ConnectionThresholds.LongRunningWarning) + ", the longest for " + SessionText.Duration(longest),
            WhyItMatters =
                "A statement that runs this long holds its locks and its snapshot for as long as it runs: vacuum cannot clean up "
                + "behind it anywhere in the database, and anything that needs a stronger lock on the same tables - a schema apply, "
                + "a rebuild's truncate - queues behind it. " + evaluation.Text.LongStatementCause,
            Evidence = evidence,
            Caveat = SessionEvidence.Caveat(facts.Visibility, facts.ReadAt, evaluation.Text.StatementsOnSessionsPage),
            Link = SessionEvidence.SessionsLink,
            Remedy = new HealthRemedy(
                """
                // A ceiling for Marten's own statements, in seconds (Npgsql's default is 30):
                opts.CommandTimeout = 60;

                // ...and an index for the filter a slow document query uses:
                opts.Schema.For<Order>().Index(x => x.CustomerId);
                """,
                "CommandTimeout bounds what Marten sends; it does not reach another application's statements. The Query page's index "
                + "advisor says which index a document query needs."),
        });
    }

    private static (SessionVisibility Visibility, RunningStatement? Statement, long Total) Map(NpgsqlDataReader reader)
    {
        SessionVisibility visibility = SessionReads.Visibility(reader);

        if (reader.IsDBNull(2))
        {
            return (visibility, null, 0);
        }

        return (
            visibility,
            new RunningStatement(
                reader.GetInt32(2),
                SessionReads.Text(reader, 3),
                SessionReads.Text(reader, 4),
                SessionReads.Text(reader, 5),
                SessionReads.Value<DateTimeOffset>(reader, 6),
                SessionReads.Text(reader, 8),
                SessionReads.Seconds(reader, 9) ?? TimeSpan.Zero),
            reader.GetInt64(10));
    }
}
