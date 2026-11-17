using System.Text;
using System.Text.RegularExpressions;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// Reads the text a visitor can be shown out of the shipped source: the string literals of a C# file (concatenations joined), the markup and code of a
/// Razor file with its comments taken out, and the text files under <c>wwwroot</c> with theirs. The names of 1.x are looked for in that text only -
/// an identifier such as <c>KafkaEngineLog</c> is not text a visitor reads, and a comment is not either.
/// </summary>
internal static partial class ProductNameScan
{
    /// <summary>
    /// The 1.x names no sentence, title, credit, tile or heading says any more: the product is Data Studio, and a module is Marten, Postgres or Kafka.
    /// Both spellings of each - the product's ("Marten Studio") and the type's (<c>MartenStudio</c>) - because a message that names a type is read too.
    /// A spelled name ("Marten Studio", "Postgres Studio") is prose and is a name whatever follows it, "Marten Studios" included; a
    /// joined one (<c>MartenStudio</c>) is a name when nothing that could continue an identifier follows it (<c>MartenDescriptor</c> and
    /// <c>KStudioValue</c> are the types' and members', which a Razor file's code names and a visitor never reads), and <c>KStudio</c> must begin a
    /// word, so that nothing that merely ends in it is one.
    /// </summary>
    public static readonly Regex Names = NamesRegex();

    /// <summary>
    /// The literals that may keep one of the names, each with its reason, by <c>file</c> (<c>*</c> for any) and the exact text: a name a
    /// visitor's browser or a host has already saved, a package id or a name a protocol fixes, and nothing else. The allowed text is taken out
    /// of what is scanned, so the rest of the same literal is still read.
    /// </summary>
    public static readonly IReadOnlyList<(string File, string Literal, string Why)> Allowed =
    [
        ("src/DataStudio.Postgres/Services/Health/HealthMutes.cs", "PostgresStudio.Health.MuteKeys.v1",
            "Persisted: the data-protection purpose a mute's key is protected under, so a new name would orphan every mute a host has saved"),
        ("*", "martenStudio",
            "The browser global studio.js defines and the interop calls name (martenStudio.prefs, .clipboard, .dialog ...): one identifier on both sides, "
            + "not a sentence a visitor reads, and kept so that a host's own script that reaches it keeps working"),
        ("samples/DataStudio.Sample.Postgres.Data/RelationalDemoSchema.cs", "Marten Studio sample",
            "Persisted: the exact comment the first version of the demo put on the quartz schema, which a reused database may still carry and which "
            + "the demo matches so that it re-stamps its own schema instead of leaving it alone as a stranger's"),
    ];

