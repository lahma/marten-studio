using System.Globalization;

using JasperFx.Events.Projections;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Projections;

using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One progression row D10 found that nothing the store registers advances.</summary>
/// <param name="Name">The row's <c>name</c>.</param>
/// <param name="Sequence">Its <c>last_seq_id</c>.</param>
/// <param name="LastUpdated">Its <c>last_updated</c>, in server time.</param>
/// <param name="Class">What the store's configuration says it is (<see cref="ProgressionRowClassifier" />).</param>
internal sealed record OrphanedRowFact(string Name, long Sequence, DateTimeOffset? LastUpdated, ProgressionRowClass Class);

/// <summary>What D10 read.</summary>
/// <param name="Configuration">The store's daemon settings, for the published types of a projection a row names.</param>
/// <param name="Orphans">The orphaned rows, by name.</param>
/// <param name="Truncated">Whether the read stopped at its limit, so the count is "at least".</param>
internal sealed record OrphanedProgressionFacts(
    DaemonCheckConfiguration Configuration,
    IReadOnlyList<OrphanedRowFact> Orphans,
    bool Truncated);

/// <summary>
/// D10: progression rows that nothing this store registers advances - left by a projection that was renamed, removed or
/// given a new version, or by a subscription that was removed (M4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Info, and read-only.</b> Nothing reads an orphaned row but tools that list every row - and the daemon itself in one
/// case, a lower version's row of a projection that gates its side effects behind the prior version
/// (<c>GateSideEffectsBehindPriorVersion</c>), which it reads on every start of the new version. The finding says what the
/// rows are and where they came from; removing one is the host's decision, and the studio writes no DML (D6). Its remedy
/// is Marten's own removal by the row's exact name, <c>IEventDatabase.DeleteProjectionProgressByShardNameAsync</c>
/// (marten#4785, at <c>V9.39.0</c> and <c>v9.54.2</c>), as lines for the host's code; the name-keyed
/// <c>IEventStore.DeleteProjectionProgressAsync</c> throws <see cref="ArgumentOutOfRangeException" /> for a name no
/// projection registers.
/// </para>
/// <para>
/// <b>One finding per projection a row names</b> - its name and, above version 1, its version - so a rename is one
/// finding however many shards or tenants it left rows for, and muting it mutes that projection's leftovers alone. A
/// tenant's row is counted, never named: a finding that named a tenant would need the activity gate (D29).
/// </para>
/// <para>
/// <b>What is registered</b> is the classifier's answer: the daemon's own <c>AllShards()</c> - every subscription's
/// included - the composites' members and, under <c>UseTenantPartitionedEvents</c>, every tenant's row of those. The read
/// leaves out what is registered by name (<see cref="HealthDaemonQueries.OrphanCandidates" />), so it stays one bounded
/// read of a few rows on a store with thousands of tenants.
/// </para>
/// </remarks>
[HealthCheck("D10", HealthArea.Daemon, HealthCostTier.Catalog, "Orphaned progression rows")]
internal sealed class OrphanedProgressionCheck : HealthCheck<OrphanedProgressionFacts>
{
    /// <summary>The projections screen, where the rows are listed with the statements that remove them.</summary>
    internal const string ProjectionsRoute = "projections";

    /// <summary>How many rows a finding names in its evidence; the rest are counted.</summary>
    internal const int NamedRows = 10;

