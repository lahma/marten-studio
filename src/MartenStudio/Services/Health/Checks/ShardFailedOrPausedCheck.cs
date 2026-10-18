using System.Globalization;

using JasperFx.Events.Projections;

using MartenStudio.Services.Projections;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What D05 reads.</summary>
/// <param name="Configuration">The store's daemon settings.</param>
/// <param name="Rows">
/// The progression rows the extended tracking says are not running or carry a failure, or <see langword="null" /> when
/// the store does not write that tracking (or the table lacks it) and only the daemon in this process can say.
/// </param>
/// <param name="InProcess">The daemon in this process, its agents, and what the studio recorded.</param>
internal sealed record ShardFailedOrPausedFacts(
    DaemonCheckConfiguration Configuration,
    IReadOnlyList<DaemonShardRow>? Rows,
    DaemonInProcess InProcess);

/// <summary>
/// D05: a shard has failed or is paused.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two sources, and the fresher wins.</b> The daemon hosted in this process says what each agent is doing right now
/// (<c>ISubscriptionAgent.Status</c> and, from JasperFx 2.74, <c>Failure</c>). The progression table says what the
/// node that ran the shard last wrote - under <c>EnableExtendedProgressionTracking</c> only, which writes the status,
/// pause reason and classified failure on every status change (and is off by default: K02). A row that says Paused
/// on a node that has since gone keeps saying it, so the time it was written is evidence.
/// </para>
/// <para>
/// <b>What fires.</b> Stopped with a failure: Critical - the error policy gave up and nothing restarts it. Paused: Warning
/// - Marten pauses a shard after a failure and restarts it after the policy's pause, so a pause that lasts is one that
/// keeps failing. Stopped with no failure is somebody's stop (the studio's coordinator pause, a host shutting down) and
/// is not reported; whether anything runs the daemon at all is D02's. A shard whose projection is being rebuilt is
/// stopped by the rebuild, on purpose.
/// </para>
/// <para>
/// <b>Classified where JasperFx classifies.</b> A start the studio asked for and the daemon refused carries
/// <c>ShardStartException.Reason</c> and whether it is transient (JasperFx 2.76+, F1); a live failure is classified as
/// transient or permanent by <c>TransientFailures.IsTransient</c> where that exists (JasperFx 2.89, in no Marten release
/// yet). A shard of one tenant names that tenant, so its finding is shown only through the activity gate and only to a
/// visitor the store policy lets see the tenant.
/// </para>
/// </remarks>
[HealthCheck("D05", HealthArea.Daemon, HealthCostTier.Catalog, "Shard failed or paused")]
internal sealed class ShardFailedOrPausedCheck : HealthCheck<ShardFailedOrPausedFacts>
{
    /// <inheritdoc />
    public override async Task<ShardFailedOrPausedFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        DaemonCheckConfiguration configuration = DaemonCheckConfiguration.Read(context);
        DaemonInProcess inProcess = await DaemonInProcess.ReadAsync(context, configuration.AsyncShards, cancellationToken).ConfigureAwait(false);

        if (!configuration.ExtendedProgressionTracking)
        {
            return new ShardFailedOrPausedFacts(configuration, null, inProcess);
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);

        if (shape.Progression is null || !shape.ProgressionColumns.Contains("agent_status", StringComparer.Ordinal))
        {
            return new ShardFailedOrPausedFacts(configuration, null, inProcess);
        }

        IReadOnlyList<DaemonShardRow> rows = await DaemonShardRow
            .ReadProblemsAsync(context, shape, configuration.EventSchema, cancellationToken)
            .ConfigureAwait(false);

