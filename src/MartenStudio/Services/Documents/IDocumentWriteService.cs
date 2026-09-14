namespace MartenStudio.Services.Documents;

/// <summary>
/// Everything the studio can do that changes a document: preview an edit, save it, delete it, undelete
/// it, and delete a selection.
/// </summary>
/// <remarks>
/// <para>
/// Separate from the read seam on purpose. These five methods are the only code in the studio that
/// writes to a document table, so the capability gate, the authorization call and the audit entry are
/// in one file rather than scattered through the pages that call them (AGENTS.md hard rule 5). A page
/// that forgets to hide a button is a cosmetic bug; a service that forgets to refuse is a
/// vulnerability, and a Blazor circuit is a long-lived object a client can drive.
/// </para>
/// <para>
/// Every method takes a <see cref="StudioScope" /> and resolves it itself. None of them accepts an
/// <c>IDocumentStore</c>, an <c>IMartenDatabase</c> or a <c>ResolvedScope</c>: a resolved object has
/// already passed authorization, and accepting one would make it possible to write to a database using
/// a scope that was resolved for reading something else.
/// </para>
/// <para>
/// Writes go through a Marten session — <c>StoreObjects</c> and <c>DeleteObjects</c> — and never through
/// SQL the studio wrote itself (D6). Upsert semantics, metadata columns, soft delete and tenancy stay
/// exactly as Marten defines them, which is the only way a UI can write to a store it did not design.
/// </para>
/// </remarks>
internal interface IDocumentWriteService
{
    /// <summary>
    /// Works out what saving <paramref name="editedJson" /> would do, without writing anything.
    /// </summary>
    /// <remarks>
    /// Requires <c>EditDocuments</c>, because the round trip deserializes the edit into the document's
    /// CLR type and a type's constructor is application code. A preview is a read of the database and a
    /// write of nothing, but it is gated and audited like the save it precedes.
    /// </remarks>
    /// <param name="scope">The store, database and tenant to look in.</param>
    /// <param name="alias">The collection alias.</param>
    /// <param name="id">The document id, as it arrived from the URL.</param>
    /// <param name="editedJson">The edited document.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <exception cref="StudioCapabilityDeniedException"><c>EditDocuments</c> is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    Task<WritePreview> PreviewAsync(
        StudioScope scope,
        string alias,
        string id,
        string editedJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves an edited document, refusing if the row moved or if the round trip would lose data that
    /// nobody acknowledged.
    /// </summary>
    /// <param name="scope">The store, database and tenant to write to.</param>
    /// <param name="alias">The collection alias.</param>
    /// <param name="id">The document id.</param>
    /// <param name="editedJson">The edited document.</param>
    /// <param name="expectedToken">
    /// The version the editor was opened on, from <see cref="WritePreview.CurrentToken" />. Compared
    /// against the row inside the write transaction; a mismatch is a
    /// <see cref="WriteStatus.Conflict" /> and nothing is written.
    /// </param>
    /// <param name="acknowledgeDrops">
    /// Whether the caller has seen <see cref="WritePreview.DroppedPaths" /> and still wants the save.
    /// Without it a save that would drop properties is refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <exception cref="StudioCapabilityDeniedException"><c>EditDocuments</c> is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    Task<WriteResult> SaveAsync(
        StudioScope scope,
        string alias,
        string id,
        string editedJson,
        DocumentConcurrencyToken expectedToken,
        bool acknowledgeDrops,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes one document, the way the mapping says to: soft when the type is soft-deleted, hard
    /// otherwise.
    /// </summary>
    /// <param name="scope">The store, database and tenant to write to.</param>
    /// <param name="alias">The collection alias.</param>
    /// <param name="id">The document id.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <exception cref="StudioCapabilityDeniedException"><c>DeleteDocuments</c> is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    Task<DeleteResult> DeleteAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings a soft-deleted document back, and refuses for a type that has no soft delete to undo.
    /// </summary>
    /// <remarks>
    /// Gated on <c>DeleteDocuments</c> rather than on <c>EditDocuments</c>: undelete is the other half
    /// of delete, and a host that granted the ability to hide rows granted the ability to unhide them.
    /// </remarks>
    /// <param name="scope">The store, database and tenant to write to.</param>
    /// <param name="alias">The collection alias.</param>
    /// <param name="id">The document id.</param>
    /// <param name="cancellationToken">Cancels the undelete.</param>
    /// <exception cref="StudioCapabilityDeniedException"><c>DeleteDocuments</c> is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    Task<DeleteResult> UndeleteAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a selection in one transaction, reporting what happened to each id.
    /// </summary>
    /// <param name="scope">The store, database and tenant to write to.</param>
    /// <param name="alias">The collection alias.</param>
    /// <param name="ids">The ids, at most <see cref="DocumentWriteService.MaxBulkDeleteIds" /> of them.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <exception cref="StudioCapabilityDeniedException"><c>DeleteDocuments</c> is not enabled.</exception>
    /// <exception cref="StudioNotAuthorizedException">The visitor may not have this scope.</exception>
    Task<BulkDeleteResult> BulkDeleteAsync(
        StudioScope scope,
        string alias,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);
}
