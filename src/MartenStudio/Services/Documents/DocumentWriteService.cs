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
/// to it. The write itself is <c>StoreObjects</c>/<c>DeleteObjects</c> on a real session, so upsert
/// semantics, metadata columns, soft-delete style and tenancy stay exactly as Marten defines them.
/// <c>mt_upsert_*</c> is never called and no <c>insert</c> is ever written here.
/// </para>
/// <para>
/// <b>The concurrency check is inside the write transaction.</b> The version read for the preview is
/// minutes old by the time somebody clicks save, so it is read again — with <c>for update</c>, on the
/// session's own connection, inside the session's own transaction — and compared there. Read committed
/// alone would let another session commit between the check and the upsert; the row lock is what closes
/// that window. For a type that also uses Marten's own optimistic concurrency the expected version is
/// handed to Marten as well, so the database's <c>where mt_version = ?</c> guard runs too and the two
/// checks have to agree.
/// </para>
/// <para>
/// <b>Nothing is saved silently lossy</b> (D7). Every save runs the round trip first and refuses when it
/// would drop properties unless the caller says it has seen them, and a save that drops properties
/// anyway is logged as event 9211 with the paths.
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
                    _ => BuildPreview(
                        alias,
                        id,
                        writeTarget.ClrType,
                        resolved.Store.Options.Serializer(),
                        load.Row!.Json,
                        editedJson,
                        load.Row.Token,
                        HasConcurrencyColumn(writeTarget.Table)).Preview,
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(PreviewAction, target, succeeded: false, ex.Message, StudioCapability.EditDocuments, scope);
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
            audit.Record(SaveAction, target, succeeded: false, ex.Message, StudioCapability.EditDocuments, scope);
            throw;
        }

        audit.Record(
            SaveAction,
            target,
            result.IsSaved,
            result.IsSaved ? result.Preview?.Summary() ?? "Saved." : result.Reason,
            StudioCapability.EditDocuments,
            scope);

        // 9211: the save happened and it lost something. The dialog showed the paths before the click;
        // this is the record that somebody accepted them, and it is the only place the list survives.
        if (result.IsSaved && result.Preview is { } preview && preview.HasDrops)
        {
            logger.DocumentWriteRoundTripDropped(
                UserName(),
                preview.DocumentTypeName ?? alias,
                id,
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
            audit.Record(DeleteAction, target, succeeded: false, ex.Message, StudioCapability.DeleteDocuments, scope);
            throw;
        }

        audit.Record(DeleteAction, target, result.Changed, result.Describe(), StudioCapability.DeleteDocuments, scope);
        return result;
    }

    /// <inheritdoc />
    public async Task<DeleteResult> UndeleteAsync(
        StudioScope scope,
        string alias,
        string id,
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
            result = await UndeleteCoreAsync(resolved, alias, id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(UndeleteAction, target, succeeded: false, ex.Message, StudioCapability.DeleteDocuments, scope);
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

        var target = string.Create(CultureInfo.InvariantCulture, $"{alias} ({ids.Count} ids)");
        var resolved = await AuthorizeAsync(scope, StudioCapability.DeleteDocuments, BulkDeleteAction, target, cancellationToken)
            .ConfigureAwait(false);

        BulkDeleteResult result;

        try
        {
            result = await BulkDeleteCoreAsync(resolved, alias, ids, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.Record(BulkDeleteAction, target, succeeded: false, ex.Message, StudioCapability.DeleteDocuments, scope);
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
    /// <param name="documentType">The CLR type Marten would store this document as.</param>
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
                    currentToken: currentToken),
                null);
        }

        if (!TryDeserialize(serializer, documentType, editedJson, out var document, out var typeError))
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
                    currentToken: currentToken),
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
                    currentToken: currentToken),
                null);
        }

        // Edited against round-tripped is the loss; stored against edited is the change. Two questions,
        // one differ, because they are the same operation with different arguments.
        var roundTripDiff = RoundTripDiffer.Diff(editedJson, roundTrippedJson);
        var editDiff = RoundTripDiffer.Diff(storedJson, editedJson);

        var caveat = hasConcurrencyColumn
            ? null
            : "This collection has no version column, so the save cannot be checked against a " +
              "concurrent change. Saving will overwrite whatever is there.";

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

            var built = BuildPreview(
                alias,
                id,
                writeTarget.ClrType,
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
        }

        if (preview.HasDrops && !acknowledgeDrops)
        {
            return WriteResult.Refused(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Saving would drop {preview.DroppedPaths.Length} propert{(preview.DroppedPaths.Length == 1 ? "y" : "ies")} that '{writeTarget.TypeName}' has no member for: {preview.DroppedPathsSummary()}. Nothing was saved."),
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

        await using (command)
        {
            await using var reader = await session.ExecuteReaderAsync(command, cancellationToken).ConfigureAwait(false);
            current = await ReadRowAsync(reader, writeTarget.Table, cancellationToken).ConfigureAwait(false);
        }

        if (current is null)
        {
            return WriteResult.Refused(
                "The document was deleted while it was open, so there was nothing to save over.",
                preview);
        }

        if (guarded && current.Token != expectedToken)
        {
            return WriteResult.Conflict(current.Token, preview);
        }

        QueueStore(session, writeTarget, document, expectedToken.IsKnown ? expectedToken : current.Token);

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
        object document;

        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            writeTarget = await ReconcileAsync(described.Target, connection, cancellationToken).ConfigureAwait(false);

            var load = await LoadAsync(connection, writeTarget, id, resolved.TenantId, cancellationToken).ConfigureAwait(false);
            if (load.Error is not null)
            {
                return DeleteResult.Refused(alias, id, load.Error);
            }

            if (load.Row is null)
            {
                return DeleteResult.NotFound(alias, id);
            }

            if (!TryDeserialize(
                    resolved.Store.Options.Serializer(),
                    writeTarget.ClrType,
                    load.Row.Json,
                    out var deserialized,
                    out var error))
            {
                return DeleteResult.Refused(alias, id, error);
            }

            document = deserialized;
        }

        await using var session = OpenSession(resolved);

        // DeleteObjects, never a delete statement of our own: it is what knows that this type is
        // soft-deleted and that that type is not, and it is what keeps tenancy right.
        session.DeleteObjects([document]);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return writeTarget.IsSoftDeleted
            ? DeleteResult.SoftDeleted(alias, id)
            : DeleteResult.HardDeleted(alias, id);
    }

    private async Task<DeleteResult> UndeleteCoreAsync(
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

        if (!described.Target.IsSoftDeleted)
        {
            return DeleteResult.Refused(
                alias,
                id,
                $"'{alias}' is not soft-deleted, so a deleted document of this type is gone and there is " +
                "nothing to bring back.");
        }

        WriteTarget writeTarget;
        object document;
        DocumentConcurrencyToken token;

        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            writeTarget = await ReconcileAsync(described.Target, connection, cancellationToken).ConfigureAwait(false);

            // The single read carries no soft-delete predicate, so the deleted row is right there.
            var load = await LoadAsync(connection, writeTarget, id, resolved.TenantId, cancellationToken).ConfigureAwait(false);
            if (load.Error is not null)
            {
                return DeleteResult.Refused(alias, id, load.Error);
            }

            if (load.Row is null)
            {
                return DeleteResult.NotFound(alias, id);
            }

            if (!load.Row.IsDeleted)
            {
                return DeleteResult.Undeleted(alias, id, "The document was not deleted; nothing was changed.");
            }

            if (!TryDeserialize(
                    resolved.Store.Options.Serializer(),
                    writeTarget.ClrType,
                    load.Row.Json,
                    out var deserialized,
                    out var error))
            {
                return DeleteResult.Refused(alias, id, error);
            }

            document = deserialized;
            token = load.Row.Token;
        }

        await using (var session = OpenSession(resolved))
        {
            // Marten's upsert writes mt_deleted = false on every store, so storing the document back is
            // the undelete (verified against Weasel.Storage.DocumentSoftDeletedBinder, Marten 9.35).
            QueueStore(session, writeTarget, document, token);
            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            var after = await LoadAsync(connection, writeTarget, id, resolved.TenantId, cancellationToken).ConfigureAwait(false);

            if (after.Row is null || after.Row.IsDeleted)
            {
                return DeleteResult.Refused(
                    alias,
                    id,
                    "The document is still marked deleted after the write. Nothing else was changed.");
            }
        }

        return DeleteResult.Undeleted(alias, id);
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
        var outcomes = new DeleteResult[ids.Count];
        List<object> documents = [];
        List<int> queued = [];

        await using (var connection = await OpenReadConnectionAsync(resolved, cancellationToken).ConfigureAwait(false))
        {
            writeTarget = await ReconcileAsync(described.Target, connection, cancellationToken).ConfigureAwait(false);
            var serializer = resolved.Store.Options.Serializer();

            for (var i = 0; i < ids.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var id = ids[i];
                var load = await LoadAsync(connection, writeTarget, id, resolved.TenantId, cancellationToken).ConfigureAwait(false);

                if (load.Error is not null)
                {
                    outcomes[i] = DeleteResult.Refused(alias, id, load.Error);
                    continue;
                }

                if (load.Row is null)
                {
                    outcomes[i] = DeleteResult.NotFound(alias, id);
                    continue;
                }

                if (!TryDeserialize(serializer, writeTarget.ClrType, load.Row.Json, out var document, out var error))
                {
                    outcomes[i] = DeleteResult.Refused(alias, id, error);
                    continue;
                }

                documents.Add(document);
                queued.Add(i);
            }
        }

        if (documents.Count > 0)
        {
            // One session, one SaveChangesAsync: the whole selection is one transaction, so a failure
            // halfway leaves the database as it was rather than half-deleted.
            await using var session = OpenSession(resolved);
            session.DeleteObjects(documents);
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
            audit.Record(action, target, succeeded: false, ex.Message, capability, scope);
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

    private static string Target(string alias, string id) => alias + "/" + id;

    private int CommandTimeoutSeconds => (int) Math.Ceiling(options.Value.QueryTimeout.TotalSeconds);

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
    /// What the studio knows about the collection it is about to write to: Marten's mapping, and the
    /// table as it actually exists.
    /// </summary>
    private sealed record WriteTarget(IDocumentType DocumentType, DocumentMapping? Mapping, DocumentTableInfo Table)
    {
        public Type ClrType => DocumentType.DocumentType;

        public string TypeName => DocumentType.DocumentType.Name;

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

    /// <summary>One document row, as much of it as a write cares about.</summary>
    private sealed record DocumentRow(string Json, DocumentConcurrencyToken Token, bool IsDeleted);

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

        var deleted = false;
        var deletedColumn = table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted);

        if (deletedColumn is not null)
        {
            var ordinal = reader.GetOrdinal(deletedColumn);
            deleted = !await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
                && reader.GetBoolean(ordinal);
        }

        return new DocumentRow(json, token, deleted);
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
    /// so the call goes through a per-type compiled delegate (<see cref="TypedSessionWrites" />). That
    /// is the one place in this codebase where reflection stands in for a generic call, and it is here
    /// because Marten offers no non-generic overload of either.
    /// </para>
    /// </remarks>
    private static void QueueStore(
        IDocumentSession session,
        WriteTarget target,
        object document,
        DocumentConcurrencyToken token)
    {
        if (target.UsesOptimisticConcurrency && token.Version is { } version)
        {
            TypedSessionWrites.UpdateExpectedVersion(session, target.ClrType, document, version);
            return;
        }

        if (target.UsesNumericRevisions && token.Revision is { } revision)
        {
            // UpdateRevision takes the *new* revision and refuses if the database is already at or past
            // it, so the next one up is both the check and the increment.
            TypedSessionWrites.UpdateRevision(session, target.ClrType, document, revision + 1);
            return;
        }

        session.StoreObjects([document]);
    }

    private static bool IsConcurrencyFailure(Exception exception) => exception switch
    {
        ConcurrencyException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(static x => x is ConcurrencyException),
        _ => false,
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
    /// An abstract type, a type with no usable constructor, a converter that throws — all of them are
    /// ordinary answers about a document somebody is trying to edit, and all of them have to reach the
    /// screen as text. <c>FromJson(Type, Stream)</c> is the only non-generic entry point Marten's
    /// serializer has (there is no string overload), so the edit goes in as UTF-8 over a
    /// <see cref="MemoryStream" />.
    /// </remarks>
    private static bool TryDeserialize(
        ISerializer serializer,
        Type documentType,
        string json,
        out object document,
        out string reason)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json), writable: false);
            var result = serializer.FromJson(documentType, stream);

            if (result is null)
            {
                document = null!;
                reason = $"The edit deserialized to nothing at all as '{documentType.Name}'.";
                return false;
            }

            document = result;
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            document = null!;
            reason = $"'{documentType.Name}' could not be built from this JSON{Where(ex)}: {ex.Message}";
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
    /// Marten's two generic concurrency-aware write calls, reachable from a runtime <see cref="Type" />.
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

        public static void UpdateExpectedVersion(IDocumentSession session, Type documentType, object document, Guid version) =>
            ExpectedVersionCalls.GetOrAdd(documentType, BuildExpectedVersionCall)(session, document, version);

        public static void UpdateRevision(IDocumentSession session, Type documentType, object document, long revision) =>
            RevisionCalls.GetOrAdd(documentType, BuildRevisionCall)(session, document, revision);

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
    }
}
