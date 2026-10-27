using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Maintenance;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;
using PostgresStudio.Services.Storage;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// <see cref="IPostgresDiagnostics" />: the studio's findings and the facts behind its pages, for host code, through the host
/// gate (D31, API design §1-§3). Registered by <c>AddPostgresStudio()</c> and by <c>AddMartenStudio()</c>, a singleton over
/// the studio's own singletons.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pages' reads, never new ones.</b> A snapshot is the regular sweep (<see cref="HealthSweepCache" />, the one every page
/// shares, one per <c>HealthInterval</c> per database), and, as asked, the deeper and statement sweeps and the Storage,
/// Statements, Server and sessions facts - each through the cache the page reads it through. A read the caches already hold
/// costs nothing; one they do not passes the process's <see cref="FreshReadLimiter" />, two at a time.
/// </para>
/// <para>
/// <b>No visitor.</b> A database is resolved with no policy (<see cref="IStudioSourceProvider.ResolveUnattendedAsync" />, the
/// health export's path): only one a source lists or a tenancy holds, never one a tenancy would build (hard rule 14, D27). No
/// capability or <c>ReadOnly</c> is asked: they are visitors' switches, and the host's is
/// <see cref="PostgresDiagnosticsRequest.IncludeActivity" /> (<see cref="HostGate" />).
/// </para>
/// <para>
/// <b>Never served by the studio, never audited.</b> Nothing in either package hands a result to an endpoint, a log line above
/// Debug (event 9250 carries codes and counts), a metric, the Activity ring or the health check (<c>DiagnosticsApiIsNotServedTests</c>).
/// </para>
/// <para>
/// <b>Failure is a value.</b> An unknown source or database is <see cref="PostgresReadOutcome.NotFound" />; one that cannot be
/// opened, or whose every catalog check failed, is <see cref="PostgresReadOutcome.Failed" />, with the studio's sentence; a
/// section that fails is that section's outcome. Only a null argument, cancellation of the caller's own wait and options that
/// fail validation are thrown.
/// </para>
/// </remarks>
internal sealed class PostgresDiagnosticsService : IPostgresDiagnostics
{
    private readonly IServiceProvider services;
    private readonly IServiceScopeFactory scopes;
    private readonly HealthCheckRegistry registry;
    private readonly HealthSweepCache cache;
    private readonly FreshReadLimiter limiter;
    private readonly StudioLogThrottle throttle;
    private readonly ILogger<PostgresDiagnosticsService> logger;
    private readonly TimeProvider timeProvider;
    private readonly IStudioRuntime runtime;
    private readonly StudioDescriptor descriptor;
    private readonly Lazy<IReadOnlyList<PostgresCheck>> catalogue;

    public PostgresDiagnosticsService(
        IServiceProvider services,
        IServiceScopeFactory scopes,
        HealthCheckRegistry registry,
        HealthSweepCache cache,
        FreshReadLimiter limiter,
        StudioLogThrottle throttle,
        ILogger<PostgresDiagnosticsService> logger,
        StudioDescriptor descriptor,
        TimeProvider? timeProvider = null,
        IStudioRuntime? runtime = null)
    {
        this.services = services;
        this.scopes = scopes;
        this.registry = registry;
        this.cache = cache;
        this.limiter = limiter;
        this.throttle = throttle;
        this.logger = logger;
        this.descriptor = descriptor;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.runtime = runtime ?? NoStudioRuntime.Instance;
        catalogue = new Lazy<IReadOnlyList<PostgresCheck>>(
            () => [.. registry.All.Select(static x => DiagnosticsMapper.Check(x.Info)).OrderBy(static x => x.Code, StringComparer.Ordinal)]);
    }

    /// <inheritdoc />
    public IReadOnlyList<PostgresCheck> Checks => catalogue.Value;

    /// <inheritdoc />
    public async IAsyncEnumerable<PostgresDatabaseRef> ListDatabasesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The source provider is a circuit's service; a host's call has none, so it gets a scope of its own, as the export does.
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IStudioSourceProvider sources = scope.ServiceProvider.GetRequiredService<IStudioSourceProvider>();