    /// <inheritdoc />
    public override async Task<OrphanedProgressionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DaemonCheckConfiguration configuration = DaemonCheckConfiguration.Read(context);

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);
        if (shape.Progression is null)
        {
            throw new HealthCheckUnavailableException(EventStoreRelations.NoEventStoreReason);
        }

        ProgressionRowClassifier classifier = ProgressionRowClassifier.For(
            context.Store, ProjectionDataService.RegisteredProjectionsOf(context.Store));

        IReadOnlyList<(string Name, long Sequence, DateTimeOffset? LastUpdated)> rows = await context.Sql
            .QueryAsync(
                HealthDaemonQueries.OrphanCandidates(
                    configuration.EventSchema,
                    [.. classifier.RegisteredIdentities, HealthDaemonQueries.HighWaterMarkRow, HealthDaemonQueries.StuckGapRow, HealthDaemonQueries.AllocationFenceRow],
                    classifier.TenantPrefixes),
                static reader => (reader.GetString(0), reader.IsDBNull(1) ? 0L : reader.GetInt64(1), HighWaterReading.NullableTime(reader, 2)),
                cancellationToken)
            .ConfigureAwait(false);

        List<OrphanedRowFact> orphans = [];
        foreach ((string name, long sequence, DateTimeOffset? updated) in rows)
        {
            ProgressionRowClass row = classifier.Classify(name);

            if (row.Kind == ProgressionRowKind.Orphan)
            {
                orphans.Add(new OrphanedRowFact(name, sequence, updated, row));
            }
        }

        return new OrphanedProgressionFacts(configuration, orphans, rows.Count >= HealthDaemonQueries.MaxShardRows);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(OrphanedProgressionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.Orphans.Count == 0)
        {
            return HealthVerdict.Quiet(
                "Every row of mt_event_progression is a registered shard's, a composite member's, a tenant's row of one under "
                + "per-tenant partitioning, or Marten's own bookkeeping.");
        }

        List<HealthFinding> findings = [];

        foreach (IGrouping<string, OrphanedRowFact> group in facts.Orphans
                     .GroupBy(static x => SubjectOf(x.Name), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            findings.Add(Finding(facts, group.Key, [.. group.OrderBy(static x => x.Name, StringComparer.Ordinal)]));
        }

        return new HealthVerdict(findings);
    }

    /// <summary>
    /// The projection a row names - its first segment and, above version 1, its version (<c>Trips:V2</c>) - or the whole name
    /// where it does not parse as a shard.
    /// </summary>
    internal static string SubjectOf(string name)
    {
        if (!ShardName.TryParse(name, out ShardName? parsed) || parsed is null)
        {
            return name;
        }

        return parsed.Version > 1
            ? parsed.Name + ":V" + parsed.Version.ToString(CultureInfo.InvariantCulture)
            : parsed.Name;
    }

    private static HealthFinding Finding(OrphanedProgressionFacts facts, string subject, IReadOnlyList<OrphanedRowFact> rows)
    {
        OrphanedRowFact first = rows[0];
        int tenantRows = rows.Count(static x => x.Class.TenantId is not null);
        List<OrphanedRowFact> named = [.. rows.Where(static x => x.Class.TenantId is null).Take(NamedRows)];
        int unnamed = rows.Count - tenantRows - named.Count;
        bool read = rows.Any(static x => x.Class.ReadByDaemon);

        List<HealthEvidence> evidence =
        [
            new("Rows", DaemonCheckConfiguration.Number(rows.Count)
                + (tenantRows > 0 ? ", " + DaemonCheckConfiguration.Number(tenantRows) + " of them tenants' rows, which are counted and not named" : string.Empty)),
        ];

        if (named.Count > 0)
        {
            evidence.Add(new HealthEvidence("Rows by name", string.Join(", ", named.Select(static x =>
                x.Name + " at " + DaemonCheckConfiguration.Number(x.Sequence)))
                + (unnamed > 0 ? ", and " + DaemonCheckConfiguration.Number(unnamed) + " more" : string.Empty)));
        }

        if (rows.Max(static x => x.LastUpdated) is { } latest)
        {
            evidence.Add(new HealthEvidence("Last written", EventHealth.Time(latest)));
        }

        // A projection this store still registers - at another version, without that shard, inline - carries its published
        // types, so one that writes a hidden document type is never named.
        IReadOnlyList<Type> types = first.Class.Projection is { } projection
            ? facts.Configuration.PublishedTypes.GetValueOrDefault(projection) ?? []
            : [];

        return new HealthFinding
        {
            Code = "D10",
            Severity = HealthSeverity.Info,
            Subject = subject,
            DocumentTypes = types,
            Title = rows.Count == 1
                ? "A progression row of " + subject + " that nothing in this store advances"
                : DaemonCheckConfiguration.Number(rows.Count) + " progression rows of " + subject + " that nothing in this store advances",
            WhyItMatters =
                OrphanWording.Explain(first.Class.TenantId is null ? first.Name : Neutral(first.Name), first.Class)
                + " A row like this is bookkeeping nothing advances any more: it shows as a shard that never moves in any tool "
                + "that lists every row, and a projection registered again under the same name and version resumes from it "
                + "rather than replaying from the first event."
                + (read
                    ? " The daemon still reads it: the registered version gates its side effects behind the prior version's "
                      + "position (GateSideEffectsBehindPriorVersion), so removing it before that version has passed it would "
                      + "raise side effects again for events the prior version already handled."
                    : string.Empty),
            Evidence = evidence,
            Caveat = "Marten Studio never removes one: whether to is the host's decision. The projections screen lists the rows, "
                + "each with Marten's call and the statement that removes it."
                + (facts.Truncated
                    ? " The read stopped at " + DaemonCheckConfiguration.Number(HealthDaemonQueries.MaxShardRows) + " rows, so there may be more."
                    : string.Empty),
            Remedy = Remedy(rows),
            Link = new HealthLink("Open the projections screen", ProjectionsRoute),
        };
    }

    /// <summary>
    /// Marten's own removal of the rows that may be removed - <c>IEventDatabase.DeleteProjectionProgressByShardNameAsync</c>,
    /// by each row's exact name (marten#4785, public at <c>V9.39.0</c> and <c>v9.54.2</c>) - as lines for the host's own
    /// code; a tenant's row is said in words, never named. <see langword="null" /> when the daemon may still read every row.
    /// </summary>
    internal static HealthRemedy? Remedy(IReadOnlyList<OrphanedRowFact> rows)
    {
        List<OrphanedRowFact> removable = [.. rows.Where(static x => !x.Class.ReadByDaemon)];
        if (removable.Count == 0)
        {
            return null;
        }

        List<string> lines = ["// Marten's own removal, by each row's exact name, against this database's IMartenDatabase:"];
        lines.AddRange(removable
            .Where(static x => x.Class.TenantId is null)
            .Take(NamedRows)
            .Select(static x => OrphanWording.MartenCall(x.Name))
            .OfType<string>());

        int tenantRows = removable.Count(static x => x.Class.TenantId is not null);
        if (tenantRows > 0)
        {
            lines.Add("// and the same for each of the " + DaemonCheckConfiguration.Number(tenantRows) + " tenants' rows, Name:ShardKey:tenant, "
                + "listed on the projections screen.");
        }

        try
        {
            return new HealthRemedy(
                string.Join("\n", lines),
                "Marten deletes the one row whose name is the identity given, whether or not a projection of that name is registered "
                + "- DeleteProjectionProgressAsync, which takes a projection's name, refuses one that is no longer registered - and "
                + "first ensures the event store's storage, as every write to it does. Remove a row once the rename or removal has "
                + "shipped; the documents and tables the projection wrote stay. A row the daemon still reads is left out.");
        }
        catch (ArgumentException)
        {
            // A row name that reads like a statement the remedy rules refuse (a "; drop ..." inside its string literal): the
            // finding stands, with no lines to paste - the projections screen still lists it.
            return null;
        }
    }

    /// <summary>A tenant's row named without its tenant: <c>Name:Key:tenant</c> as <c>Name:Key</c>.</summary>
    private static string Neutral(string name) =>
        ShardName.TryParse(name, out ShardName? parsed) && parsed is not null
            ? ShardName.Compose(parsed.Name, parsed.ShardKey, null, parsed.Version).Identity
            : name;
}
