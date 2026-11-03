using System.Globalization;

namespace PostgresStudio.Services.Connection;

/// <summary>What became of a request to compare a pasted connection string.</summary>
internal enum ConnectionCompareOutcome
{
    /// <summary>The string was read and compared.</summary>
    Compared,

    /// <summary>There was nothing to compare.</summary>
    Empty,

    /// <summary>The text is longer than any connection string.</summary>
    TooLong,

    /// <summary>Npgsql could not read it.</summary>
    Unreadable,

    /// <summary>Npgsql rejected a keyword, or the value given to one.</summary>
    Rejected,

    /// <summary>The source policy refused the database as a whole.</summary>
    Refused,

    /// <summary>The source or database could not be resolved, or the current connection could not be read.</summary>
    Unavailable,
}

/// <summary>Whether a piece of advice about the pasted settings is news.</summary>
internal enum ConnectionCompareChange
{
    /// <summary>The connection in effect does not have this advice: the pasted settings bring it.</summary>
    New,

    /// <summary>The connection in effect has this advice too.</summary>
    Unchanged,
}

/// <summary>One piece of advice about the pasted settings, and whether it is news.</summary>
/// <param name="Card">The card, with an id of its own (<c>conn-cmp-…</c>) so the page's ids stay unique.</param>
/// <param name="Change">Whether the connection in effect has it too.</param>
internal sealed record ConnectionCompareCard(ConnectionCard Card, ConnectionCompareChange Change);

/// <summary>One keyword in both columns.</summary>
/// <param name="Keyword">The keyword.</param>
/// <param name="Category">Npgsql's category for it.</param>
/// <param name="Anchor">The row's element id (<c>conn-cmp-row-…</c>).</param>
/// <param name="Current">The connection in effect, or <see langword="null" /> when it could not be read.</param>
/// <param name="Pasted">The pasted settings, already redacted.</param>
/// <param name="Differs">Whether the two say something different (a password is never compared; an option by its names only).</param>
/// <param name="AdviceId">The id of the pasted advice card that is about this keyword, or <see langword="null" />.</param>
internal sealed record ConnectionCompareRow(
    string Keyword,
    string Category,
    string Anchor,
    ConnectionSettingRow? Current,
    ConnectionSettingRow Pasted,
    bool Differs,
    string? AdviceId);

/// <summary>
/// A pasted connection string compared with the one in effect (1.1, PASTE). Holds only redacted records and the names of
/// keywords: nothing in it is, or contains, any of the text that was pasted.
/// </summary>
internal sealed record ConnectionCompareView
{
    /// <summary>The refusal for text Npgsql could not read, and nothing more: anything more could quote the text.</summary>
    public const string UnreadableSentence = "Npgsql could not read this connection string.";

    /// <summary>What is asked of a visitor who pressed Compare with nothing in the box.</summary>
    public const string EmptySentence = "Paste a connection string first.";

    /// <summary>What the page says about passwords above the box.</summary>
    public const string Promise = "Passwords are removed before anything is read, and the studio keeps and logs nothing you paste.";

    /// <summary>What the disclosure's help says of the host's own logging, which the studio does not control.</summary>
    public const string LoggingNote = "If your application logs ASP.NET Core's SignalR at Debug level, that log records what the browser sends, as it does for every page.";

    /// <summary>What happened.</summary>
    public ConnectionCompareOutcome Outcome { get; init; }

    /// <summary>The sentence for any outcome but <see cref="ConnectionCompareOutcome.Compared" />.</summary>
    public string? Reason { get; init; }

    /// <summary>The keywords Npgsql rejected, by name only.</summary>
    public IReadOnlyList<PastedRejection> Rejected { get; init; } = [];

    /// <summary>The canonical keywords of the secrets that were in the text and were removed - never their values.</summary>
    public IReadOnlyList<string> RemovedSecrets { get; init; } = [];

    /// <summary>Whether the connection in effect could be read, so there is a first column.</summary>
    public bool CurrentReadable { get; init; }

    /// <summary>Every keyword, in the settings table's order.</summary>
    public IReadOnlyList<ConnectionCompareRow> Rows { get; init; } = [];

    /// <summary>The advice that counts, evaluated against the pasted settings: "if you switched to the pasted settings".</summary>
    public IReadOnlyList<ConnectionCompareCard> Cards { get; init; } = [];

    /// <summary>Context that is new with the pasted settings (a socket, which timeout cancels first).</summary>
    public IReadOnlyList<ConnectionCompareCard> Notes { get; init; } = [];

    /// <summary>The headings of the advice the connection in effect has and the pasted settings would no longer trigger.</summary>
    public IReadOnlyList<string> Resolved { get; init; } = [];

    /// <summary>The loaded Npgsql's version: both columns are read by it.</summary>
    public string NpgsqlVersion { get; init; } = string.Empty;

    /// <summary>How many keywords differ.</summary>
    public int DifferenceCount => Rows.Count(static x => x.Differs);

    /// <summary>A result that is only a sentence.</summary>
    public static ConnectionCompareView Of(ConnectionCompareOutcome outcome, string reason) => new() { Outcome = outcome, Reason = reason };

    /// <summary>The sentence for a rejected keyword or value: its name, never what was given.</summary>
    /// <param name="rejection">The rejection.</param>
    public static string Sentence(PastedRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);

        if (rejection.AfterPassword)
        {
            return "A keyword after a password could not be read: a password with ';' in it must be quoted.";
        }

        return rejection.Known
            ?"Npgsql did not accept the value given for \"" + rejection.Keyword + "\"."
            : "Npgsql does not know the keyword \"" + rejection.Keyword + "\".";
    }

    /// <summary>The sentence for text longer than <see cref="PastedConnectionString.MaxCharacters" />.</summary>
    public static string TooLongSentence() =>
        "That is more than " + PastedConnectionString.MaxCharacters.ToString("N0", CultureInfo.InvariantCulture)
        + " characters, far more than a connection string. Paste only the connection string.";
}
