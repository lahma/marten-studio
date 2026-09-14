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
/// <remarks>
/// <para>
/// <b>Three of these are actions, not reads.</b> <see cref="CheckAsync" />, <see cref="PreviewAsync" />
/// and <see cref="DdlAsync" /> reach Weasel's <c>CreateMigrationAsync()</c> / <c>ToDatabaseScript()</c>,
/// which walk <c>IMartenDatabase.AllObjects()</c> - and that builds Marten's feature schemas, including
/// the lazy HiLo <c>Sequences</c> feature, whose initialiser applies a migration under the database's own
/// <c>AutoCreate</c>. They can therefore create Marten's own bookkeeping objects (<c>mt_hilo</c>,
/// <c>mt_get_next_hi</c>) on a schema that did not have them. Every one of them is behind a button that
/// says so, and none of them may be called on navigation.
/// </para>
/// <para>
/// <see cref="TablesAsync" />, <see cref="IndexesAsync" /> and <see cref="FunctionsAsync" /> are the
/// navigation paths and execute no DDL at all: their schema list and their declarations come from
/// <c>StoreOptions</c> (see <see cref="SchemaDeclarationReader" />) and their numbers from
/// <c>pg_catalog</c> over a read connection. <c>SchemaNoDdlLiveTests</c> holds them to it.
/// </para>
/// </remarks>
internal interface ISchemaDataService
{
    /// <summary>
    /// Whether the database matches its configuration.
    /// </summary>
    /// <remarks>
    /// An action, not a read: it opens connections, reads the whole catalog and builds every feature
    /// schema, which can create Marten's own HiLo objects. Never called on navigation - the Drift tab
    /// runs it from a button that says what it may do.
    /// </remarks>
    Task<SchemaCheck> CheckAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The SQL Marten would run to bring the database up to its configuration.
    /// </summary>
    /// <remarks>
    /// Applies nothing of what it renders - the assertion in the live tests is that the declared indexes
    /// are unchanged. It does build Marten's feature schemas to get there, so like
    /// <see cref="CheckAsync" /> it is an explicit action rather than something a tab does on arrival.
    /// </remarks>
    Task<MigrationPreview> PreviewAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies every configured change to the database in scope.
    /// </summary>
    /// <param name="scope">The store and database to change.</param>
    /// <param name="confirmation">
    /// What the visitor <em>typed</em> into the confirm dialog - carried out of the dialog's own input
    /// rather than re-supplied by the page. It has to equal the database's identity; the dialog checks it
    /// too, but the dialog is a convenience and this is the check that matters. A page that passed its
    /// own copy of the identity here would be handing the service the answer, and the check would prove
    /// nothing.
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
    /// <remarks>
    /// <c>IMartenDatabase.ToDatabaseScript()</c>, which walks <c>AllObjects()</c> - so, like
    /// <see cref="CheckAsync" />, it is an explicit action behind a button rather than something the DDL
    /// tab does on arrival.
    /// </remarks>
    Task<DdlScript> DdlAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The identity of the database in scope, which is what the apply dialog makes the visitor type.
    /// </summary>
    Task<string> DatabaseIdentityAsync(StudioScope scope, CancellationToken cancellationToken = default);
}
