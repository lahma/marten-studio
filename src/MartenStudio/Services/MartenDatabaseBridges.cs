using Marten;
using Marten.Storage;

using MartenStudio.Components.Pages.Documents;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Relationships;

namespace MartenStudio;

/// <summary>
/// MartenStudio's spellings of the database browser's and Sessions' shared members, whose shared form no longer names a
/// Marten type - C# 14 static and instance extension members, so that Marten-only code, and the shared-bound files whose
/// own packet has not moved them yet, compile unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bridge, not an API</b>, on <see cref="Services.MartenServiceBridges" />' terms: Marten-only code may keep
/// calling these; a file bound for the shared assembly must not, and each member lists the shared-bound callers left
/// when PS-1b landed, for the packet that owns each to move it onto the shared member. PS-1e's fence over the whole
/// manifest and PS-2's compile are what prove none is left.
/// </para>
/// <para>
/// <b>Declared in the root namespace</b> so that every caller sees it without a <c>using</c> of its own: the
/// Relationships pages name <c>MartenStudio.Services.Database.DatabaseLinks</c> fully qualified, and the health
/// suite's tests live in <c>MartenStudio.Integration.Tests.*</c>, under the same root.
/// </para>
/// </remarks>
internal static class MartenDatabaseBridges
{
    extension(SessionLockLabels)
    {
        /// <summary>
        /// The labels for <paramref name="store" /> (<see cref="MartenSessionLockLabeler.For(IDocumentStore, string, DatabaseObjectClassifier?)" />).
        /// Shared code asks the studio's <see cref="Services.Sources.ISessionLockLabeler" />. Shared-bound callers left
        /// at PS-1b: <c>IdleInTransactionCheck</c>, <c>LockWaitCheck</c> and <c>HorizonHeldCheck</c> (PS-1c).
        /// </summary>
        /// <param name="store">The store.</param>
        /// <param name="storeKey">Its registration key.</param>
        /// <param name="classifier">The classifier over every registered store's declarations, or <see langword="null" />.</param>
        public static SessionLockLabels For(IDocumentStore store, string storeKey, DatabaseObjectClassifier? classifier) =>
            MartenSessionLockLabeler.For(store, storeKey, classifier);
    }

    extension(DatabaseAccess)
    {
        /// <summary>
        /// Every registered store's declarations, read afresh
        /// (<see cref="MartenDatabaseDeclarationsSource.DeclareEveryStore" />). Shared code asks the studio's
        /// <see cref="Services.Sources.IDatabaseDeclarationsSource.ReadUncached" />. Shared-bound caller left at PS-1b:
        /// <c>LockWaitCheck</c> (PS-1c).
        /// </summary>
        /// <param name="registry">The store registry.</param>
        /// <param name="provider">What the other stores are resolved from.</param>
        /// <param name="registration">The resolved store's registration.</param>
        /// <param name="resolvedStore">The resolved store.</param>
        /// <param name="isDocumentTypeVisible"><see cref="MartenStudioOptions.IsDocumentTypeVisible" />.</param>
        internal static DatabaseDeclarations DeclareEveryStore(
            MartenStoreRegistry registry,
            IServiceProvider provider,
            MartenStoreRegistration registration,
            IDocumentStore resolvedStore,
            Func<Type, bool>? isDocumentTypeVisible) =>
            MartenDatabaseDeclarationsSource.DeclareEveryStore(registry, provider, registration, resolvedStore, isDocumentTypeVisible);

        /// <summary>
        /// The policy a store-policy refusal names in event 9203, from MartenStudio's options
        /// (<see cref="DatabaseAccess.StorePolicyName(Services.Settings.IStudioSettings)" />). Marten-only caller:
        /// <c>SchemaDataService</c>.
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        internal static string StorePolicyName(MartenStudioOptions options) =>
            DatabaseAccess.StorePolicyName(MartenStudioSettings.Of(options));
    }

