using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;

using JasperFx;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;
using Marten.Services;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Json;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Services.Documents;

/// <summary>
/// The only code in the studio that changes a document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the point.</b> Every method starts with
/// <c>StudioCapabilityGuard.Require(…)</c> — is this operation enabled in this process at all — then
/// <c>StudioScopeResolver.ResolveAsync(…, capability)</c> — may <em>this visitor</em> write to this
/// store, database and tenant — and only then looks anything up. A refusal therefore costs no database
/// work and leaks nothing about what exists, and both refusals are written to the audit before they are
/// thrown (AGENTS.md hard rule 5; events 9202 and 9203).
/// </para>
/// <para>
/// <b>Reads are the studio's SQL, writes are Marten's</b> (D6). The current JSON and the version come
/// from <see cref="DocumentQueryBuilder" /> on a read connection, because the studio only knows a
/// document type as a runtime <see cref="Type" /> and <c>session.Query&lt;T&gt;()</c> is not available
/// to it. The write itself is <c>StoreObjects</c>, <c>UpdateExpectedVersion&lt;T&gt;</c>,
/// <c>UpdateRevision&lt;T&gt;</c>, <c>Delete&lt;T&gt;(id)</c> or <c>UndoDeleteWhere&lt;T&gt;</c> on a
/// real session, so upsert semantics, metadata columns, soft-delete style and tenancy stay exactly as
/// Marten defines them. <c>mt_upsert_*</c> is never called and no <c>insert</c>, <c>update</c> or
/// <c>delete</c> is ever written here.
/// </para>
/// <para>
/// <b>Only the save needs the document.</b> A delete is <c>Delete&lt;T&gt;(id)</c> and an undelete is
/// <c>UndoDeleteWhere&lt;T&gt;(x =&gt; x.Id == id)</c>, so neither deserializes anything: a row whose
/// JSON no CLR type can read is still a row somebody can remove, and bringing a soft-deleted row back
/// cannot lose a property on the way — it is an <c>update … set mt_deleted = false, mt_deleted_at =
/// null</c> that never touches <c>data</c> (verified against <c>Marten.Linq.SqlGeneration.UnSoftDelete</c>,
/// Marten 9.35).
/// </para>
/// <para>
/// <b>A subclass stays a subclass.</b> <c>AllKnownDocumentTypes()</c> hands back root mappings only, so
/// the alias <c>vehicle</c> resolves to <c>Vehicle</c> even for a row that holds a <c>Car</c>. Every row
/// this service reads therefore carries its <c>mt_doc_type</c> with it and the concrete type comes from
/// <c>IDocumentType.TypeFor(alias)</c>; that type is what the JSON is deserialized as and what the typed
/// write is made generic over, so the upsert stamps the discriminator the row already had. Without it a
/// save through this service would quietly turn every <c>Car</c> into a <c>Vehicle</c>.
/// </para>
/// <para>
/// <b>The concurrency check is inside the write transaction.</b> The version read for the preview is
/// minutes old by the time somebody clicks save, so it is read again — with <c>for update</c>, on the
/// session's own connection, inside the session's own transaction — and compared there. Read committed
/// alone would let another session commit between the check and the upsert; the row lock is what closes
/// that window. A <c>lock_timeout</c> is set on the same transaction first, so a row somebody else is
/// holding comes back as a refusal rather than as a page that never answers. For a type that also uses
/// Marten's own optimistic concurrency the expected version is handed to Marten as well, so the
/// database's <c>where mt_version = ?</c> guard runs too and the two checks have to agree.
/// </para>
/// <para>
/// <b>Nothing is saved silently lossy</b> (D7). Every save runs the round trip first and refuses when it
/// would drop properties unless the caller says it has seen them, and a save that drops properties
/// anyway is logged as event 9211 with the paths.
/// </para>
/// <para>
/// <b>Nothing user-supplied reaches a log line unsanitised.</b> The alias and the id come from a URL and
/// the dropped paths are property names out of somebody's document; every audit and log message is put
/// through <see cref="WriteAuditText" /> first, and a serializer's own exception message never leaves
/// the screen (see <see cref="WritePreview.AuditReason" />).
/// </para>
/// </remarks>
internal sealed class DocumentWriteService : IDocumentWriteService
{
    /// <summary>
    /// The most ids one bulk delete will take.
    /// </summary>
    /// <remarks>
    /// A browser holding a transaction open over more rows than this is the wrong tool: past here the
    /// answer is a migration or <c>DeleteDocumentsByTypeAsync</c>. The cap is refused rather than
    /// silently truncated, because a bulk delete that quietly did nine tenths of the job is worse than
    /// one that did none.
    /// </remarks>
    public const int MaxBulkDeleteIds = 500;

    /// <summary>
    /// How long a save waits for the row it is about to overwrite before it gives up.
    /// </summary>
    /// <remarks>
    /// Five seconds is long enough that the ordinary case — another circuit's save, which takes
    /// milliseconds — never notices, and short enough that a row held by somebody's forgotten
    /// <c>begin;</c> in a psql window produces an answer while the person is still looking at the page.
    /// Clamped down to <see cref="MartenStudioOptions.QueryTimeout" /> when that is shorter, because a
    /// host that said "no statement of mine takes more than two seconds" meant this one too.
    /// </remarks>
    internal static readonly TimeSpan DefaultRowLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Postgres <c>lock_not_available</c>: the <c>lock_timeout</c> above expired.</summary>
    private const string LockNotAvailableSqlState = "55P03";

    /// <summary>Postgres <c>query_canceled</c>, which is what a <c>statement_timeout</c> looks like.</summary>
    private const string QueryCanceledSqlState = "57014";

    /// <summary>What a caller is told when somebody else is holding the row.</summary>
    private const string RowIsLockedMessage =
        "This document is being edited by another session, so the studio stopped waiting for it rather " +
        "than holding the page open. Nothing was saved. Try again in a moment.";

    private const string PreviewAction = "PreviewDocumentEdit";
    private const string SaveAction = "EditDocument";
    private const string DeleteAction = "DeleteDocument";
    private const string UndeleteAction = "UndeleteDocument";
    private const string BulkDeleteAction = "BulkDeleteDocuments";

    /// <summary>
    /// The parse limits for an edited document. The depth cap is the same one the JSON viewer uses: a
    /// document nested deeper than this is a document nobody is editing by hand, and an uncapped parse
    /// of attacker-supplied JSON is a stack overflow waiting for a paste.
    /// </summary>
    private static readonly JsonDocumentOptions EditedJsonOptions = new() { MaxDepth = 64 };

    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioScopeResolver resolver;
    private readonly StudioActionLog audit;
    private readonly ColumnCatalog columnCatalog;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly ILogger<DocumentWriteService> logger;
    private readonly AuthenticationStateProvider authenticationStateProvider;

    public DocumentWriteService(
        StudioCapabilityGuard capabilities,
        StudioScopeResolver resolver,
        StudioActionLog audit,
        ColumnCatalog columnCatalog,
        IOptions<MartenStudioOptions> options,
        ILogger<DocumentWriteService> logger,
        AuthenticationStateProvider authenticationStateProvider)
    {
        this.capabilities = capabilities;
        this.resolver = resolver;
        this.audit = audit;
        this.columnCatalog = columnCatalog;
        this.options = options;
        this.logger = logger;
        this.authenticationStateProvider = authenticationStateProvider;
    }