    [GeneratedRegex(@"[Mm]arten Studio|Postgres Studio|(?:[Mm]artenStudio|PostgresStudio|\bKStudio)(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex NamesRegex();

    [GeneratedRegex(@"@\*.*?\*@|<!--.*?-->|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex BlockComments();

    /// <summary>The close of one literal, a <c>+</c>, and the open of the next: <c>"Marten " + "Studio"</c> is one sentence.</summary>
    [GeneratedRegex("\"\\s*\\+\\s*[$@]{0,2}\"", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Concatenation();

    /// <summary>The text files under <c>wwwroot</c> a visitor's browser is served, by extension.</summary>
    public static readonly string[] ServedExtensions = [".js", ".css", ".html", ".json", ".svg", ".txt", ".md"];

    /// <summary>The text a visitor can read in <paramref name="source" />: literals of a <c>.cs</c> file, the whole of a <c>.razor</c> file or a served file below its comments.</summary>
    /// <param name="path">The file's path, whose extension says which it is.</param>
    /// <param name="source">The file's text.</param>
    public static IEnumerable<string> TextOf(string path, string source)
    {
        if (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) || ServedExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
        {
            string markup = BlockComments().Replace(source, " ");

            yield return string.Join('\n', markup.Split('\n').Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            yield break;
        }

        foreach (string literal in Literals(Concatenation().Replace(source, string.Empty)))
        {
            yield return literal;
        }
    }

    /// <summary>The contents of every string literal of a C# source, comments skipped, an interpolated string's holes skipped.</summary>
    /// <param name="source">The source.</param>
    public static IEnumerable<string> Literals(string source)
    {
        int index = 0;

        while (index < source.Length)
        {
            char c = source[index];

            if (c == '/' && Peek(source, index + 1) == '/')
            {
                index = LineEnd(source, index);
            }
            else if (c == '/' && Peek(source, index + 1) == '*')
            {
                int end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? source.Length : end + 2;
            }
            else if (c == '\'')
            {
                index = SkipCharacter(source, index);
            }
            else if (c is '"' or '$' or '@' && StartsString(source, index))
            {
                (string? text, index) = ReadString(source, index);

                if (text is not null)
                {
                    yield return text;
                }
            }
            else
            {
                index++;
            }
        }
    }

    private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';

    private static int LineEnd(string text, int index)
    {
        int end = text.IndexOf('\n', index);

        return end < 0 ? text.Length : end + 1;
    }

    /// <summary>A character literal ('a', '\n', '\''): skipped, so that its quote does not open a string.</summary>
    private static int SkipCharacter(string text, int index)
    {
        int end = index + 1;

        while (end < text.Length && end < index + 10)
        {
            if (text[end] == '\\')
            {
                end += 2;
                continue;
            }

            if (text[end] == '\'')
            {
                return end + 1;
            }

            end++;
        }

        // An apostrophe in prose, not a literal.
        return index + 1;
    }

    private static bool StartsString(string text, int index)
    {
        int at = index;

        while (at < text.Length && text[at] is '$' or '@')
        {
            at++;
        }

        return Peek(text, at) == '"';
    }

    private static (string? Text, int Next) ReadString(string text, int start)
    {
        bool interpolated = false;
        bool verbatim = false;
        int index = start;

        while (text[index] is '$' or '@')
        {
            interpolated |= text[index] == '$';
            verbatim |= text[index] == '@';
            index++;
        }

        int quotes = 0;

        while (Peek(text, index + quotes) == '"')
        {
            quotes++;
        }

        StringBuilder result = new();

        // A raw string: three or more quotes, closed by as many.
        if (quotes >= 3)
        {
            string close = new('"', quotes);
            int end = text.IndexOf(close, index + quotes, StringComparison.Ordinal);

            return end < 0 ? (null, text.Length) : (text[(index + quotes)..end], end + quotes);
        }

        // An empty string.
        if (quotes == 2)
        {
            return (string.Empty, index + 2);
        }

        index++;

        while (index < text.Length)
        {
            char c = text[index];

            if (c == '"')
            {
                if (verbatim && Peek(text, index + 1) == '"')
                {
                    result.Append('"');
                    index += 2;
                    continue;
                }

                return (result.ToString(), index + 1);
            }

            if (c == '\\' && !verbatim)
            {
                result.Append(Peek(text, index + 1));
                index += 2;
                continue;
            }

            if (interpolated && c == '{')
            {
                if (Peek(text, index + 1) == '{')
                {
                    result.Append('{');
                    index += 2;
                    continue;
                }

                index = SkipHole(text, index);
                result.Append(' ');
                continue;
            }

            result.Append(c);
            index++;
        }

        return (null, text.Length);
    }

    /// <summary>An interpolated string's hole, <c>{expression}</c>: skipped whole, strings inside it with it.</summary>
    private static int SkipHole(string text, int start)
    {
        int depth = 0;

        for (int index = start; index < text.Length; index++)
        {
            char c = text[index];

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return index + 1;
            }
            else if (c == '"')
            {
                (_, index) = ReadString(text, index);
                index--;
            }
        }

        return text.Length;
    }
}
