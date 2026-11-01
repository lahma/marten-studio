using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Maintenance;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Statements;
using PostgresStudio.Services.Storage;

namespace PostgresStudio.Services.Settings;

/// <summary>
/// Registers the diagnostics engine - the health sweep and its history, the facts the pages and the API read, and
/// <see cref="IPostgresDiagnostics" /> - for whichever of <c>AddPostgresDiagnostics()</c> or a studio's <c>Add*Studio()</c>
/// calls it.
/// </summary>
/// <remarks>
/// <para>
/// The engine takes what makes it itself from the caller: its <see cref="DiagnosticsDescriptor" />, its
/// <see cref="IDiagnosticsSettings" />, and - registered by the caller - its implementations of the seams
/// (<see cref="Sources.IStudioSourceProvider" />, <see cref="Sources.ISessionLockLabeler" />,
/// <see cref="Sources.IDatabaseDeclarationsSource" />, and the optional health-advisor seams).
/// </para>
/// <para>
/// <b>Nothing here changes how the host behaves, and nothing here runs.</b> Every service is registered with
/// <c>TryAdd</c> so an application that registered its own first is the one that answers; no hosted service is added, no
/// timer started and nothing connects: a sweep happens when a page, the health check or host code asks.
/// </para>
/// </remarks>
internal static class DiagnosticsCoreRegistration
{
    /// <summary>Registers the diagnostics engine for the caller <paramref name="registeredBy" /> names.</summary>
    /// <param name="services">The application's service collection.</param>
    /// <param name="descriptor">The caller's descriptor; a studio's own descriptor, which is one of these.</param>
    /// <param name="settings">How the engine's settings are built from the container.</param>
    /// <param name="registeredBy">The public method registering, as a refusal names it: <c>AddPostgresDiagnostics()</c>.</param>
    /// <param name="byStudio">Whether the caller is a studio, which registers the engine as part of itself.</param>
    /// <exception cref="InvalidOperationException">The other kind of registration of the engine is already in this container.</exception>
    public static void Add(
        IServiceCollection services,
        DiagnosticsDescriptor descriptor,
        Func<IServiceProvider, IDiagnosticsSettings> settings,
        string registeredBy,
        bool byStudio)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(settings);

        // First, so that the other kind of registration is refused before anything of this one is registered.
        DiagnosticsRegistrationMarker.Claim(services, registeredBy, byStudio);

        services.TryAddSingleton(descriptor);
        services.TryAddSingleton(settings);

        // DB-0: log hygiene
        // One process-wide memory of which anomalies have already been reported, so a failure on a path
        // every open page polls is a Warning once per source, database and exception type per ten minutes
        // and Debug in between. The host's TimeProvider when it registered one, the system clock when not.
        services.TryAddSingleton(static provider =>
            new StudioLogThrottle(provider.GetService<TimeProvider>() ?? TimeProvider.System));

        // One snapshot per interval however many readers are asking (D10).
        services.TryAddSingleton<StudioSnapshotCache>();

        // H1: the health advisor. The checks, their history and the one sweep per database per interval are
        // process-wide singletons - a sweep's facts are about the database and the same for every reader - and none of
        // them runs anything on its own: no hosted service, no timer. A sweep happens when somebody asks.
        services.TryAddSingleton<HealthCheckRegistry>();

        // W5-API: one process-wide bound on the databases the advisor keeps state for (API design §3.5) - the history, the
        // sweep cache and the statement texts each register with it and forget a database together.
        services.TryAddSingleton<HealthDatabaseBound>();
        services.TryAddSingleton<HealthHistory>();
        services.TryAddSingleton<HealthSweeper>();
        services.TryAddSingleton<HealthSweepCache>();

        // H7: the statement checks' memory of the texts they have read, so a statement sweep whose statements are all
        // known makes no text call - each one costs pg_stat_statements its whole query-text file.
        services.TryAddSingleton<StatementTextCache>();

        // W5-V1: Maintenance's facts are the regular sweep's own reads, the database's and every reader's.
        services.TryAddSingleton<IMaintenanceFacts, MaintenanceFactsReader>();

        // H3: the sessions and the connection checks. The pool observer is a singleton because a MeterListener is
        // process-wide, and it listens only while a sweep or an open Sessions page holds a lease. The session snapshot is
        // the database's, shared by every reader for ten seconds.
        services.TryAddSingleton<SessionPoolObserver>();
        services.TryAddSingleton<ISessionActivityReader, SessionActivityReader>();
        services.TryAddSingleton<SessionActivitySnapshots>();

        // W5-CAP: Storage. The facts are the database's - one read per database per HealthInterval, single flight, the same
        // for every reader.
        services.TryAddSingleton<IStorageFacts, StorageFactsReader>();

        // W5-R2: Replication. The facts are the database's, read once per HealthInterval for every reader.
        services.TryAddSingleton<IReplicationFactsReader, ReplicationFactsReader>();
        services.TryAddSingleton<ReplicationFactsCache>();

        // W5-S2: the Server facts, read once per HealthInterval for every reader.
        services.TryAddSingleton<IServerSettingsFactsReader, ServerSettingsFactsReader>();
        services.TryAddSingleton<ServerSettingsFactsCache>();

        // S07's update paths, remembered by database, extension and both versions across sweeps.
        services.TryAddSingleton<ExtensionUpdatePaths>();

        // W5-AUR: the host's capacity source, asked once per database and instance per HealthInterval for the whole process
        // (issue #8 A8); and the Aurora facts, which are the database's.
        services.TryAddSingleton<Aurora.CapacityReader>();
        services.TryAddSingleton<Aurora.IAuroraFactsReader, Aurora.AuroraFactsReader>();
        services.TryAddSingleton<Aurora.AuroraFactsCache>();

        // W5-Q1: the Statements facts are the statement sweep's own read - the database's, single flight, kept for five
        // minutes for every reader - grouped by the studio's IStatementClassifier.
        services.TryAddSingleton<IStatementFacts, StatementFactsReader>();

        // W5-API: the diagnostics API for the host's own code (D31) - a singleton over the engine's own caches, and the
        // process's limit of two fresh reads at a time for it. Inert until host code calls it: no endpoint, no hosted service.
        services.TryAddSingleton<FreshReadLimiter>();
        services.TryAddSingleton<IPostgresDiagnostics, PostgresDiagnosticsService>();
    }
}
