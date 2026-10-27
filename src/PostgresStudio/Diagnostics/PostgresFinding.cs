namespace PostgresStudio;

/// <summary>One thing the studio has to say about a database.</summary>
/// <remarks>
/// <para>
/// <b>Key your wording on <see cref="Code" /> and <see cref="Variant" />.</b> Both are frozen: a code is never reused,
/// renamed or moved to another area, and a variant keeps its meaning within its code (<c>docs/health/codes.md</c>). A newer
/// studio may return a code or variant you have no wording for; fall back to <see cref="Title" />, which is always set.
/// </para>
/// <para>
/// The English texts - <see cref="Title" />, <see cref="Detail" />, <see cref="Caveat" />, <see cref="ActionExplanation" />
/// and <see cref="Evidence" /> - are for display, and their wording may change between releases. Numbers a host acts on
/// are in the typed sections.
/// </para>
/// </remarks>
public sealed record PostgresFinding
{
    /// <summary>The check's code: a letter and two digits (<c>T07</c>). Stable: never reused, never renamed.</summary>
    public string Code { get; init; } = "";

    /// <summary>
    /// Which of the check's fixed kinds of finding it is (<c>deadlocks</c>), or null for a check with one kind. Frozen per
    /// code, like the code: a check that lists variants (<see cref="PostgresCheck.Variants" />) names one on every finding.
    /// </summary>
    public string? Variant { get; init; }

    /// <summary>
    /// What it is about, by name: a qualified table or index, a setting, a shard. Its kind is fixed per code
    /// (<c>docs/health/codes.md</c>). Null for a finding about the database as a whole.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>
    /// The tenant it is about, or null. Present only with <see cref="PostgresDiagnosticsRequest.IncludeActivity" />: without
    /// it a finding about one tenant is withheld whole. Marten Studio only.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>How much it matters.</summary>
    public PostgresSeverity Severity { get; init; }

    /// <summary>The check's area.</summary>
    public PostgresFindingCategory Category { get; init; }

    /// <summary>One English sentence, in this studio's own words. A fallback for a code the host has no wording for.</summary>
    public string Title { get; init; } = "";

    /// <summary>Why it matters, in English.</summary>
    public string Detail { get; init; } = "";

    /// <summary>How sure it is (a statistics reset, a privilege that hides sessions), or null.</summary>
    public string? Caveat { get; init; }

    /// <summary>
    /// What to change: a configuration line, migration SQL, a server setting, a managed platform's control or a one-off
    /// repair, as text to hand on. The studio never runs it. Null when the finding is the answer in itself.
    /// </summary>
    public string? Action { get; init; }

    /// <summary>What <see cref="Action" /> changes and what it costs, in English.</summary>
    public string? ActionExplanation { get; init; }

    /// <summary>What kind of change <see cref="Action" /> is, and so whose desk it goes to; null without an action.</summary>
    public PostgresActionKind? ActionKind { get; init; }

    /// <summary>What <see cref="Action" /> needs that this host does not have (<c>Marten 9.47+</c>), or null.</summary>
    public string? ActionRequires { get; init; }

    /// <summary>
    /// The facts behind it, as an English label and a formatted value, for display. Not a contract: key on
    /// <see cref="Code" />, <see cref="Variant" /> and the typed sections for numbers. A value the request did not include
    /// (Activity-grade) reads <c>withheld</c>, never the value.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Evidence { get; init; } = [];

    /// <summary>The check's documentation, pinned to this release.</summary>
    public string DocsUrl { get; init; } = "";

    /// <summary>
    /// The finding's card on the studio's Health page, for this database, relative to the studio's mount path with no
    /// leading slash (<see cref="PostgresStudioLink" />). Empty for a finding built outside the studio.
    /// </summary>
    public string StudioPath { get; init; } = "";

    /// <summary>The studio screens that help with it (<c>Open the index advisor</c>), in the same relative form.</summary>
    public IReadOnlyList<PostgresStudioLink> StudioLinks { get; init; } = [];

    /// <summary>When its facts were read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>
    /// What identifies it among one database's findings, and across snapshots while it lasts: the code, then the variant
    /// when there is one, then the subject when there is one, joined by <c>|</c> - <c>T03|index|public.orders_idx</c>,
    /// <c>C08|deadlocks</c>. The studio refuses two findings of one database under one key, so it needs no tenant of its own.
    /// </summary>
    public string Key => (Variant, Subject) switch
    {
        (null, null) => Code,
        (null, { } subject) => Code + "|" + subject,
        ({ } variant, null) => Code + "|" + variant,
        ({ } variant, { } subject) => Code + "|" + variant + "|" + subject,
    };
}