    extension(DatabasePoolSettings)
    {
        /// <summary>
        /// The pool settings of a Marten database (<see cref="DatabasePoolSettings.Of(Services.Sources.IStudioDatabase)" />).
        /// Marten-only callers: <c>ConnectionBudgetCheck</c> and <c>DaemonPoolCheck</c>.
        /// </summary>
        /// <param name="database">The Marten database.</param>
        public static DatabasePoolSettings? Of(IMartenDatabase database) =>
            DatabasePoolSettings.Of(MartenStudioDatabase.Of(database));

        /// <summary>
        /// Whether a pool is a Marten database's (<see cref="DatabasePoolSettings.IsPoolOf(Services.Sources.IStudioDatabase, string)" />).
        /// Shared-bound caller left at PS-1b: <c>PoolSaturationCheck</c> (PS-1c).
        /// </summary>
        /// <param name="database">The Marten database.</param>
        /// <param name="poolName">Npgsql's <c>pool.name</c> tag.</param>
        public static bool IsPoolOf(IMartenDatabase database, string poolName) =>
            DatabasePoolSettings.IsPoolOf(MartenStudioDatabase.Of(database), poolName);
    }

    extension(DatabaseLinks)
    {
        /// <summary>
        /// One relation's detail page, with MartenStudio's options and descriptor. Marten-only callers:
        /// <c>Schema.razor</c>, <c>ReferencedBy.razor</c>; no shared-bound caller is left (PS-1d moved
        /// <c>Relationships.razor</c> and <c>NeighbourhoodGraph.razor</c> onto the settings and the descriptor).
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        /// <param name="scope">The scope to carry.</param>
        /// <param name="schema">The relation's schema.</param>
        /// <param name="name">The relation's name.</param>
        /// <param name="tab">The tab to open, or <see langword="null" />.</param>
        /// <param name="extra">Further parameters to carry.</param>
        public static string ToObject(
            MartenStudioOptions options,
            StudioScope? scope,
            string schema,
            string name,
            DatabaseObjectTab? tab = null,
            IReadOnlyList<KeyValuePair<string, string?>>? extra = null) =>
            DatabaseLinks.ToObject(MartenStudioSettings.Of(options), MartenStudioDescriptor.Instance, scope, schema, name, tab, extra);

        /// <summary>
        /// A relation's Rows tab, with MartenStudio's options and descriptor. Marten-only caller: <c>ReferencedBy.razor</c>.
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        /// <param name="scope">The scope to carry.</param>
        /// <param name="schema">The relation's schema.</param>
        /// <param name="name">The relation's name.</param>
        /// <param name="filter">A row filter, or <see langword="null" />.</param>
        public static string ToRows(MartenStudioOptions options, StudioScope? scope, string schema, string name, string? filter = null) =>
            DatabaseLinks.ToRows(MartenStudioSettings.Of(options), MartenStudioDescriptor.Instance, scope, schema, name, filter);

        /// <summary>
        /// The Relationships screen, with MartenStudio's options and descriptor. No caller is left (PS-1d moved
        /// <c>Relationships.razor</c> onto the settings and the descriptor); kept for Marten-only code.
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        /// <param name="scope">The scope to carry.</param>
        /// <param name="view">Documents, tables or both.</param>
        /// <param name="schema">One schema chip, or <see langword="null" />.</param>
        public static string ToRelationships(MartenStudioOptions options, StudioScope? scope, RelationshipViewMode view, string? schema) =>
            DatabaseLinks.ToRelationships(MartenStudioSettings.Of(options), MartenStudioDescriptor.Instance, scope, view, schema);
    }

    extension(SafeReturnLink)
    {
        /// <summary>
        /// A document page's return link, into the documents area. Marten-only caller: <c>DocumentDetail.razor</c>.
        /// </summary>
        /// <param name="from">The <c>?from=</c> value.</param>
        /// <param name="options">MartenStudio's options.</param>
        public static string? Sanitize(string? from, MartenStudioOptions options) =>
            SafeReturnLink.Sanitize(from, MartenStudioSettings.Of(options), DocumentLinks.Root);
    }
}
