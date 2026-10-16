using System.Text.RegularExpressions;

namespace MartenStudio.Services.Database;

/// <summary>A session's statement text as one visitor is shown it, or why it is not.</summary>
/// <param name="Text">The text, masked and capped, or <see langword="null" /> when none is shown.</param>
/// <param name="Note">Why the text is cut or missing, when it is.</param>
internal sealed record SessionStatementText(string? Text, string? Note);

/// <summary>
/// Another application's statement, as Database › Sessions shows it: masked for the visitor's withheld schemas
/// (D27), never ending in half a name, and capped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Masked like Postgres' own words.</b> <c>pg_stat_activity.query</c> is somebody's source text - any
/// application on the server, any schema, the literals included - so every withheld schema's name is masked in
/// it with <see cref="PostgresErrorText.Redact" />: as a qualifier, and as every whole token that spells it,
/// whatever precedes it (a <c>set search_path to hr</c> names a schema with no dot after it). That errs
/// towards masking - a column or a word that happens to spell a withheld schema's name is masked too - and
/// leaks nothing. A name the statement reaches only through its own <c>search_path</c> is not a qualifier and is
/// shown as written: D27's known limit for source text, unchanged.
/// </para>
/// <para>
/// <b>When the visitor's withheld schemas cannot be read, there is no text.</b> The gate that knows them is
/// the database browser's, which reads the schema list; if it fails, the statement is withheld outright
/// rather than shown unmasked.
/// </para>
/// <para>
/// <b>A statement about a hidden document type is not shown at all.</b> Marten's own SQL names a document's
/// table and spells its members out - <c>select d.data from public.mt_doc_payroll as d where d.data ->> 'Salary'</c>
/// - and <c>IsDocumentTypeVisible</c> hides a type from everybody, schema or no schema. So a statement that names
/// a hidden type's table (<see cref="DatabaseObjectClassifier.MentionsHiddenTable" />) is withheld with a note
/// rather than masked; and while hiding is configured at all, so is one that names an <c>mt_doc_</c> table no
/// registered store declares - a type Marten has not learned yet may be a hidden one, and a tenant partition's
/// name carries a tenant id. When every store's declarations cannot be read and hiding is configured, nothing
/// can be checked and every statement is withheld: classification fails closed (D27).
/// </para>
/// <para>
/// <b>A cut text loses its last word.</b> Postgres keeps at most <c>track_activity_query_size</c> bytes of a
/// statement, and the studio reads at most <see cref="Internal.Sql.HealthConnectionQueries.MaxQueryCharacters" />
/// characters of that - and a text cut in the middle of a name ends in the name's first half, which no mask
/// recognises: <c>from hr_payr</c> is the start of a withheld <c>hr_payroll</c>. So a text that was cut, by
/// either, first loses its trailing identifier and any quoted identifier or string literal left open, and is
/// then masked. The display cap applies last, to text that is masked already.
/// </para>
/// </remarks>
internal static partial class SessionQueryText
{
    /// <summary>The most characters of one statement a page shows.</summary>
    public const int DisplayCharacters = 2000;

    /// <summary>What a cut text ends with.</summary>
    public const string Cut = " …";

    /// <summary>What Postgres says instead of a statement this role may not read.</summary>
    public const string InsufficientPrivilege = "<insufficient privilege>";

    /// <summary>What a statement withheld for naming a hidden document type's table says.</summary>
    public const string HiddenTypeNote = "Withheld: it names the table of a document type the studio does not show.";

    /// <summary>What a statement withheld for naming a document table nobody declares says.</summary>
    public const string UndeclaredTableNote =
        "Withheld: it names a Marten document table no registered store declares, which may be a document type the studio does not show.";

    /// <summary>What every statement says when the stores' declarations could not be read and types may be hidden.</summary>
    public const string UnclassifiedNote =
        "Withheld: the configuration of every Marten store could not be read, so it cannot be checked for document types the studio does not show.";


