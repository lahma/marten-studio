using System.Collections.Immutable;
using System.Globalization;

using MartenStudio.Services.Json;

namespace MartenStudio.Services.Documents;

/// <summary>
/// What a document's row says about the version a save is allowed to replace.
/// </summary>
/// <remarks>
/// <para>
/// Marten keeps this in one column — <c>mt_version</c> — but in two shapes. A store using optimistic
/// concurrency holds a <see cref="Guid"/> there; a store using numeric revisions holds an integer. Both
/// are read back as whatever the column actually contains rather than from what the mapping says it
/// should contain, because a studio reads databases other people migrated.
/// </para>
/// <para>
/// A table with <c>DisableInformationalFields()</c> has no version column at all, and then the token is
/// <see cref="None"/>: there is nothing to compare, the save is unguarded, and
/// <see cref="WritePreview.Reason"/> says so out loud rather than pretending a check happened.
/// </para>
/// </remarks>
/// <param name="Version">The Guid version, when the column holds one.</param>
/// <param name="Revision">The numeric revision, when the column holds one.</param>
internal readonly record struct DocumentConcurrencyToken(Guid? Version, long? Revision)
{
    /// <summary>No token: either the table has no version column, or nobody read one.</summary>
    public static DocumentConcurrencyToken None => default;

    /// <summary>Whether this token names a version the database could be compared against.</summary>
    public bool IsKnown => Version.HasValue || Revision.HasValue;

    /// <summary>A Guid <c>mt_version</c>.</summary>
    public static DocumentConcurrencyToken ForVersion(Guid version) => new(version, null);

    /// <summary>A numeric revision.</summary>
    public static DocumentConcurrencyToken ForRevision(long revision) => new(null, revision);

    /// <summary>
    /// The token in whatever shape the column handed back. An unrecognised type is
    /// <see cref="None"/> rather than a guess, because a wrong token is worse than no token.
    /// </summary>
    public static DocumentConcurrencyToken FromColumnValue(object? value) => value switch
    {
        Guid version => ForVersion(version),
        int revision => ForRevision(revision),
        long revision => ForRevision(revision),
        short revision => ForRevision(revision),
        _ => None,
    };

    /// <summary>
    /// The token as it travels through a form field or a query string, and back again through
    /// <see cref="TryParse"/>.
    /// </summary>
    public string ToTokenString()
    {
        if (Version.HasValue)
        {
            return Version.Value.ToString("D", CultureInfo.InvariantCulture);
        }

        return Revision.HasValue
            ? Revision.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    /// <summary>Reads a token back. Anything that is neither a GUID nor an integer is <see cref="None"/>.</summary>
    public static bool TryParse(string? text, out DocumentConcurrencyToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            token = None;
            return false;
        }

        var trimmed = text.Trim();

        if (Guid.TryParse(trimmed, CultureInfo.InvariantCulture, out var version))
        {
            token = ForVersion(version);
            return true;
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision))
        {
            token = ForRevision(revision);
            return true;
        }

        token = None;
        return false;
    }

    /// <summary>How the token reads in an audit entry or a conflict message.</summary>
    public string Describe() => IsKnown ? ToTokenString() : "(none)";
}

/// <summary>What a preview found.</summary>
internal enum WritePreviewStatus
{
    /// <summary>The document is there, the edit parses, and the round trip succeeded.</summary>
    Ready,

    /// <summary>No such row in this collection, in this tenant.</summary>
    NotFound,

    /// <summary>
    /// The edit cannot be saved at all: the JSON is malformed, the collection is unknown, the type
    /// cannot be constructed, or the scope does not name a tenant a conjoined collection needs.
    /// </summary>
    Refused,
}

/// <summary>
/// What a save would do, worked out before anything is written.
/// </summary>
/// <remarks>
/// <para>
/// This is design decision D7 made into a value. Marten has no untyped write path, so saving an edited
/// document means deserializing it into the CLR type and serializing that back — and any property the
/// type has no member for is gone at that moment, silently. The preview runs exactly that round trip
/// and reports what came back, so the loss is visible <em>before</em> it happens and the save can be
/// refused unless somebody acknowledges it.
/// </para>
/// <para>
/// Two diffs, because the screen asks two questions. <see cref="RoundTripDiff"/> compares the edit with
/// what the CLR type could carry — its <c>Dropped</c> bucket is the data loss. <see cref="EditDiff"/>
/// compares what is stored with the edit — that is what the person actually changed.
/// </para>
/// </remarks>
internal sealed record WritePreview
{
    /// <summary>The collection alias the document belongs to.</summary>
    public required string Alias { get; init; }

    /// <summary>The document id, as it was typed or linked.</summary>
    public required string Id { get; init; }

    /// <summary>What the preview found.</summary>
    public required WritePreviewStatus Status { get; init; }

    /// <summary>The CLR type name, when the collection is one Marten knows.</summary>
    public string? DocumentTypeName { get; init; }

