using System.Collections.Immutable;
using System.Globalization;
using System.Text;

using MartenStudio.Services.Json;

namespace MartenStudio.Services.Documents;

/// <summary>
/// Makes a line safe to write to the audit ring and to the application's log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything in a write message came from somewhere untrusted.</b> The alias and the id arrive from a
/// URL, the dropped paths are property names out of a JSON document somebody pasted, and the log is a
/// line-oriented file that an operator greps and a shipper parses. A property called
/// <c>"a\nb"</c> therefore ends the log line and starts one that looks like a second event — log
/// injection, and the cheapest possible way to make an audit trail lie. Control characters, the Unicode
/// line and paragraph separators and the invisible format characters (<c>U+200E</c> and friends, which
/// can reverse how a line reads without changing what it says) are all folded to a single space here, and
/// the result is capped.
/// </para>
/// <para>
/// The cap is the second half of the same problem: a document with a thousand unknown properties would
/// otherwise put a thousand paths into one log line.
/// </para>
/// <para>
/// This is deliberately <em>not</em> applied to <see cref="WritePreview.Reason"/>. That string is
/// rendered into the page, where Blazor escapes it and a newline is just a newline, and it is the one
/// place a person gets to read the serializer's own words about what went wrong.
/// </para>
/// </remarks>
internal static class WriteAuditText
{
    /// <summary>How long one audit line may be before it is cut.</summary>
    public const int MaxLength = 1000;

    /// <summary>How long one interpolated value — an alias, an id, a JSONPath — may be.</summary>
    public const int MaxTokenLength = 200;

    /// <summary>What a cut line ends with.</summary>
    private const char Ellipsis = '…';

    /// <summary>Folds the unsafe characters out of <paramref name="value"/> and caps it at <see cref="MaxLength"/>.</summary>
    public static string Sanitize(string? value) => Sanitize(value, MaxLength);

    /// <summary>Folds the unsafe characters out of <paramref name="value"/> and caps it.</summary>
    /// <param name="value">The text, from wherever it came from.</param>
    /// <param name="maxLength">The most characters to keep, not counting the ellipsis.</param>
    public static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength) + 1);
        var pendingSpace = false;
        var truncated = false;

        foreach (var c in value)
        {
            if (IsUnsafe(c))
            {
                // A run of newlines and tabs is one space, not five: the point is to keep the line one
                // line, and a message padded out with whitespace is its own small denial of service.
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (builder.Length >= maxLength)
                {
                    truncated = true;
                    break;
                }

                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length >= maxLength)
            {
                truncated = true;
                break;
            }

            builder.Append(c);
        }

        // Never end on half a surrogate pair: a cut there is not a character, and a log shipper that
        // validates UTF-8 rejects the whole line.
        if (builder.Length > 0 && char.IsHighSurrogate(builder[^1]))
        {
            builder.Length--;
            truncated = true;
        }

        // Trimmed at the ends, so a value that was nothing but whitespace is nothing rather than a line
        // of spaces with a message's worth of alignment in it.
        var text = builder.ToString().Trim();

        return truncated ? text + Ellipsis : text;
    }

    /// <summary>
    /// Whether a character has no business in a log line: the C0 and C1 controls (newline and tab
    /// included), the Unicode line and paragraph separators, and the invisible format characters.
    /// </summary>
    private static bool IsUnsafe(char c) =>
        char.IsControl(c) ||
        c is LineSeparator or ParagraphSeparator ||
        char.GetUnicodeCategory(c) == UnicodeCategory.Format;

    /// <summary>U+2028, which is a line break to a JavaScript parser and to several log shippers.</summary>
    private const char LineSeparator = (char) 0x2028;

    /// <summary>U+2029, the same problem one code point further along.</summary>
    private const char ParagraphSeparator = (char) 0x2029;
}

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
/// <para>
/// <b>A version member is a member like any other, and it is routinely out of date.</b> A type that
/// carries its own version — a <c>[Version]</c> member, <c>JasperFx.Metadata.IVersioned</c> or
/// <c>JasperFx.IRevisioned</c> — has that version in its JSON as well as in <c>mt_version</c>, and the
/// two do not agree. Marten fills the member in when it <em>reads</em> a document and does not re-stamp
/// it before serializing, so a host's own load-edit-save leaves <c>data</c> carrying the version the row
/// had <em>before</em> that write while the column moves on; the studio never reads through Marten at
/// all, so a save here carries the member through the round trip exactly as the edit had it. Verified
/// against Marten 9.35 by <c>DocumentWriteRevisionLiveTests</c>, 2026-09-14.
/// </para>
/// <para>
/// Two consequences worth knowing before reading a diff. The member is <em>editable</em> — nothing stops
/// somebody typing a different version into it — and editing it changes nothing about whether the save is
/// allowed, because <see cref="CurrentToken"/> and the concurrency check are the column and never the
/// member. And a document whose member looks stale next to its column is not a document anything has gone
/// wrong with; it is every document of such a type.
/// </para>
/// </remarks>
internal sealed record WritePreview
{
    /// <summary>How many dropped paths one audit line names before it stops counting them out.</summary>
    private const int MaxSummarizedPaths = 20;

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
    /// <remarks>
    /// This is the string the page shows, and it is the only place a serializer's own exception message
    /// is allowed to appear: a person fixing an edit needs to read what the converter actually said.
    /// <see cref="Summary"/> — what the audit and the application log get — uses
    /// <see cref="AuditReason"/> instead wherever the two differ.
    /// </remarks>
    public string? Reason { get; init; }

