using MartenStudio.Services.Database;

namespace MartenStudio.Services.Sources;

/// <summary>
/// What the database browser knows about who owns which object in a database, and what it may say about the other
/// sources that share it - the seam between D27's gate, which is shared, and the declarations it classifies against,
/// which are a studio's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>MartenStudio</b> answers with every registered store's declarations (<c>SchemaDeclarationReader</c> over each
/// store's options - configuration, no connection) and with each other store's policies for this database.
/// <b>A plain source</b> answers with an empty classifier whose own schemas are its admitted browsable schemas, and no
/// other sources: one source is one database, and another source's objects are not classified as its.
/// </para>
/// <para>
/// <b>Fails closed.</b> When any registered source's declarations cannot be read, the answer is
/// <see cref="DatabaseDeclarations.Succeeded" /> <see langword="false" /> with the reason, and nothing is called
/// "Other": an object nobody could classify is not browsable as somebody else's.
/// </para>
/// <para>
/// <b>What it must not do.</b> No connection and no schema-building call (AGENTS.md hard rule 14): never
/// <c>AllObjects()</c>, <c>AllSchemaNames()</c> or a storage-ensuring call. <see cref="OtherSourcesAsync" /> may
/// enumerate another source's databases only from memory and only when a policy will be asked (D27), and never a
/// dynamic tenancy's.
/// </para>
/// </remarks>
internal interface IDatabaseDeclarationsSource
{
    /// <summary>
    /// Every registered source's declarations for the database of <paramref name="resolved" />, with that source as the
    /// resolved one - remembered for the circuit while what each source knows has not changed.
    /// </summary>
    /// <param name="resolved">The scope, resolved.</param>
    DatabaseDeclarations Read(ResolvedSource resolved);

    /// <summary>
    /// The same, with <paramref name="source" /> as the resolved one, before any database of it is resolved - for the
    /// database browser's routing, which decides from the declarations alone whether a read is the source's own
    /// structure or a <c>BrowseDatabase</c> read, and must do so before the capability is asked and so before any
    /// database or tenant of the source is enumerated or discovered (AGENTS.md hard rule 14, D27).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Must agree with <see cref="Read(ResolvedSource)" />.</b> For one source, both answer the same
    /// <see cref="DatabaseDeclarations.StoreSchemas" /> and the same classification, whichever of its databases the
    /// second was resolved to. The routing reads this one to decide that a schema is the source's own and its structure
    /// needs no capability; the gate reads the other to show it. Were they to disagree, a schema the gate treats as a
    /// <c>BrowseDatabase</c> read could be routed as the source's own structure and skip
    /// <c>DatabaseAccess.RequireBrowseAsync</c> - and with it the audited refusal.
    /// </para>
    /// <para>
    /// <b>So own schemas come from configuration alone</b> - the source's options, never a resolved database, its
    /// catalog or a tenant. A source whose own schemas depended on which database a scope resolved to could not answer
    /// here at all, and would answer the two members differently.
    /// </para>
    /// </remarks>
    /// <param name="source">The source the visitor's scope names, found and built but not resolved to a database.</param>
    DatabaseDeclarations Read(IStudioSource source);

    /// <summary>
    /// The same, read afresh and remembered nowhere - for a caller with no circuit: a health check labelling the locks
    /// it reads (C06).
    /// </summary>
    /// <param name="resolved">The scope, resolved with no tenant.</param>
    DatabaseDeclarations ReadUncached(ResolvedSource resolved);

    /// <summary>
    /// For each other source that shares the database and owns objects in it, whether the visitor may see its identity
    /// and read its rows - the store policy asked about that source's own spelling of this database, and the write
    /// policy with <c>BrowseDatabase</c> while the capability is on.
    /// </summary>
    /// <param name="resolved">The scope, resolved.</param>
    /// <param name="classifier">The classifier <see cref="Read(ResolvedSource)" /> answered.</param>
    /// <param name="capabilityEnabled">Whether <c>BrowseDatabase</c> is on for the process.</param>
    /// <param name="cancellationToken">Cancels the policy questions.</param>
    /// <returns>By source key, ignoring case; empty when no other source owns anything here.</returns>
    Task<IReadOnlyDictionary<string, DatabaseStoreAccess>> OtherSourcesAsync(
        ResolvedSource resolved,
        DatabaseObjectClassifier classifier,
        bool capabilityEnabled,
        CancellationToken cancellationToken);
}
