using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Services.Connection;

/// <summary>Why a visitor is shown nothing of the connection.</summary>
internal enum ConnectionRefusal
{
    /// <summary>Shown.</summary>
    None,

    /// <summary>The source policy refused the database as a whole.</summary>
    StorePolicy,

    /// <summary>The source or the database could not be resolved.</summary>
    Unavailable,
}

/// <summary>What the TLS fact of the header strip says.</summary>
internal enum ConnectionTlsState
{
    /// <summary>The connection was not read, so nothing is known.</summary>
    NotRead,

    /// <summary>The server shows no <c>pg_stat_ssl</c> row for the studio's backend: the reading role cannot see its own.</summary>
    NotVisible,

    /// <summary>TLS, and the server's certificate is checked (<c>VerifyCA</c> or <c>VerifyFull</c>).</summary>
    Verified,

    /// <summary>TLS, and the server's certificate is not checked.</summary>
    EncryptedNotVerified,

    /// <summary>TLS, but the settings could not be read, so whether the server's certificate is checked is not known.</summary>
    EncryptedVerificationUnknown,

    /// <summary>Not on TLS, and not a local connection.</summary>
    NotEncrypted,

    /// <summary>Not on TLS, and the host is a socket or loopback: nothing leaves the machine.</summary>
    NotEncryptedLocal,

    /// <summary>Not on TLS to the server, but the Cloud SQL Auth Proxy wraps the way onward in it.</summary>
    EncryptedByProxy,

    /// <summary>Not on TLS, and encrypted by GSSAPI.</summary>
    EncryptedGss,
}

/// <summary>The TLS fact: its state and the words for it.</summary>
/// <param name="State">What it is.</param>
/// <param name="Text">The header strip's words: "verified", "encrypted, not verified", "not encrypted".</param>
/// <param name="Detail">The sentence behind it.</param>
internal sealed record ConnectionTls(ConnectionTlsState State, string Text, string Detail)
{
    /// <summary>
    /// What the strip calls the fact: "TLS", or "Encryption" where the connection is encrypted with GSSAPI, which is not TLS.
    /// </summary>
    public string Label => State == ConnectionTlsState.EncryptedGss ? "Encryption" : "TLS";

    /// <summary>The Overview's words for it: "TLS verified (VerifyFull)", "Encryption: GSSAPI".</summary>
    public string Summary => State == ConnectionTlsState.EncryptedGss ? "Encryption: GSSAPI" : "TLS " + Text;
}

/// <summary>One fact of the header strip, with the in-page anchor its detail is at.</summary>
/// <param name="Label">What the fact is about.</param>
/// <param name="Value">The fact.</param>
/// <param name="Anchor">The element id of the section or row that holds the detail.</param>
/// <param name="Tone">A CSS modifier (<c>ok</c>, <c>warn</c>, <c>info</c>, <c>none</c>) - never the only carrier of meaning.</param>
internal sealed record ConnectionFact(string Label, string Value, string Anchor, string Tone = "none");

/// <summary>One fix on a card: a label and the text to copy.</summary>
/// <param name="Label">"Connection string", "NpgsqlDataSourceBuilder", "StoreOptions".</param>
/// <param name="Code">The text the copy button puts on the clipboard.</param>
internal sealed record ConnectionFix(string Label, string Code);

/// <summary>Where a card came from.</summary>
internal enum ConnectionCardKind
{
    /// <summary>A check's finding, from the visitor's Health report.</summary>
    Finding,

    /// <summary>A hint (NP-DESIGN §6) that has a fix to make.</summary>
    Hint,

    /// <summary>A hint that is context only: no fix, or a default worth knowing.</summary>
    Note,

    /// <summary>A card the studio's contributor adds.</summary>
    Contributed,
}

