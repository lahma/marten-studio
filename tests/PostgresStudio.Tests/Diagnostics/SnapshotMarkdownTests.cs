using System.Text;
using System.Text.RegularExpressions;

using PostgresStudio.Services.Diagnostics;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The Markdown report (1.2-H-B, D-HEALTH D): a reviewed baseline of the fixture, the escaping unit by unit, and an injection
/// corpus pushed through every context - names, prose, code blocks, links - with a small shape check of what comes out.
/// </summary>
public partial class SnapshotMarkdownTests
{
    private const string WordJoiner = "⁠";

    // -------------------------------------------------------------------------------------------------
    // The baseline
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public Task The_report_of_a_full_snapshot_matches_its_baseline() =>
        Verify(SnapshotMarkdown.Write(SnapshotFileReaderTests.Everything()), extension: "txt")
            .UseDirectory("../Verify")
            .UseFileName("SnapshotMarkdown_v1");

    [Fact]
    public void A_snapshot_of_a_database_that_was_not_read_is_a_report_of_why()
    {
        PostgresDiagnosticsSnapshot snapshot = DiagnosticsJsonTests.Minimal();

        string markdown = SnapshotMarkdown.Write(snapshot);

        markdown.Should().Contain("# Health report: `tenants` / `gone`")
            .And.Contain("**Outcome:** `notFound` - Data source \\'tenants\\' lists no database named \\'gone\\'\\.")
            .And.NotContain("## Findings");
    }

    [Fact]
    public void A_section_the_visitor_may_not_see_is_only_a_sentence_under_withheld_from_you()
    {
        PostgresDiagnosticsSnapshot snapshot = DiagnosticsJsonTests.Full() with
        {
            Statements = null,
            Withheld = new Dictionary<string, string> { ["statements"] = "Statements need Capabilities.ViewDatabaseActivity." },
        };

        string markdown = SnapshotMarkdown.Write(snapshot);

        markdown.Should().NotContain("## Statements");
        markdown.Should().Contain("## Withheld from you").And.Contain("- `statements`: Statements need Capabilities\\.ViewDatabaseActivity\\.");
        markdown.Should().NotContain("select d.data from public.mt_doc_order", "the report holds nothing the gated snapshot does not");
    }

    [Fact]
    public void Accepted_findings_are_apart_from_the_findings()
    {
        string markdown = SnapshotMarkdown.Write(SnapshotFileReaderTests.Everything());

        markdown.Should().Contain("## Accepted checks").And.Contain("We know");
        markdown.Split("## Accepted checks")[0].Should().NotContain("Long streams", "an accepted finding is not under Findings");
    }

    [Fact]
    public void Times_are_written_in_the_one_utc_spelling()
    {
        string markdown = SnapshotMarkdown.Write(SnapshotFileReaderTests.Everything());

        markdown.Should().Contain("2026-11-02 06:00:04 UTC");
        MarkdownText.Utc(new DateTimeOffset(2026, 11, 2, 8, 0, 4, TimeSpan.FromHours(2))).Should().Be("2026-11-02 06:00:04 UTC");
        MarkdownText.Utc((DateTimeOffset?) null).Should().Be("never");
    }

