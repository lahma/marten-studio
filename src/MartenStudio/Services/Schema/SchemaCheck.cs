namespace MartenStudio.Services.Schema;

/// <summary>Whether the database matches what <c>StoreOptions</c> says it should be.</summary>
internal enum SchemaCheckStatus
{
    /// <summary>Nobody has asked yet. The check opens connections, so it never runs on navigation.</summary>
    NotChecked = 0,

    /// <summary>Everything Marten configures is there, in the shape it configures.</summary>
    Matches,

    /// <summary>Something differs, and <see cref="SchemaCheck.Objects" /> says what.</summary>
    Differences,

    /// <summary>
    /// The question could not be answered - the database refused, or is not there. "Cannot report" is a
    /// value and is drawn differently from "in sync" (plan §4.8).
    /// </summary>
    Unavailable,

    /// <summary>
    /// This visitor may not see the answer: it spans every tenant and every document type
    /// (<see cref="SchemaScriptGate" />), and <see cref="SchemaCheck.Withheld" /> says which gate refused.
    /// </summary>
    Withheld,
}

/// <summary>One schema object that differs from its configuration.</summary>
/// <param name="Name">The qualified name, as Weasel reports it.</param>
/// <param name="Kind">What sort of object it is - the delta's own type name, made readable.</param>
/// <param name="Difference">Weasel's verdict: <c>Create</c>, <c>Update</c> or <c>Invalid</c>.</param>
internal sealed record SchemaObjectDifference(string Name, string Kind, string Difference);

/// <summary>
/// The drift verdict for one database.
/// </summary>
/// <param name="Status">Whether it matches, differs, or could not be established.</param>
/// <param name="DifferenceCount">How many objects differ.</param>
/// <param name="Objects">Which ones, and how.</param>
/// <param name="Schemas">The schemas the store owns in this database.</param>
/// <param name="AssertionMessage">
/// What <c>AssertDatabaseMatchesConfigurationAsync</c> said. Rendered as a value rather than thrown:
/// Marten's own message names every object it disagrees about and is the most useful sentence on the
/// page, but it arrives as an exception, and an exception on a tab is a blank tab.
/// </param>
/// <param name="Reason">
/// Why the check could not run, when <see cref="Status" /> is unavailable, or why it was not run for this
/// visitor, when it is withheld.
/// </param>
internal sealed record SchemaCheck(
    SchemaCheckStatus Status,
    int DifferenceCount,
    IReadOnlyList<SchemaObjectDifference> Objects,
    IReadOnlyList<string> Schemas,
    string? AssertionMessage,
    string? Reason)
{
    /// <summary>Nobody has asked yet.</summary>
    public static SchemaCheck NotChecked { get; } = new(SchemaCheckStatus.NotChecked, 0, [], [], null, null);

    /// <summary>Everything matches.</summary>
    public static SchemaCheck Matches(IReadOnlyList<string> schemas, string? assertionMessage) =>
        new(SchemaCheckStatus.Matches, 0, [], schemas, assertionMessage, null);

    /// <summary>These objects differ.</summary>
    public static SchemaCheck Differences(
        IReadOnlyList<SchemaObjectDifference> objects,
        IReadOnlyList<string> schemas,
        string? assertionMessage) =>
        new(SchemaCheckStatus.Differences, objects.Count, objects, schemas, assertionMessage, null);

    /// <summary>The check could not run.</summary>
    public static SchemaCheck Unavailable(string reason) =>
        new(SchemaCheckStatus.Unavailable, 0, [], [], null, reason);

    /// <summary>The check was not run for this visitor, and this is why.</summary>
    public static SchemaCheck Refused(SchemaScriptRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return new(SchemaCheckStatus.Withheld, 0, [], [], null, refusal.Reason) { Withheld = refusal };
    }

    /// <summary>
    /// Why this visitor may not see the check, or <see langword="null" /> when they may. Set exactly when
    /// <see cref="Status" /> is <see cref="SchemaCheckStatus.Withheld" />.
    /// </summary>
    public SchemaScriptRefusal? Withheld { get; init; }

    /// <summary>The badge's text.</summary>
    public string BadgeText => Status switch
    {
        SchemaCheckStatus.Matches => "In sync",
        SchemaCheckStatus.Differences => DifferenceCount == 1 ? "1 difference" : $"{DifferenceCount} differences",
        SchemaCheckStatus.Unavailable => "Cannot report",
        SchemaCheckStatus.Withheld => "Withheld",
        _ => "Not checked",
    };
}
