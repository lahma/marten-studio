using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health;

/// <summary>What the Health page asks to offer and to run a finding's actions (1.2).</summary>
/// <remarks>
/// An interface so the page can be rendered in a component test with a fake behind it - the same seam every other area has
/// (D12).
/// </remarks>
internal interface IHealthActionService
{
    /// <summary>
    /// The actions <paramref name="finding" /> offers: the registry filtered by each action's own <c>AppliesTo</c>. Pure - no policy
    /// is asked, nothing is read, and a finding about a gated schema offers none (fail closed).
    /// </summary>
    /// <param name="finding">The finding, as the visitor's report shows it.</param>
    IReadOnlyList<HealthActionOffer> OffersFor(HealthReportFinding finding);

    /// <summary>
    /// Runs one action for this visitor, under at least the finding's own gate, re-asked now. Never throws but for cancellation: a
    /// refusal, a timeout and a failure are values.
    /// </summary>
    /// <param name="scope">The scope the visitor selected.</param>
    /// <param name="finding">The finding the action was pressed on, as the visitor's report shows it.</param>
    /// <param name="actionId">The action's id, as <see cref="OffersFor" /> named it.</param>
    /// <param name="cancellationToken">Cancels the run; the page left.</param>
    Task<HealthActionResult> RunAsync(StudioScope scope, HealthReportFinding finding, string actionId, CancellationToken cancellationToken = default);
}

/// <summary>One run of one action per database at once: the runs going now, by store, database and action.</summary>
/// <remarks>
/// A singleton, because a database is everybody's. A second visitor is <em>refused, never joined</em>: joining would hand one
/// visitor's result to another whose gates differ (unlike <see cref="Maintenance.BloatMeasureFlights" />, whose result is a
/// measure of a table both may see).
/// </remarks>
internal sealed class HealthActionFlights
{
    private readonly ConcurrentDictionary<string, byte> running = new(StringComparer.Ordinal);

    /// <summary>Takes the key; <see langword="false" /> when a run holds it.</summary>
    /// <param name="key">Store key, database identity and action id.</param>
    public bool TryEnter(string key) => running.TryAdd(key, 0);

    /// <summary>Releases the key.</summary>
    /// <param name="key">What <see cref="TryEnter" /> took.</param>
    public void Exit(string key) => running.TryRemove(key, out _);

    /// <summary>How many runs are going now, for a test.</summary>
    internal int Count => running.Count;
}

/// <summary>
/// Runs a finding's on-demand action for one visitor (1.2): the gates re-asked, the capability and write policy when the action
/// declares one, one run per tab and per database, the cost bounded, and every run audited.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is at least the finding's own, asked now.</b> The page's button is a courtesy: a circuit is a client-drivable
/// object, so each press asks the finding's gate (and its tenant's store policy) again in this service, through the one
/// <see cref="HealthVisitorGate" /> the report asks (hard rule 5). An action may ask further gates through the context and
/// answer a refusal itself.
/// </para>
/// <para>
/// <b>Audited, always.</b> A press that got past the "already running in this tab" and "no such action" checks is one entry in
/// the Activity ring - success, failure, timeout and every refusal - with a count in its message and never a value the action
/// read. <b>Logged</b> as 9223 (a failure, Warning) or 9224 (a refusal, Debug).
/// </para>
/// <para>
/// <b>Holds nothing.</b> Its fields are the services and one flag; no result, finding or row is ever a field, so a result cannot
/// reach another circuit's service and there is nothing to cache. The result goes to the page that asked and is dropped there.
/// </para>
/// </remarks>
internal sealed class HealthActionService : IHealthActionService
{
    private readonly IStudioSettings settings;
    private readonly StudioSourceResolver resolver;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly HealthActionFlights flights;
    private readonly IReadOnlyList<IHealthFindingAction> actions;
    private readonly ILogger<HealthActionService> logger;
    private readonly TimeProvider time;
    private int running;

