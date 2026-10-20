using System.Globalization;
using System.Text.Json;

using JasperFx.Events.Daemon;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What D06 reads.</summary>
/// <param name="Counts">Dead letters per projection (a blank name for rows that carry none).</param>
internal sealed record DeadLettersFacts(IReadOnlyDictionary<string, long> Counts)
{
    /// <summary>Every dead letter in the database.</summary>
    public long Total => Counts.Values.Sum();

    /// <summary>What each projection writes, so a finding naming one carries its types (and is dropped if one is hidden).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Type>> PublishedTypes { get; init; } =
        new Dictionary<string, IReadOnlyList<Type>>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// D06: dead letters are accumulating - a projection keeps skipping events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Growth, not presence.</b> A dead letter is an event a projection could not apply and skipped - which is what
/// <c>Projections.Errors.SkipApplyErrors</c> asks for - so a store that has some has, at some point, had a bad event;
/// that alone is the dead-letter screen's business and the navigation badge's count. What this reports is a count that
/// is still going up across the advisor's window: a projection that is skipping events now, every one of which its
/// readers will never see. The sample store has a dead letter on purpose, and this check is quiet about it.
/// </para>
/// <para>
/// <b>Read raw, and only when visible.</b> <c>DeadLetterEvent</c> is a Marten document, by default in the event store's
/// schema and single-tenanted. Marten's own <c>FetchDeadLetterCountsAsync</c> applies the table's migration first, which
/// a health read never does (hard rule 14), so this counts the table grouped by the JSON property the serializer's
/// casing stores <c>ProjectionName</c> under. A host that hides <c>DeadLetterEvent</c> with <c>IsDocumentTypeVisible</c>
/// - or whose delegate throws for it, which is read the same way (<see cref="DeadLetterVisibility" />) - gets no read at
/// all, its table name never goes into a statement, and a status row says why.
/// </para>
/// <para>
/// <b>The table is found the way the dead-letter list finds it</b> (CL1 item 3): <c>DeadLetterQueries.TableFor</c>, its
/// schema and its name, so a host's <c>Schema.For&lt;DeadLetterEvent&gt;().DocumentAlias(...)</c> is counted where the
/// screen reads it, rather than looked for as <c>mt_doc_deadletterevent</c> and reported as zero. (The schema is the event
/// store's whatever the host says: Marten's <c>ApplyConfiguration</c> registers the type after the host's configuration,
/// read at <c>V9.39.0</c>.) Every row is counted, soft-deleted or not: what grows is how many failures have been recorded.
/// </para>
/// </remarks>
[HealthCheck("D06", HealthArea.Daemon, HealthCostTier.Catalog, "Dead letters accumulating")]
internal sealed class DeadLettersGrowingCheck : HealthCheck<DeadLettersFacts>
{
    /// <summary>The property <c>DeadLetterEvent</c> names its projection by.</summary>
    private const string ProjectionNameProperty = nameof(DeadLetterEvent.ProjectionName);

    /// <summary>What D06 says when the host hides <c>DeadLetterEvent</c>.</summary>
    internal const string HiddenReason =
        "MartenStudioOptions.IsDocumentTypeVisible hides DeadLetterEvent, so the advisor does not read the dead letters.";

    /// <summary>What D06 says when the host's visibility delegate throws for <c>DeadLetterEvent</c>.</summary>
    internal const string VisibilityFailedReason =
        "MartenStudioOptions.IsDocumentTypeVisible threw for DeadLetterEvent, so the advisor treats it as hidden and does not read "
        + "the dead letters.";