    /// <summary>The text one visitor is shown.</summary>
    /// <param name="query">What <c>pg_stat_activity</c> holds, as read (already cut on the server).</param>
    /// <param name="truncated">Whether Postgres or the read cut it.</param>
    /// <param name="withheld">
    /// The visitor's withheld schemas (<see cref="DatabaseGate.WithheldSchemas" />), or <see langword="null" />
    /// when they could not be read - which withholds the text.
    /// </param>
    /// <param name="classifier">
    /// The classifier over every registered store's declarations, or <see langword="null" /> when they could not all
    /// be read.
    /// </param>
    /// <param name="hidesDocumentTypes">Whether <c>MartenStudioOptions.IsDocumentTypeVisible</c> is set at all.</param>
    public static SessionStatementText Present(
        string? query,
        bool truncated,
        IReadOnlySet<string>? withheld,
        DatabaseObjectClassifier? classifier,
        bool hidesDocumentTypes)
    {
        if (string.IsNullOrEmpty(query))
        {
            return new SessionStatementText(null, null);
        }

        if (string.Equals(query, InsufficientPrivilege, StringComparison.Ordinal))
        {
            return new SessionStatementText(
                null,
                "Not readable: the studio's Postgres role is not a member of pg_read_all_stats, nor of this session's role.");
        }

        if (withheld is null)
        {
            return new SessionStatementText(
                null,
                "Withheld: the schemas it may name could not be checked against the ones you may see.");
        }

        if (classifier is null)
        {
            if (hidesDocumentTypes)
            {
                return new SessionStatementText(null, UnclassifiedNote);
            }
        }
        else if (classifier.MentionsHiddenTable(query))
        {
            return new SessionStatementText(null, HiddenTypeNote);
        }
        else if (classifier.MayHideDocumentTypes && NamesUndeclaredDocumentTable(query, classifier))
        {
            return new SessionStatementText(null, UndeclaredTableNote);
        }

        string text = truncated ? DropOpenTail(query) : query;
        string masked = PostgresErrorText.Redact(text, withheld) ?? text;
        bool capped = masked.Length > DisplayCharacters;

        if (capped)
        {
            masked = masked[..SafeCut(masked, DisplayCharacters)];
        }

        return truncated || capped
            ? new SessionStatementText(masked.TrimEnd() + Cut, "Cut: only the start of the statement is shown.")
            : new SessionStatementText(masked, null);
    }

    /// <summary>
    /// Whether <paramref name="text" /> names an <c>mt_doc_</c> table no registered store declares - qualified, in
    /// that schema; unqualified, in any schema.
    /// </summary>
    internal static bool NamesUndeclaredDocumentTable(string text, DatabaseObjectClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(classifier);

        foreach (Match match in DocumentTableName().Matches(text))
        {
            string name = match.Groups["name"].Value;
            bool declared = match.Groups["schema"].Success
                ? !classifier.IsUndeclaredDocumentTable(match.Groups["schema"].Value, name)
                : classifier.DeclaresDocumentTableNamed(name);

            if (!declared)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="text" /> without whatever a cut may have left half-written at its end: an open
    /// double-quoted identifier, an open string literal, and the identifier it ends in.
    /// </summary>
    internal static string DropOpenTail(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string result = text;

        // An odd count is an identifier or a literal the cut left open: everything from its opening quote goes,
        // because a name with a space in it ("HR Data") would otherwise leave its first word behind.
        result = CutAtOpenQuote(result, '"');
        result = CutAtOpenQuote(result, '\'');

        int end = result.Length;
        while (end > 0 && IsNameCharacter(result[end - 1]))
        {
            end--;
        }

        return result[..end];
    }

    /// <summary>A cut point at or before <paramref name="limit" /> that does not split the mask token.</summary>
    internal static int SafeCut(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit >= text.Length)
        {
            return text.Length;
        }

        // Only a token that starts before the limit and ends after it can be split by it.
        int from = Math.Max(0, limit - WithheldNames.Token.Length + 1);
        int token = text.IndexOf(WithheldNames.Token, from, StringComparison.Ordinal);

        return token >= 0 && token < limit && token + WithheldNames.Token.Length > limit ? token : limit;
    }

    private static string CutAtOpenQuote(string text, char quote)
    {
        int count = 0;
        int last = -1;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == quote)
            {
                count++;
                last = i;
            }
        }

        return count % 2 == 1 ? text[..last] : text;
    }

    /// <summary>A character that can continue a bare name: a letter, a digit, <c>_</c>, <c>$</c>, or a high character.</summary>
    private static bool IsNameCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c >= '\u0080';

    /// <summary>A document table's name in a statement: optionally qualified, either part optionally quoted.</summary>
    [GeneratedRegex(
        "(?:\"?(?<schema>[A-Za-z_][A-Za-z0-9_$]*)\"?\\s*\\.\\s*)?\"?(?<name>mt_doc_[A-Za-z0-9_$]+)\"?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DocumentTableName();
}
