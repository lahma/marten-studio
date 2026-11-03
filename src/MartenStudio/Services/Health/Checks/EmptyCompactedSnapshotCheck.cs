using JasperFx.MultiTenancy;

using MartenStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One damaged stream E09 names.</summary>
/// <param name="Id">The stream's id or key, as text.</param>
/// <param name="TenantId">Its tenant on a conjoined event store; <see langword="null" /> elsewhere.</param>
/// <param name="Version">The version of its compaction watermark, where the empty snapshot is.</param>
internal sealed record EmptySnapshotStream(string Id, string? TenantId, long Version);

/// <summary>What E09 read.</summary>
/// <param name="Probed">How many compacted streams it probed: at most <see cref="HealthEventQueries.MaxCompactedProbes" />.</param>
/// <param name="Capped">Whether there were more compacted streams than it probed.</param>
/// <param name="Damaged">How many of the probed streams hold an empty snapshot at their watermark, in all.</param>
/// <param name="Conjoined">Whether the event store is conjoined-tenanted, so a stream belongs to one tenant.</param>
/// <param name="Streams">The damaged streams it names - at most five per tenant (per database when not conjoined).</param>
/// <param name="InTenant">The damaged streams each listed tenant holds, in all; empty when not conjoined.</param>
internal sealed record EmptySnapshotFacts(
    long Probed,
    bool Capped,
    long Damaged,
    bool Conjoined,
    IReadOnlyList<EmptySnapshotStream> Streams,
    IReadOnlyDictionary<string, long> InTenant);

/// <summary>
/// E09: a stream was compacted into an empty snapshot - a <c>Compacted&lt;T&gt;</c> event whose snapshot is JSON
/// <c>null</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What happened</b>, read at Marten <c>V9.56.0</c> and <c>V9.57.0</c> (<c>EventStore.StreamCompacting.cs</c>, #5797):
/// compaction folds a stream's events into its aggregate and writes the result as one <c>Compacted&lt;T&gt;</c> in place of
/// the last event, deleting the rest. A stream its own aggregate had deleted (<c>ShouldDelete</c>, <c>DeleteEvent&lt;T&gt;</c>)
/// folds to no aggregate, and below 9.57 Marten wrote that nothing as the snapshot all the same - the whole history
/// replaced by a <c>Compacted&lt;T&gt;</c> holding <c>null</c>, which no projection and no replay can make anything of. From
/// 9.57 the stream is left alone, so a compaction run by this Marten (or by the studio at the floor) never does it; E09 finds
/// the streams an older Marten did it to.
/// </para>
/// <para>
/// <b>Tier 2, bounded.</b> It never scans <c>mt_events</c> by type: it reads the streams with a compaction watermark
/// (<c>mt_streams.compacted_version</c>) and probes the one event at that version of each, through the
/// <c>(stream_id, version)</c> key (<see cref="HealthEventQueries.EmptyCompactedSnapshots" />), at most
/// <see cref="HealthEventQueries.MaxCompactedProbes" /> of them - and says so when it stopped there. <c>mt_streams</c> has
/// no index on <c>compacted_version</c>, so the candidate read is a sequential scan of it until 10,001 compacted streams are
/// found - the whole table when few are - and the probe alone is by key. It runs behind "Run deeper checks" under the
/// sweep's statement timeout; running out of time is a "not checked" row, never a failure.
/// </para>
/// <para>
/// <b>What it cannot see.</b> Marten records a compaction watermark since 9.32 (before it the column does not exist, and its
/// migration fills 0), so a stream compacted by 9.0 to 9.31, or by 8.x, has watermark 0 and is never probed. Only the release
/// notes' query, run by hand over <c>mt_events</c>, finds those.
/// </para>
/// <para>
/// <b>A stream id is data, as E03's is.</b> Elsewhere than a conjoined store the ids are evidence at the check's own gate. On
/// a conjoined store a stream belongs to one tenant, so there is one finding per tenant that holds a damaged stream
/// (<see cref="HealthFinding.TenantId" />, its subject the tenant), shown only to a visitor through the activity gate whom
/// the store policy lets see that tenant - and the title and the counts of the others name nothing of it.
/// </para>
/// </remarks>
[HealthCheck("E09", HealthArea.Events, HealthCostTier.BoundedData, "Stream compacted into an empty snapshot")]
internal sealed class EmptyCompactedSnapshotCheck : HealthCheck<EmptySnapshotFacts>
{
    /// <summary>The most stream ids one finding lists.</summary>
    internal const int MaxListed = HealthEventQueries.MaxEmptySnapshotStreams;

