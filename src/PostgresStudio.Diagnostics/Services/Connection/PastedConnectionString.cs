using System.Data.Common;

using Npgsql;

namespace PostgresStudio.Services.Connection;

/// <summary>What became of a pasted connection string.</summary>
internal enum PastedConnectionOutcome
{
    /// <summary>It was read: <see cref="PastedConnection.Settings" /> holds the redacted settings.</summary>
    Read,

    /// <summary>There was nothing but white space.</summary>
    Empty,

    /// <summary>It is longer than <see cref="PastedConnectionString.MaxCharacters" />.</summary>
    TooLong,

    /// <summary>Npgsql could not read it. Nothing more is said, because anything more can quote the text.</summary>
    Unreadable,

    /// <summary>Npgsql rejected a keyword, or the value given to one: <see cref="PastedConnection.Rejected" /> names them.</summary>
    Rejected,
}

/// <summary>A keyword Npgsql rejected: its name, and nothing it was given.</summary>
/// <param name="Keyword">The keyword as the visitor wrote it (a name that looks like one, at most 40 characters); empty when <paramref name="AfterPassword" />.</param>
/// <param name="Known">Whether Npgsql knows the keyword and refused its value (<see langword="true" />), or does not know it at all.</param>
/// <param name="AfterPassword">
/// Whether the rejected segment followed a password whose value was not quoted. A password with <c>;</c> in it, unquoted, ends at
/// the <c>;</c> and the rest of it reads as a segment of its own, so the "keyword" is password text: it is refused without a name.
/// </param>
internal sealed record PastedRejection(string Keyword, bool Known, bool AfterPassword = false);

/// <summary>A pasted connection string after the sanitiser and the reader: the settings, or why there are none.</summary>
/// <param name="Outcome">What became of it.</param>
/// <param name="Settings">The redacted settings, when <paramref name="Outcome" /> is <see cref="PastedConnectionOutcome.Read" />.</param>
/// <param name="Rejected">The keywords Npgsql rejected, by name only, when <paramref name="Outcome" /> is <see cref="PastedConnectionOutcome.Rejected" />.</param>
/// <param name="RemovedSecrets">The canonical keywords of the secrets that were in the text and were removed, never their values.</param>
internal sealed record PastedConnection(
    PastedConnectionOutcome Outcome,
    ConnectionSettings? Settings,
    IReadOnlyList<PastedRejection> Rejected,
    IReadOnlyList<string> RemovedSecrets)
{
    internal static PastedConnection Failed(PastedConnectionOutcome outcome) => new(outcome, null, [], []);
}

/// <summary>
/// Reads a connection string a visitor pasted (1.1, PASTE): takes every secret out of the text first, then hands what is left to
/// <see cref="NpgsqlSettingsReader" /> with the <see cref="ConnectionSettingOrigin.Pasted" /> provenance. Pure: it parses in memory,
/// opens no connection, reads no file, and keeps and logs nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Secrets are removed before the reader sees the text.</b> The text is cut into <c>keyword=value</c> segments (the grammar of
/// <see cref="DbConnectionStringBuilder" />, quotes included), and each segment is given to a builder of its own, which says through
/// Npgsql's own canonicalisation which keyword it really is: <c>Pwd</c>, <c>PWD</c> and <c>Password</c> are one, and no alias list is
/// kept here. A segment whose keyword the catalogue marks <see cref="NpgsqlSettingExposure.Secret" /> - or any keyword with
/// <c>password</c> in its name, for an Npgsql newer than the catalogue - is dropped. An <c>Options</c> segment is rewritten to
/// the names it sets with no value: a pasted string's option values are never shown (the data source's own are shown for a server
/// setting, which needs the server to say; a pasted string is never looked up). What is left is checked once more for a secret
/// before it is read.
/// </para>
/// <para>
/// <b>A refusal says a name and nothing else.</b> Npgsql's messages quote the value they rejected, so none is used: not for the
/// outcome, not for the reason. A keyword that does not look like a keyword (a URL's <c>postgres://user:pass@host/db?sslmode</c> parses as
/// one very odd keyword) is not echoed; the whole text is "unreadable".
/// </para>
/// </remarks>
internal static class PastedConnectionString
{
    /// <summary>
    /// The most characters read. Far past any connection string, and small enough that a message carrying it - at up to three
    /// bytes a character - is well inside a Blazor Server circuit's 32 KB receive limit.
    /// </summary>
    public const int MaxCharacters = 8_000;

