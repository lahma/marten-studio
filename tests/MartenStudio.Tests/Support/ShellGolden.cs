using System.Text;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace MartenStudio.Tests.Support;

/// <summary>
/// The golden-file support of the shell's prerendered chrome, linked into the three fast suites that verify it
/// (<c>tests/PostgresStudio.Tests/Shell</c>, <c>tests/MartenStudio.Tests/Shell</c>, <c>tests/KStudio.Tests/Shell</c>): one file, so the three
/// cut and scrub the same way.
/// </summary>
/// <remarks>
/// A page's golden text holds three fragments of its prerendered HTML, as rendered: the anonymous banner, the rail and the header. They are
/// the shell's own markup, which the host-neutral shell restructuring must leave byte for byte as it was. Only what differs between runs is
/// scrubbed: the version in the rail's credit line, Blazor's component-state and marker comments, and the time-zone picker's machine-specific options.
/// </remarks>
internal static class ShellGolden
{
    private static readonly Regex BlazorComment = new(@"<!--(?:Blazor:.*?|!)-->", RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(5));

    /// <summary>The rail's first credit line, <c>Product Name 1.2.3+sha</c>: the version is the only part that moves between builds.</summary>
    private static readonly Regex CreditVersion = new(
        @"(<span class=""ms-rail-credit-line"">[^<]*? )\d+\.\d+[^<]*(</span>)",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The time-zone picker: its selected value and its options are this machine's zones (Windows ids here, IANA ids on Linux), so they differ
    /// between the developer's machine and CI. Its element, id, class and label are kept.
    /// </summary>
    private static readonly Regex TimeZoneSelect = new(
        @"(<select id=""ms-timezone-select""[^>]*?) value=""[^""]*"">.*?</select>",
        RegexOptions.Compiled | RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    /// <summary>Starts the host and fetches <paramref name="path" />, asserting a 200, and returns the golden text of its prerendered chrome.</summary>
    public static async Task<string> GoldenAsync(WebApplication app, string path, CancellationToken cancellationToken)
    {
        await app.StartAsync(cancellationToken);
        try
        {
            using HttpClient client = app.GetTestClient();
            using HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, "{0} is a page the host serves", path);
            return Fragments(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        finally
        {
            await app.StopAsync(cancellationToken);
        }
    }

    /// <summary>The three fragments of <paramref name="html" />, scrubbed, each under a line naming it.</summary>
    public static string Fragments(string html)
    {
        StringBuilder text = new();
        text.Append("<!-- banner -->\n").Append(Scrub(Element(html, "<div class=\"ms-anonymous-banner\"", "div"))).Append("\n\n");
        text.Append("<!-- rail -->\n").Append(Scrub(Element(html, "<aside class=\"ms-sidebar\"", "aside"))).Append("\n\n");
        text.Append("<!-- header -->\n").Append(Scrub(Element(html, "<header class=\"ms-header\"", "header"))).Append('\n');
        return text.ToString();
    }

    /// <summary>The element that starts with <paramref name="start" />, through its matching close tag, nested elements of the same name counted.</summary>
    private static string Element(string html, string start, string tag)
    {
        int from = html.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThanOrEqualTo(0, "the prerendered page holds {0}", start);

        string open = "<" + tag;
        string close = "</" + tag + ">";
        int depth = 0;
        int position = from;
        while (position < html.Length)
        {
            int nextOpen = html.IndexOf(open, position, StringComparison.Ordinal);
            int nextClose = html.IndexOf(close, position, StringComparison.Ordinal);
            nextClose.Should().BeGreaterThanOrEqualTo(0, "{0} is closed", start);

            if (nextOpen >= 0 && nextOpen < nextClose && IsTagStart(html, nextOpen + open.Length))
            {
                depth++;
                position = nextOpen + open.Length;
                continue;
            }

            depth--;
            position = nextClose + close.Length;
            if (depth == 0)
            {
                return html[from..position];
            }
        }

        throw new InvalidOperationException("Unbalanced element " + start);
    }

    /// <summary>Whether the character after a tag name ends the name: <c>&lt;div</c> opens an element, <c>&lt;divider</c> does not.</summary>
    private static bool IsTagStart(string html, int index) => index < html.Length && (html[index] is ' ' or '>' or '\n' or '\t' or '/');

    private static string Scrub(string fragment)
    {
        string scrubbed = BlazorComment.Replace(fragment, string.Empty);
        scrubbed = TimeZoneSelect.Replace(scrubbed, "$1 value=\"<time zone>\"><time zone options></select>");
        return CreditVersion.Replace(scrubbed, "$1<version>$2");
    }
}