/// <summary>One card of "Worth a look".</summary>
/// <param name="Id">The element id of the card.</param>
/// <param name="Kind">Where it came from.</param>
/// <param name="Heading">One sentence.</param>
/// <param name="Text">What it means and why.</param>
/// <param name="Answers">The ids a settings row names in <see cref="ConnectionSettingRow.HintIds" /> that this card answers: a hint's id and a finding's code.</param>
/// <param name="Keywords">The settings it is about, which the card anchors to in the table.</param>
/// <param name="Fixes">The changes, each with a copy button.</param>
internal sealed record ConnectionCard(
    string Id,
    ConnectionCardKind Kind,
    string Heading,
    string Text,
    IReadOnlyList<string> Answers,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<ConnectionFix> Fixes)
{
    /// <summary>The finding behind the card, for its severity, its mute key and its link to the Health page.</summary>
    public HealthReportFinding? Finding { get; init; }

    /// <summary>The code or hint id, shown beside the heading.</summary>
    public string? Code { get; init; }

    /// <summary>Whether the card is drawn only while the table shows every setting (the ambient-transaction note).</summary>
    public bool AllSettingsOnly { get; init; }

    /// <summary>Whether the card is advice that counts as "a thing worth a look" (a finding, a hint with a fix).</summary>
    public bool Counts => Kind is ConnectionCardKind.Finding or ConnectionCardKind.Hint or ConnectionCardKind.Contributed;
}

/// <summary>One row of the settings table as the page draws it.</summary>
/// <param name="Row">The redacted row.</param>
/// <param name="Anchor">The row's element id.</param>
/// <param name="From">The badge's words.</param>
/// <param name="AdviceCard">The card the row's advice marker links to, or <see langword="null" />.</param>
/// <param name="AlwaysShown">Whether the row stays when the table shows only what differs from the default.</param>
internal sealed record ConnectionSettingView(ConnectionSettingRow Row, string Anchor, string From, ConnectionCard? AdviceCard, bool AlwaysShown)
{
    /// <summary>Whether the row is in the "only what differs" view: something set it, or it has advice, or it is one of the rows that identify the connection.</summary>
    public bool Differs => Row.Origin != ConnectionSettingOrigin.Default || AdviceCard is { Counts: true } || AlwaysShown;
}

/// <summary>One line of "What the server saw".</summary>
/// <param name="Label">What it is.</param>
/// <param name="Value">The value.</param>
/// <param name="Note">A sentence beside it, or <see langword="null" />.</param>
internal sealed record ConnectionObservedLine(string Label, string Value, string? Note = null);

/// <summary>"What the server saw": the lines, and why there are none.</summary>
/// <param name="Lines">The values.</param>
/// <param name="NotRead">Why nothing was read, or <see langword="null" />.</param>
internal sealed record ConnectionObservedView(IReadOnlyList<ConnectionObservedLine> Lines, string? NotRead);

/// <summary>A section the studio's contributor adds below the page's own.</summary>
/// <param name="Id">The section's element id.</param>
/// <param name="Title">Its heading.</param>
/// <param name="Intro">A sentence under it, or <see langword="null" />.</param>
/// <param name="Rows">Its rows.</param>
/// <param name="Cards">Its advice.</param>
internal sealed record ConnectionContributedSection(
    string Id,
    string Title,
    string? Intro,
    IReadOnlyList<ConnectionObservedLine> Rows,
    IReadOnlyList<ConnectionCard> Cards);

/// <summary>The Connection page for one visitor and one database. Nothing in it is, or contains, a connection string.</summary>
internal sealed record ConnectionPageView
{
    /// <summary>Why nothing is shown, or <see cref="ConnectionRefusal.None" />.</summary>
    public ConnectionRefusal Refusal { get; init; }

    /// <summary>The refusal's sentence.</summary>
    public string? Reason { get; init; }

    /// <summary>When the settings and the server's answers were read.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The header strip's facts, in order.</summary>
    public IReadOnlyList<ConnectionFact> Header { get; init; } = [];

    /// <summary>The platform's name ("Amazon RDS"), or <see langword="null" /> when no managed platform was detected.</summary>
    public string? PlatformName { get; init; }

    /// <summary>The poolers detected in front of the server, by name; empty when none was.</summary>
    public IReadOnlyList<string> Poolers { get; init; } = [];

    /// <summary>The TLS fact, for the Overview's line and the snapshot.</summary>
    public ConnectionTls Tls { get; init; } = new(ConnectionTlsState.NotRead, "not read", "The connection was not read.");

    /// <summary>The pool's <c>Maximum Pool Size</c>, or <see langword="null" /> when it is not known.</summary>
    public int? PoolMax { get; init; }