    private const int MaxSegments = 120;
    private const int MaxKeywordLength = 40;
    private const int MaxRejections = 5;

    /// <summary>Reads <paramref name="text" />.</summary>
    /// <param name="text">What the visitor pasted; it is not kept.</param>
    /// <param name="context">The environment and the facts the hints need; <see cref="ConnectionReadContext.Pasted" /> is set here.</param>
    public static PastedConnection Read(string? text, ConnectionReadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(text))
        {
            return PastedConnection.Failed(PastedConnectionOutcome.Empty);
        }

        if (text.Length > MaxCharacters)
        {
            return PastedConnection.Failed(PastedConnectionOutcome.TooLong);
        }

        List<Segment>? segments = Split(text);

        if (segments is null || segments.Count > MaxSegments || segments.Any(static x => !LooksLikeKeyword(x.Key)))
        {
            return PastedConnection.Failed(PastedConnectionOutcome.Unreadable);
        }

        List<string> kept = [];
        HashSet<string> removed = new(StringComparer.OrdinalIgnoreCase);
        List<PastedRejection> rejected = [];
        NpgsqlConnectionStringBuilder known = new();

        // Set by an unquoted secret and kept through the segments refused after it: they may be the rest of the password.
        bool afterUnquotedSecret = false;

        foreach (Segment segment in segments)
        {
            string[] canonical;
            NpgsqlConnectionStringBuilder? parsed;

            try
            {
                parsed = new NpgsqlConnectionStringBuilder(segment.Raw);
                canonical = [.. ((DbConnectionStringBuilder) parsed).Keys.Cast<string>()];
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The exception is dropped unread: Npgsql's text quotes the value it refused.
                if (afterUnquotedSecret)
                {
                    // What follows a password that ended at a ';' may be the rest of the password: no name, no value.
                    if (!rejected.Exists(static x => x.AfterPassword))
                    {
                        rejected.Add(new(string.Empty, Known: false, AfterPassword: true));
                    }
                }
                else if (rejected.Count < MaxRejections && !rejected.Exists(x => string.Equals(x.Keyword, segment.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    rejected.Add(new(segment.Key, known.ContainsKey(segment.Key)));
                }

                continue;
            }

            if (canonical.Length == 0)
            {
                // An empty value (Password=; Options=) sets nothing, and Npgsql reads the string without it.
                afterUnquotedSecret = false;
                continue;
            }

            if (canonical.Length != 1)
            {
                // One segment is one keyword. Anything else is a reading of the text this class did not expect: say nothing of it.
                return PastedConnection.Failed(PastedConnectionOutcome.Unreadable);
            }

            string keyword = NpgsqlSettingsCatalog.Find(canonical[0])?.Keyword ?? canonical[0];

            afterUnquotedSecret = IsSecret(keyword) && !segment.Quoted;

            if (IsSecret(keyword))
            {
                removed.Add(keyword);
            }
            else if (NpgsqlSettingsCatalog.Find(keyword)?.Exposure == NpgsqlSettingExposure.Options)
            {
                if (parsed.Options is { Length: > 0 } options)
                {
                    kept.Add("Options='" + WithoutValues(options).Replace("'", "''", StringComparison.Ordinal) + "'");
                }
            }
            else
            {
                kept.Add(segment.Raw);
            }
        }

        if (rejected.Count > 0)
        {
            return new(PastedConnectionOutcome.Rejected, null, rejected, []);
        }

        string sanitized = string.Join(';', kept);

        // Once more, from the other side: whatever was left must hold no secret for the reader to see.
        try
        {
            if (((DbConnectionStringBuilder) new NpgsqlConnectionStringBuilder(sanitized)).Keys.Cast<string>().Any(IsSecret))
            {
                return PastedConnection.Failed(PastedConnectionOutcome.Unreadable);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return PastedConnection.Failed(PastedConnectionOutcome.Unreadable);
        }

        ConnectionSettings settings = NpgsqlSettingsReader.Read(sanitized, context with { Pasted = new PastedStrip(removed) });

        return settings.Readable
            ? new(PastedConnectionOutcome.Read, settings, [], [.. removed.Order(StringComparer.Ordinal)])
            : PastedConnection.Failed(PastedConnectionOutcome.Unreadable);
    }

    /// <summary>Whether <paramref name="keyword" /> is one whose value must not be read: the catalogue's secrets, and any new keyword named for a password.</summary>
    private static bool IsSecret(string keyword) =>
        NpgsqlSettingsCatalog.Find(keyword) is { } entry
            ? entry.Exposure == NpgsqlSettingExposure.Secret
            : keyword.Contains("password", StringComparison.OrdinalIgnoreCase);

    /// <summary>The names an <c>Options</c> value sets, each with no value, and a lone <c>-</c> where text did not parse as a setting.</summary>
    private static string WithoutValues(string options)
    {
        ConnectionOptions parsed = NpgsqlSettingsReader.ParseOptions(options, serverNames: null);
        List<string> tokens = [.. parsed.Items.Select(static x => "-c " + x.Name + "=")];

        if (parsed.OtherWithheld)
        {
            tokens.Add("-");
        }

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Whether a key could be a keyword at all: a letter, then letters, digits, spaces and a few marks, at most 40 of them. A URL, a
    /// password with no keyword in front of it or a key with a quote in it is none of these, and is never echoed.
    /// </summary>
    private static bool LooksLikeKeyword(string key)
    {
        if (key.Length is 0 or > MaxKeywordLength || !char.IsAsciiLetter(key[0]))
        {
            return false;
        }

        foreach (char c in key)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct Segment(string Key, string Raw, bool Quoted);

    /// <summary>
    /// Cuts the text into <c>keyword=value</c> segments the way <see cref="DbConnectionStringBuilder" /> reads them: separated by
    /// <c>;</c>, a value optionally in single or double quotes with the quote doubled to write it. <see langword="null" /> for text
    /// that is not of that shape - a segment without <c>=</c>, an unterminated quote, text after a closing quote.
    /// </summary>
    private static List<Segment>? Split(string text)
    {
        List<Segment> segments = [];
        int i = 0;
        int n = text.Length;

        while (true)
        {
            while (i < n && (char.IsWhiteSpace(text[i]) || text[i] == ';'))
            {
                i++;
            }

            if (i >= n)
            {
                break;
            }

            int start = i;
            int equals = -1;

            while (i < n && text[i] != ';')
            {
                if (text[i] == '=')
                {
                    equals = i;
                    break;
                }

                i++;
            }

            if (equals < 0)
            {
                return null;
            }

            string key = text[start..equals].Trim();
            i = equals + 1;

            while (i < n && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            bool quoted = i < n && text[i] is '\'' or '"';

            if (quoted)
            {
                char quote = text[i++];
                bool closed = false;

                while (i < n)
                {
                    if (text[i] == quote)
                    {
                        if (i + 1 < n && text[i + 1] == quote)
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        closed = true;
                        break;
                    }

                    i++;
                }

                if (!closed)
                {
                    return null;
                }

                while (i < n && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (i < n && text[i] != ';')
                {
                    return null;
                }
            }
            else
            {
                while (i < n && text[i] != ';')
                {
                    i++;
                }
            }

            segments.Add(new Segment(key, text[start..i].Trim(), quoted));
        }

        return segments.Count == 0 ? null : segments;
    }
}