    /// <inheritdoc />
    public override async Task<EmptySnapshotFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);
        EventStoreRelation events = EventHealth.RequireEvents(shape);

        if (shape.Streams is not { } streams)
        {
            throw new HealthCheckUnavailableException(
                "The event store's tables do not exist in this database yet, so there are no streams to look at.");
        }

        if (!streams.Has("compacted_version"))
        {
            throw new HealthCheckUnavailableException(
                "mt_streams has no compacted_version column yet - Marten adds it with the event store migration - so there is no "
                + "compaction watermark to start from.");
        }

        if (!events.Has("mt_dotnet_type") || !events.Has("data"))
        {
            throw new HealthCheckUnavailableException(
                "mt_events has no mt_dotnet_type or data column, which the probe reads to recognise an empty snapshot.");
        }

        bool conjoined = context.StoreOptions.Events.TenancyStyle == TenancyStyle.Conjoined;

        var query = HealthEventQueries.EmptyCompactedSnapshots(new EmptySnapshotShape(shape.Schema, conjoined));
        IReadOnlyList<EmptySnapshotRow> rows = await context.Sql
            .QueryAsync(query, HealthEventQueries.ReadEmptySnapshot, cancellationToken)
            .ConfigureAwait(false);

        return From(rows, conjoined);
    }

    /// <summary>Folds the probe's rows.</summary>
    /// <param name="rows">What <see cref="HealthEventQueries.EmptyCompactedSnapshots" /> returned.</param>
    /// <param name="conjoined">Whether the event store is conjoined-tenanted.</param>
    internal static EmptySnapshotFacts From(IReadOnlyList<EmptySnapshotRow> rows, bool conjoined)
    {
        ArgumentNullException.ThrowIfNull(rows);

        EmptySnapshotRow? summary = rows.FirstOrDefault(static x => x.Kind == HealthEventQueries.SummaryRow);
        long candidates = summary?.First ?? 0;

        List<EmptySnapshotStream> streams = [];
        Dictionary<string, long> inTenant = new(StringComparer.Ordinal);

        foreach (EmptySnapshotRow row in rows.Where(static x => x.Kind == HealthEventQueries.StreamRow && x.Stream is not null))
        {
            streams.Add(new EmptySnapshotStream(row.Stream!, conjoined ? row.TenantId : null, row.First));

            if (conjoined && row.TenantId is { } tenant)
            {
                inTenant[tenant] = row.Second;
            }
        }

        return new EmptySnapshotFacts(
            Math.Min(candidates, HealthEventQueries.MaxCompactedProbes),
            candidates > HealthEventQueries.MaxCompactedProbes,
            summary?.Second ?? 0,
            conjoined,
            streams,
            inTenant);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(EmptySnapshotFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Probed == 0)
        {
            return HealthVerdict.Quiet(
                "No stream has a compaction watermark (Marten records one since 9.32). Streams compacted by an older Marten are not seen.");
        }

        if (facts.Damaged == 0)
        {
            return HealthVerdict.Quiet(
                "The event at the compaction watermark of " + Probed(facts) + " holds a snapshot: no stream was compacted into an empty one.");
        }

        if (!facts.Conjoined)
        {
            return HealthVerdict.Of(Finding(facts, facts.Damaged, facts.Streams, tenant: null));
        }

        List<HealthFinding> findings = [];

        foreach (IGrouping<string?, EmptySnapshotStream> group in facts.Streams.GroupBy(static x => x.TenantId, StringComparer.Ordinal))
        {
            if (group.Key is not { } tenant)
            {
                continue;
            }

            long inTenant = facts.InTenant.TryGetValue(tenant, out long counted) ? counted : group.Count();
            findings.Add(Finding(facts, inTenant, [.. group], tenant));
        }

        // Nothing could be attributed to a tenant (a stream row without one): the database-wide count, naming nothing.
        return findings.Count > 0
            ? new HealthVerdict(findings)
            : HealthVerdict.Of(Finding(facts, facts.Damaged, [], tenant: null));
    }

    private static HealthFinding Finding(EmptySnapshotFacts facts, long damaged, IReadOnlyList<EmptySnapshotStream> streams, string? tenant)
    {
        string title = damaged == 1
            ? "A stream was compacted into an empty snapshot"
            : EventHealth.Count(damaged) + " streams were compacted into an empty snapshot";

        List<HealthEvidence> evidence =
        [
            new("Streams compacted into an empty snapshot", EventHealth.Count(damaged) + (tenant is null ? string.Empty : " in this tenant")),
            new(
                "Compacted streams probed",
                EventHealth.Count(facts.Probed) + (facts.Capped ? " - there are more, and they were not probed" : string.Empty)),
        ];

        if (tenant is not null)
        {
            evidence.Add(new HealthEvidence("Tenant", tenant));
        }

        if (streams.Count > 0)
        {
            string listed = string.Join(", ", streams.Select(static x => x.Id + " (version " + x.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"));
            long unlisted = damaged - streams.Count;

            evidence.Add(new HealthEvidence(
                streams.Count == 1 ? "Stream" : "Streams (up to " + MaxListed + ")",
                listed + (unlisted > 0 ? " and " + EventHealth.Count(unlisted) + " more" : string.Empty)));
        }

        return new HealthFinding
        {
            Code = "E09",
            Severity = HealthSeverity.Warning,
            Title = title,
            WhyItMatters =
                "A stream whose own aggregate had deleted it (ShouldDelete, DeleteEvent<T>) folds to no aggregate. Before Marten "
                + "9.57 compaction wrote that nothing as the snapshot all the same: the stream's whole history was deleted and "
                + "replaced by one Compacted<T> event holding null. No projection and no replay can make anything of it, and the "
                + "events it replaced cannot be got back from Marten. Restore those streams from a backup, or from wherever an "
                + "archiver copied them before they were compacted. A compaction run by Marten 9.57.0 or later, "
                + "including the studio's, leaves such a stream alone and never does this.",
            Subject = tenant,
            TenantId = tenant,
            Evidence = evidence,
            Caveat =
                "Found by reading the event at the compaction watermark (mt_streams.compacted_version) of each compacted stream, "
                + "through its (stream_id, version) key - at most " + EventHealth.Count(HealthEventQueries.MaxCompactedProbes)
                + " streams, read once when somebody asked for the deeper checks - and looking for a JasperFx.Events.Compacted "
                + "event whose snapshot is JSON null. A stream compacted again since is judged at its new watermark. Marten "
                + "records the watermark since 9.32, so a stream compacted by an older Marten has none and is not seen here."
                + (tenant is null
                    ? string.Empty
                    : " The streams belong to one tenant, so they are shown only to a visitor who may see that tenant."
                        + (TenantCapHit(facts)
                            ? " The tenant cap was hit: only " + HealthEventQueries.MaxEmptySnapshotTenants
                                + " tenants are listed, and other tenants hold damaged streams too."
                            : string.Empty)),
        };
    }

    /// <summary>Whether damaged streams of more tenants exist than the probe lists: the listed tenants hold fewer than were found.</summary>
    private static bool TenantCapHit(EmptySnapshotFacts facts) =>
        facts.Conjoined && facts.InTenant.Values.Sum() < facts.Damaged;

    /// <summary>"3 compacted streams", "the first 10,000 compacted streams".</summary>
    private static string Probed(EmptySnapshotFacts facts) =>
        facts.Capped
            ? "the first " + EventHealth.Count(facts.Probed) + " compacted streams"
            : EventHealth.Count(facts.Probed) + " compacted stream" + (facts.Probed == 1 ? string.Empty : "s");
}
