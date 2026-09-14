namespace MartenStudio.Services.Schema;

/// <summary>
/// What the Schema screen reads, and the one place a schema change can be applied from.
/// </summary>
/// <remarks>
/// An interface so a component test can drive the page without a Postgres, and so the capability gate
/// has exactly one implementation to live in. Every method takes a <see cref="StudioScope" /> and
/// resolves it: none of them accepts an <c>IDocumentStore</c> or an <c>IMartenDatabase</c>, because a
/// resolved database has already passed authorization and a method that accepted one would make the
/// check skippable (see <see cref="StudioScopeResolver" />).
/// </remarks>
internal interface ISchemaDataService
{
    /// <summary>
    /// Whether the database matches its configuration.
    /// </summary>
    /// <remarks>
    /// Opens connections and reads the whole catalog, so it is never called on navigation - the Drift tab
    /// runs it from a button.
    /// </remarks>
    Task<SchemaCheck> CheckAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The SQL Marten would run to bring the database up to its configuration.
    /// </summary>
    /// <remarks>Reads. Never writes - the assertion in the live tests is that <c>pg_indexes</c> is unchanged.</remarks>
    Task<MigrationPreview> PreviewAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies every configured change to the database in scope.
    /// </summary>
    /// <param name="scope">The store and database to change.</param>
    /// <param name="confirmation">
    /// What the visitor typed into the confirm dialog. It has to equal the database's identity; the
    /// dialog checks it too, but the dialog is a convenience and this is the check that matters.
    /// </param>
    /// <param name="cancellationToken">Cancels the apply.</param>
    /// <exception cref="StudioCapabilityDeniedException">
    /// <c>MartenStudioOptions.Capabilities.ApplySchemaChanges</c> is off, or <c>ReadOnly</c> is on.
    /// </exception>
    /// <exception cref="StudioNotAuthorizedException">The write policy refused this scope.</exception>
    Task<SchemaApplyResult> ApplyAsync(
        StudioScope scope,
        string confirmation,
        CancellationToken cancellationToken = default);

    /// <summary>Table sizes and activity for the store's schemas.</summary>
    Task<SchemaTables> TablesAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>Every index that exists, every index that is declared, and what to do about the difference.</summary>
    Task<SchemaIndexes> IndexesAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>The functions in the store's schemas, with their definitions.</summary>
    Task<SchemaFunctions> FunctionsAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>The whole creation script for the store's schema objects.</summary>
    Task<DdlScript> DdlAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The identity of the database in scope, which is what the apply dialog makes the visitor type.
    /// </summary>
    Task<string> DatabaseIdentityAsync(StudioScope scope, CancellationToken cancellationToken = default);
}