    public HealthActionService(
        IStudioSettings settings,
        StudioSourceResolver resolver,
        StudioAuthorization authorization,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        HealthActionFlights flights,
        IEnumerable<IHealthFindingAction> actions,
        ILogger<HealthActionService> logger,
        TimeProvider? time = null)
    {
        this.settings = settings;
        this.resolver = resolver;
        this.authorization = authorization;
        this.capabilities = capabilities;
        this.audit = audit;
        this.flights = flights;
        this.actions = [.. actions];
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IReadOnlyList<HealthActionOffer> OffersFor(HealthReportFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        if (finding.Finding.GatedSchemas.Count > 0)
        {
            return [];
        }

        return [.. actions.Where(x => x.AppliesTo(finding.Finding)).Select(static x => new HealthActionOffer(x.Id, x.Label, x.Description) { Gate = x.Gate })];
    }

    /// <inheritdoc />
    public async Task<HealthActionResult> RunAsync(
        StudioScope scope,
        HealthReportFinding finding,
        string actionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(actionId);

        // 0. The action, by id, that this finding offers. Not audited: a press the page could not have offered is a programming
        // error or a client driving a control it was not given, and it read nothing.
        IHealthFindingAction? action = finding.Finding.GatedSchemas.Count > 0
            ? null
            : actions.FirstOrDefault(x => string.Equals(x.Id, actionId, StringComparison.Ordinal) && x.AppliesTo(finding.Finding));

        if (action is null)
        {
            return Unaudited(scope, actionId, "This finding offers no such action.");
        }

        // 1. One at a time in this circuit. Not audited: nothing was asked and nothing is read.
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            return Unaudited(scope, actionId, "Another action is running in this tab; wait for it to finish.");
        }

        try
        {
            return await RunOwnedAsync(scope, finding, action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private async Task<HealthActionResult> RunOwnedAsync(
        StudioScope scope,
        HealthReportFinding finding,
        IHealthFindingAction action,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = time.GetUtcNow();

        // 2. The capability, if the action declares one: the switch first, refused through the same audit path every capability is.
        if (action.Capability is { } capability)
        {
            try
            {
                capabilities.Require(capability);
            }
            catch (StudioCapabilityDeniedException denial)
            {
                audit.RecordCapabilityDenied(denial, action.AuditAction, action.AuditTarget);
                logger.HealthActionRefused(settings.Text.Product, action.Id, scope.StoreKey, scope.DatabaseId, denial.Message);
                return HealthActionResult.Refused(denial.Message, now);
            }
        }

        // 3. The scope, as the visitor selected it: the store policy is the resolver's first question.
        // Steps 3 to 6 ask the host's own code - its authorization policies, its tenant listing - and anything that code throws is
        // the press's failure, not the circuit's: logged as 9223, audited, answered as Failed. No flight is held until step 7.
        ResolvedSource resolved;
        StudioScope whole;
        StudioScope audited;
        HealthVisitorGate gate;

        try
        {
            try
            {
                resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);
            }
            catch (StudioNotAuthorizedException exception)
            {
                audit.RecordScopeDenied(scope, DatabaseAccess.StorePolicyName(settings), action.AuditAction, action.AuditTarget);
                logger.HealthActionRefused(settings.Text.Product, action.Id, scope.StoreKey, scope.DatabaseId, exception.Message);
                return HealthActionResult.Refused(exception.Message, now);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
            {
                return Audited(scope, action, capability: null, HealthActionResult.Refused(exception.Message, now));
            }

            // 4. The database as a whole, by the registered key and its identity - never the visitor's spelling of either (D29) - and
            // this visitor's gates.
            whole = new(resolved.Source.Key, resolved.Target.Identity, null);
            audited = whole with { TenantId = scope.TenantId };

            // 4b. The finding is this database's. A finding is bound to the database the report it came from was read for (its mute key
            // names store, database and finding), and the page presses with the scope selected *now*: between a scope change and the
            // page dropping its report, a finding of database A could be pressed with scope B and be run against B. Refused here,
            // before any gate or read, and not audited, as every refusal before a gate is asked is (step 0, step 1): nothing was read.
            if (!string.Equals(finding.MuteKey, HealthService.MuteKeyOf(whole.StoreKey, whole.DatabaseId, finding.Finding), StringComparison.Ordinal))
            {
                return Unaudited(scope, action.Id, "This finding is from another database; run the checks again.");
            }

            gate = new(settings, authorization, capabilities, whole);

            // 5. At least the finding's own gate, and its tenant's store policy.
            HealthGate required = Stricter(action.Gate, HealthGateRule.Required(finding.Finding));

            if (!await gate.PassesAsync(required).ConfigureAwait(false))
            {
                return Audited(audited, action, action.Capability, HealthActionResult.Refused(
                    "This action " + gate.WhyNot(required) + ".", now));
            }

            if (finding.Finding.TenantId is { } findingTenant && !await gate.TenantAsync(findingTenant).ConfigureAwait(false))
            {
                return Audited(audited, action, action.Capability, HealthActionResult.Refused(
                    "This finding names a tenant the store policy does not show you.", now));
            }

            // 6. The write policy, if a capability is declared: the question the capability exists to be asked.
            if (action.Capability is { } declared
                && !await authorization.IsAuthorizedAsync(whole, declared, cancellationToken).ConfigureAwait(false))
            {
                return Audited(audited, action, declared, HealthActionResult.Refused(
                    "Your account's write policy does not allow " + declared + " for this database.", now));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.HealthActionFailed(settings.Text.Product, exception, action.Id, scope.StoreKey, scope.DatabaseId, (exception as PostgresException)?.SqlState);

            return Audited(scope, action, capability: null, HealthActionResult.Failed(
                "The action failed (" + exception.GetType().Name + "); the server log has the details.", now));
        }

        // 7. One run of this action per database, process-wide. Refused, never joined.
        string flight = whole.StoreKey + "|" + whole.DatabaseId + "|" + action.Id;

        if (!flights.TryEnter(flight))
        {
            return Audited(audited, action, action.Capability, HealthActionResult.Refused(
                "This action is already running on this database; one runs at a time. Try again when it has finished.", now));
        }

        HealthActionResult result;
        long started = Stopwatch.GetTimestamp();

        try
        {
            // 8. The action, in the read-only session's bounds.
            ReadOnlySqlOptions session = new()
            {
                StatementTimeout = settings.QueryTimeout,
                Role = settings.SqlConsoleRole,
            };

            int commandTimeout = (int) Math.Ceiling(settings.QueryTimeout.TotalSeconds) + 5;

            result = await action
                .RunAsync(new HealthActionContext(resolved, whole, finding, gate, session, commandTimeout), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The page was left: nothing to show and nothing to audit.
            throw;
        }
        catch (Exception exception)
        {
            logger.HealthActionFailed(
                settings.Text.Product,
                exception,
                action.Id,
                whole.StoreKey,
                whole.DatabaseId,
                (exception as PostgresException)?.SqlState);

            result = HealthActionResult.Failed(
                "The action failed (" + exception.GetType().Name + "); the server log has the details.", now);
        }
        finally
        {
            flights.Exit(flight);
        }

        TimeSpan took = Stopwatch.GetElapsedTime(started);
        List<HealthActionRow> rows = [.. result.Rows.Take(HealthActionResult.MaxRows)];

        result = result with
        {
            RanAt = time.GetUtcNow(),
            Took = took,
            Rows = rows,
            Notes = rows.Count < result.Rows.Count
                ? [.. result.Notes, "Only the first " + HealthActionResult.MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture) + " rows are shown."]
                : result.Notes,
        };

        // 9. Audited, whatever the outcome; a refusal the action returned is logged there too.
        return Audited(audited, action, result.AuditCapability ?? action.Capability, result);
    }

    /// <summary>A refusal that is not audited: logged at Debug, and returned in words.</summary>
    private HealthActionResult Unaudited(StudioScope scope, string actionId, string reason)
    {
        logger.HealthActionRefused(settings.Text.Product, actionId, scope.StoreKey, scope.DatabaseId, reason);
        return HealthActionResult.Refused(reason, time.GetUtcNow());
    }

    /// <summary>The audit entry for a press, and the result as it is.</summary>
    private HealthActionResult Audited(StudioScope scope, IHealthFindingAction action, StudioCapability? capability, HealthActionResult result)
    {
        bool succeeded = result.Outcome == HealthActionOutcome.Done;

        audit.Record(
            action.AuditAction,
            action.AuditTarget,
            succeeded,
            // Never the summary: it is what the action read. An action says what the ring may hold in AuditMessage, and without
            // one a refusal holds its reason (a studio sentence) and a done press holds nothing but that it was done.
            result.AuditMessage ?? result.Reason ?? (succeeded ? "Done." : null),
            capability,
            scope);

        if (result.Outcome == HealthActionOutcome.Refused)
        {
            logger.HealthActionRefused(settings.Text.Product, action.Id, scope.StoreKey, scope.DatabaseId, result.Reason ?? "refused");
        }

        return result;
    }

    private static HealthGate Stricter(HealthGate first, HealthGate second) => (HealthGate) Math.Max((int) first, (int) second);
}
