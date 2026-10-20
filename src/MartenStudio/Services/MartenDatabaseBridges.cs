using Marten.Storage;

using MartenStudio.Services;
using PostgresStudio.Services.Database;
using MartenStudio.Services.Documents;
using PostgresStudio.Components.Shared;
using PostgresStudio.Services;

namespace MartenStudio;

/// <summary>
/// MartenStudio's spellings of the database browser's and Sessions' shared members, whose shared form no longer names a
/// Marten type - C# 14 static extension members, so that Marten-only code compiles unchanged.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bridge, not an API</b>, on <see cref="Services.MartenServiceBridges" />' terms: Marten-only code may keep calling
/// these; a file bound for the shared assembly must not. Each member lists its callers, every one of them MartenStudio's
/// own since PS-1e, which deleted the members nothing called any more (<c>SessionLockLabels.For</c>,
/// <c>DatabaseAccess.DeclareEveryStore</c>, <c>DatabasePoolSettings.IsPoolOf</c>, <c>DatabaseLinks.ToRelationships</c>)
/// and moved their last test callers onto the members they forwarded to.
/// </para>
/// <para>
/// <b>Declared in the root namespace</b> so that every caller sees it without a <c>using</c> of its own.
/// </para>
/// </remarks>
internal static class MartenDatabaseBridges
{
    extension(DatabaseAccess)
    {
        /// <summary>
        /// The policy a store-policy refusal names in event 9203, from MartenStudio's options
        /// (<see cref="DatabaseAccess.StorePolicyName(PostgresStudio.Services.Settings.IStudioSettings)" />). Caller: <c>SchemaDataService</c>.
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        internal static string StorePolicyName(MartenStudioOptions options) =>
            DatabaseAccess.StorePolicyName(MartenStudioSettings.Of(options));
    }

    extension(DatabasePoolSettings)
    {
        /// <summary>
        /// The pool settings of a Marten database (<see cref="DatabasePoolSettings.Of(PostgresStudio.Services.Sources.IStudioDatabase)" />).
        /// Callers: <c>ConnectionBudgetCheck</c> and <c>DaemonPoolCheck</c>.
        /// </summary>
        /// <param name="database">The Marten database.</param>
        public static DatabasePoolSettings? Of(IMartenDatabase database) =>
            DatabasePoolSettings.Of(MartenStudioDatabase.Of(database));
    }

    extension(DatabaseLinks)
    {
        /// <summary>
        /// One relation's detail page, with MartenStudio's options and descriptor. Callers: <c>Schema.razor</c> and
        /// <c>ReferencedBy.razor</c>.
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
        /// A relation's Rows tab, with MartenStudio's options and descriptor. Caller: <c>ReferencedBy.razor</c>.
        /// </summary>
        /// <param name="options">MartenStudio's options.</param>
        /// <param name="scope">The scope to carry.</param>
        /// <param name="schema">The relation's schema.</param>
        /// <param name="name">The relation's name.</param>
        /// <param name="filter">A row filter, or <see langword="null" />.</param>
        public static string ToRows(MartenStudioOptions options, StudioScope? scope, string schema, string name, string? filter = null) =>
            DatabaseLinks.ToRows(MartenStudioSettings.Of(options), MartenStudioDescriptor.Instance, scope, schema, name, filter);
    }

    extension(SafeReturnLink)
    {
        /// <summary>
        /// A document page's return link, into the documents area. Caller: <c>DocumentDetail.razor</c>.
        /// </summary>
        /// <param name="from">The <c>?from=</c> value.</param>
        /// <param name="options">MartenStudio's options.</param>
        public static string? Sanitize(string? from, MartenStudioOptions options) =>
            SafeReturnLink.Sanitize(from, MartenStudioSettings.Of(options), DocumentLinks.Root);
    }
}
