using System.Collections.Immutable;
using System.Globalization;

namespace MartenStudio.Services.Documents;

/// <summary>How a save ended.</summary>
internal enum WriteStatus
{
    /// <summary>The document was written and the transaction committed.</summary>
    Saved,

    /// <summary>
    /// Somebody else changed the row between the edit being opened and the save being attempted, so
    /// nothing was written.
    /// </summary>
    Conflict,

    /// <summary>
    /// The save never got as far as the database: bad JSON, an unconstructible type, unacknowledged
    /// data loss, a missing document, or a scope that cannot address the row.
    /// </summary>
    Refused,
}

/// <summary>
/// What <see cref="IDocumentWriteService.SaveAsync" /> did.
/// </summary>
/// <remarks>
/// <para>
/// A conflict is a value rather than an exception because it is an ordinary thing: two people had the
/// same document open. The screen has to be able to show the version that is there now and offer to
/// reload, which it cannot do from a stack trace.
/// </para>
/// <para>
/// <see cref="CurrentToken" /> means different things by status, and both are useful: after
/// <see cref="WriteStatus.Saved" /> it is the version the row carries <em>now</em>, so an editor that
/// stays open can save again; after <see cref="WriteStatus.Conflict" /> it is the version somebody else
/// left there.
/// </para>
/// </remarks>
/// <param name="Status">How it ended.</param>
/// <param name="CurrentToken">The row's version after the attempt, when one was read.</param>
/// <param name="Reason">Why it was refused, or what the conflict was.</param>
/// <param name="Preview">The preview the save was decided from, when one was built.</param>
internal sealed record WriteResult(
    WriteStatus Status,
    DocumentConcurrencyToken CurrentToken,
    string? Reason,
    WritePreview? Preview)
{
    /// <summary>Whether the document was written.</summary>
    public bool IsSaved => Status == WriteStatus.Saved;

    /// <summary>
    /// What the audit records instead of <see cref="Reason" />, when the two have to differ.
    /// </summary>
    /// <remarks>
    /// <see cref="WritePreview.AuditReason" />, for the same reason: a serializer's exception message
    /// belongs on the screen and not in a log line.
    /// </remarks>
    public string? AuditReason { get; init; }

    /// <summary>The outcome as one sanitised line, which is what the audit entry carries.</summary>
    public string AuditMessage() =>
        WriteAuditText.Sanitize(AuditReason ?? Reason ?? Status.ToString());

    /// <summary>The document was written; <paramref name="currentToken" /> is what the row carries now.</summary>
    public static WriteResult Saved(DocumentConcurrencyToken currentToken, WritePreview preview) =>
        new(WriteStatus.Saved, currentToken, null, preview);

    /// <summary>The row moved under the edit. Nothing was written.</summary>
    public static WriteResult Conflict(DocumentConcurrencyToken currentToken, WritePreview? preview = null) =>
        new(
            WriteStatus.Conflict,
            currentToken,
            "The document changed while it was open, so nothing was saved. It is now at version " +
            currentToken.Describe() + ". Reload it and apply the edit again.",
            preview);

    /// <summary>The save was refused before any write.</summary>
    /// <param name="reason">Why, phrased for whoever is looking at the editor.</param>
    /// <param name="preview">The preview the refusal was decided from, when there was one.</param>
    /// <param name="auditReason">What the audit records instead, when it has to differ.</param>
    public static WriteResult Refused(string reason, WritePreview? preview = null, string? auditReason = null) =>
        new(WriteStatus.Refused, preview?.CurrentToken ?? DocumentConcurrencyToken.None, reason, preview)
        {
            AuditReason = auditReason ?? preview?.AuditReason,
        };
}

/// <summary>What a delete, or an undelete, did to one document.</summary>
internal enum DeleteOutcome
{
    /// <summary>The row is gone.</summary>
    HardDeleted,

    /// <summary>The row is still there with <c>mt_deleted</c> set, because the type is soft-deleted.</summary>
    SoftDeleted,

    /// <summary>A soft-deleted row was made visible again.</summary>
    Undeleted,

    /// <summary>There was no such row to begin with.</summary>
    NotFound,

    /// <summary>
    /// The operation does not apply to this document — undeleting a type that is hard-deleted, or a
    /// conjoined collection reached without a tenant. Nothing was written.
    /// </summary>
    Refused,
}

/// <summary>
/// What <see cref="IDocumentWriteService.DeleteAsync" /> or
/// <see cref="IDocumentWriteService.UndeleteAsync(StudioScope, string, string, CancellationToken)" /> did
/// to one document.
/// </summary>
/// <remarks>
/// Hard and soft are reported apart rather than folded into "deleted" because they are different
/// promises: one is recoverable from the studio and one is recoverable only from a backup, and a UI
/// that says the same word for both is a UI that gets somebody to click the wrong button.
/// </remarks>
/// <param name="Outcome">What happened.</param>
/// <param name="Alias">The collection.</param>
/// <param name="Id">The document id, as it was given.</param>
/// <param name="Reason">Why it was refused, or a note about an outcome that needs one.</param>
internal sealed record DeleteResult(DeleteOutcome Outcome, string Alias, string Id, string? Reason = null)
{
    /// <summary>Whether the database actually changed.</summary>
    public bool Changed => Outcome is DeleteOutcome.HardDeleted or DeleteOutcome.SoftDeleted or DeleteOutcome.Undeleted;

