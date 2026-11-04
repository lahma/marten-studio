using System.Text.Json;

namespace MartenStudio.Services.Shapes;

/// <summary>How much of the collection (or event type) a check read.</summary>
internal enum ShapeCoverage
{
    /// <summary>Everything in scope was read: it held no more than the sample size.</summary>
    Complete,

    /// <summary>A bounded sample was read.</summary>
    Sampled,

    /// <summary>The check stopped early - the budget or a statement timeout ran out - with what it had read.</summary>
    Partial,

    /// <summary>Nothing was read: there was nothing to read, or the check could not say anything.</summary>
    NotChecked,
}

/// <summary>What a stored item and its round trip disagree about at one path.</summary>
internal enum ShapeMemberKind
{
    /// <summary>Stored, with a value, and the round trip has no such path: the next save drops it.</summary>
    Dropped,

    /// <summary>Stored with a value, and the round trip has <c>null</c> there.</summary>
    Nulled,

    /// <summary>Both have a value, of different JSON kinds.</summary>
    Retyped,

    /// <summary>Both have a value of the same scalar kind, and it comes back different.</summary>
    Rewritten,

    /// <summary>A dropped and a defaulted sibling that are one value under two spellings.</summary>
    Respelled,

    /// <summary>The round trip has the path and the stored item does not.</summary>
    Defaulted,
}

/// <summary>Why an item could not be checked at all.</summary>
internal enum ShapeFailureKind
{
    /// <summary>The store's reader threw.</summary>
    Unreadable,

    /// <summary>The reader returned nothing.</summary>
    ReadAsNothing,

    /// <summary>The item loaded, and writing it back threw.</summary>
    NotWritable,

    /// <summary>A document whose <c>mt_doc_type</c> names a subclass the store no longer registers.</summary>
    UnknownSubclass,

    /// <summary>An event whose type alias no registered type, and no loadable <c>mt_dotnet_type</c>, answers to.</summary>
    UnknownEventType,
}

/// <summary>
/// Where an example is: the document's id, or an event's stream id or key. Never a value of the item.
/// </summary>
/// <param name="Id">The document id, or the event's stream id or key.</param>
/// <param name="TenantId">The item's tenant, when its table has one.</param>
/// <param name="Sequence">An event's sequence; <see langword="null" /> for a document.</param>
/// <param name="Version">An event's version in its stream; <see langword="null" /> for a document.</param>
internal sealed record ShapeItemKey(string Id, string? TenantId, long? Sequence = null, long? Version = null);

/// <summary>
/// One kind of member problem in one type: a path, how many items have it, and the first of them.
/// </summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Path">The normalized path, array indices written <c>[*]</c>.</param>
/// <param name="PairedPath">The path the value comes back under, for <see cref="ShapeMemberKind.Respelled" />.</param>
/// <param name="Count">How many examined items have it.</param>
/// <param name="Example">The first of them in key order.</param>
/// <param name="StoredKind">What the stored value is, for <see cref="ShapeMemberKind.Retyped" />.</param>
/// <param name="ReadKind">What the round trip writes, for <see cref="ShapeMemberKind.Retyped" />.</param>
internal sealed record ShapeMemberFinding(
    ShapeMemberKind Kind,
    string Path,
    string? PairedPath,
    int Count,
    ShapeItemKey Example,
    JsonValueKind? StoredKind = null,
    JsonValueKind? ReadKind = null);

/// <summary>
/// A group of items that failed the same way.
/// </summary>
/// <param name="Kind">How they failed.</param>
/// <param name="ExceptionType">The exception type's short name; never its message.</param>
/// <param name="Path">The path the failure was at, when the exception knew it.</param>
/// <param name="StoredKind">The JSON kind found in the stored item at that path.</param>
/// <param name="Detail">The subclass alias (UnknownSubclass) or "alias / mt_dotnet_type" (UnknownEventType); never a message.</param>
/// <param name="Count">How many examined items failed so.</param>
/// <param name="Example">The first of them in key order.</param>
/// <param name="FirstSequence">The first failing event's sequence, for events.</param>
internal sealed record ShapeFailureGroup(
    ShapeFailureKind Kind,
    string ExceptionType,
    string? Path,
    JsonValueKind? StoredKind,
    string? Detail,
    int Count,
    ShapeItemKey Example,
    long? FirstSequence);