    /// <inheritdoc />
    public async Task<WritePreview> PreviewAsync(
        StudioScope scope,
        string alias,
        string id,
        string editedJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(editedJson);

        var target = Target(alias, id);
        var resolved = await AuthorizeAsync(scope, StudioCapability.EditDocuments, PreviewAction, target, cancellationToken)
            .ConfigureAwait(false);

        WritePreview preview;

        try
        {
            var described = TryFindTarget(resolved, alias);

            if (described.Target is null)
            {
                preview = WritePreview.Refused(alias, id, described.Refusal!, editedJson: editedJson);
            }
            else
            {
                await using var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false);
                var writeTarget = await ReconcileAsync(described.Target, connection, cancellationToken).ConfigureAwait(false);

                var load = await LoadAsync(connection, writeTarget, id, resolved.TenantId, cancellationToken)
                    .ConfigureAwait(false);

                preview = load switch
                {
                    { Error: not null } => WritePreview.Refused(
                        alias, id, load.Error, editedJson: editedJson, documentTypeName: writeTarget.TypeName),
                    { Row: null } => WritePreview.NotFound(alias, id, writeTarget.TypeName),
                    _ => PreviewRow(writeTarget, alias, id, editedJson, load.Row!, resolved),
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(PreviewAction, target, succeeded: false, AuditLine(ex), StudioCapability.EditDocuments, scope);
            throw;
        }

        audit.Record(
            PreviewAction,
            target,
            preview.Status == WritePreviewStatus.Ready,
            preview.Summary(),
            StudioCapability.EditDocuments,
            scope);

        return preview;
    }

    /// <inheritdoc />
    public async Task<WriteResult> SaveAsync(
        StudioScope scope,
        string alias,
        string id,
        string editedJson,
        DocumentConcurrencyToken expectedToken,
        bool acknowledgeDrops,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(editedJson);

        var target = Target(alias, id);
        var resolved = await AuthorizeAsync(scope, StudioCapability.EditDocuments, SaveAction, target, cancellationToken)
            .ConfigureAwait(false);

        WriteResult result;

        try
        {
            result = await SaveCoreAsync(resolved, alias, id, editedJson, expectedToken, acknowledgeDrops, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(SaveAction, target, succeeded: false, AuditLine(ex), StudioCapability.EditDocuments, scope);
            throw;
        }

        audit.Record(
            SaveAction,
            target,
            result.IsSaved,
            result.IsSaved ? result.Preview?.Summary() ?? "Saved." : result.AuditMessage(),
            StudioCapability.EditDocuments,
            scope);

        // 9211: the save happened and it lost something. The dialog showed the paths before the click;
        // this is the record that somebody accepted them, and it is the only place the list survives.
        if (result.IsSaved && result.Preview is { } preview && preview.HasDrops)
        {
            logger.DocumentWriteRoundTripDropped(
                UserName(),
                WriteAuditText.Sanitize(preview.DocumentTypeName ?? alias, WriteAuditText.MaxTokenLength),
                WriteAuditText.Sanitize(id, WriteAuditText.MaxTokenLength),
                preview.DroppedPaths.Length,
                preview.DroppedPathsSummary());
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<DeleteResult> DeleteAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var target = Target(alias, id);
        var resolved = await AuthorizeAsync(scope, StudioCapability.DeleteDocuments, DeleteAction, target, cancellationToken)
            .ConfigureAwait(false);

        DeleteResult result;

        try
        {
            result = await DeleteCoreAsync(resolved, alias, id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(DeleteAction, target, succeeded: false, AuditLine(ex), StudioCapability.DeleteDocuments, scope);
            throw;
        }

        audit.Record(DeleteAction, target, result.Changed, result.Describe(), StudioCapability.DeleteDocuments, scope);
        return result;
    }

    /// <inheritdoc />
    public Task<DeleteResult> UndeleteAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default) =>
        UndeleteAsync(scope, alias, id, acknowledgeDrops: false, cancellationToken);

    /// <inheritdoc />
    public async Task<DeleteResult> UndeleteAsync(
        StudioScope scope,
        string alias,
        string id,
        bool acknowledgeDrops,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        var target = Target(alias, id);
        var resolved = await AuthorizeAsync(scope, StudioCapability.DeleteDocuments, UndeleteAction, target, cancellationToken)
            .ConfigureAwait(false);

        DeleteResult result;

        try
        {
            result = await UndeleteCoreAsync(resolved, alias, id, acknowledgeDrops, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(UndeleteAction, target, succeeded: false, AuditLine(ex), StudioCapability.DeleteDocuments, scope);
            throw;
        }

        audit.Record(UndeleteAction, target, result.Changed, result.Describe(), StudioCapability.DeleteDocuments, scope);
        return result;
    }

    /// <inheritdoc />
    public async Task<BulkDeleteResult> BulkDeleteAsync(
        StudioScope scope,
        string alias,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(ids);

        var target = string.Create(
            CultureInfo.InvariantCulture,
            $"{WriteAuditText.Sanitize(alias, WriteAuditText.MaxTokenLength)} ({ids.Count} ids)");

        var resolved = await AuthorizeAsync(scope, StudioCapability.DeleteDocuments, BulkDeleteAction, target, cancellationToken)
            .ConfigureAwait(false);

        BulkDeleteResult result;

        try
        {
            result = await BulkDeleteCoreAsync(resolved, alias, ids, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(BulkDeleteAction, target, succeeded: false, AuditLine(ex), StudioCapability.DeleteDocuments, scope);
            throw;
        }

        audit.Record(
            BulkDeleteAction,
            target,
            result.Accepted,
            result.Summary(),
            StudioCapability.DeleteDocuments,
            scope);

        return result;
    }

    /// <summary>
    /// The round trip, as a pure function: no database, no session, no scope.
    /// </summary>
    /// <remarks>
    /// Deliberately separable from everything around it. This is the part of D7 that has to be exactly
    /// right and the part a test can hold still: give it a stored document, an edit and a CLR type, and
    /// it answers what saving would do. Everything else in this class is plumbing around it.
    /// </remarks>
    /// <param name="alias">The collection alias.</param>
    /// <param name="id">The document id.</param>
    /// <param name="documentType">
    /// The CLR type Marten would store this document as. For a hierarchy this is the <em>concrete</em>
    /// type the row's <c>mt_doc_type</c> names, not the root the alias resolves to.
    /// </param>
    /// <param name="serializer">
    /// The store's own serializer. Never a <c>JsonSerializer</c> of the studio's own making
    /// (AGENTS.md hard rule 10): a document round-tripped through different settings than Marten wrote
    /// it with produces a diff full of differences nobody made.
    /// </param>
    /// <param name="storedJson">The document as it is stored.</param>
    /// <param name="editedJson">The document as it was edited.</param>
    /// <param name="currentToken">The version the row carries.</param>
    /// <param name="hasConcurrencyColumn">Whether the table has a version column to guard the save with.</param>
    internal static PreviewResult BuildPreview(
        string alias,
        string id,
        Type documentType,
        ISerializer serializer,
        string storedJson,
        string editedJson,
        DocumentConcurrencyToken currentToken,
        bool hasConcurrencyColumn)
    {
        ArgumentNullException.ThrowIfNull(documentType);
        ArgumentNullException.ThrowIfNull(serializer);

        if (!TryParseEdited(editedJson, out var parseError))
        {
            return new PreviewResult(
                WritePreview.Refused(
                    alias,
                    id,
                    parseError,
                    editedJson: editedJson,
                    storedJson: storedJson,
                    documentTypeName: documentType.Name,
                    currentToken: currentToken,
                    auditReason: "The edit is not valid JSON."),
                null);
        }

        if (!TryDeserialize(serializer, documentType, editedJson, out var document, out var typeError, out var typeAudit))
        {
            return new PreviewResult(
                WritePreview.Refused(
                    alias,
                    id,
                    typeError,
                    typeIsConstructible: false,
                    editedJson: editedJson,
                    storedJson: storedJson,
                    documentTypeName: documentType.Name,
                    currentToken: currentToken,
                    auditReason: typeAudit),
                null);
        }

        string roundTrippedJson;

        try
        {
            roundTrippedJson = serializer.ToJson(document);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PreviewResult(
                WritePreview.Refused(
                    alias,
                    id,
                    $"'{documentType.Name}' could not be serialized back: {ex.Message}",
                    typeIsConstructible: false,
                    editedJson: editedJson,
                    storedJson: storedJson,
                    documentTypeName: documentType.Name,
                    currentToken: currentToken,
                    auditReason: $"'{documentType.Name}' could not be serialized back ({ex.GetType().Name})."),
                null);
        }

        // Edited against round-tripped is the loss; stored against edited is the change. Two questions,
        // one differ, because they are the same operation with different arguments.
        var roundTripDiff = RoundTripDiffer.Diff(editedJson, roundTrippedJson);
        var editDiff = RoundTripDiffer.Diff(storedJson, editedJson);

        var caveat = !hasConcurrencyColumn
            ? "This collection has no version column, so the save cannot be checked against a " +
              "concurrent change. Saving will overwrite whatever is there."
            : currentToken.IsKnown
                ? null
                : "This row's version column is empty, so the save cannot be checked against a " +
                  "concurrent change and will be refused. A row written outside Marten, or migrated by " +
                  "hand, does this.";

        return new PreviewResult(
            WritePreview.Ready(
                alias,
                id,
                documentType.Name,
                storedJson,
                editedJson,
                roundTrippedJson,
                roundTripDiff,
                editDiff,
                currentToken,
                caveat),
            document);
    }

    /// <summary>A preview and, when it is ready, the instance the round trip produced.</summary>
    /// <param name="Preview">What the save would do.</param>
    /// <param name="Document">The deserialized document, or <see langword="null" /> when the preview is not ready.</param>
    internal readonly record struct PreviewResult(WritePreview Preview, object? Document);

    /// <summary>Builds the preview for a row that is there, once its concrete type is settled.</summary>
    private static WritePreview PreviewRow(
        WriteTarget writeTarget,
        string alias,
        string id,
        string editedJson,
        DocumentRow row,
        ResolvedScope resolved)
    {
        var runtime = ResolveRuntimeType(writeTarget, row.DocType);

        if (runtime.Type is null)
        {
            return WritePreview.Refused(
                alias,
                id,
                runtime.Refusal!,
                typeIsConstructible: false,
                editedJson: editedJson,
                storedJson: row.Json,
                documentTypeName: writeTarget.TypeName,
                currentToken: row.Token);
        }

        return BuildPreview(
            alias,
            id,
            runtime.Type,
            resolved.Store.Options.Serializer(),
            row.Json,
            editedJson,
            row.Token,
            HasConcurrencyColumn(writeTarget.Table)).Preview;
    }

    private async Task<WriteResult> SaveCoreAsync(
        ResolvedScope resolved,
        string alias,
        string id,
        string editedJson,
        DocumentConcurrencyToken expectedToken,
        bool acknowledgeDrops,
        CancellationToken cancellationToken)
    {
        var described = TryFindTarget(resolved, alias);
        if (described.Target is null)
        {
            return WriteResult.Refused(described.Refusal!);
        }

        WriteTarget writeTarget;
        WritePreview preview;
        Type documentType;
        string? storedDocType;
        object document;

        // The read connection is opened, used and closed before the session opens: two connections held
        // at once per save is the kind of thing that only shows up as pool exhaustion under load.
        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            writeTarget = await ReconcileAsync(described.Target, connection, cancellationToken).ConfigureAwait(false);

            var load = await LoadAsync(connection, writeTarget, id, resolved.TenantId, cancellationToken).ConfigureAwait(false);
            if (load.Error is not null)
            {
                return WriteResult.Refused(load.Error);
            }

            if (load.Row is null)
            {
                var notFound = WritePreview.NotFound(alias, id, writeTarget.TypeName);
                return WriteResult.Refused(notFound.Reason!, notFound);
            }

            storedDocType = load.Row.DocType;

            var runtime = ResolveRuntimeType(writeTarget, storedDocType);
            if (runtime.Type is null)
            {
                return WriteResult.Refused(runtime.Refusal!);
            }

            documentType = runtime.Type;

            var built = BuildPreview(
                alias,
                id,
                documentType,
                resolved.Store.Options.Serializer(),
                load.Row.Json,
                editedJson,
                load.Row.Token,
                HasConcurrencyColumn(writeTarget.Table));

            preview = built.Preview;

            if (built.Document is null)
            {
                return WriteResult.Refused(preview.Reason ?? "The edit could not be saved.", preview);
            }

            document = built.Document;

            // A version column that is there and empty is not a conflict — it is a row nothing can be
            // checked against, and reporting it as a conflict would put the editor in a loop where
            // reloading changes nothing and saving never works. The preview goes back with it so the
            // screen can still show what the edit would have done.
            if (HasConcurrencyColumn(writeTarget.Table) && !load.Row.Token.IsKnown)
            {
                return WriteResult.Refused(NullVersionMessage(writeTarget), preview);
            }
        }

        if (preview.HasDrops && !acknowledgeDrops)
        {
            var typeName = preview.DocumentTypeName ?? writeTarget.TypeName;

            return WriteResult.Refused(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Saving would drop {preview.DroppedPaths.Length} propert{(preview.DroppedPaths.Length == 1 ? "y" : "ies")} that '{typeName}' has no member for: {preview.DroppedPathsSummary()}. Nothing was saved."),
                preview);
        }

        var guarded = HasConcurrencyColumn(writeTarget.Table);

        if (guarded && !expectedToken.IsKnown)
        {
            return WriteResult.Refused(
                "This edit did not carry the document's version, so it could not be checked against a " +
                "concurrent change. Reopen the document and apply the edit again.",
                preview);
        }

        await using var session = OpenSession(resolved);

        // Force the connection and the transaction open, so the check below and the write below are one
        // transaction rather than two statements that happen to follow each other.
        await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ApplyLockTimeoutAsync(session, cancellationToken).ConfigureAwait(false);

        if (!DocumentQueryBuilder.TryBuildSingle(
                writeTarget.Table,
                id,
                resolved.TenantId,
                DocumentRowLock.ForUpdate,
                out var command,
                out var idError))
        {
            return WriteResult.Refused(idError, preview);
        }

        DocumentRow? current;

        try
        {
            await using (command)
            {
                await using var reader = await ExecuteInSessionTransactionAsync(session, command, cancellationToken)
                    .ConfigureAwait(false);

                current = await ReadRowAsync(reader, writeTarget.Table, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsRowUnavailable(ex, cancellationToken))
        {
            return WriteResult.Refused(RowIsLockedMessage, preview);
        }

        if (current is null)
        {
            return WriteResult.Refused(
                "The document was deleted while it was open, so there was nothing to save over.",
                preview);
        }

        if (!string.Equals(current.DocType, storedDocType, StringComparison.Ordinal))
        {
            return WriteResult.Refused(
                "The document's own type changed while it was open, so saving the edit would have " +
                "rewritten it as something else. Nothing was saved; reload the document.",
                preview);
        }

        if (guarded && !current.Token.IsKnown)
        {
            return WriteResult.Refused(NullVersionMessage(writeTarget), preview);
        }

        if (guarded && current.Token != expectedToken)
        {
            return WriteResult.Conflict(current.Token, preview);
        }

        QueueStore(session, writeTarget, documentType, document, expectedToken.IsKnown ? expectedToken : current.Token);

        try
        {
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsConcurrencyFailure(ex))
        {
            // Marten's own check fired. Whatever is in the row now is what the screen has to show.
            var latest = await ReadTokenAsync(resolved, writeTarget, id, cancellationToken).ConfigureAwait(false);
            return WriteResult.Conflict(latest, preview);
        }
        catch (Exception ex) when (IsRowUnavailable(ex, cancellationToken))
        {
            return WriteResult.Refused(RowIsLockedMessage, preview);
        }

        var saved = await ReadTokenAsync(resolved, writeTarget, id, cancellationToken).ConfigureAwait(false);
        return WriteResult.Saved(saved, preview);
    }

    private async Task<DeleteResult> DeleteCoreAsync(
        ResolvedScope resolved,
        string alias,
        string id,
        CancellationToken cancellationToken)
    {
        var described = TryFindTarget(resolved, alias);
        if (described.Target is null)
        {
            return DeleteResult.Refused(alias, id, described.Refusal!);
        }

        WriteTarget writeTarget;
        ExistingRow row;

        // The existence read does not select `data`: a delete does not need the document, and a row whose
        // JSON no CLR type can read has to stay deletable (that is the whole point of deleting by id).
        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            writeTarget = await ReconcileAsync(described.Target, connection, cancellationToken).ConfigureAwait(false);

            var lookup = await LoadIdsAsync(connection, writeTarget, [id], resolved.TenantId, cancellationToken)
                .ConfigureAwait(false);

            if (lookup.Errors[0] is { } error)
            {
                return DeleteResult.Refused(alias, id, error);
            }

            if (lookup.Rows[0] is not { } found)
            {
                return DeleteResult.NotFound(alias, id);
            }

            row = found;
        }

        // A row whose discriminator the mapping cannot name is still a row: the primary key says which
        // one it is, and refusing to delete it would leave the studio unable to clean up exactly the
        // rows somebody most wants gone.
        var documentType = ResolveRuntimeType(writeTarget, row.DocType).Type ?? writeTarget.ClrType;

        await using var session = OpenSession(resolved);

        if (TypedIds.TryConvert(writeTarget.IdMemberType, row.IdValue, out var typedId))
        {
            // Delete<T>(id) rather than DeleteObjects: it is what knows that this type is soft-deleted
            // and that that type is not, it keeps tenancy right, and it never has to see the document.
            TypedSessionWrites.DeleteById(session, documentType, typedId);
        }
        else if (await TryQueueDeleteByDocumentAsync(session, resolved, writeTarget, documentType, id, cancellationToken)
                     .ConfigureAwait(false) is { } refusal)
        {
            return DeleteResult.Refused(alias, id, refusal);
        }

        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return writeTarget.IsSoftDeleted
            ? DeleteResult.SoftDeleted(alias, id)
            : DeleteResult.HardDeleted(alias, id);
    }

    private async Task<DeleteResult> UndeleteCoreAsync(
        ResolvedScope resolved,
        string alias,
        string id,
        bool acknowledgeDrops,
        CancellationToken cancellationToken)
    {
        var described = TryFindTarget(resolved, alias);
        if (described.Target is null)
        {
            return DeleteResult.Refused(alias, id, described.Refusal!);
        }

        if (!described.Target.IsSoftDeleted)
        {
            return DeleteResult.Refused(
                alias,
                id,
                $"'{alias}' is not soft-deleted, so a deleted document of this type is gone and there is " +
                "nothing to bring back.");
        }

        WriteTarget writeTarget;
        ExistingRow row;

        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            writeTarget = await ReconcileAsync(described.Target, connection, cancellationToken).ConfigureAwait(false);

            // The existence read carries no soft-delete predicate, so the deleted row is right there.
            var lookup = await LoadIdsAsync(connection, writeTarget, [id], resolved.TenantId, cancellationToken)
                .ConfigureAwait(false);

            if (lookup.Errors[0] is { } error)
            {
                return DeleteResult.Refused(alias, id, error);
            }

            if (lookup.Rows[0] is not { } found)
            {
                return DeleteResult.NotFound(alias, id);
            }

            row = found;
        }

        if (!row.IsDeleted)
        {
            return DeleteResult.Undeleted(alias, id, "The document was not deleted; nothing was changed.");
        }

        var queued = false;

        await using (var session = OpenSession(resolved))
        {
            if (TypedIds.TryConvert(writeTarget.IdMemberType, row.IdValue, out var typedId) &&
                TypedSessionWrites.TryQueueUndoDelete(session, writeTarget.ClrType, writeTarget.IdMember, typedId))
            {
                await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                queued = true;
            }
        }

        if (!queued)
        {
            var fallback = await UndeleteByRewritingAsync(resolved, writeTarget, alias, id, acknowledgeDrops, cancellationToken)
                .ConfigureAwait(false);

            if (fallback is not null)
            {
                return fallback;
            }
        }

        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            var after = await LoadIdsAsync(connection, writeTarget, [id], resolved.TenantId, cancellationToken)
                .ConfigureAwait(false);

            if (after.Rows[0] is not { IsDeleted: false })
            {
                return DeleteResult.Refused(
                    alias,
                    id,
                    "The document is still marked deleted after the write. Nothing else was changed.");
            }
        }

        return DeleteResult.Undeleted(alias, id);
    }

    /// <summary>
    /// The undelete of last resort: deserialize the document and store it back, because Marten's upsert
    /// clears <c>mt_deleted</c> on every write.
    /// </summary>
    /// <remarks>
    /// Only reached for a document type whose id the studio cannot turn into
    /// <c>x =&gt; x.Id == id</c> — an F# discriminated-union id, or a value object with no constructor
    /// Marten's own rules would accept. It is a <em>full rewrite of the document body</em>, so it runs
    /// the round-trip differ first and refuses unless the caller has acknowledged what that would change.
    /// Returns the refusal, or <see langword="null" /> when the write was queued and committed.
    /// </remarks>
    private async Task<DeleteResult?> UndeleteByRewritingAsync(
        ResolvedScope resolved,
        WriteTarget writeTarget,
        string alias,
        string id,
        bool acknowledgeDrops,
        CancellationToken cancellationToken)
    {
        object document;
        DocumentConcurrencyToken token;
        Type documentType;
        JsonDiffResult diff;

        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            var load = await LoadAsync(connection, writeTarget, id, resolved.TenantId, cancellationToken).ConfigureAwait(false);

            if (load.Error is not null)
            {
                return DeleteResult.Refused(alias, id, load.Error);
            }

            if (load.Row is null)
            {
                return DeleteResult.NotFound(alias, id);
            }

            var runtime = ResolveRuntimeType(writeTarget, load.Row.DocType);
            if (runtime.Type is null)
            {
                return DeleteResult.Refused(alias, id, runtime.Refusal!);
            }

            documentType = runtime.Type;
            var serializer = resolved.Store.Options.Serializer();

            if (!TryDeserialize(serializer, documentType, load.Row.Json, out var deserialized, out var error, out var auditError))
            {
                return DeleteResult.Refused(alias, id, error, auditError);
            }

            document = deserialized;
            token = load.Row.Token;

            try
            {
                diff = RoundTripDiffer.Diff(load.Row.Json, serializer.ToJson(document));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return DeleteResult.Refused(
                    alias,
                    id,
                    $"'{documentType.Name}' could not be serialized back, so the document cannot be brought " +
                    $"back without losing it: {ex.Message}",
                    $"'{documentType.Name}' could not be serialized back ({ex.GetType().Name}).");
            }
        }

        var changes = diff.Dropped.Length + diff.Changed.Length;

        if (!acknowledgeDrops && (changes > 0 || diff.IsFaulted))
        {
            return DeleteResult.Refused(
                alias,
                id,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The studio cannot bring a '{writeTarget.TypeName}' back without rewriting its JSON, and " +
                    $"the rewrite would change {changes} value{(changes == 1 ? string.Empty : "s")}. Nothing was changed."));
        }

        await using var session = OpenSession(resolved);

        QueueStore(session, writeTarget, documentType, document, token);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return null;
    }

    private async Task<BulkDeleteResult> BulkDeleteCoreAsync(
        ResolvedScope resolved,
        string alias,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count > MaxBulkDeleteIds)
        {
            return BulkDeleteResult.Refused(string.Create(
                CultureInfo.InvariantCulture,
                $"{ids.Count} documents is more than the {MaxBulkDeleteIds} this screen will delete at once. Narrow the selection, or use a migration."));
        }

        if (ids.Count == 0)
        {
            return BulkDeleteResult.Completed([]);
        }

        var described = TryFindTarget(resolved, alias);
        if (described.Target is null)
        {
            return BulkDeleteResult.Refused(described.Refusal!);
        }

        WriteTarget writeTarget;
        IdLookup lookup;

        // One read for the whole selection — `id = any(@ids)` — rather than one round trip per id, and
        // without the `data` column, because a bulk delete never needs the documents.
        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            writeTarget = await ReconcileAsync(described.Target, connection, cancellationToken).ConfigureAwait(false);
            lookup = await LoadIdsAsync(connection, writeTarget, ids, resolved.TenantId, cancellationToken)
                .ConfigureAwait(false);
        }

