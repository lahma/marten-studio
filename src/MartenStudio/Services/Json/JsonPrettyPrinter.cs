using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MartenStudio.Services.Json;

/// <summary>What a run of characters in the raw view is, so the stylesheet can colour it.</summary>
internal enum JsonTokenKind
{
    /// <summary>Braces, brackets, commas, colons and the leading indent.</summary>
    Punctuation = 0,

    /// <summary>An object member name.</summary>
    Key,

    /// <summary>A string value.</summary>
    String,

    /// <summary>A number.</summary>
    Number,

    /// <summary><c>true</c> or <c>false</c>.</summary>
    Boolean,

    /// <summary><c>null</c>.</summary>
    Null,
}

/// <summary>One coloured run of text in the raw view.</summary>
/// <param name="Text">The characters, exactly as they appear.</param>
/// <param name="Kind">What they are.</param>
internal readonly record struct JsonToken(string Text, JsonTokenKind Kind);

/// <summary>One line of the raw view.</summary>
/// <param name="Number">The 1-based line number shown in the gutter.</param>
/// <param name="Tokens">The line's coloured runs, in order; concatenating their text reproduces the line.</param>
internal readonly record struct JsonLine(int Number, ImmutableArray<JsonToken> Tokens);

/// <summary>A pretty-printed document ready to render, and whatever had to be said about it.</summary>
/// <param name="Lines">The lines to show.</param>
/// <param name="Text">The full pretty-printed text, which is what the copy and download actions hand over.</param>
/// <param name="TotalLines">How many lines the document has, which is more than <paramref name="Lines"/> when truncated.</param>
/// <param name="Truncated">Whether the document was clamped.</param>
/// <param name="Notice">What to tell the reader about the clamp, or about a document that would not parse.</param>
internal sealed record JsonRawDocument(
    ImmutableArray<JsonLine> Lines,
    string Text,
    int TotalLines,
    bool Truncated,
    string? Notice)
{
    /// <summary>Nothing to show.</summary>
    public static JsonRawDocument Empty { get; } = new(ImmutableArray<JsonLine>.Empty, string.Empty, 0, false, null);
}

/// <summary>
/// Pretty-prints JSON and colours it, entirely on the server.
/// </summary>
/// <remarks>
/// There is no client-side highlighter here and there will not be one: hard rule 3 rules out a
/// third-party library, and an embeddable RCL that fetches one from a CDN is worse still. Indenting with
/// <see cref="Utf8JsonWriter"/> and then scanning the result line by line is a few dozen lines of code
/// that produce exactly the same spans, run before the markup ever leaves the server, and cannot break
/// the host's page.
/// </remarks>
internal static class JsonPrettyPrinter
{
    /// <summary>How much text survives when a document is over the raw-view byte limit.</summary>
    public const int TruncatedRawLength = 1024 * 1024;

