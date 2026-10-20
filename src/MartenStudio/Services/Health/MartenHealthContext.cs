using Marten;
using Marten.Storage;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Settings;
using MartenStudio.Services.Sources;

namespace MartenStudio.Services.Health;

/// <summary>
/// MartenStudio's reading of the shared health context and evaluation: the store, its options, its database and the
/// Marten it runs - C# 14 extension members, so that MartenStudio's own checks read the context as they always have.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only for a Marten source.</b> The shared context holds the resolver's own record, which for a Marten store is a
/// <see cref="ResolvedScope" />; a check in MartenStudio's assembly is only ever offered MartenStudio's sources, so
/// <c>Scope</c>'s cast holds - and where it would not, it throws and the check is a "could not check" row rather than a
/// misreading.
/// </para>
/// <para>
/// <b>Not for shared code.</b> A file bound for the shared assembly must not use these: after the move it cannot see
/// this class, and the text fence cannot tell an extension call from a member of the shared type. The shared checks read
/// <see cref="HealthCheckContext.Source" />, <see cref="HealthCheckContext.Target" /> and the studio's seams instead.
/// </para>
/// </remarks>
internal static class MartenHealthContext
{
    extension(HealthCheckContext context)
    {
        /// <summary>The store and database being swept, resolved with no tenant.</summary>
        /// <exception cref="InvalidOperationException">The source is not a Marten store's.</exception>
        public ResolvedScope Scope => context.Source as ResolvedScope
            ?? throw new InvalidOperationException(
                "A Marten health check was handed a source that is not a Marten store: '" + context.StoreKey + "'.");

        /// <summary>The store.</summary>
        public IDocumentStore Store => context.Scope.Store;

        /// <summary>What the host told Marten.</summary>
        public IReadOnlyStoreOptions StoreOptions => context.Scope.Store.Options;

        /// <summary>The database.</summary>
        public IMartenDatabase Database => context.Scope.Database;

        /// <summary>The Marten this host runs, and its light-up probes.</summary>
        public MartenRuntime Runtime => (context.StudioRuntime as MartenStudioRuntime)?.Runtime ?? MartenRuntime.Current;

        /// <summary>
        /// The studio's settings, under the name MartenStudio's checks read them by - the interval and the document-type
        /// filter, which are all they read, live from <c>MartenStudioOptions</c>.
        /// </summary>
        public IStudioSettings StudioOptions => context.Settings;
    }

    extension(HealthEvaluation evaluation)
    {
        /// <summary>The Marten the host runs, for a remedy or a judgement that needs a newer one.</summary>
        public MartenRuntime Runtime => (evaluation.StudioRuntime as MartenStudioRuntime)?.Runtime ?? MartenRuntime.Current;

        /// <summary>
        /// An evaluation at <paramref name="now" /> as MartenStudio's sweep makes one, for a unit test: against
        /// <paramref name="runtime" /> (this process's when <see langword="null" />), with MartenStudio's remedy wording and
        /// plan advisor.
        /// </summary>
        /// <param name="now">When.</param>
        /// <param name="runtime">The Marten the host runs.</param>
        /// <param name="history">The history, or none.</param>
        public static HealthEvaluation At(DateTimeOffset now, MartenRuntime? runtime, HealthHistoryView? history = null) =>
            new(now, new MartenStudioRuntime(runtime ?? MartenRuntime.Current), history ?? HealthHistoryView.Empty)
            {
                TableRemedies = MartenHealthRemedyProvider.Instance,
                PlanAdvisor = MartenStatementPlanAdvisor.Instance,
            };
    }
}

/// <summary>
/// MartenStudio's spellings of shared health members whose shared form takes the studio's seams - C# 14 static extension
/// members, so that Marten-only code compiles unchanged. The rule is <see cref="MartenServiceBridges" />'s: Marten-only code
/// may call these; a shared-bound file must not, and each member lists the shared-bound callers left.
/// </summary>
internal static class MartenHealthBridges
{
    extension(DataSourceEndpoints)
    {
        /// <summary>
        /// <see cref="DataSourceEndpoints.Of(IStudioDatabase)" /> for a Marten database. Called by C10
        /// (<c>PooledDaemonLockCheck</c>), which is MartenStudio's own.
        /// </summary>
        /// <param name="database">The database.</param>
        internal static DataSourceEndpoints? Of(IMartenDatabase database) => DataSourceEndpoints.Of(MartenStudioDatabase.Of(database));
    }

    extension(HealthLinks)
    {
        /// <summary>
        /// <see cref="HealthLinks.ToPage(IStudioSettings, string, StudioScope?)" /> with MartenStudio's options and source
        /// parameter. Shared-bound caller left: <c>Overview.razor</c> (PS-1d).
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        /// <param name="scope">The scope to carry.</param>
        internal static string ToPage(MartenStudioOptions options, StudioScope? scope) =>
            HealthLinks.ToPage(MartenStudioSettings.Of(options), MartenStudioDescriptor.SourceQueryParameter, scope);

        /// <summary>
        /// <see cref="HealthLinks.ToFinding(IStudioSettings, string, StudioScope?, HealthReportFinding)" /> with
        /// MartenStudio's options and source parameter. Shared-bound caller left: <c>Overview.razor</c> (PS-1d).
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        /// <param name="scope">The scope to carry.</param>
        /// <param name="finding">The finding.</param>
        internal static string ToFinding(MartenStudioOptions options, StudioScope? scope, HealthReportFinding finding) =>
            HealthLinks.ToFinding(MartenStudioSettings.Of(options), MartenStudioDescriptor.SourceQueryParameter, scope, finding);
    }

    extension(HealthService)
    {
        /// <summary>
        /// <see cref="HealthService.WithheldSchemaReasonFor" /> in MartenStudio's words, for a test that looks for it.
        /// </summary>
        internal static string WithheldSchemaReason => HealthService.WithheldSchemaReasonFor(MartenStudioSettings.Of(new MartenStudioOptions()));
    }
}