        await foreach (StudioListedDatabase entry in sources.ListUnattendedAsync(cancellationToken).ConfigureAwait(false))
        {
            // A source that could not be listed has logged why (9210, 9213) and is left out, as a tenancy that holds nothing is.
            if (entry.Outcome == StudioSweepTargetOutcome.Database && entry.DatabaseId is { } database)
            {
                yield return new PostgresDatabaseRef(entry.Source.Key, database);
            }
        }
    }

    /// <inheritdoc />
    public string GetStudioPath(PostgresDatabaseRef database, PostgresStudioPage page)
    {
        ArgumentNullException.ThrowIfNull(database);

        string route = page switch
        {
            PostgresStudioPage.Health => HealthLinks.Route,
            PostgresStudioPage.Sessions or PostgresStudioPage.Blocking => SessionLinks.Route,
            PostgresStudioPage.Statements => StatementLinks.Route,
            PostgresStudioPage.Maintenance => MaintenanceLinks.Route,
            PostgresStudioPage.Storage => StorageLinks.Route,
            PostgresStudioPage.Server => ServerLinks.Route,
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

        IReadOnlyList<KeyValuePair<string, string?>> query = page == PostgresStudioPage.Blocking
            ? [new KeyValuePair<string, string?>(SessionLinks.ViewParameter, SessionLinks.BlockingView)]
            : [];

        return HealthLinks.Relative(descriptor.SourceQueryParameter, route, new StudioScope(database.Source, database.Database, null), query);
    }

    /// <inheritdoc />
    public async Task<PostgresDiagnosticsSnapshot> GetSnapshotAsync(
        PostgresDatabaseRef database,
        PostgresDiagnosticsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(request);

        // Options that fail validation throw here, from the host's own call, as a page's first read would.
        IStudioSettings settings = services.GetRequiredService<IStudioSettings>();
        _ = settings.EffectiveHealthInterval;

        PostgresDiagnosticsSnapshot snapshot = await ReadAsync(database, request, settings, cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.DiagnosticsSnapshotRead(
                settings.Text.Product,
                snapshot.Database.Source,
                snapshot.Database.Database,
                DiagnosticsJson.Name(snapshot.Outcome),
                snapshot.Findings.Count,
                snapshot.FindingsWithheld,
                Sections(request));
        }

        return snapshot;
    }

    private async Task<PostgresDiagnosticsSnapshot> ReadAsync(
        PostgresDatabaseRef asked,
        PostgresDiagnosticsRequest request,
        IStudioSettings settings,
        CancellationToken cancellationToken)
    {
        PostgresDiagnosticsSnapshot empty = new()
        {
            SchemaVersion = DiagnosticsJson.SchemaVersion,
            Producer = DiagnosticsJson.ProducerFor(settings.Text.Product),
            GeneratedAt = timeProvider.GetUtcNow(),
            Database = asked,
        };

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IStudioSourceProvider sources = scope.ServiceProvider.GetRequiredService<IStudioSourceProvider>();

        StudioUnattendedResolution resolution;

        try
        {
            resolution = await sources.ResolveUnattendedAsync(asked.Source, asked.Database, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return empty with
            {
                Outcome = PostgresReadOutcome.Failed,
                Reason = "The database could not be resolved (" + exception.GetType().Name + ").",
            };
        }

        if (resolution.Resolved is not { } resolved)
        {
            return empty with
            {
                Outcome = resolution.Outcome == StudioUnattendedOutcome.NotFound ? PostgresReadOutcome.NotFound : PostgresReadOutcome.Failed,
                Reason = resolution.Reason,
            };
        }

        PostgresDatabaseRef named = new(resolved.Source.Key, resolved.Target.Identity);

        try
        {
            return await SnapshotAsync(empty with { Database = named }, resolved, request, settings, scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Nothing below should throw - every read is a value - but a host's call is never the one that finds out it did.
            LogSectionFailure("snapshot", exception, named);

            return empty with
            {
                Database = named,
                Outcome = PostgresReadOutcome.Failed,
                Reason = "The snapshot could not be put together (" + exception.GetType().Name + "); the server log has it (event 9251).",
            };
        }
    }

    private async Task<PostgresDiagnosticsSnapshot> SnapshotAsync(
        PostgresDiagnosticsSnapshot snapshot,
        ResolvedSource resolved,
        PostgresDiagnosticsRequest request,
        IStudioSettings settings,
        IServiceProvider scope,
        CancellationToken cancellationToken)
    {
        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;
        StudioSourceKind kind = resolved.Source.Kind;

        HostGate gate = new(request.IncludeActivity, HostGate.ScreenFor(Classifier(scope, resolved), settings.IsDocumentTypeVisible is not null));
        Dictionary<string, string> withheld = new(StringComparer.Ordinal);

        // The regular sweep, always: the findings, the server that answered and its capacity come from it, as on the Health page.
        HealthSweep regular = await limiter.RunAsync(
                cache.IsFresh(storeKey, databaseId, HealthSweepKind.Regular),
                token => cache.GetAsync(resolved, HealthSweepKind.Regular, null, token),
                cancellationToken)
            .ConfigureAwait(false);

        PostgresServerInfo? server = regular.TryShared<PostgresServerFacts>(HealthServerQueries.PlatformKey, out PostgresServerFacts? facts)
            ? facts.ToServerInfo()
            : null;
        PostgresComputeCapacity? compute = await ComputeAsync(regular, resolved, facts, cancellationToken).ConfigureAwait(false);

        bool reached = Reached(regular, out string? unreachable);

        snapshot = snapshot with
        {
            Outcome = reached ? PostgresReadOutcome.Read : PostgresReadOutcome.Failed,
            Reason = unreachable,
            Server = server,
            Compute = compute,
        };

        if (request.Findings)
        {
            List<HealthCheckResult> results = [.. regular.Results];

            // A database the regular sweep could not reach is asked nothing more: no costlier sweep is started or read.
            if (reached
                && registry.HasTier(HealthCostTier.BoundedData, kind)
                && await CostlierAsync(resolved, HealthSweepKind.Deeper, request.RunDeeperChecks, cancellationToken).ConfigureAwait(false) is { } deeper)
            {
                results.AddRange(deeper.Results);
            }

            if (reached
                && registry.HasTier(HealthCostTier.Statements, kind)
                && request.IncludeActivity
                && await CostlierAsync(resolved, HealthSweepKind.Statements, request.RunDeeperChecks, cancellationToken).ConfigureAwait(false) is { } statements)
            {
                results.AddRange(statements.Results);
            }

            (IReadOnlyList<PostgresFinding> findings, IReadOnlyList<PostgresCheck> checks, int count) =
                DiagnosticsSnapshotBuilder.Findings(results, gate, runtime, descriptor.SourceQueryParameter, new StudioScope(storeKey, databaseId, null));

            // A statement check the request did not include is still a check of this database: listed as withheld, whether or
            // not it would find anything - read nothing to say so (the Health page's "findings not shown to you" row).
            IEnumerable<PostgresCheck> unrun = request.IncludeActivity
                ? []
                : registry.All
                    .Where(x => x.Info.Tier == HealthCostTier.Statements && x.RunsAgainst(kind))
                    .Select(static x => DiagnosticsMapper.Check(x.Info) with { Outcome = PostgresReadOutcome.Withheld });

            snapshot = snapshot with
            {
                Findings = findings,
                Checks = [.. checks.Concat(unrun).OrderBy(static x => x.Code, StringComparer.Ordinal)],
                FindingsMeasuredAt = regular.MeasuredAt,
                FindingsWithheld = count,
            };
        }

        if (!reached)
        {
            // Every check that reads the catalog already failed to connect: each section asked for says so, without a second
            // failed connection per section and without a 9251 Warning per section for what the sweep has already logged.
            return Unreached(snapshot, request, timeProvider.GetUtcNow());
        }

        if (request.Storage)
        {
            snapshot = snapshot with { Storage = await StorageAsync(resolved, scope, settings, cancellationToken).ConfigureAwait(false) };
        }

        if (request.Statements)
        {
            if (!request.IncludeActivity)
            {
                withheld["statements"] = HostGate.WithheldSection;
            }
            else
            {
                snapshot = snapshot with
                {
                    Statements = await StatementsAsync(resolved, scope, gate, settings, request.RunDeeperChecks, cancellationToken).ConfigureAwait(false),
                };
            }
        }

        if (request.Settings)
        {
            snapshot = snapshot with { Settings = await SettingsAsync(resolved, scope, cancellationToken).ConfigureAwait(false) };
        }

        if (request.Connections)
        {
            if (!request.IncludeActivity)
            {
                withheld["connections.byApplication"] = HostGate.WithheldSection;
            }

            snapshot = snapshot with { Connections = await ConnectionsAsync(resolved, scope, gate, cancellationToken).ConfigureAwait(false) };
        }

        return snapshot with { Withheld = withheld };
    }

    /// <summary>
    /// A costlier sweep: started (or joined) when the request asks for it - through the limiter, when it would be a fresh one -
    /// and otherwise only a recent one still cached, as the Health page shows a recent run to a visitor who did not start it.
    /// </summary>
    private async Task<HealthSweep?> CostlierAsync(ResolvedSource resolved, HealthSweepKind kind, bool run, CancellationToken cancellationToken)
    {
        string storeKey = resolved.Source.Key;
        string databaseId = resolved.Target.Identity;

        if (run)
        {
            return await limiter.RunAsync(
                    cache.IsFresh(storeKey, databaseId, kind),
                    token => cache.GetAsync(resolved, kind, null, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        HealthSweep? recent = cache.Latest(storeKey, databaseId, kind);
        return recent is not null && cache.Now - recent.MeasuredAt < HealthSweepCache.DeeperTimeToLive ? recent : null;
    }

    /// <summary>What each section a snapshot of an unreached database was asked for says, in place of reading it.</summary>
    internal const string NotReachedSection = "Not read: the studio could not reach this database (the snapshot's Reason says why).";

    /// <summary>
    /// A snapshot of a database the regular sweep could not reach: every section asked for is there, <see cref="PostgresReadOutcome.Failed" />
    /// with <see cref="NotReachedSection" />, and nothing is read for it; the Activity-grade ones the request did not include are
    /// named in <see cref="PostgresDiagnosticsSnapshot.Withheld" /> as on any other call.
    /// </summary>
    internal static PostgresDiagnosticsSnapshot Unreached(PostgresDiagnosticsSnapshot snapshot, PostgresDiagnosticsRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        Dictionary<string, string> withheld = new(StringComparer.Ordinal);

        if (request.Statements && !request.IncludeActivity)
        {
            withheld["statements"] = HostGate.WithheldSection;
        }

        if (request.Connections && !request.IncludeActivity)
        {
            withheld["connections.byApplication"] = HostGate.WithheldSection;
        }

        return snapshot with
        {
            Storage = request.Storage
                ? new PostgresStorageSection { Outcome = PostgresReadOutcome.Failed, Reason = NotReachedSection, MeasuredAt = now }
                : null,
            Statements = request.Statements && request.IncludeActivity
                ? new PostgresStatementsSection { Outcome = PostgresReadOutcome.Failed, Reason = NotReachedSection, MeasuredAt = now }
                : null,
            Settings = request.Settings
                ? new PostgresSettingsSection { Outcome = PostgresReadOutcome.Failed, Reason = NotReachedSection, MeasuredAt = now }
                : null,
            Connections = request.Connections
                ? new PostgresConnectionsSection { Outcome = PostgresReadOutcome.Failed, Reason = NotReachedSection, MeasuredAt = now }
                : null,
            Withheld = withheld,
        };
    }

    /// <summary>
    /// Whether the regular sweep reached the database at all: not when every check that reads the catalog failed - a database
    /// that refuses connections, a data source the host disposed - which is the snapshot's <c>Failed</c>, with the checks'
    /// own rows beside it.
    /// </summary>
    internal static bool Reached(HealthSweep sweep, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(sweep);

        HealthCheckResult[] catalog = [.. sweep.Results.Where(static x => x.Check.Tier == HealthCostTier.Catalog)];

        if (catalog.Length > 0 && catalog.All(static x => x.Outcome == HealthCheckOutcome.Failed))
        {
            string? first = catalog
                .Select(static x => x.Problem?.Reason)
                .Where(static x => x is not null)
                .GroupBy(static x => x, StringComparer.Ordinal)
                .OrderByDescending(static x => x.Count())
                .Select(static x => x.Key)
                .FirstOrDefault();

            reason = "No check that reads the database could: " + (first ?? "every one failed.");
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// The compute capacity behind the database: the regular sweep's own ask of the host's source (C03's), or - when no check
    /// asked it - the process's one reader, whose answer is shared per interval, database and instance either way.
    /// </summary>
    private async Task<PostgresComputeCapacity?> ComputeAsync(HealthSweep regular, ResolvedSource resolved, PostgresServerFacts? server, CancellationToken cancellationToken)
    {
        if (regular.TryShared<CapacityReading>(CapacityReading.Key, out CapacityReading? reading))
        {
            return reading.Capacity;
        }

        if (server is null || services.GetService<CapacityReader>() is not { SourceRegistered: true } reader)
        {
            return null;
        }

        return (await reader.GetAsync(resolved, server, cancellationToken).ConfigureAwait(false)).Capacity;
    }

    private async Task<PostgresStorageSection> StorageAsync(ResolvedSource resolved, IServiceProvider scope, IStudioSettings settings, CancellationToken cancellationToken)
    {
        IStorageFacts storage = scope.GetRequiredService<IStorageFacts>();

        try
        {
            StorageFacts facts = await limiter.RunAsync(storage.IsCached(resolved), token => storage.ReadAsync(resolved, token), cancellationToken)
                .ConfigureAwait(false);
            return DiagnosticsSnapshotBuilder.Storage(facts, settings);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            (PostgresReadOutcome outcome, string reason) = Failure("storage", exception, resolved);
            return new PostgresStorageSection { Outcome = outcome, Reason = reason, MeasuredAt = timeProvider.GetUtcNow() };
        }
    }

    private async Task<PostgresStatementsSection> StatementsAsync(
        ResolvedSource resolved,
        IServiceProvider scope,
        HostGate gate,
        IStudioSettings settings,
        bool run,
        CancellationToken cancellationToken)
    {
        IStatementFacts statements = scope.GetRequiredService<IStatementFacts>();

        try
        {
            StatementFacts? facts = run
                ? await limiter.RunAsync(
                        cache.IsFresh(resolved.Source.Key, resolved.Target.Identity, HealthSweepKind.Statements),
                        token => statements.ReadAsync(resolved, token),
                        cancellationToken)
                    .ConfigureAwait(false)
                : statements.Latest(resolved);

            return facts is null
                ? new PostgresStatementsSection
                {
                    Outcome = PostgresReadOutcome.Unavailable,
                    Reason = NoStatementRun,
                    MeasuredAt = timeProvider.GetUtcNow(),
                }
                : DiagnosticsSnapshotBuilder.Statements(facts, gate, settings.Text);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            (PostgresReadOutcome outcome, string reason) = Failure("statements", exception, resolved);
            return new PostgresStatementsSection { Outcome = outcome, Reason = reason, MeasuredAt = timeProvider.GetUtcNow() };
        }
    }

    /// <summary>What the statements section says before any statement sweep of the database has finished in this process.</summary>
    internal const string NoStatementRun =
        "The statement checks have not read pg_stat_statements of this database in this process yet, and a snapshot starts that "
        + "read only when asked to: call with RunDeeperChecks = true, or open the Statements page.";

    private async Task<PostgresSettingsSection> SettingsAsync(ResolvedSource resolved, IServiceProvider scope, CancellationToken cancellationToken)
    {
        ServerSettingsFactsCache server = scope.GetRequiredService<ServerSettingsFactsCache>();

        try
        {
            ServerSettingsFacts facts = await limiter.RunAsync(server.IsCached(resolved), token => server.GetAsync(resolved, token), cancellationToken)
                .ConfigureAwait(false);
            return DiagnosticsSnapshotBuilder.Settings(facts);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            (PostgresReadOutcome outcome, string reason) = Failure("settings", exception, resolved);
            return new PostgresSettingsSection { Outcome = outcome, Reason = reason, MeasuredAt = timeProvider.GetUtcNow() };
        }
    }

    private async Task<PostgresConnectionsSection> ConnectionsAsync(ResolvedSource resolved, IServiceProvider scope, HostGate gate, CancellationToken cancellationToken)
    {
        SessionActivitySnapshots sessions = scope.GetRequiredService<SessionActivitySnapshots>();

        try
        {
            SessionSnapshot snapshot = await limiter.RunAsync(sessions.IsCached(resolved), token => sessions.GetAsync(resolved, token), cancellationToken)
                .ConfigureAwait(false);
            return DiagnosticsSnapshotBuilder.Connections(snapshot, gate);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            (PostgresReadOutcome outcome, string reason) = Failure("connections", exception, resolved);
            return new PostgresConnectionsSection { Outcome = outcome, Reason = reason, MeasuredAt = timeProvider.GetUtcNow() };
        }
    }

    /// <summary>
    /// Every registered store's classifier, read from configuration with no connection, for the host's statement screen - or
    /// <see langword="null" /> when it cannot be read, which withholds every statement's text while types may be hidden.
    /// </summary>
    private DatabaseObjectClassifier? Classifier(IServiceProvider scope, ResolvedSource resolved)
    {
        try
        {
            return scope.GetService<IDatabaseDeclarationsSource>()?.Read(resolved) is { Succeeded: true } declared ? declared.Classifier : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "The diagnostics API could not classify the relations of {DatabaseId}; statement texts are withheld", resolved.Target.Identity);
            }

            return null;
        }
    }

    /// <summary>
    /// A section that could not be read, in the studio's words and the SQLSTATE - never Postgres' own - and the log line:
    /// Debug for an expected state, event 9251 at the throttle's level for anything else.
    /// </summary>
    private (PostgresReadOutcome Outcome, string Reason) Failure(string section, Exception exception, ResolvedSource resolved)
    {
        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;

        (PostgresReadOutcome Outcome, string Reason)? expected = postgres?.SqlState switch
        {
            PostgresErrorCodes.InsufficientPrivilege => (PostgresReadOutcome.Unavailable, "The role the studio reads as may not read the " + section + " (SQLSTATE 42501)."),
            PostgresErrorCodes.QueryCanceled => (PostgresReadOutcome.TimedOut, "Reading the " + section + " ran out of its statement_timeout (SQLSTATE 57014)."),
            PostgresErrorCodes.LockNotAvailable => (PostgresReadOutcome.TimedOut, "Reading the " + section + " waited too long for a lock (SQLSTATE 55P03)."),
            _ => PostgresFailure.IsTimeout(exception) ? (PostgresReadOutcome.TimedOut, "Reading the " + section + " timed out.") : null,
        };

        if (expected is { } state)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(exception, "The diagnostics API did not read the {Section} of {DatabaseId}: {Reason}", section, resolved.Target.Identity, state.Reason);
            }

            return state;
        }

        LogSectionFailure(section, exception, new PostgresDatabaseRef(resolved.Source.Key, resolved.Target.Identity));

        return (PostgresReadOutcome.Failed, PostgresFailure.IsConnectionFailure(exception)
            ? "The studio could not connect to this database."
            : "The " + section + " could not be read (" + (postgres is null ? exception.GetType().Name : "SQLSTATE " + postgres.SqlState)
              + "); the server log has it (event 9251).");
    }

    private void LogSectionFailure(string section, Exception exception, PostgresDatabaseRef database)
    {
        PostgresException? postgres = exception as PostgresException ?? exception.InnerException as PostgresException;
        LogLevel level = throttle.WarningOrDebug("Diagnostics." + section, database.Source, database.Database, StudioLogThrottle.KindOf(exception));

        if (services.GetService<IStudioSettings>()?.Text.Product == StudioProduct.PostgresStudio)
        {
            logger.PostgresStudioDiagnosticsSectionFailed(level, exception, section, database.Source, database.Database, postgres?.SqlState);
        }
        else
        {
            logger.DiagnosticsSectionFailed(level, exception, section, database.Source, database.Database, postgres?.SqlState);
        }
    }

    /// <summary>The sections a request asked for, by name, for event 9250.</summary>
    private static string Sections(PostgresDiagnosticsRequest request)
    {
        List<string> sections = [];

        if (request.Findings)
        {
            sections.Add("findings");
        }

        if (request.Storage)
        {
            sections.Add("storage");
        }

        if (request.Statements)
        {
            sections.Add("statements");
        }

        if (request.Settings)
        {
            sections.Add("settings");
        }

        if (request.Connections)
        {
            sections.Add("connections");
        }

        string flags = (request.IncludeActivity ? "+activity" : string.Empty) + (request.RunDeeperChecks ? "+deeper" : string.Empty);
        return (sections.Count == 0 ? "none" : string.Join(",", sections)) + flags;
    }
}