    /// <summary>How many lines the raw view renders before clamping, so a huge document cannot flood the DOM.</summary>
    public const int DefaultMaxLines = 5_000;

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 64 };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,

        // Utf8JsonWriter defaults to Environment.NewLine, which would put CRLF into a document written on
        // Windows and LF into the same document written on Linux - a difference that would show up as a
        // diff in a round-trip dialog and as a churning golden test. The wire format is LF.
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Re-indents <paramref name="json"/>; throws <see cref="JsonException"/> if it is not JSON.</summary>
    public static string PrettyPrint(string json)
    {
        using var document = JsonDocument.Parse(json, ParseOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            document.RootElement.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Pretty-prints and colours <paramref name="json"/>, clamping what is too large to render.</summary>
    /// <param name="json">The document text.</param>
    /// <param name="maxBytes">Above this many UTF-8 bytes only the first <see cref="TruncatedRawLength"/> characters are shown, unformatted.</param>
    /// <param name="maxLines">The most lines to render.</param>
    /// <param name="headLength">How many characters survive the truncation.</param>
    public static JsonRawDocument Render(
        string? json,
        int maxBytes,
        int maxLines = DefaultMaxLines,
        int headLength = TruncatedRawLength)
    {
        if (string.IsNullOrEmpty(json))
        {
            return JsonRawDocument.Empty;
        }

        var totalBytes = Encoding.UTF8.GetByteCount(json);
        if (totalBytes > maxBytes)
        {
            var head = json.Length > headLength ? json[..headLength] : json;
            var notice = head.Length < json.Length
                ? "Showing the first " + JsonModelBuilder.FormatBytes(Encoding.UTF8.GetByteCount(head)) +
                  " of a " + JsonModelBuilder.FormatBytes(totalBytes) + " document."
                : "This document is " + JsonModelBuilder.FormatBytes(totalBytes) + ", over the " +
                  JsonModelBuilder.FormatBytes(maxBytes) + " limit, so it is shown unformatted.";

            return Clamp(PlainLines(head), head, maxLines, notice);
        }

        string pretty;
        try
        {
            pretty = PrettyPrint(json);
        }
        catch (JsonException ex)
        {
            return Clamp(PlainLines(json), json, maxLines, "This is not valid JSON: " + ex.Message);
        }

        return Clamp(Tokenize(pretty), pretty, maxLines, null);
    }

    /// <summary>Splits text into lines with no colouring, for content that did not parse.</summary>
    public static ImmutableArray<JsonLine> PlainLines(string text)
    {
        var lines = ImmutableArray.CreateBuilder<JsonLine>();
        var number = 1;
        foreach (var line in SplitLines(text))
        {
            lines.Add(new JsonLine(number, [new JsonToken(line, JsonTokenKind.Punctuation)]));
            number++;
        }

        return lines.ToImmutable();
    }

    /// <summary>Splits pretty-printed JSON into coloured lines.</summary>
    public static ImmutableArray<JsonLine> Tokenize(string prettyJson)
    {
        var lines = ImmutableArray.CreateBuilder<JsonLine>();
        var number = 1;
        foreach (var line in SplitLines(prettyJson))
        {
            lines.Add(new JsonLine(number, TokenizeLine(line)));
            number++;
        }

        return lines.ToImmutable();
    }

    private static JsonRawDocument Clamp(ImmutableArray<JsonLine> lines, string text, int maxLines, string? notice)
    {
        if (lines.Length <= maxLines)
        {
            return new JsonRawDocument(lines, text, lines.Length, notice is not null, notice);
        }

        var clampNotice = (notice is null ? string.Empty : notice + " ") +
            "Showing the first " + maxLines.ToString("N0", CultureInfo.InvariantCulture) + " of " +
            lines.Length.ToString("N0", CultureInfo.InvariantCulture) + " lines.";

        return new JsonRawDocument([.. lines.Take(maxLines)], text, lines.Length, true, clampNotice);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            yield return text[start..end];
            start = i + 1;
        }

        if (start <= text.Length - 1 || text.Length == 0)
        {
            yield return text[start..];
        }
    }

    /// <summary>
    /// Scans one line of already-indented JSON into coloured runs. A line of <see cref="Utf8JsonWriter"/>
    /// output is always self-contained - every string is escaped onto a single line - so no scanner state
    /// has to survive from one line to the next.
    /// </summary>
    private static ImmutableArray<JsonToken> TokenizeLine(string line)
    {
        if (line.Length == 0)
        {
            return ImmutableArray<JsonToken>.Empty;
        }

        var tokens = ImmutableArray.CreateBuilder<JsonToken>();
        var index = 0;

        while (index < line.Length)
        {
            var c = line[index];

            if (c == '"')
            {
                var end = EndOfString(line, index);
                var text = line[index..end];
                var isKey = IsFollowedByColon(line, end);
                tokens.Add(new JsonToken(text, isKey ? JsonTokenKind.Key : JsonTokenKind.String));
                index = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || c == '-')
            {
                var end = index;
                while (end < line.Length && (char.IsAsciiDigit(line[end]) || line[end] is '-' or '+' or '.' or 'e' or 'E'))
                {
                    end++;
                }

                tokens.Add(new JsonToken(line[index..end], JsonTokenKind.Number));
                index = end;
                continue;
            }

            if (StartsWith(line, index, "true") || StartsWith(line, index, "false"))
            {
                var length = line[index] == 't' ? 4 : 5;
                tokens.Add(new JsonToken(line.Substring(index, length), JsonTokenKind.Boolean));
                index += length;
                continue;
            }

            if (StartsWith(line, index, "null"))
            {
                tokens.Add(new JsonToken(line.Substring(index, 4), JsonTokenKind.Null));
                index += 4;
                continue;
            }

            var punctuationEnd = index;
            while (punctuationEnd < line.Length)
            {
                var p = line[punctuationEnd];
                if (p == '"' || char.IsAsciiDigit(p) || p == '-' || p is 't' or 'f' or 'n')
                {
                    break;
                }

                punctuationEnd++;
            }

            if (punctuationEnd == index)
            {
                punctuationEnd++;
            }

            tokens.Add(new JsonToken(line[index..punctuationEnd], JsonTokenKind.Punctuation));
            index = punctuationEnd;
        }

        return tokens.ToImmutable();
    }

    private static bool StartsWith(string line, int index, string literal) =>
        line.AsSpan(index).StartsWith(literal, StringComparison.Ordinal);

    private static int EndOfString(string line, int start)
    {
        var index = start + 1;
        while (index < line.Length)
        {
            var c = line[index];
            if (c == '\\')
            {
                index += 2;
                continue;
            }

            index++;
            if (c == '"')
            {
                return index;
            }
        }

        return line.Length;
    }

    private static bool IsFollowedByColon(string line, int index)
    {
        while (index < line.Length && line[index] == ' ')
        {
            index++;
        }

        return index < line.Length && line[index] == ':';
    }
}