    /// <summary>Whether the connection string parsed. When not, there are no settings and <see cref="UnreadableReason" /> says why.</summary>
    public bool Readable { get; init; } = true;

    /// <summary>The exception type that stopped the parse, never its message.</summary>
    public string? UnreadableReason { get; init; }

    /// <summary>The loaded Npgsql's version.</summary>
    public string NpgsqlVersion { get; init; } = string.Empty;

    /// <summary>The cards that count as things worth a look: findings first, worst first, then hints.</summary>
    public IReadOnlyList<ConnectionCard> Cards { get; init; } = [];

    /// <summary>The cards that are context, not advice.</summary>
    public IReadOnlyList<ConnectionCard> Notes { get; init; } = [];

    /// <summary>How many of the connection findings the visitor is shown the host accepted: not drawn as cards, and in no count.</summary>
    public int AcceptedFindings { get; init; }

    /// <summary>How many of the visitor's connection findings are withheld from them, and why.</summary>
    public int FindingsWithheld { get; init; }

    /// <summary>What it would take to see the withheld findings.</summary>
    public string? FindingsWithheldReason { get; init; }

    /// <summary>Why the findings could not be read at all, when they could not.</summary>
    public string? FindingsNote { get; init; }

    /// <summary>The settings table, grouped by Npgsql's categories.</summary>
    public IReadOnlyList<ConnectionSettingView> Settings { get; init; } = [];

    /// <summary>What the server saw of the studio's own connection.</summary>
    public ConnectionObservedView Observed { get; init; } = new([], "not read");

    /// <summary>Sentences about the settings as a whole: the default password file, a newer Npgsql.</summary>
    public IReadOnlyList<string> SettingsNotes { get; init; } = [];

    /// <summary>What the page cannot see, in words.</summary>
    public IReadOnlyList<ConnectionObservedLine> NotVisible { get; init; } = [];

    /// <summary>The sections the studio's contributor adds.</summary>
    public IReadOnlyList<ConnectionContributedSection> Contributed { get; init; } = [];

    /// <summary>Whether "This application's sessions" may be read by this visitor, and what it takes when not.</summary>
    public SessionDetailsAccess Activity { get; init; } = new(false, false, null, DatabaseRefusal.CapabilityOff, null);

    /// <summary>The statements the facts ran, for "Show SQL".</summary>
    public IReadOnlyList<HealthStatement> Statements { get; init; } = [];

    /// <summary>The number of things worth a look, counted before this browser's mutes.</summary>
    public int WorthALook => Cards.Count;

    /// <summary>A refusal.</summary>
    public static ConnectionPageView Refused(ConnectionRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}

/// <summary>One application's sessions as the page shows them.</summary>
/// <param name="Application">Its <c>application_name</c>, empty for none.</param>
/// <param name="Sessions">How many sessions.</param>
/// <param name="OldestConnection">The oldest <c>backend_start</c> the role can see, or <see langword="null" />.</param>
/// <param name="TlsKnown">How many sessions the role could tell the TLS state of.</param>
/// <param name="TlsOn">How many of them are on TLS.</param>
internal sealed record ConnectionApplicationRow(string Application, long Sessions, DateTimeOffset? OldestConnection, long TlsKnown, long TlsOn);

/// <summary>"This application's sessions" for one visitor. Refused with a value when the activity gate is shut (hard rule 5).</summary>
internal sealed record ConnectionApplicationsView
{
    /// <summary>Which gate is shut, or <see cref="DatabaseRefusal.None" />.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>The sentence for a refusal or a failed read.</summary>
    public string? Reason { get; init; }

    /// <summary>When the sessions were read.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>The role the application logs in as, whose sessions these are.</summary>
    public string? Role { get; init; }

    /// <summary>The applications, the most sessions first.</summary>
    public IReadOnlyList<ConnectionApplicationRow> Rows { get; init; } = [];

    /// <summary>How many applications there are, beyond <see cref="Rows" /> when the read was capped.</summary>
    public long ApplicationCount { get; init; }

    /// <summary>A refusal.</summary>
    public static ConnectionApplicationsView Refused(DatabaseRefusal refusal, string reason) => new() { Refusal = refusal, Reason = reason };
}