    // -------------------------------------------------------------------------------------------------
    // The escaping, unit by unit
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("plain", "`plain`")]
    [InlineData("a`b", "``a`b``")]
    [InlineData("`x`", "`` `x` ``")]
    [InlineData("a``b", "```a``b```")]
    [InlineData("a```b", "````a```b````")]
    [InlineData("a````b", "`````a````b`````")]
    [InlineData("a`````b", "``````a`````b``````")]
    [InlineData("a\r\nb", "`a  b`")]
    [InlineData("a\tb", "`a b`")]
    [InlineData("", "` `")]
    [InlineData("evil‮text", "`evil�text`")]
    [InlineData("zero​width", "`zero�width`")]
    [InlineData("bom﻿here", "`bom�here`")]
    [InlineData("ctl\u0007\u0085x", "`ctl��x`")]
    public void A_name_or_value_is_a_code_span_that_cannot_be_closed_from_inside(string value, string expected) =>
        MarkdownText.Code(value).Should().Be(expected);

    [Fact]
    public void A_pipe_in_a_table_cell_is_escaped_inside_the_code_span_too() =>
        MarkdownText.Code("a|b", inTableCell: true).Should().Be("`a\\|b`");

    [Theory]
    [InlineData("**b**", "\\*\\*b\\*\\*")]
    [InlineData("# h", "\\#\u2060 h")]
    [InlineData("#12", "\\#\u206012")]
    [InlineData("---", "\\-\\-\\-")]
    [InlineData("| a | b |", "\\| a \\| b \\|")]
    [InlineData("&amp;", "\\&amp\\;")]
    [InlineData(":smile:", "\\:\u2060smile\\:\u2060")]
    [InlineData("http://evil", "http\\:\u2060\\/\\/evil")]
    [InlineData("<script>alert(1)</script>", "\\<script\\>alert\\(1\\)\\<\\/script\\>")]
    [InlineData("<img src=x onerror=alert(1)>", "\\<img src\\=x onerror\\=alert\\(1\\)\\>")]
    [InlineData("[x](javascript:alert(1))", "\\[x\\]\\(javascript\\:\u2060alert\\(1\\)\\)")]
    [InlineData("![i](http://evil/x.png)", "\\!\\[i\\]\\(http\\:\u2060\\/\\/evil\\/x\\.png\\)")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("   lead and   trail   ", "lead and trail")]
    [InlineData("1. not a list", "1\\. not a list")]
    public void Prose_is_backslash_escaped_to_the_last_punctuation_character(string text, string expected) =>
        MarkdownText.Inline(text).Should().Be(expected);

    [Fact]
    public void An_at_sign_is_followed_by_a_word_joiner_so_a_pasted_report_notifies_nobody()
    {
        MarkdownText.Inline("@channel").Should().Be("\\@" + WordJoiner + "channel");
        MarkdownText.Inline("a@b.com").Should().Be("a\\@" + WordJoiner + "b\\.com");
    }

    [Fact]
    public void Issue_references_and_emoji_get_a_word_joiner_after_their_first_character_as_a_mention_does()
    {
        MarkdownText.Inline("see #12").Should().Be("see \\#" + WordJoiner + "12");
        MarkdownText.Inline(":smile:").Should().Be("\\:" + WordJoiner + "smile\\:" + WordJoiner);
    }

    [Fact]
    public void The_word_joiner_in_the_input_is_not_trusted()
    {
        MarkdownText.Inline("x" + WordJoiner + "@" + WordJoiner + "y").Should().NotContain("x" + WordJoiner);
    }

    [Fact]
    public void A_code_block_is_fenced_longer_than_anything_inside_it()
    {
        MarkdownText.Fence("select 1", "sql").Should().Be("```sql\nselect 1\n```");
        MarkdownText.Fence("a\n```\nb", "sql").Should().Be("````sql\na\n```\nb\n````");
        MarkdownText.Fence("a\n``````\nb", "text").Should().Be("```````text\na\n``````\nb\n```````");
    }

    [Fact]
    public void A_code_block_keeps_newlines_and_tabs_and_replaces_other_controls_and_formatting_characters()
    {
        MarkdownText.Fence("a\r\n\tb\rc\u0007d‮e", "text").Should().Be("```text\na\n\tb\nc�d�e\n```");
    }

    [Theory]
    [InlineData("https://example.test/docs#a", "[About X](<https://example.test/docs#a>)")]
    [InlineData("http://example.test/docs", "About X")]
    [InlineData("javascript:alert(1)", "About X")]
    [InlineData("https://example.test/a b", "About X")]
    [InlineData("https://example.test/<a>", "About X")]
    [InlineData("https://example.test/\u0007", "About X")]
    [InlineData("//evil/x", "About X")]
    [InlineData("", "About X")]
    public void A_link_is_written_only_for_a_plain_https_address(string url, string expected) =>
        MarkdownText.Link("About X", url).Should().Be(expected);

    // -------------------------------------------------------------------------------------------------
    // The injection corpus, in every context
    // -------------------------------------------------------------------------------------------------

    private static readonly string[] Hostile =
    [
        "<script>alert(1)</script>",
        "<img src=x onerror=alert(1)>",
        "[x](javascript:alert(1))",
        "![i](http://evil.example/x.png)",
        "**b**",
        "# h",
        "---",
        "| a | b |",
        "a`b",
        "a``b",
        "a```b",
        "a````b",
        "a`````b",
        "line1\r\nline2",
        "evil‮text",
        "zero​width",
        "@channel",
        "#12",
        "fixes #12 and :+1: :smile:",
        "&amp;",
        ":smile:",
        "http://evil",
        "_under_ ~~strike~~",
        "```\nclosing fence",
    ];

    private static PostgresDiagnosticsSnapshot Poisoned(string hostile) => DiagnosticsJsonTests.Full() with
    {
        Findings =
        [
            new PostgresFinding
            {
                Code = "T" + hostile,
                Variant = hostile,
                Subject = hostile,
                Severity = PostgresSeverity.Warning,
                Category = PostgresFindingCategory.Tables,
                Title = hostile,
                Detail = hostile,
                Caveat = hostile,
                Action = hostile,
                ActionExplanation = hostile,
                ActionKind = PostgresActionKind.Repair,
                ActionRequires = hostile,
                Evidence = [new KeyValuePair<string, string>(hostile, hostile)],
                DocsUrl = hostile,
                MeasuredAt = DiagnosticsTestFacts.At,
            },
            new PostgresFinding
            {
                Code = "C01",
                Severity = PostgresSeverity.Info,
                Category = PostgresFindingCategory.Connections,
                Title = "Accepted",
                Detail = "d",
                MeasuredAt = DiagnosticsTestFacts.At,
                Accepted = true,
                AcceptedReason = hostile,
            },
        ],
        Checks = [new PostgresCheck { Code = "T01", Title = hostile, Outcome = PostgresReadOutcome.Failed, Reason = hostile }],
        Storage = new PostgresStorageSection
        {
            Outcome = PostgresReadOutcome.Read,
            Reason = hostile,
            LargestTables = [new PostgresTableStorage { Schema = hostile, Name = hostile, TotalBytes = 10 }],
        },
        Statements = new PostgresStatementsSection
        {
            Outcome = PostgresReadOutcome.Read,
            Top = [new PostgresStatement { QueryId = 1, Text = hostile }, new PostgresStatement { QueryId = 2, TextWithheld = hostile }],
        },
        Settings = new PostgresSettingsSection
        {
            Outcome = PostgresReadOutcome.Read,
            Settings = [new PostgresSetting { Name = hostile, Value = hostile, Unit = hostile, Source = hostile }],
            Extensions = [new PostgresExtension { Name = hostile, InstalledVersion = hostile, AvailableVersion = hostile, Note = hostile }],
        },
        Connections = new PostgresConnectionsSection
        {
            Outcome = PostgresReadOutcome.Read,
            ByApplication = [new PostgresConnectionCounts { ApplicationName = hostile, Total = 1 }],
        },
        Connection = new ConnectionSnapshotSection
        {
            NpgsqlVersion = hostile,
            Platform = hostile,
            Pooler = [hostile],
            Tls = hostile,
            Settings = [new ConnectionSnapshotSetting(hostile, "c", hostile, hostile, null, null, null)],
            Observed = [new ConnectionSnapshotValue(hostile, hostile)],
            Hints = [new ConnectionSnapshotHint(hostile, hostile, hostile, null, null)],
            Applications = [new ConnectionSnapshotApplication(hostile, 1, null, 0, 0)],
            Notes = [hostile],
        },
        Withheld = new Dictionary<string, string> { [hostile] = hostile },
    };

    public static TheoryData<int> HostileIndexes
    {
        get
        {
            TheoryData<int> data = [];

            for (int i = 0; i < Hostile.Length; i++)
            {
                data.Add(i);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(HostileIndexes))]
    public void No_hostile_value_forms_markup_in_any_context(int index)
    {
        string hostile = Hostile[index];
        string markdown = SnapshotMarkdown.Write(Poisoned(hostile));

        // Nothing invisible or line-breaking survives anywhere, in a code span or a block as much as in prose.
        markdown.Should().NotContain("‮").And.NotContain("​").And.NotContain("\r");

        // Outside code - where a renderer would read markup - nothing is left to form any: a tag, a link, an image, an emphasis, a
        // heading, a table cell or a mention. Inside a code span or a fence the same characters are inert text.
        List<string> offences = Shape(markdown);

        offences.Should().BeEmpty(string.Join(Environment.NewLine, offences));
        EscapedWithoutJoiner().IsMatch(markdown).Should().BeFalse("every escaped @, # and : is followed by a word joiner, which no backslash can stand in for");
    }

    [Fact]
    public void The_corpus_in_one_report_keeps_every_table_row_and_fence_whole()
    {
        string markdown = SnapshotMarkdown.Write(Poisoned(string.Join(" ", Hostile)));

        Shape(markdown).Should().BeEmpty();
    }

    [Fact]
    public void A_docs_url_that_is_not_https_is_text_and_an_https_one_is_a_link()
    {
        PostgresDiagnosticsSnapshot snapshot = Poisoned("x") with
        {
            Findings =
            [
                DiagnosticsJsonTests.Full().Findings[0] with { DocsUrl = "http://evil.example/docs" },
                DiagnosticsJsonTests.Full().Findings[0] with { Code = "T08", Subject = "other", DocsUrl = "https://example.test/docs#t08" },
            ],
        };

        string markdown = SnapshotMarkdown.Write(snapshot);

        markdown.Should().NotContain("(<http://");
        markdown.Should().Contain("[About T08](<https://example.test/docs#t08>)");
    }

    /// <summary>
    /// The shape of a report: every table's rows have the header's cell count (counted outside code spans and escapes), every
    /// fence is closed by one of its own length, and outside code nothing but the report's own bold markers is left to form markup.
    /// </summary>
    private static List<string> Shape(string markdown)
    {
        List<string> offences = [];
        string[] lines = markdown.Split('\n');
        string? fence = null;
        int pipes = -1;

        foreach (string line in lines)
        {
            if (fence is not null)
            {
                if (line == fence)
                {
                    fence = null;
                }

                continue;
            }

            Match open = FenceLine().Match(line);

            if (open.Success)
            {
                fence = open.Groups[1].Value;
                continue;
            }

            string prose = Prose(line, offences);

            if (line.StartsWith('|'))
            {
                // On the raw row, before any code span is taken out: GitHub splits a row into cells before it reads code, so a pipe
                // that is not escaped counts even inside backticks, and a code span that forgot `inTableCell` fails here.
                int count = UnescapedPipe().Count(line);
                pipes = pipes < 0 ? count : pipes;

                if (count != pipes)
                {
                    offences.Add("row has " + count + " pipes, its table's header has " + pipes + ": " + line);
                }
            }
            else
            {
                pipes = -1;
            }

            if (prose.Contains('@', StringComparison.Ordinal))
            {
                offences.Add("a bare '@' outside code, which a chat would read as a mention: " + line);
            }

            foreach (char c in "<>[]_`~")
            {
                if (prose.Contains(c, StringComparison.Ordinal))
                {
                    offences.Add("unescaped '" + c + "' outside code: " + line);
                }
            }

            if (prose.Replace("**", string.Empty, StringComparison.Ordinal).Contains('*', StringComparison.Ordinal))
            {
                offences.Add("a stray '*' outside code: " + line);
            }

            if (line.StartsWith('#') && !HeadingLine().IsMatch(line))
            {
                offences.Add("a heading that is not one of the report's: " + line);
            }
        }

        if (fence is not null)
        {
            offences.Add("a fence is never closed");
        }

        return offences;
    }

    /// <summary>A line with its escapes and code spans taken out, as a CommonMark reader would read them: what is left is what could still form markup.</summary>
    private static string Prose(string line, List<string> offences)
    {
        StringBuilder result = new();

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (c == '\\' && i + 1 < line.Length)
            {
                i++;
                continue;
            }

            if (c != '`')
            {
                result.Append(c);
                continue;
            }

            int start = i;

            while (i < line.Length && line[i] == '`')
            {
                i++;
            }

            int length = i - start;
            int close = -1;

            for (int j = i; j < line.Length; j++)
            {
                // Backslashes are literal inside a code span, so nothing here is skipped.
                if (line[j] != '`')
                {
                    continue;
                }

                int run = j;

                while (j < line.Length && line[j] == '`')
                {
                    j++;
                }

                if (j - run == length)
                {
                    close = j;
                    break;
                }

                j--;
            }

            if (close < 0)
            {
                offences.Add("a code span is never closed: " + line);
                return result.ToString();
            }

            i = close - 1;
        }

        return result.ToString();
    }

    [GeneratedRegex(@"\\[#:@](?!\u2060)", RegexOptions.CultureInvariant)]
    private static partial Regex EscapedWithoutJoiner();

    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapedPipe();

    [GeneratedRegex("^(`{3,})(sql|text)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FenceLine();

    [GeneratedRegex(@"^#{1,4} ", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingLine();
}