        return new ShardFailedOrPausedFacts(configuration, rows, inProcess);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ShardFailedOrPausedFacts facts, HealthEvaluation evaluation)
    {
        DaemonInProcess inProcess = facts.InProcess;
        bool hostedHere = inProcess.Hosting == DaemonHostingState.Hosted;

        if (facts.Rows is null && !hostedHere)
        {
            return HealthVerdict.Quiet(
                "Extended progression tracking is off (K02) and no daemon runs in this process, so nothing records whether a shard "
                + "is paused or why it failed.");
        }

        Dictionary<string, Problem> problems = new(StringComparer.OrdinalIgnoreCase);

        foreach (DaemonShardRow row in facts.Rows ?? [])
        {
            if (!ProjectionDataService.IsBookkeepingRow(row.Name))
            {
                problems[row.Name] = new Problem(row.Name, row.AgentStatus ?? "Unknown", row.FailureCategory, row.PauseReason,
                    row.Heartbeat, row.FailureEventSequence, row.FailureEventType, FromThisProcess: false);
            }
        }

        // The daemon here is fresher than any row it wrote, so its word replaces the row's for the shards it runs.
        foreach (DaemonAgentFact agent in inProcess.Agents)
        {
            if (string.Equals(agent.Status, "Running", StringComparison.OrdinalIgnoreCase))
            {
                problems.Remove(agent.ShardName);
                continue;
            }

            problems.TryGetValue(agent.ShardName, out Problem? persisted);
            problems[agent.ShardName] = new Problem(
                agent.ShardName,
                agent.Status,
                agent.FailureCategory ?? persisted?.FailureCategory,
                agent.FailureMessage ?? persisted?.Reason,
                agent.PausedAt ?? persisted?.Since,
                persisted?.FailureEventSequence,
                persisted?.FailureEventType,
                FromThisProcess: true);
        }

        List<HealthFinding> findings = [];

        foreach (Problem problem in problems.Values.OrderBy(static x => x.Shard, StringComparer.OrdinalIgnoreCase))
        {
            if (inProcess.IsKnownRebuilding(problem.Shard))
            {
                continue;
            }

            bool paused = string.Equals(problem.Status, "Paused", StringComparison.OrdinalIgnoreCase);

            if (!paused && problem.FailureCategory is not { Length: > 0 })
            {
                // A stop with no failure: the studio's own coordinator pause, a shutdown, an operator's stop.
                continue;
            }

            findings.Add(Finding(problem, paused, inProcess) with { DocumentTypes = facts.Configuration.PublishedTypesOf(problem.Shard) });
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return facts.Rows is null
            ? HealthVerdict.Quiet("Every agent the daemon in this process runs is running. Extended progression tracking is off (K02), so shards run elsewhere are not known.")
            : HealthVerdict.Quiet();
    }

    private static HealthFinding Finding(Problem problem, bool paused, DaemonInProcess inProcess)
    {
        string? tenant = ShardName.TryParse(problem.Shard, out ShardName? name) ? name?.TenantId : null;

        List<HealthEvidence> evidence =
        [
            new("Status", problem.Status + (problem.FromThisProcess ? " (the daemon in this process)" : " (mt_event_progression, as its node last wrote it)")),
        ];

        if (tenant is not null)
        {
            evidence.Add(new HealthEvidence("Tenant", tenant, HealthGate.Activity));
        }

        if (problem.Since is { } since)
        {
            evidence.Add(new HealthEvidence(paused ? "Paused at" : "Stopped at", since.ToString("u", CultureInfo.InvariantCulture)));
        }

        if (problem.FailureCategory is { Length: > 0 } category)
        {
            evidence.Add(new HealthEvidence("Failure", category));
        }

        if (problem.Reason is { Length: > 0 } reason)
        {
            evidence.Add(new HealthEvidence("Reason", reason));
        }

        if (problem.FailureEventSequence is { } sequence)
        {
            evidence.Add(new HealthEvidence("Failed on event", DaemonCheckConfiguration.Number(sequence)
                + (problem.FailureEventType is { Length: > 0 } type ? " (" + type + ")" : string.Empty)));
        }

        if (inProcess.LiveExceptions?.GetValueOrDefault(problem.Shard) is { } live && DaemonLightUp.IsTransient(live) is { } transient)
        {
            evidence.Add(new HealthEvidence("Transient or permanent", transient
                ? "transient: JasperFx expects a retry to succeed"
                : "permanent: a retry will fail the same way until the cause is fixed"));
        }

        if (inProcess.StartRefusals.GetValueOrDefault(problem.Shard) is { } refusal)
        {
            evidence.Add(new HealthEvidence(
                "Last start from Marten Studio",
                "refused " + refusal.RefusedAt.ToString("u", CultureInfo.InvariantCulture)
                + (refusal.Reason is { Length: > 0 } classified ? ": " + classified : string.Empty)
                + (refusal.IsTransient switch
                {
                    true => ", which a retry is expected to resolve",
                    false => ", which a retry will not resolve on its own",
                    null => string.Empty,
                })));
        }

        bool critical = !paused;

        return new HealthFinding
        {
            Code = "D05",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Subject = problem.Shard,
            TenantId = tenant,
            Title = critical
                ? "Shard " + ShardLabel(problem.Shard, tenant) + " failed and stopped"
                : "Shard " + ShardLabel(problem.Shard, tenant) + " is paused",
            WhyItMatters = critical
                ? "The daemon's error policy gave up on this shard: it is stopped, and nothing restarts it until somebody does - so the "
                  + "projection it feeds stops changing. The failure below is what stopped it; fix the cause, then start or rebuild it."
                : "Marten pauses a shard after a failure and starts it again after the error policy's pause. A pause the advisor sees is one "
                  + "that is still in effect - and one that keeps coming back is a shard failing on every attempt.",
            Evidence = evidence,
            Caveat = problem.FromThisProcess
                ? "Read from the daemon hosted in this process."
                : "Read from mt_event_progression, which the node that ran the shard wrote when its status last changed; if that node is gone, "
                  + "this is the last thing it said.",
            Remedy = RemedyFor(problem.FailureCategory),
        };
    }

    /// <summary>The <c>StoreOptions</c> switch that turns a failure of this category into a dead letter, when there is one.</summary>
    private static HealthRemedy? RemedyFor(string? category) => category switch
    {
        nameof(JasperFx.Events.Daemon.ShardFailureCategory.ApplyEvent) => new HealthRemedy(
            "opts.Projections.Errors.SkipApplyErrors = true;",
            "An event the projection's code throws on becomes a dead letter and the shard carries on, instead of stopping. The "
            + "projection then never reflects that event: fix the code and rebuild it, or rewind from the dead letter."),
        nameof(JasperFx.Events.Daemon.ShardFailureCategory.EventSerialization) => new HealthRemedy(
            "opts.Projections.Errors.SkipSerializationErrors = true;",
            "An event that cannot be deserialized becomes a dead letter instead of stopping the shard - usually an event type whose "
            + "shape changed without an upcaster."),
        nameof(JasperFx.Events.Daemon.ShardFailureCategory.UnknownEventType) => new HealthRemedy(
            "opts.Projections.Errors.SkipUnknownEvents = true;",
            "An event of a type this process cannot resolve is skipped instead of stopping the shard. Registering the type "
            + "(opts.Events.AddEventType) fixes the cause; the exception's own message names it."),
        _ => null,
    };

    /// <summary>The shard as a title names it: the tenant's shard is named by its projection, the tenant left to the gated evidence.</summary>
    private static string ShardLabel(string shard, string? tenant) =>
        tenant is null ? shard : shard[..shard.LastIndexOf(':')] + " (one tenant's)";

    /// <summary>One shard that is not running, merged from its row and the daemon here.</summary>
    private sealed record Problem(
        string Shard,
        string Status,
        string? FailureCategory,
        string? Reason,
        DateTimeOffset? Since,
        long? FailureEventSequence,
        string? FailureEventType,
        bool FromThisProcess);
}