        var outcomes = new DeleteResult[ids.Count];
        List<int> queued = [];

        // One session, one SaveChangesAsync: the whole selection is one transaction, so a failure
        // halfway leaves the database as it was rather than half-deleted.
        await using var session = OpenSession(resolved);

        HashSet<string> alreadyQueued = new(StringComparer.Ordinal);

        for (var i = 0; i < ids.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (lookup.Errors[i] is { } error)
            {
                outcomes[i] = DeleteResult.Refused(alias, ids[i], error);
                continue;
            }

            if (lookup.Rows[i] is not { } row)
            {
                outcomes[i] = DeleteResult.NotFound(alias, ids[i]);
                continue;
            }

            if (!TypedIds.TryConvert(writeTarget.IdMemberType, row.IdValue, out var typedId))
            {
                outcomes[i] = DeleteResult.Refused(
                    alias,
                    ids[i],
                    $"The studio cannot build an id of type '{writeTarget.IdMemberType.Name}' for this row, so " +
                    "it cannot be deleted from a selection. Open it and delete it on its own page.");
                continue;
            }

            // The same id twice in one selection is one delete and two answers.
            if (alreadyQueued.Add(IdKey(row.IdValue)))
            {
                var documentType = ResolveRuntimeType(writeTarget, row.DocType).Type ?? writeTarget.ClrType;
                TypedSessionWrites.DeleteById(session, documentType, typedId);
            }

            queued.Add(i);
        }

