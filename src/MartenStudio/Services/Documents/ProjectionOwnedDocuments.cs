using Marten.Schema;

namespace MartenStudio.Services.Documents;

/// <summary>
/// Documents a projection writes with the version of their event stream as their revision, and why the studio does not edit
/// them.
/// </summary>
/// <remarks>
/// <para>
/// A mapping with <see cref="DocumentMapping.UseVersionFromMatchingStream" /> and numeric revisions has a revision that
/// <em>is</em> the version of the stream the snapshot reflects (Marten 9.55, #5773). The studio's save moves a numeric
/// revision on by one, which puts it past the stream: the async fetch (<c>FetchLatest</c>, <c>FetchForWriting</c>) reads
/// only the events above the stored revision, so the next event is skipped, and a rebuild is the only thing that sets it
/// right (proved live at the floor and at the latest-tested Marten by <c>ProjectionDocumentEditLiveTests</c>).
/// </para>
/// <para>
/// So an edit of such a document is refused in the service, in <c>PreviewAsync</c> and <c>SaveAsync</c> before a
/// connection or a session is opened; the page asks the same question here to show the reason without being asked to try.
/// A delete is not refused (a rebuild recreates the document), and neither is an undelete, which does not touch the
/// revision (the same live tests).
/// </para>
/// </remarks>
internal static class ProjectionOwnedDocuments
{
    /// <summary>What the audit records for a refused edit: no id, because the id came from a URL.</summary>
    internal const string AuditReason = "The document is written by its projection, so the edit was refused.";

    /// <summary>
    /// Whether a projection writes this mapping's documents with the version of the stream as their revision.
    /// </summary>
    /// <param name="mapping">The mapping, or <see langword="null" /> for a table nothing maps.</param>
    internal static bool IsProjectionOwned(DocumentMapping? mapping) => mapping is { UseVersionFromMatchingStream: true };

    /// <summary>Why an edit of this document is refused, phrased for the person looking at the editor.</summary>
    /// <param name="id">The document's id, which is also the id of the stream its projection reads.</param>
    internal static string EditRefusal(string id) =>
        $"This document is written by its projection from the events of stream '{WriteAuditText.Sanitize(id, WriteAuditText.MaxTokenLength)}'; " +
        "an edit would move its revision past the stream and the daemon would skip the next event. " +
        "Change the events, or rebuild the projection.";
}