    /// <inheritdoc />
    public override async Task<DeadLettersFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        if (!DeadLetterVisibility.IsVisible(context.StudioOptions.IsDocumentTypeVisible, out Exception? failure))
        {
            throw new HealthCheckUnavailableException(failure is null ? HiddenReason : VisibilityFailedReason);
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, IReadOnlyList<Type>> published = DaemonCheckConfiguration.PublishedTypesOf(context.Store);

        if (!shape.HasDeadLetters)
        {
            // No table is the daemon never having recorded one: a real zero, as the navigation badge counts it.
            return new DeadLettersFacts(new Dictionary<string, long>(StringComparer.Ordinal)) { PublishedTypes = published };
        }

        JsonNamingPolicy? policy = DocumentDataService.NamingPolicyFor(context.Store);
        string key = policy?.ConvertName(ProjectionNameProperty) ?? ProjectionNameProperty;

        var query = HealthDaemonQueries.DeadLettersByProjection(shape.DeadLetterSchema!, shape.DeadLetters!.Name, key);
        IReadOnlyList<(string Projection, long Count)> rows = await context.Sql.QueryAsync(
                query,
                static reader => (reader.IsDBNull(0) ? string.Empty : reader.GetString(0), reader.GetInt64(1)),
                cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        foreach ((string projection, long count) in rows)
        {
            counts[projection] = counts.GetValueOrDefault(projection) + count;
        }

        return new DeadLettersFacts(counts) { PublishedTypes = published };
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(DeadLettersFacts facts)
    {
        // The total is written every time, so a later sample can tell "this projection had none then" from "D06 did
        // not run then".
        yield return new HealthSamplePart(Total, facts.Total);

        foreach ((string projection, long count) in facts.Counts)
        {
            yield return new HealthSamplePart(DaemonSeries.DeadLetters(projection), count);
        }
    }

    /// <summary>The series that records every sweep D06 ran in.</summary>
    internal const string Total = "daemon.dead-letters-total";

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DeadLettersFacts facts, HealthEvaluation evaluation)
    {
        // The earliest sample within the window in which D06 ran - the baseline every projection is compared to.
        HealthSample? baseline = evaluation.History.Samples
            .Where(x => x.At >= evaluation.Now - DaemonThresholds.TrendWindow && x.At < evaluation.Now && x.Values.ContainsKey(Total))
            .OrderBy(static x => x.At)
            .FirstOrDefault();

        if (baseline is null || evaluation.Now - baseline.At < DaemonThresholds.MinimumWatch)
        {
            return HealthVerdict.Quiet(
                DaemonCheckConfiguration.Number(facts.Total) + " dead letter(s) now. Growth needs two samples a minute apart: "
                + evaluation.History.Describe(evaluation.Now));
        }

        TimeSpan watched = evaluation.Now - baseline.At;
        List<HealthFinding> findings = [];

        foreach ((string projection, long count) in facts.Counts.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            long before = baseline.Values.TryGetValue(DaemonSeries.DeadLetters(projection), out double then) ? (long) then : 0;
            long added = count - before;

            if (added <= 0)
            {
                continue;
            }

            string named = projection.Length == 0 ? "a projection the dead letters do not name" : projection;
            double perMinute = added / watched.TotalMinutes;

            findings.Add(new HealthFinding
            {
                Code = "D06",
                Severity = HealthSeverity.Warning,
                Subject = projection.Length == 0 ? "(unnamed)" : projection,
                Title = DaemonCheckConfiguration.Number(added) + " dead letter(s) added for " + named + " in the last "
                    + DaemonCheckConfiguration.Format(watched),
                WhyItMatters =
                    "Every dead letter is an event this projection could not apply and skipped: whatever reads the projection will "
                    + "never reflect it. A count that keeps rising is a projection failing on events as they arrive - a bug in its "
                    + "code, or an event shape it does not expect - rather than one bad event in the past. The dead-letter screen, "
                    + "filtered to this projection, shows each one with its exception.",
                Evidence =
                [
                    new HealthEvidence("Dead letters now", DaemonCheckConfiguration.Number(count)),
                    new HealthEvidence("Added", DaemonCheckConfiguration.Number(added) + " in " + DaemonCheckConfiguration.Format(watched)
                        + " (about " + perMinute.ToString("0.#", CultureInfo.InvariantCulture) + " a minute)"),
                ],
                Link = new HealthLink(
                    "Open its dead letters",
                    EventLinks.DeadLettersRoute,
                    [new KeyValuePair<string, string?>("projection", projection.Length == 0 ? null : projection)]),
                Caveat =
                    "Counted across every tenant of this database: DeadLetterEvent is single-tenanted. The window is the advisor's "
                    + "own samples, taken while the Health page or the Overview is open.",
                Remedy = null,
                DocumentTypes = [typeof(DeadLetterEvent), .. facts.PublishedTypes.GetValueOrDefault(projection) ?? []],
            });
        }

        return findings.Count > 0
            ? new HealthVerdict(findings)
            : HealthVerdict.Quiet(DaemonCheckConfiguration.Number(facts.Total) + " dead letter(s), none added in the last "
                + DaemonCheckConfiguration.Format(watched) + ".");
    }
}