        if (queued.Count > 0)
        {
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var index in queued)
        {
            outcomes[index] = writeTarget.IsSoftDeleted
                ? DeleteResult.SoftDeleted(alias, ids[index])
                : DeleteResult.HardDeleted(alias, ids[index]);
        }

        return BulkDeleteResult.Completed([.. outcomes]);
    }

    /// <summary>
    /// Capability first, then policy, then anything at all. Both refusals are audited before they are
    /// thrown, and neither of them has touched the database.
    /// </summary>
    private async Task<ResolvedScope> AuthorizeAsync(
        StudioScope scope,
        StudioCapability capability,
        string action,
        string target,
        CancellationToken cancellationToken)
    {
        try
        {
            capabilities.Require(capability);
        }
        catch (StudioCapabilityDeniedException denial)
        {
            audit.RecordCapabilityDenied(denial, action, target);
            throw;
        }

        try
        {
            return await resolver.ResolveAsync(scope, capability.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            audit.RecordScopeDenied(scope, WritePolicyName(), action, target);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(action, target, succeeded: false, AuditLine(ex), capability, scope);
            throw;
        }
    }

    /// <summary>
    /// Which policy a scope refusal is attributed to, spelled the way the host configured it. With only
    /// a store policy set, that is the one that answered for the write as well.
    /// </summary>
    private string WritePolicyName()
    {
        var value = options.Value;
        return value.WriteAuthorizationPolicy ?? value.StoreAuthorizationPolicy ?? "(none)";
    }

    /// <summary>
    /// What an unexpected failure says in the audit: the exception's type and message, sanitised and
    /// capped. The stack trace belongs to whoever catches it further up.
    /// </summary>
    private static string AuditLine(Exception exception) =>
        WriteAuditText.Sanitize(exception.GetType().Name + ": " + exception.Message);

    /// <summary>
    /// The audit's name for what was acted on. Both halves come from a URL, so both are sanitised — a
    /// document id containing a newline would otherwise write a second line into the log.
    /// </summary>
    private static string Target(string alias, string id) =>
        WriteAuditText.Sanitize(alias, WriteAuditText.MaxTokenLength) + "/" +
        WriteAuditText.Sanitize(id, WriteAuditText.MaxTokenLength);

    private int CommandTimeoutSeconds => (int) Math.Ceiling(options.Value.QueryTimeout.TotalSeconds);

    /// <summary>
    /// How long the <c>for update</c> read waits before it gives up on the row.
    /// </summary>
    /// <remarks>
    /// Half the query timeout, capped at <see cref="DefaultRowLockTimeout" />. Half rather than all of it
    /// because the two timeouts must not be able to fire at the same moment: <c>lock_timeout</c> expiring
    /// is <c>55P03</c> from the server and turns into a sentence about somebody else editing the
    /// document, while Npgsql giving up on the command is a socket-level failure with nothing in it a
    /// person could act on. The one that has an explanation has to win.
    /// </remarks>
    private TimeSpan RowLockTimeout
    {
        get
        {
            var queryTimeout = options.Value.QueryTimeout;

            if (queryTimeout <= TimeSpan.Zero)
            {
                return DefaultRowLockTimeout;
            }

            var half = TimeSpan.FromTicks(queryTimeout.Ticks / 2);
            return half < DefaultRowLockTimeout ? half : DefaultRowLockTimeout;
        }
    }

    private static async Task<NpgsqlConnection> OpenReadConnectionAsync(
        ResolvedScope resolved,
        CancellationToken cancellationToken)
    {
        var connection = resolved.Database.CreateConnection(ConnectionUsage.Read);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The session every write goes through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SessionOptions.ForDatabase(tenantId, database)</c> is what makes a write to a named database
    /// in a multi-database store possible at all (Appendix B: the static factories exist, so the plan's
    /// "refuse when unsupported" fallback is not needed). A scope with no tenant uses
    /// <c>ForDatabase(database)</c>, which Marten resolves to the default tenant <c>*DEFAULT*</c> — fine
    /// for a single-tenant collection and wrong for a conjoined one, which is exactly why
    /// <see cref="TryFindTarget" /> refuses a conjoined collection before we ever get here.
    /// </para>
    /// <para>
    /// Lightweight: the studio has no use for an identity map, and <c>ForDatabase</c> sets
    /// <c>DocumentTracking.None</c> anyway. The timeout is the studio's own query timeout, because
    /// Marten applies <c>SessionOptions.Timeout</c> to every command it runs on the session — including
    /// the concurrency re-read, whose own <c>CommandTimeout</c> the session would otherwise overwrite.
    /// </para>
    /// </remarks>
    private IDocumentSession OpenSession(ResolvedScope resolved)
    {
        var sessionOptions = resolved.TenantId is null
            ? SessionOptions.ForDatabase(resolved.Database)
            : SessionOptions.ForDatabase(resolved.TenantId, resolved.Database);

        sessionOptions.Timeout = CommandTimeoutSeconds;

        return resolved.Store.LightweightSession(sessionOptions);
    }

    /// <summary>
    /// Caps how long this transaction will wait for a row lock.
    /// </summary>
    /// <remarks>
    /// Run on the session's own connection, after <c>BeginTransactionAsync</c>, so the <c>true</c> third
    /// argument to <c>set_config</c> scopes it to the transaction that is about to take the lock and
    /// nothing else on that connection afterwards.
    /// </remarks>
    private async Task ApplyLockTimeoutAsync(IDocumentSession session, CancellationToken cancellationToken)
    {
        await using var command = DocumentQueryBuilder.BuildLockTimeout(RowLockTimeout);
        await using var reader = await ExecuteInSessionTransactionAsync(session, command, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one of the studio's own statements inside the session's transaction, on the session's
    /// connection, and deliberately <em>not</em> through <c>session.ExecuteReaderAsync</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Marten runs everything that goes through the session's own execute methods under
    /// <c>StoreOptions.ResiliencePipeline</c>, whose default retries any <c>NpgsqlException</c> or
    /// <c>MartenCommandException</c> three times (verified against
    /// <c>Marten.Util.ResilientPipelineBuilderExtensions.AddMartenDefaults</c>, 9.35). That is the right
    /// policy for an ordinary read and precisely the wrong one for a statement that takes a lock: a
    /// <c>lock_timeout</c> expiry is <c>55P03</c>, which aborts the transaction, so the retry comes back
    /// as <c>25P02</c> — "current transaction is aborted" — and the error that says <em>why</em> is gone.
    /// The screen would get an unhandled exception where it should have got "somebody else is editing
    /// this".
    /// </para>
    /// <para>
    /// <c>IQuerySession.Connection</c> is the session's own open connection, so the statement is still in
    /// the session's transaction — the part that makes the lock mean anything. Npgsql executes a command
    /// in whatever transaction its connection is currently in; the <c>Transaction</c> property is not
    /// consulted.
    /// </para>
    /// </remarks>
    private async Task<DbDataReader> ExecuteInSessionTransactionAsync(
        IDocumentSession session,
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        command.Connection = session.Connection;
        command.CommandTimeout = CommandTimeoutSeconds;

        return await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What the studio knows about the collection it is about to write to: Marten's mapping, and the
    /// table as it actually exists.
    /// </summary>
    private sealed record WriteTarget(IDocumentType DocumentType, DocumentMapping? Mapping, DocumentTableInfo Table)
    {
        public Type ClrType => DocumentType.DocumentType;

        public string TypeName => DocumentType.DocumentType.Name;

        /// <summary>The member Marten treats as this type's identity, which an id expression is built on.</summary>
        public MemberInfo IdMember => DocumentType.IdMember;

        /// <summary>
        /// The CLR type of that member — <see cref="IDocumentType.IdType" />, which Marten defines as the
        /// id member's type, so a strong-typed id reports the wrapper and not the value inside it.
        /// </summary>
        public Type IdMemberType => DocumentType.IdType;

        /// <summary>
        /// Whether this alias covers more than one CLR type, in which case every row carries an
        /// <c>mt_doc_type</c> saying which one it is.
        /// </summary>
        public bool IsHierarchy => DocumentType.IsHierarchy();

        /// <summary>
        /// Whether a delete hides the row or removes it. Read from the concrete mapping's
        /// <c>DeleteStyle</c>, because <c>Metadata.IsSoftDeleted.Enabled</c> is true even on a type that
        /// has no <c>mt_deleted</c> column at all (Appendix B addendum).
        /// </summary>
        public bool IsSoftDeleted => Mapping is not null
            ? Mapping.DeleteStyle == DeleteStyle.SoftDelete
            : Table.SoftDeleteEnabled;

        public bool UsesOptimisticConcurrency => Mapping?.UseOptimisticConcurrency ?? false;

        public bool UsesNumericRevisions => Mapping?.UseNumericRevisions ?? false;
    }

    private readonly record struct DescribeResult(WriteTarget? Target, string? Refusal);

    /// <summary>
    /// What the mapping alone can say, before a connection is opened.
    /// </summary>
    /// <remarks>
    /// Deliberately does no database work. An alias nobody mapped and a scope that cannot address a
    /// multi-tenanted collection are both refusals a connection would not change, and a write service
    /// that opened a connection to find that out would be a way to make a server connect on demand by
    /// typing a URL.
    /// </remarks>
    private DescribeResult TryFindTarget(ResolvedScope resolved, string alias)
    {
        var documentType = FindDocumentType(resolved.Store, alias);

        if (documentType is null)
        {
            return new DescribeResult(
                null,
                $"'{alias}' is not a document type this store has a mapping for, so the studio has no CLR " +
                "type to write it as. Tables the studio discovered without a mapping are read-only.");
        }

        // A conjoined collection reached with no tenant in scope would be written as the *DEFAULT*
        // tenant — a row in a tenant nobody asked for, which is the worst possible answer. Refuse, and
        // say what to do about it.
        if (documentType.TenancyStyle == TenancyStyle.Conjoined && resolved.TenantId is null)
        {
            return new DescribeResult(
                null,
                $"'{alias}' is multi-tenanted, so a write has to say which tenant it is for. Choose a tenant " +
                "in the scope selector and try again.");
        }

        return new DescribeResult(
            new WriteTarget(documentType, documentType as DocumentMapping, DocumentTableInfo.FromDocumentType(documentType)),
            null);
    }

    /// <summary>
    /// Settles the configured table against the one that is actually there, which is what keeps the
    /// generated SQL working across schema drift.
    /// </summary>
    private async Task<WriteTarget> ReconcileAsync(
        WriteTarget target,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var physical = await columnCatalog
            .GetAsync(connection, target.Table.Schema, target.Table.Table, cancellationToken)
            .ConfigureAwait(false);

        return target with { Table = target.Table.WithPhysicalColumns(physical) };
    }

    /// <summary>
    /// The mapping for one alias, or nothing.
    /// </summary>
    /// <remarks>
    /// <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> is honoured here as well as on the read
    /// side: a type a host hid from the studio must not be writable through a hand-typed alias either.
    /// A hidden type answers exactly as an unmapped one does — the refusal is the same sentence — so the
    /// gate does not tell a visitor which types exist and are merely hidden.
    /// </remarks>
    private IDocumentType? FindDocumentType(IDocumentStore store, string alias)
    {
        var isVisible = options.Value.IsDocumentTypeVisible;

        foreach (var documentType in store.Options.AllKnownDocumentTypes())
        {
            if (!string.Equals(documentType.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return isVisible is not null && !isVisible(documentType.DocumentType) ? null : documentType;
        }

        return null;
    }

    /// <summary>The concrete CLR type one row is, or why the studio cannot say.</summary>
    /// <param name="Type">The type to deserialize as and to write as.</param>
    /// <param name="Refusal">Why it could not be settled, when it could not.</param>
    private readonly record struct RuntimeType(Type? Type, string? Refusal);

    /// <summary>
    /// Resolves what a row actually is, which for a hierarchy is not what its alias says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IReadOnlyStoreOptions.AllKnownDocumentTypes()</c> returns <c>Storage.AllDocumentMappings</c>,
    /// which holds root mappings only — a <c>SubClassMapping</c> is not an <c>IDocumentType</c> at all.
    /// So the alias <c>vehicle</c> resolves to <c>Vehicle</c>, and a <c>Car</c> row deserialized as
    /// <c>Vehicle</c> and stored back would be stamped <c>mt_doc_type = 'BASE'</c> and
    /// <c>mt_dotnet_type = Vehicle</c>: the row would silently stop being a <c>Car</c>, and for a marker
    /// subclass with no members of its own the round-trip differ would report nothing dropped, because
    /// nothing in the JSON was.
    /// </para>
    /// <para>
    /// <c>IDocumentType.TypeFor(alias)</c> is the mapping's own reverse lookup and is what fixes it. It
    /// answers <c>"BASE"</c> — the value Marten's <c>DocTypeArgument</c> writes for an instance of the
    /// root type itself — with the root, and <b>throws</b> <see cref="ArgumentOutOfRangeException" /> for
    /// an alias it does not know, which is why the call is guarded: a row stamped by an older version of
    /// the application with a subclass that has since been removed must be a refusal a person can read,
    /// not an unhandled exception.
    /// </para>
    /// </remarks>
    private static RuntimeType ResolveRuntimeType(WriteTarget target, string? docTypeAlias)
    {
        if (!target.IsHierarchy)
        {
            return new RuntimeType(target.ClrType, null);
        }

        if (string.IsNullOrWhiteSpace(docTypeAlias))
        {
            if (target.ClrType.IsAbstract || target.ClrType.IsInterface)
            {
                return new RuntimeType(
                    null,
                    $"This row carries no document-type discriminator and '{target.TypeName}' cannot be " +
                    "instantiated, so the studio has no type to read it as. Nothing was changed.");
            }

            return new RuntimeType(target.ClrType, null);
        }

        try
        {
            var resolved = target.DocumentType.TypeFor(docTypeAlias);

            return resolved is null
                ? new RuntimeType(null, UnknownSubclass(target, docTypeAlias))
                : new RuntimeType(resolved, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new RuntimeType(null, UnknownSubclass(target, docTypeAlias));
        }
    }

    private static string UnknownSubclass(WriteTarget target, string docTypeAlias) =>
        $"This row says it is a '{docTypeAlias}', which is not a subclass '{target.TypeName}' is mapped " +
        "with in this process. Writing it would change what it is, so nothing was changed. Register the " +
        "subclass, or use a store that has it.";

    /// <summary>What a save is told about a row whose version column is there and empty.</summary>
    private static string NullVersionMessage(WriteTarget target) =>
        $"This row's version column is empty, so there is nothing to check the save against and the " +
        $"studio will not overwrite it blindly. A '{target.TypeName}' row written outside Marten, or " +
        "migrated by hand, does this; give the column a value and try again.";

    /// <summary>One document row, as much of it as a write cares about.</summary>
    /// <param name="Json">The stored document.</param>
    /// <param name="Token">The version or revision the row carries.</param>
    /// <param name="IsDeleted">Whether it is soft-deleted.</param>
    /// <param name="DocType">The <c>mt_doc_type</c> discriminator, for a hierarchy.</param>
    private sealed record DocumentRow(string Json, DocumentConcurrencyToken Token, bool IsDeleted, string? DocType);

    private readonly record struct DocumentLoad(DocumentRow? Row, string? Error);

    private async Task<DocumentLoad> LoadAsync(
        NpgsqlConnection connection,
        WriteTarget target,
        string id,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        if (!DocumentQueryBuilder.TryBuildSingle(target.Table, id, tenantId, out var command, out var error))
        {
            return new DocumentLoad(null, error);
        }

        await using (command)
        {
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return new DocumentLoad(await ReadRowAsync(reader, target.Table, cancellationToken).ConfigureAwait(false), null);
        }
    }

    /// <summary>One row as the delete path knows it: its key, its discriminator and whether it is hidden.</summary>
    /// <param name="IdValue">The id exactly as the column holds it.</param>
    /// <param name="DocType">The <c>mt_doc_type</c> discriminator, for a hierarchy.</param>
    /// <param name="IsDeleted">Whether it is soft-deleted.</param>
    private sealed record ExistingRow(object IdValue, string? DocType, bool IsDeleted);

    /// <summary>
    /// The answer to "which of these ids are there", per requested id.
    /// </summary>
    /// <param name="Errors">Why an id could not be used at all, per requested id.</param>
    /// <param name="Rows">The row for each requested id, or <see langword="null" /> when there is none.</param>
    private readonly record struct IdLookup(IReadOnlyList<string?> Errors, ExistingRow?[] Rows);

    /// <summary>
    /// Reads which of <paramref name="ids" /> exist, in one statement and without the <c>data</c> column.
    /// </summary>
    /// <remarks>
    /// The single-document delete uses this too, with one id. Rows come back in whatever order Postgres
    /// produces them, so each is matched to the requested ids by a canonical key — and a selection that
    /// names the same document twice gets two answers and one delete.
    /// </remarks>
    private async Task<IdLookup> LoadIdsAsync(
        NpgsqlConnection connection,
        WriteTarget target,
        IReadOnlyList<string> ids,
        string? tenantId,
        CancellationToken cancellationToken)
    {
        var rows = new ExistingRow?[ids.Count];

        if (!DocumentQueryBuilder.TryBuildMany(target.Table, ids, tenantId, out var command, out var errors))
        {
            return new IdLookup(errors, rows);
        }

        Dictionary<string, List<int>> wanted = new(StringComparer.Ordinal);

        for (var i = 0; i < ids.Count; i++)
        {
            if (errors[i] is not null)
            {
                continue;
            }

            var parsed = DocumentQueryBuilder.ParseId(target.Table.IdColumnType, ids[i]);
            var key = IdKey(parsed.Value!);

            if (!wanted.TryGetValue(key, out var indexes))
            {
                wanted[key] = indexes = [];
            }

            indexes.Add(i);
        }

        var docTypeColumn = target.Table.MetadataColumnName(DocumentMetadataColumn.DocumentType);
        var deletedColumn = target.Table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted);

        await using (command)
        {
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var idValue = reader.GetValue(0);
                var docType = await ReadOptionalStringAsync(reader, docTypeColumn, cancellationToken).ConfigureAwait(false);
                var deleted = await ReadOptionalBooleanAsync(reader, deletedColumn, cancellationToken).ConfigureAwait(false);

                if (!wanted.TryGetValue(IdKey(idValue), out var indexes))
                {
                    continue;
                }

                foreach (var index in indexes)
                {
                    rows[index] = new ExistingRow(idValue, docType, deleted);
                }
            }
        }

        return new IdLookup(errors, rows);
    }

    /// <summary>
    /// One id as a string that two equal ids always agree on, so a row read back can be matched to the id
    /// that asked for it whatever CLR type the column produced.
    /// </summary>
    private static string IdKey(object value) => value switch
    {
        Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Reads the version the row carries now, on a connection of its own.</summary>
    private async Task<DocumentConcurrencyToken> ReadTokenAsync(
        ResolvedScope resolved,
        WriteTarget target,
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false);
        var load = await LoadAsync(connection, target, id, resolved.TenantId, cancellationToken).ConfigureAwait(false);

        return load.Row?.Token ?? DocumentConcurrencyToken.None;
    }

    private static async Task<DocumentRow?> ReadRowAsync(
        DbDataReader reader,
        DocumentTableInfo table,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var json = reader.GetString(reader.GetOrdinal(DocumentTableInfo.DataColumn));

        var token = DocumentConcurrencyToken.None;
        var versionColumn = table.MetadataColumnName(DocumentMetadataColumn.Version)
            ?? table.MetadataColumnName(DocumentMetadataColumn.Revision);

        if (versionColumn is not null)
        {
            var ordinal = reader.GetOrdinal(versionColumn);
            if (!await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
            {
                token = DocumentConcurrencyToken.FromColumnValue(reader.GetValue(ordinal));
            }
        }

        var deleted = await ReadOptionalBooleanAsync(
            reader, table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted), cancellationToken).ConfigureAwait(false);

        var docType = await ReadOptionalStringAsync(
            reader, table.MetadataColumnName(DocumentMetadataColumn.DocumentType), cancellationToken).ConfigureAwait(false);

        return new DocumentRow(json, token, deleted, docType);
    }

    private static async Task<string?> ReadOptionalStringAsync(
        DbDataReader reader,
        string? columnName,
        CancellationToken cancellationToken)
    {
        if (columnName is null)
        {
            return null;
        }

        var ordinal = reader.GetOrdinal(columnName);

        return await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
            ? null
            : reader.GetString(ordinal);
    }

    private static async Task<bool> ReadOptionalBooleanAsync(
        DbDataReader reader,
        string? columnName,
        CancellationToken cancellationToken)
    {
        if (columnName is null)
        {
            return false;
        }

        var ordinal = reader.GetOrdinal(columnName);

        return !await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false) && reader.GetBoolean(ordinal);
    }

    /// <summary>Whether this table has a version column for a save to be checked against.</summary>
    private static bool HasConcurrencyColumn(DocumentTableInfo table) =>
        table.HasMetadata(DocumentMetadataColumn.Version) || table.HasMetadata(DocumentMetadataColumn.Revision);

    /// <summary>
    /// Queues the write, letting Marten's own concurrency check run where the mapping asked for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not belt and braces, it is necessary. A mapping with <c>UseOptimisticConcurrency</c>
    /// generates <c>… on conflict (id) do update set … where table.mt_version = ?</c>, and a session
    /// that never loaded the document binds <c>null</c> into that slot — the update matches nothing, no
    /// row comes back, and Marten raises <c>ConcurrencyException</c>. So a plain <c>StoreObjects</c>
    /// would fail <em>every</em> save of such a type. Handing the expected version to
    /// <c>UpdateExpectedVersion</c> is what makes the write possible, and it makes the database check
    /// the same thing the studio just checked.
    /// </para>
    /// <para>
    /// Both APIs are generic in the document type and the studio only has a runtime <see cref="Type" />,
    /// so the call goes through a per-type compiled delegate (<see cref="TypedSessionWrites" />). The
    /// type they are made generic over is <paramref name="documentType" /> — the row's concrete type, not
    /// the alias's root — because that is what decides which storage Marten uses and therefore what
    /// <c>mt_doc_type</c> and <c>mt_dotnet_type</c> end up saying.
    /// </para>
    /// </remarks>
    private static void QueueStore(
        IDocumentSession session,
        WriteTarget target,
        Type documentType,
        object document,
        DocumentConcurrencyToken token)
    {
        if (target.UsesOptimisticConcurrency && token.Version is { } version)
        {
            TypedSessionWrites.UpdateExpectedVersion(session, documentType, document, version);
            return;
        }

        if (target.UsesNumericRevisions && token.Revision is { } revision)
        {
            // UpdateRevision takes the *new* revision and refuses if the database is already at or past
            // it, so the next one up is both the check and the increment.
            TypedSessionWrites.UpdateRevision(session, documentType, document, revision + 1);
            return;
        }

        // StoreObjects groups by the instance's own GetType(), which is the concrete subclass because
        // that is what the JSON was deserialized as.
        session.StoreObjects([document]);
    }

    /// <summary>
    /// The delete of last resort: read the document, deserialize it, and hand the instance to
    /// <c>DeleteObjects</c>.
    /// </summary>
    /// <remarks>
    /// <c>DeleteObjects</c> survives for exactly one case — a document type whose id member the studio
    /// cannot build a value for, which in Marten 9.35 means an F# discriminated-union id or a value
    /// object that does not match <c>ValueTypeIdGeneration</c>'s shape. Everything else deletes by id and
    /// never reads the document at all. Returns the refusal, or <see langword="null" /> when the delete
    /// was queued.
    /// </remarks>
    private async Task<string?> TryQueueDeleteByDocumentAsync(
        IDocumentSession session,
        ResolvedScope resolved,
        WriteTarget writeTarget,
        Type documentType,
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false);
        var load = await LoadAsync(connection, writeTarget, id, resolved.TenantId, cancellationToken).ConfigureAwait(false);

        if (load.Error is not null)
        {
            return load.Error;
        }

        if (load.Row is null)
        {
            return "The document was deleted while this was being read; nothing was changed.";
        }

        if (!TryDeserialize(
                resolved.Store.Options.Serializer(), documentType, load.Row.Json, out var document, out var error, out _))
        {
            return error;
        }

        session.DeleteObjects([document]);
        return null;
    }

    private static bool IsConcurrencyFailure(Exception exception) => exception switch
    {
        ConcurrencyException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(static x => x is ConcurrencyException),
        _ => false,
    };

    /// <summary>
    /// Whether a failure is "somebody else is holding this row": the <c>lock_timeout</c> expiring
    /// (<c>55P03</c>) or the statement being cut short (<c>57014</c>).
    /// </summary>
    /// <remarks>
    /// <c>57014</c> is also what a genuinely cancelled statement looks like, so a request the caller
    /// cancelled is deliberately not folded into this: that one has to keep propagating as a
    /// cancellation. Marten wraps its command failures, so the whole inner chain is walked.
    /// </remarks>
    private static bool IsRowUnavailable(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return HasLockSqlState(exception);
    }

    private static bool HasLockSqlState(Exception? exception) => exception switch
    {
        null => false,
        PostgresException postgres => postgres.SqlState is LockNotAvailableSqlState or QueryCanceledSqlState,
        AggregateException aggregate => aggregate.InnerExceptions.Any(HasLockSqlState),
        _ => HasLockSqlState(exception.InnerException),
    };

    private static bool TryParseEdited(string editedJson, out string reason)
    {
        try
        {
            using var document = JsonDocument.Parse(editedJson, EditedJsonOptions);
            reason = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            reason = "That is not valid JSON" + Where(ex) + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Deserializes through the store's own serializer, and turns every way that can go wrong into a
    /// message instead of an exception.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An abstract type, a type with no usable constructor, a converter that throws — all of them are
    /// ordinary answers about a document somebody is trying to edit, and all of them have to reach the
    /// screen as text. <c>FromJson(Type, Stream)</c> is the only non-generic entry point Marten's
    /// serializer has (there is no string overload), so the edit goes in as UTF-8 over a
    /// <see cref="MemoryStream" />.
    /// </para>
    /// <para>
    /// Two messages come back, and the difference matters. <paramref name="reason" /> carries the
    /// exception's own words and goes on screen, where a person needs them and where Blazor escapes
    /// them. <paramref name="auditReason" /> names the exception type and the JSON path instead and is
    /// what the audit ring and the application log get: a converter's message can be arbitrarily long
    /// and can quote a fragment of somebody's document into a log file.
    /// </para>
    /// </remarks>
    private static bool TryDeserialize(
        ISerializer serializer,
        Type documentType,
        string json,
        out object document,
        out string reason,
        out string auditReason)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json), writable: false);
            var result = serializer.FromJson(documentType, stream);

            if (result is null)
            {
                document = null!;
                reason = $"The edit deserialized to nothing at all as '{documentType.Name}'.";
                auditReason = reason;
                return false;
            }

            document = result;
            reason = string.Empty;
            auditReason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            document = null!;
            reason = $"'{documentType.Name}' could not be built from this JSON{Where(ex)}: {ex.Message}";
            auditReason = $"'{documentType.Name}' could not be built from this JSON{Where(ex)} ({ex.GetType().Name}).";
            return false;
        }
    }

    /// <summary>Where in the document a JSON failure happened, when the exception knows.</summary>
    private static string Where(Exception exception)
    {
        if (exception is not JsonException json)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        if (!string.IsNullOrEmpty(json.Path))
        {
            builder.Append(" at ").Append(json.Path);
        }

        if (json.LineNumber.HasValue)
        {
            builder.Append(builder.Length == 0 ? " at " : ", ")
                .Append("line ")
                .Append(json.LineNumber.Value.ToString(CultureInfo.InvariantCulture));

            if (json.BytePositionInLine.HasValue)
            {
                builder.Append(", column ")
                    .Append(json.BytePositionInLine.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Who the circuit belongs to, for event 9211.
    /// </summary>
    /// <remarks>
    /// Read from the already-completed task rather than awaited, for the same reason
    /// <see cref="StudioActionLog" /> does it: a circuit's authentication state is settled long before
    /// anybody can click save, and blocking on that task here is the one way this could go wrong.
    /// </remarks>
    private string UserName()
    {
        var state = authenticationStateProvider.GetAuthenticationStateAsync();

        if (!state.IsCompletedSuccessfully)
        {
            return "(unknown)";
        }

        var name = state.Result.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
    }

    /// <summary>
    /// Turns a value out of an <c>id</c> column into the CLR type the document's id member actually has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The studio reads ids as the <em>column</em> says they are — <see cref="Guid" />, <see cref="int" />,
    /// <see cref="long" />, <see cref="string" /> — because that is what a parameter has to bind as. Every
    /// generic write call needs the other thing: the type the document's own id member is declared as,
    /// which for a strong-typed id is a wrapper around one of those four.
    /// </para>
    /// <para>
    /// The shape it looks for is exactly the one <c>Marten.Schema.Identity.ValueTypeIdGeneration</c>
    /// accepts (verified against Marten 9.35): a public struct with exactly one property of a valid id
    /// type, and either a one-argument constructor taking that type or a public static factory returning
    /// the struct from it. A type that does not match — an F# discriminated union is the real example —
    /// comes back as <see langword="false" />, and the caller falls back to a path that does not need an
    /// id value.
    /// </para>
    /// </remarks>
    private static class TypedIds
    {
        /// <summary>The four things a Marten id column can hold, which is what a wrapper must wrap.</summary>
        private static readonly Type[] InnerIdTypes = [typeof(Guid), typeof(string), typeof(int), typeof(long)];

        private static readonly ConcurrentDictionary<Type, Wrapper?> Wrappers = new();

        /// <summary>A value-object id: the type inside it, and how to build one.</summary>
        private sealed record Wrapper(Type InnerType, Func<object, object> Build);

        public static bool TryConvert(Type? idMemberType, object columnValue, out object typed)
        {
            ArgumentNullException.ThrowIfNull(columnValue);

            if (idMemberType is null)
            {
                typed = null!;
                return false;
            }

            if (TryCoerce(idMemberType, columnValue, out typed))
            {
                return true;
            }

            var wrapper = Wrappers.GetOrAdd(Nullable.GetUnderlyingType(idMemberType) ?? idMemberType, FindWrapper);

            if (wrapper is null || !TryCoerce(wrapper.InnerType, columnValue, out var inner))
            {
                typed = null!;
                return false;
            }

            try
            {
                typed = wrapper.Build(inner);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                typed = null!;
                return false;
            }
        }

        /// <summary>
        /// The column value as <paramref name="wanted" />, when the two are the same thing in different
        /// widths. An <c>int4</c> column under a <c>long</c> id member is the case this exists for.
        /// </summary>
        private static bool TryCoerce(Type wanted, object columnValue, out object result)
        {
            var target = Nullable.GetUnderlyingType(wanted) ?? wanted;

            if (target.IsInstanceOfType(columnValue))
            {
                result = columnValue;
                return true;
            }

            if (columnValue is IConvertible && (target == typeof(int) || target == typeof(long) || target == typeof(string)))
            {
                try
                {
                    result = Convert.ChangeType(columnValue, target, CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    result = null!;
                    return false;
                }
            }

            result = null!;
            return false;
        }

        private static Wrapper? FindWrapper(Type idType)
        {
            // Marten only recognises a struct here; a class id is an F# union and has its own generator.
            if (!idType.IsValueType || idType.IsPrimitive || idType.IsEnum)
            {
                return null;
            }

            var candidates = idType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static x => Array.IndexOf(InnerIdTypes, x.PropertyType) >= 0)
                .ToArray();

            if (candidates.Length != 1)
            {
                return null;
            }

            var inner = candidates[0].PropertyType;

            var constructor = idType.GetConstructors()
                .FirstOrDefault(x => x.GetParameters() is [{ } parameter] && parameter.ParameterType == inner);

            if (constructor is not null)
            {
                return new Wrapper(inner, Compile(inner, value => Expression.New(constructor, value)));
            }

            var factory = idType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => x.ReturnType == idType &&
                    x.GetParameters() is [{ } parameter] &&
                    parameter.ParameterType == inner);

            return factory is null
                ? null
                : new Wrapper(inner, Compile(inner, value => Expression.Call(factory, value)));
        }

        private static Func<object, object> Compile(Type innerType, Func<Expression, Expression> build)
        {
            var parameter = Expression.Parameter(typeof(object), "value");
            var body = build(Expression.Convert(parameter, innerType));

            return Expression.Lambda<Func<object, object>>(Expression.Convert(body, typeof(object)), parameter).Compile();
        }
    }

    /// <summary>
    /// Marten's generic write calls, reachable from a runtime <see cref="Type" />.
    /// </summary>
    /// <remarks>
    /// One compiled delegate per document type, built once and cached. A compiled expression rather than
    /// <c>MethodInfo.Invoke</c> for one reason that matters beyond speed: <c>Invoke</c> wraps whatever
    /// the call throws in a <see cref="TargetInvocationException" />, and the message a person needs to
    /// read would then be two levels down inside it.
    /// </remarks>
    private static class TypedSessionWrites
    {
        private static readonly ConcurrentDictionary<Type, Action<IDocumentSession, object, Guid>> ExpectedVersionCalls = new();
        private static readonly ConcurrentDictionary<Type, Action<IDocumentSession, object, long>> RevisionCalls = new();
        private static readonly ConcurrentDictionary<Type, Action<IDocumentSession, object>> DeleteCalls = new();
        private static readonly ConcurrentDictionary<Type, Action<IDocumentSession, LambdaExpression>> UndoDeleteCalls = new();

        public static void UpdateExpectedVersion(IDocumentSession session, Type documentType, object document, Guid version) =>
            ExpectedVersionCalls.GetOrAdd(documentType, BuildExpectedVersionCall)(session, document, version);

        public static void UpdateRevision(IDocumentSession session, Type documentType, object document, long revision) =>
            RevisionCalls.GetOrAdd(documentType, BuildRevisionCall)(session, document, revision);

        /// <summary>
        /// <c>Delete&lt;T&gt;(object id)</c>: Marten's own untyped-id entry point, which resolves the
        /// storage from the runtime type of the id it is handed — so a strong-typed id works by being
        /// passed as itself, and the document never has to be read.
        /// </summary>
        public static void DeleteById(IDocumentSession session, Type documentType, object id) =>
            DeleteCalls.GetOrAdd(documentType, BuildDeleteCall)(session, id);

        /// <summary>
        /// Queues <c>UndoDeleteWhere&lt;T&gt;(x =&gt; x.Id == id)</c>, which is an
        /// <c>update … set mt_deleted = false, mt_deleted_at = null where …</c> and never touches
        /// <c>data</c>.
        /// </summary>
        /// <remarks>
        /// Returns <see langword="false" /> rather than throwing when the predicate cannot be built or
        /// Marten's LINQ parser will not take it — a type whose id member has no <c>==</c> at all, for
        /// instance. The caller then falls back to the round-trip undelete, which is lossy and says so.
        /// Marten parses the expression inside <c>UndoDeleteWhere</c> itself, so a parser refusal shows
        /// up here and not at <c>SaveChangesAsync</c>.
        /// </remarks>
        public static bool TryQueueUndoDelete(
            IDocumentSession session,
            Type documentType,
            MemberInfo? idMember,
            object id)
        {
            if (idMember is null)
            {
                return false;
            }

            try
            {
                var parameter = Expression.Parameter(documentType, "x");
                var member = Expression.MakeMemberAccess(parameter, idMember);

                // The id travels as a field on a constant holder rather than as a ConstantExpression of
                // its own: that is exactly the shape the C# compiler gives `x => x.Id == local`, and it is
                // the shape Marten's where-clause parser is written against.
                var holderType = typeof(IdHolder<>).MakeGenericType(member.Type);
                var holder = Activator.CreateInstance(holderType, id);
                var value = Expression.Field(
                    Expression.Constant(holder, holderType),
                    holderType.GetField(nameof(IdHolder<object>.Value))!);

                var predicate = Expression.Lambda(
                    typeof(Func<,>).MakeGenericType(documentType, typeof(bool)),
                    Expression.Equal(member, value),
                    parameter);

                UndoDeleteCalls.GetOrAdd(documentType, BuildUndoDeleteCall)(session, predicate);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>The closure a built id predicate captures its value in.</summary>
        /// <typeparam name="T">The id member's own type.</typeparam>
        private sealed class IdHolder<T>(T value)
        {
            /// <summary>The id, as the member's type.</summary>
            public readonly T Value = value;
        }

        private static Action<IDocumentSession, object, Guid> BuildExpectedVersionCall(Type documentType) =>
            Build<Guid>(nameof(IDocumentOperations.UpdateExpectedVersion), documentType);

        private static Action<IDocumentSession, object, long> BuildRevisionCall(Type documentType) =>
            Build<long>(nameof(IDocumentOperations.UpdateRevision), documentType);

        private static Action<IDocumentSession, object, TValue> Build<TValue>(string methodName, Type documentType)
        {
            var method = typeof(IDocumentOperations)
                .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)!
                .MakeGenericMethod(documentType);

            var session = Expression.Parameter(typeof(IDocumentSession), "session");
            var document = Expression.Parameter(typeof(object), "document");
            var value = Expression.Parameter(typeof(TValue), "value");

            var call = Expression.Call(session, method, Expression.Convert(document, documentType), value);

            return Expression.Lambda<Action<IDocumentSession, object, TValue>>(call, session, document, value).Compile();
        }

        private static Action<IDocumentSession, object> BuildDeleteCall(Type documentType)
        {
            // Delete<T>(object id), not Delete<T>(T entity): the one whose single parameter really is
            // System.Object rather than the method's own type parameter.
            var method = typeof(IDocumentOperations)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(x => x.Name == nameof(IDocumentOperations.Delete) &&
                    x.IsGenericMethodDefinition &&
                    x.GetParameters() is [{ } parameter] &&
                    parameter.ParameterType == typeof(object))
                .MakeGenericMethod(documentType);

            var session = Expression.Parameter(typeof(IDocumentSession), "session");
            var id = Expression.Parameter(typeof(object), "id");

            return Expression.Lambda<Action<IDocumentSession, object>>(
                Expression.Call(session, method, id), session, id).Compile();
        }

        private static Action<IDocumentSession, LambdaExpression> BuildUndoDeleteCall(Type documentType)
        {
            var method = typeof(IDocumentOperations)
                .GetMethod(nameof(IDocumentOperations.UndoDeleteWhere), BindingFlags.Public | BindingFlags.Instance)!
                .MakeGenericMethod(documentType);

            var session = Expression.Parameter(typeof(IDocumentSession), "session");
            var predicate = Expression.Parameter(typeof(LambdaExpression), "predicate");

            var expressionType = typeof(Expression<>).MakeGenericType(
                typeof(Func<,>).MakeGenericType(documentType, typeof(bool)));

            return Expression.Lambda<Action<IDocumentSession, LambdaExpression>>(
                Expression.Call(session, method, Expression.Convert(predicate, expressionType)),
                session,
                predicate).Compile();
        }
    }
}