    /// <summary>
    /// The refusal as the audit records it, when that has to be less than <see cref="Reason"/> says.
    /// </summary>
    /// <remarks>
    /// A deserializer failure is the case this exists for. The message can be arbitrarily long, can carry
    /// a fragment of the document, and is written by whichever converter threw; the audit gets the
    /// exception's type and the JSON path instead, which is what actually identifies the failure, and the
    /// message stays on screen where it is useful and harmless.
    /// </remarks>
    public string? AuditReason { get; init; }

    /// <summary>Whether saving this edit would drop something.</summary>
    public bool HasDrops => !RoundTripDiff.Dropped.IsDefaultOrEmpty;

    /// <summary>The JSONPaths that would be lost, for the dialog and for audit event 9211.</summary>
    public ImmutableArray<string> DroppedPaths =>
        RoundTripDiff.Dropped.IsDefaultOrEmpty
            ? []
            : [.. RoundTripDiff.Dropped.Select(static x => x.Path)];

    /// <summary>
    /// The dropped paths as one line, which is what the audit entry carries.
    /// </summary>
    /// <remarks>
    /// Every path is a property name out of a document the studio did not write, so every path goes
    /// through <see cref="WriteAuditText"/> before it reaches a log line, and the list itself is capped:
    /// a document with a thousand unmapped properties must not produce a thousand-item log line.
    /// </remarks>
    public string DroppedPathsSummary()
    {
        var paths = DroppedPaths;

        if (paths.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var shown = Math.Min(paths.Length, MaxSummarizedPaths);
        var builder = new StringBuilder();

        for (var i = 0; i < shown; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(WriteAuditText.Sanitize(paths[i], WriteAuditText.MaxTokenLength));
        }

        if (paths.Length > shown)
        {
            builder.Append(CultureInfo.InvariantCulture, $" and {paths.Length - shown} more");
        }

        return builder.ToString();
    }

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
    /// <param name="auditReason">
    /// What the audit records instead of <paramref name="reason"/>, when the two have to differ — see
    /// <see cref="AuditReason"/>.
    /// </param>
    public static WritePreview Refused(
        string alias,
        string id,
        string reason,
        bool typeIsConstructible = true,
        string? editedJson = null,
        string? storedJson = null,
        string? documentTypeName = null,
        DocumentConcurrencyToken currentToken = default,
        string? auditReason = null) =>
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
            AuditReason = auditReason,
        };

    /// <summary>
    /// One line for the audit entry and the dialog's heading, safe to write to a log.
    /// </summary>
    /// <remarks>
    /// Sanitised, because everything it is built from — the dropped paths, the alias in a refusal — came
    /// out of a URL or out of a document, and a log line is a line (see <see cref="WriteAuditText"/>).
    /// </remarks>
    public string Summary() => WriteAuditText.Sanitize(Status switch
    {
        WritePreviewStatus.Ready when HasDrops =>
            RoundTripDiff.Summary() + " Dropped: " + DroppedPathsSummary(),
        WritePreviewStatus.Ready => RoundTripDiff.IsEmpty
            ? EditDiff.Summary()
            : RoundTripDiff.Summary(),
        _ => AuditReason ?? Reason ?? Status.ToString(),
    });
}
