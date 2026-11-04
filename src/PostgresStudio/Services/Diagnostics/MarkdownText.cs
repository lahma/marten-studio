using System.Globalization;
using System.Text;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// The Markdown report's escaping (1.2-H-B, D-HEALTH D3): every value from the database is untrusted text, and the report is
/// pasted into tickets, chats and wikis that render it. A name or value is a code span, prose is backslash-escaped to the last
/// punctuation character, a code block is fenced longer than anything inside it, and a link is written only for a plain
/// <c>https</c> address. Nothing here is a Markdown parser; it only makes sure a CommonMark renderer reads back the text it was
/// given and nothing else.
/// </summary>
internal static class MarkdownText
{
    /// <summary>What a control or an invisible formatting character is replaced with.</summary>
    public const char Replacement = '�';

    private const char WordJoiner = '⁠';

    /// <summary>
    /// The text with one line's worth of characters: CR, LF and tab are a space; control characters and Unicode format
    /// characters (bidirectional overrides, zero-width marks, the byte order mark) are U+FFFD; an unpaired surrogate is too.
    /// </summary>
    /// <param name="value">The text.</param>
    public static string Sanitise(string? value) => Sanitise(value, multiline: false);

    /// <summary>
    /// A name or a value as a code span: a fence of backticks one longer than the longest run inside it, a space on each side
    /// when the value starts or ends with a backtick, and - in a table cell - every <c>|</c> as <c>\|</c>, because GitHub
    /// splits a row into cells before it looks for code spans.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="inTableCell">Whether the span sits in a table cell.</param>
    public static string Code(string? value, bool inTableCell = false)
    {
        string text = Sanitise(value);

        if (text.Length == 0)
        {
            return "` `";
        }

        int longest = LongestRun(text, '`');
        string fence = new('`', longest + 1);

        // A leading and a trailing space are both stripped by a code span, so a value that has both keeps them with one more of
        // each; a value that touches a backtick needs the space so the fence does not merge with it.
        bool pad = text[0] == '`' || text[^1] == '`' || (text[0] == ' ' && text[^1] == ' ' && text.AsSpan().Trim(' ').Length > 0);
        string content = pad ? " " + text + " " : text;

        if (inTableCell)
        {
            content = content.Replace("|", "\\|", StringComparison.Ordinal);
        }

        return fence + content + fence;
    }

    /// <summary>
    /// Prose, literal: every ASCII punctuation character is backslash-escaped (CommonMark reads an escaped punctuation character
    /// as itself), so nothing forms emphasis, a link, an image, an autolink, raw HTML, a heading, a list, a table cell, an entity
    /// or an emoji. Every <c>@</c>, <c>#</c> and <c>:</c> is followed by a word joiner, because a backslash does not stop a chat's mention,
    /// issue-reference and emoji filters.
    /// Runs of white space are one space, and the ends are trimmed, so the text cannot become an indented code block.
    /// </summary>
    /// <param name="value">The text.</param>
    public static string Inline(string? value)
    {
        string text = Sanitise(value);
        StringBuilder result = new(text.Length + 8);
        bool space = false;

        foreach (char c in text)
        {
            if (c == ' ')
            {
                space = result.Length > 0;
                continue;
            }

            if (space)
            {
                result.Append(' ');
                space = false;
            }

            if (char.IsAsciiLetterOrDigit(c) || c > 0x7F)
            {
                result.Append(c);
                continue;
            }

            result.Append('\\').Append(c);

            // A backslash does not stop GitHub's mention, issue-reference and emoji filters, which read the text, so each of
            // @name, #12 and :smile: gets a word joiner after its first character.
            if (c is '@' or '#' or ':')
            {
                result.Append(WordJoiner);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// A fenced code block: <c>\n</c> newlines, control characters (but newline and tab) and format characters as U+FFFD, a fence
    /// one longer than the longest backtick run inside (three at least), and a fixed info string.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="info">The info string, a constant of the report's own: <c>sql</c> or <c>text</c>.</param>
    public static string Fence(string? text, string info)
    {
        string body = Sanitise(text, multiline: true).TrimEnd('\n');
        string fence = new('`', Math.Max(3, LongestRun(body, '`') + 1));

        return fence + info + "\n" + body + "\n" + fence;
    }

    /// <summary>
    /// <c>[label](&lt;url&gt;)</c> when <paramref name="url" /> is an absolute <c>https</c> address without white space, control
    /// characters, <c>&lt;</c> or <c>&gt;</c>; otherwise just the label.
    /// </summary>
    /// <param name="label">What the link says.</param>
    /// <param name="url">The address.</param>
    public static string Link(string label, string? url)
    {
        string text = Inline(label);

        if (url is { Length: > 0 }
            && url.All(static c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not ('<' or '>'))
            && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            return "[" + text + "](<" + url + ">)";
        }

        return text;
    }

    /// <summary>A time, the report's one spelling: <c>yyyy-MM-dd HH:mm:ss UTC</c>. Where the studio's one time spelling lives, this is its stand-in.</summary>
    /// <param name="at">The moment.</param>
    public static string Utc(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>A time, or <c>never</c>.</summary>
    /// <param name="at">The moment.</param>
    public static string Utc(DateTimeOffset? at) => at is { } value ? Utc(value) : "never";

    private static string Sanitise(string? value, bool multiline)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder result = new(value.Length);
        string text = multiline ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : value;

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\t' && multiline)
            {
                result.Append((char) rune.Value);
            }
            else if (rune.Value is '\r' or '\n' or '\t')
            {
                result.Append(' ');
            }
            else if (rune.Value == Replacement || Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                result.Append(Replacement);
            }
            else
            {
                result.Append(rune.ToString());
            }
        }

        return result.ToString();
    }

    private static int LongestRun(string text, char c)
    {
        int longest = 0;
        int run = 0;

        foreach (char each in text)
        {
            run = each == c ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        return longest;
    }
}