    /// <summary>What the audit records instead of <see cref="Reason" />, when the two have to differ.</summary>
    public string? AuditReason { get; init; }

    /// <summary>The row is gone.</summary>
    public static DeleteResult HardDeleted(string alias, string id) => new(DeleteOutcome.HardDeleted, alias, id);

    /// <summary>The row is marked deleted.</summary>
    public static DeleteResult SoftDeleted(string alias, string id) => new(DeleteOutcome.SoftDeleted, alias, id);

    /// <summary>The row is visible again.</summary>
    public static DeleteResult Undeleted(string alias, string id, string? reason = null) =>
        new(DeleteOutcome.Undeleted, alias, id, reason);

    /// <summary>There was nothing there.</summary>
    public static DeleteResult NotFound(string alias, string id) =>
        new(DeleteOutcome.NotFound, alias, id, $"No document with id '{id}' is in '{alias}'.");

    /// <summary>Nothing was written, and this is why.</summary>
    /// <param name="alias">The collection.</param>
    /// <param name="id">The document id, as it was given.</param>
    /// <param name="reason">Why, phrased for whoever asked.</param>
    /// <param name="auditReason">What the audit records instead, when it has to differ.</param>
    public static DeleteResult Refused(string alias, string id, string reason, string? auditReason = null) =>
        new(DeleteOutcome.Refused, alias, id, reason) { AuditReason = auditReason };

    /// <summary>
    /// How the outcome reads in an audit entry.
    /// </summary>
    /// <remarks>
    /// Sanitised: the alias and the id are both interpolated into <see cref="Reason" /> and both arrive
    /// from a URL (see <see cref="WriteAuditText" />).
    /// </remarks>
    public string Describe() => (AuditReason ?? Reason) is { } text
        ? Outcome + ": " + WriteAuditText.Sanitize(text)
        : Outcome.ToString();
}

/// <summary>
/// What a bulk delete did, id by id.
/// </summary>
/// <remarks>
/// <para>
/// One session and one <c>SaveChangesAsync</c>, so the whole batch is one Postgres transaction: a
/// half-applied bulk delete is the outcome nobody can reason about afterwards. The per-id list is what
/// the screen needs — "3 deleted, 1 was not there" is an answer, "done" is not.
/// </para>
/// <para>
/// The cap exists because this is a UI. Beyond it the right tool is a migration or
/// <c>DeleteDocumentsByTypeAsync</c>, not a browser holding a transaction open.
/// </para>
/// </remarks>
/// <param name="Accepted">Whether the request was carried out at all.</param>
/// <param name="Reason">Why it was refused, when it was.</param>
/// <param name="Results">One entry per requested id, in the order they were given.</param>
internal sealed record BulkDeleteResult(bool Accepted, string? Reason, ImmutableArray<DeleteResult> Results)
{
    /// <summary>How many rows were removed outright.</summary>
    public int HardDeletedCount => Count(DeleteOutcome.HardDeleted);

    /// <summary>How many rows were marked deleted.</summary>
    public int SoftDeletedCount => Count(DeleteOutcome.SoftDeleted);

    /// <summary>How many ids matched nothing.</summary>
    public int NotFoundCount => Count(DeleteOutcome.NotFound);

    /// <summary>How many ids were refused.</summary>
    public int RefusedCount => Count(DeleteOutcome.Refused);

    /// <summary>How many rows the database actually lost or marked.</summary>
    public int DeletedCount => HardDeletedCount + SoftDeletedCount;

    /// <summary>The request was carried out; <paramref name="results" /> says what happened to each id.</summary>
    public static BulkDeleteResult Completed(ImmutableArray<DeleteResult> results) =>
        new(true, null, results);

    /// <summary>Nothing was written.</summary>
    public static BulkDeleteResult Refused(string reason) => new(false, reason, []);

    /// <summary>One line for the audit entry.</summary>
    public string Summary()
    {
        if (!Accepted)
        {
            return WriteAuditText.Sanitize(Reason ?? "Refused.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DeletedCount} deleted ({HardDeletedCount} hard, {SoftDeletedCount} soft), {NotFoundCount} not found, {RefusedCount} refused.");
    }

    private int Count(DeleteOutcome outcome)
    {
        if (Results.IsDefaultOrEmpty)
        {
            return 0;
        }

        var count = 0;
        foreach (var result in Results)
        {
            if (result.Outcome == outcome)
            {
                count++;
            }
        }

        return count;
    }
}
