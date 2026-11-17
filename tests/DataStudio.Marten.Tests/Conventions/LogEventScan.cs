using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// The log events of <c>src/</c> as the text declares them, for the two rules every assembly that owns events keeps (v2 step 6):
/// an event id is declared <b>once</b> across the whole tree, and a log template names <b>no module's product</b> - the product is
/// Data Studio, and a module is said by what it does.
/// </summary>
/// <remarks>
/// Linked into every fast test project that owns events, so each states its own folders and all of them read the same scan.
/// A <c>[LoggerMessage]</c> is found at the start of a line (never in a comment), its <c>EventId</c> and <c>Message</c> read from the
/// attribute; a direct <c>LogWarning</c>, <c>LogDebug</c> and the like is found by its call, and its message is its first string
/// literal. Both are read from the original text, because the stripped text has every literal blanked.
/// </remarks>
internal static partial class LogEventScan
{
    /// <summary>The product names a template may never say: the modules' own, as 1.x had them.</summary>
    public static readonly string[] ProductNames = ["Marten Studio", "Postgres Studio", "KStudio", "MartenStudio", "PostgresStudio"];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>One event or one direct log call, where it is and what it says.</summary>
    /// <param name="File">The file, relative to the repository root.</param>
    /// <param name="EventId">The event id, or <see langword="null" /> for a direct call.</param>
    /// <param name="Template">The message template.</param>
    public sealed record Site(string File, int? EventId, string Template);

    /// <summary>Every site of <c>src/</c>.</summary>
    public static IReadOnlyList<Site> All()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        return [.. files.SelectMany(static file => Read(SourceScanner.Relative(file), File.ReadAllText(file)))];
    }

    /// <summary>The sites of one file's text.</summary>
    public static IEnumerable<Site> Read(string relativePath, string source)
    {
        foreach (Match attribute in LoggerMessageStart().Matches(source))
        {
            int open = attribute.Index + attribute.Length;
            string arguments = Balanced(source, open);

            Match id = EventIdArgument().Match(arguments);
            int? eventId = id.Success ? int.Parse(id.Groups["id"].Value, CultureInfo.InvariantCulture) : null;

            Match message = MessageArgument().Match(arguments);
            string template = message.Success ? Literals(arguments, message.Index + message.Length) : string.Empty;

            yield return new Site(relativePath, eventId, template);
        }

        foreach (Match call in DirectCall().Matches(source))
        {
            if (IsInComment(source, call.Index))
            {
                continue;
            }

            string template = FirstLiteral(source, call.Index + call.Length);

            if (template.Length > 0)
            {
                yield return new Site(relativePath, null, template);
            }
        }
    }

    /// <summary>The text between an opening parenthesis (already consumed) and its match.</summary>
    private static string Balanced(string text, int start)
    {
        int depth = 1;
        bool inString = false;

        for (int index = start; index < text.Length; index++)
        {
            char c = text[index];

            if (inString)
            {
                if (c == '\\')
                {
                    index++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return text[start..index];
            }
        }

        return text[start..];
    }

    /// <summary>The string literals joined by <c>+</c> that start at <paramref name="start" />, as one text.</summary>
    private static string Literals(string text, int start)
    {
        StringBuilder result = new();
        int index = start;

        while (true)
        {
            while (index < text.Length && (char.IsWhiteSpace(text[index]) || text[index] == '+'))
            {
                index++;
            }

            if (index >= text.Length || text[index] != '"')
            {
                return result.ToString();
            }

            index++;

            while (index < text.Length && text[index] != '"')
            {
                if (text[index] == '\\' && index + 1 < text.Length)
                {
                    result.Append(text[index + 1]);
                    index += 2;
                }
                else
                {
                    result.Append(text[index]);
                    index++;
                }
            }

            index++;
        }
    }

    /// <summary>The first string literal of a call's arguments, or an empty text when the first argument that is not a variable holds none.</summary>
    private static string FirstLiteral(string text, int start)
    {
        int depth = 0;

        for (int index = start; index < text.Length && index < start + 600; index++)
        {
            char c = text[index];

            if (c == '"')
            {
                return Literals(text, index);
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (--depth < 0)
                {
                    return string.Empty;
                }
            }
            else if (c == ';')
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static bool IsInComment(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;

        return text.AsSpan(lineStart, index - lineStart).TrimStart().StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary><c>[LoggerMessage(</c> at the start of a line.</summary>
    [GeneratedRegex(@"^[ \t]*\[LoggerMessage\(", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex LoggerMessageStart();

    [GeneratedRegex(@"\bEventId\s*=\s*(?<id>\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex EventIdArgument();

    [GeneratedRegex(@"\bMessage\s*=\s*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex MessageArgument();

    /// <summary>A direct <c>LogWarning(</c>, <c>LogDebug(</c> and the like.</summary>
    [GeneratedRegex(@"\.\s*Log(?:Warning|Error|Debug|Information|Critical|Trace)\s*\(", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex DirectCall();

    /// <summary>Every event id declared once across <c>src/</c>, and each owned folder declaring at least one.</summary>
    /// <param name="ownedFolders">The assembly's source folders, relative to the repository root.</param>
    public static void AssertEveryIdIsDeclaredOnce(params string[] ownedFolders)
    {
        IReadOnlyList<Site> sites = All();
        Site[] events = [.. sites.Where(static x => x.EventId is not null)];

        events.Should().HaveCountGreaterThan(100, "src/ declares its events as [LoggerMessage] templates, and a scan that finds few has stopped working");

        foreach (string folder in ownedFolders)
        {
            events.Should().Contain(x => x.File.StartsWith(folder + "/", StringComparison.Ordinal), "{0} owns events, and the scan has to see them", folder);
        }

        string[] twice =
        [
            .. events.GroupBy(static x => x.EventId!.Value).Where(static x => x.Count() > 1)
                .Select(static x => x.Key.ToString(CultureInfo.InvariantCulture) + " in " + string.Join(", ", x.Select(static y => y.File))),
        ];

        twice.Should().BeEmpty("an event id names one event: it is declared once across src/, whichever assembly declares it");
    }

    /// <summary>No template or direct log message of an owned folder names a module's product.</summary>
    /// <param name="ownedFolders">The assembly's source folders, relative to the repository root.</param>
    public static void AssertNoTemplateNamesAProduct(params string[] ownedFolders)
    {
        Site[] owned = [.. All().Where(x => ownedFolders.Any(folder => x.File.StartsWith(folder + "/", StringComparison.Ordinal)))];

        owned.Should().NotBeEmpty("{0} logs, and the scan has to see it", string.Join(", ", ownedFolders));

        string[] naming =
        [
            .. owned.Where(static x => ProductNames.Any(name => x.Template.Contains(name, StringComparison.Ordinal)))
                .Select(static x => x.File + " (" + (x.EventId?.ToString(CultureInfo.InvariantCulture) ?? "direct call") + "): " + x.Template),
        ];

        naming.Should().BeEmpty("the product is Data Studio, and a log template says what happened, not which module's name it is");
    }
}
