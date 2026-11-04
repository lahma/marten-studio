using System.Globalization;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Services.Health;

/// <summary>
/// T01's on-demand action (1.2-INDEX-B): the Statements page's index advisor narrowed to the finding's table - the statements you may
/// see that name it, planned now and again under each index their plans suggest (hypopg). Planned, never run; audited.
/// </summary>
/// <remarks>
/// <para>
/// <b>At least the Activity gate</b>, although T01 itself is a <see cref="HealthGate.Database" /> finding: the action plans statement
/// texts, which are other programs' source text (D29). The advisor asks the Statements page's own gate again inside
/// (<see cref="IStatementsService.AdviseIndexesAsync(StudioScope, IndexAdviceRequest, bool, CancellationToken)" />), and the runner's
/// one audit entry carries its counts: the advisor is told not to write a second.
/// </para>
/// <para>
/// A finding about a schema the visitor may not see offers no action (fail closed), so the table the request names is one this visitor
/// already sees on the finding's card; the advisor passes over, uncounted, what it may not show them.
/// </para>
/// </remarks>
internal sealed class SequentialScanWhatIfAction(IStatementsService statements) : IHealthFindingAction
{
    /// <summary>The action's id.</summary>
    public const string ActionId = "t01.try-indexes";

    /// <summary>The most recommendations the table lists.</summary>
    internal const int MaxRecommendations = 5;

    /// <inheritdoc />
    public string Id => ActionId;

    /// <inheritdoc />
    public string Code => "T01";

    /// <inheritdoc />
    public string Label => "Try indexes against this table's statements";

    /// <inheritdoc />
    public string Description =>
        "Plans the statements you may see that name this table, now and with each index their plans suggest (hypopg). Planned, never "
        + "run; audited.";

    /// <inheritdoc />
    public string AuditAction => "Try indexes for a table";

    /// <inheritdoc />
    public string AuditTarget => "the table's statements";

    /// <inheritdoc />
    public HealthGate Gate => HealthGate.Activity;

    /// <inheritdoc />
    public StudioCapability? Capability => null;

    /// <inheritdoc />
    public bool AppliesTo(HealthFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return string.Equals(finding.Code, Code, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<HealthActionResult> RunAsync(HealthActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (context.Finding.Finding.Subject is not { Length: > 0 } table)
        {
            return HealthActionResult.Refused("This finding does not name a table the studio can try indexes on.", now);
        }

        IndexAdviceView view = await statements
            .AdviseIndexesAsync(context.Whole, new IndexAdviceRequest { Table = table }, recordAudit: false, cancellationToken)
            .ConfigureAwait(false);

        if (view.Unavailable is { } unavailable)
        {
            return Refused(unavailable, view, now);
        }

        if (view.Refusal != DatabaseRefusal.None || (view.Reason is not null && view.StatementsTried == 0))
        {
            string why = view.Reason ?? "The index advisor did not run.";

            return why.StartsWith("Index advice failed", StringComparison.Ordinal)
                ? HealthActionResult.Failed(why, now) with { AuditMessage = view.AuditOutcome }
                : Refused(why, view, now);
        }

        IReadOnlyList<IndexAdviceItem> shown = [.. view.Recommended.Take(MaxRecommendations)];
        List<string> notes = [IndexAdvisor.Caveat];

        if (view.Stopped is { Length: > 0 } stopped)
        {
            notes.Add(stopped);
        }

        if (view.Ranking is { Length: > 0 } ranking)
        {
            notes.Add(ranking);
        }

        if (view.Skipped.Count > 0)
        {
            notes.Add("Passed over: " + string.Join(", ", view.Skipped) + ".");
        }

        return new HealthActionResult
        {
            Outcome = HealthActionOutcome.Done,
            RanAt = now,
            Summary = shown.Count > 0
                ? IndexAdvisor.Headline(shown[0], view.StatementsTried)
                : "No index these plans suggest would help the " + IndexAdvisor.Statements(view.StatementsTried) + " on this table that you may see.",
            TableLabel = "Indexes tried against this table's statements",
            Columns = shown.Count == 0 ? [] : ["Index", "Helped", "Planned cost, weighted by calls", "Saved", "Remedy", "Details"],
            Rows = [.. shown.Select(item => Row(item, table))],
            Notes = notes,
            Statements = view.Statements,
            AuditMessage = view.AuditOutcome,
            AuditCapability = StudioCapability.ViewDatabaseActivity,
        };
    }

    private static HealthActionResult Refused(string reason, IndexAdviceView view, DateTimeOffset now) =>
        HealthActionResult.Refused(reason, now) with { AuditMessage = view.AuditOutcome, AuditCapability = StudioCapability.ViewDatabaseActivity };

    private static HealthActionRow Row(IndexAdviceItem item, string table)
    {
        double before = item.Statements.Sum(static x => x.Calls * x.Before);
        double after = item.Statements.Sum(static x => x.Calls * (x.After is { } cost && x.Used ? cost : x.Before));

        return new HealthActionRow(
        [
            new HealthActionCell(item.Index.Caption),
            new HealthActionCell(item.Helped.ToString("N0", CultureInfo.InvariantCulture)) { Number = true },
            new HealthActionCell(before.ToString("#,0", CultureInfo.InvariantCulture) + " → " + after.ToString("#,0", CultureInfo.InvariantCulture)) { Number = true },
            new HealthActionCell(item.Share < 0.01 ? "under 1%" : "~" + TableText.Percent(item.Share)) { Number = true },
            new HealthActionCell(item.Remedy ?? item.RemedyNote ?? "–") { Mono = item.Remedy is not null, Copy = item.Remedy is not null },
            new HealthActionCell("Details on Statements") { Link = new HealthLink("Details on Statements", StatementLinks.Route, [new KeyValuePair<string, string?>(IndexAdvisor.AdviseParameter, table)]) },
        ]);
    }
}