/// <summary>What the check found for one concrete document type or one event type.</summary>
/// <param name="TypeName">The type's short name.</param>
/// <param name="FullTypeName">The type's full name, when there is one.</param>
/// <param name="Examined">How many items of this type were examined, failures included.</param>
/// <param name="ReadAsOtherType">Events written as one type and read as another (an upcaster); not compared.</param>
/// <param name="Binary">Events with a binary body: read, not compared.</param>
/// <param name="Failures">The failure groups, capped.</param>
/// <param name="Members">The member findings, capped.</param>
/// <param name="MoreFailures">How many failure groups were left out by the cap.</param>
/// <param name="MoreMembers">How many member findings were left out by the cap.</param>
internal sealed record ShapeTypeResult(
    string TypeName,
    string? FullTypeName,
    int Examined,
    int ReadAsOtherType,
    int Binary,
    IReadOnlyList<ShapeFailureGroup> Failures,
    IReadOnlyList<ShapeMemberFinding> Members,
    int MoreFailures,
    int MoreMembers);

/// <summary>A statement the check ran, as the "Show SQL" disclosure shows it: its text and its parameters' names, never their values.</summary>
/// <param name="Sql">The statement.</param>
/// <param name="ParameterNames">The parameter names in it, spelled as the statement spells them.</param>
internal sealed record ShapeStatement(string Sql, IReadOnlyList<string> ParameterNames);

/// <summary>
/// What a shape check found. Every failure is a value (<see cref="Error" />, or <see cref="ShapeCoverage.NotChecked" /> with a
/// <see cref="CoverageReason" />); the result holds ids and paths and counts, never a value of an item or an exception message.
/// </summary>
/// <param name="Subject">The alias or event type name that was checked.</param>
/// <param name="Coverage">How much was read.</param>
/// <param name="CheckedAt">When the check finished.</param>
/// <param name="Elapsed">How long it took.</param>
/// <param name="Examined">How many items were read and judged.</param>
/// <param name="TooLarge">How many were larger than the bound, and so counted, not read.</param>
/// <param name="LowestPart">How many examined items came from the lowest keys.</param>
/// <param name="HighestPart">How many examined items came from the highest keys.</param>
/// <param name="SpreadPart">How many examined items came from the probes spread across the collection.</param>
/// <param name="CoverageReason">Why the coverage is less than a whole check's, or why nothing was checked.</param>
/// <param name="Remedy">A line the visitor can act on to widen the coverage.</param>
/// <param name="LeftOut">What the scope leaves out, one sentence each.</param>
/// <param name="Types">One result per concrete document type or event type.</param>
/// <param name="RebuildSentence">What a rebuild would do about the failures; events only.</param>
/// <param name="Error">Why the check failed, when it did.</param>
/// <param name="Statements">The statements it ran.</param>
internal sealed record ShapeCheckResult(
    string Subject,
    ShapeCoverage Coverage,
    DateTimeOffset CheckedAt,
    TimeSpan Elapsed,
    int Examined,
    int TooLarge,
    int LowestPart,
    int HighestPart,
    int SpreadPart,
    string? CoverageReason,
    string? Remedy,
    IReadOnlyList<string> LeftOut,
    IReadOnlyList<ShapeTypeResult> Types,
    string? RebuildSentence,
    string? Error,
    IReadOnlyList<ShapeStatement> Statements)
{
    /// <summary>When "Check again" will run the check again rather than return this result.</summary>
    public DateTimeOffset RecheckAt => CheckedAt + ShapeCheckLimits.RecheckAfter;

    /// <summary>A check that read nothing and says why.</summary>
    public static ShapeCheckResult NotChecked(string subject, string reason, DateTimeOffset at) =>
        new(subject, ShapeCoverage.NotChecked, at, TimeSpan.Zero, 0, 0, 0, 0, 0, reason, null, [], [], null, null, []);

    /// <summary>A check that failed, and says why in words a visitor can act on.</summary>
    public static ShapeCheckResult Failed(string subject, string error, DateTimeOffset at, TimeSpan elapsed, IReadOnlyList<ShapeStatement> statements) =>
        new(subject, ShapeCoverage.NotChecked, at, elapsed, 0, 0, 0, 0, 0, null, null, [], [], null, error, statements);
}