    /// <summary>The document as it is stored, verbatim.</summary>
    public string? StoredJson { get; init; }

    /// <summary>The document as it was edited, verbatim.</summary>
    public string? EditedJson { get; init; }

    /// <summary>What the edit becomes after a round trip through the CLR type.</summary>
    public string? RoundTrippedJson { get; init; }

    /// <summary>Edited versus round-tripped: <c>Dropped</c> is what saving would lose.</summary>
    public JsonDiffResult RoundTripDiff { get; init; } = JsonDiffResult.Empty;

    /// <summary>Stored versus edited: what the person changed.</summary>
    public JsonDiffResult EditDiff { get; init; } = JsonDiffResult.Empty;

    /// <summary>The version the row carries now, which a save has to still find there.</summary>
    public DocumentConcurrencyToken CurrentToken { get; init; }

    /// <summary>Whether the edited JSON could be turned into the document's CLR type at all.</summary>
    public bool TypeIsConstructible { get; init; }

    /// <summary>Why the preview is not <see cref="WritePreviewStatus.Ready"/>, or a caveat about a ready one.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether saving this edit would drop something.</summary>
    public bool HasDrops => !RoundTripDiff.Dropped.IsDefaultOrEmpty;

    /// <summary>The JSONPaths that would be lost, for the dialog and for audit event 9211.</summary>
    public ImmutableArray<string> DroppedPaths =>
        RoundTripDiff.Dropped.IsDefaultOrEmpty
            ? []
            : [.. RoundTripDiff.Dropped.Select(static x => x.Path)];

    /// <summary>The dropped paths as one line, which is what the audit entry carries.</summary>
    public string DroppedPathsSummary() => string.Join(", ", DroppedPaths);

    /// <summary>A ready preview.</summary>
    public static WritePreview Ready(
        string alias,
        string id,
        string documentTypeName,
        string storedJson,
        string editedJson,
        string roundTrippedJson,
        JsonDiffResult roundTripDiff,
        JsonDiffResult editDiff,
        DocumentConcurrencyToken currentToken,
        string? reason = null) =>
        new()
        {
            Alias = alias,
            Id = id,
            Status = WritePreviewStatus.Ready,
            DocumentTypeName = documentTypeName,
            StoredJson = storedJson,
            EditedJson = editedJson,
            RoundTrippedJson = roundTrippedJson,
            RoundTripDiff = roundTripDiff,
            EditDiff = editDiff,
            CurrentToken = currentToken,
            TypeIsConstructible = true,
            Reason = reason,
        };

    /// <summary>No such document.</summary>
    public static WritePreview NotFound(string alias, string id, string? documentTypeName = null) =>
        new()
        {
            Alias = alias,
            Id = id,
            Status = WritePreviewStatus.NotFound,
            DocumentTypeName = documentTypeName,
            TypeIsConstructible = true,
            Reason = $"No document with id '{id}' is in '{alias}'.",
        };

    /// <summary>
    /// An edit that cannot be saved, with the reason a person can act on.
    /// </summary>
    /// <param name="alias">The collection.</param>
    /// <param name="id">The document id.</param>
    /// <param name="reason">Why the edit was refused, phrased for whoever typed it.</param>
    /// <param name="typeIsConstructible">
    /// <see langword="false"/> when the refusal is the CLR type itself — abstract, no usable
    /// constructor, or a serializer that threw. That is the one refusal a person cannot fix by editing
    /// the JSON, so the screen says something different about it.
    /// </param>
    /// <param name="editedJson">The edit, kept so the editor can be re-rendered with it.</param>
    /// <param name="storedJson">The stored document, when it was read before the refusal.</param>
    /// <param name="documentTypeName">The CLR type name, when it is known.</param>
    /// <param name="currentToken">The row's version, when it was read before the refusal.</param>
    public static WritePreview Refused(
        string alias,
        string id,
        string reason,
        bool typeIsConstructible = true,
        string? editedJson = null,
        string? storedJson = null,
        string? documentTypeName = null,
        DocumentConcurrencyToken currentToken = default) =>
        new()
        {
            Alias = alias,
            Id = id,
            Status = WritePreviewStatus.Refused,
            DocumentTypeName = documentTypeName,
            StoredJson = storedJson,
            EditedJson = editedJson,
            CurrentToken = currentToken,
            TypeIsConstructible = typeIsConstructible,
            Reason = reason,
        };

    /// <summary>One line for the audit entry and the dialog's heading.</summary>
    public string Summary() => Status switch
    {
        WritePreviewStatus.Ready when HasDrops =>
            RoundTripDiff.Summary() + " Dropped: " + DroppedPathsSummary(),
        WritePreviewStatus.Ready => RoundTripDiff.IsEmpty
            ? EditDiff.Summary()
            : RoundTripDiff.Summary(),
        _ => Reason ?? Status.ToString(),
    };
}
