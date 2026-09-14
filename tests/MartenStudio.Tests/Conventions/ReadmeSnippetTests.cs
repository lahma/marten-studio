using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Keeps the code blocks in <c>README.md</c> equal to the <c>#region</c> bodies they were taken from in
/// <c>samples/MartenStudio.Sample/Program.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// README.md is also the nuget.org package readme, so its "Register and mount" section is the first
/// code anybody sees and the only code most people will copy. A hand-maintained copy of a registration
/// rots the moment an API changes: the sample still compiles, the README still looks plausible, and the
/// first thing a new consumer types does not build. Marking the blocks and comparing them against the
/// regions makes the compiler the thing that keeps the README honest.
/// </para>
/// <para>
/// The contract is deliberately small. In README.md, a line <c>&lt;!-- snippet: name --&gt;</c>
/// introduces the next fenced <c>csharp</c> block; in Program.cs, <c>#region name</c> … <c>#endregion</c>
/// delimits the source. The two must be equal line for line once common leading indentation and
/// surrounding blank lines are removed - the README block is never indented, while a region inside a
/// method body would be, and that difference is presentation rather than content. Everything else,
/// comments included, has to match exactly: the comments in those regions explain the decisions a
/// consumer is copying and are half of what the snippet is for.
/// </para>
/// <para>
/// Unmarked <c>csharp</c> blocks are left alone on purpose. Several sections of the README show
/// illustrative code that nothing in this repository compiles - an authorization handler a host would
/// write, for instance - and each says so in the block itself.
/// </para>
/// </remarks>
public class ReadmeSnippetTests
{
    /// <summary>How many marked snippets the README must carry, so the test cannot pass vacuously.</summary>
    private const int MinimumSnippets = 2;

    private static readonly Regex SnippetBlock = new(
        """^[ \t]*<!--[ \t]*snippet:[ \t]*(?<name>[A-Za-z0-9_]+)[ \t]*-->[ \t\r]*\n```csharp[ \t\r]*\n(?<body>.*?)^```[ \t\r]*$""",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private static readonly Regex AnyCSharpFence = new(
        """^```csharp[ \t\r]*$""",
        RegexOptions.Multiline | RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_marked_readme_snippet_equals_its_region_in_the_sample()
    {
        var snippets = ReadmeSnippets();

        snippets.Count.Should().BeGreaterThanOrEqualTo(
            MinimumSnippets,
            "README.md has to carry at least {0} '<!-- snippet: name -->' blocks, or this test proves nothing",
            MinimumSnippets);

        var regions = SampleRegions();

        foreach ((string name, string body) in snippets)
        {
            regions.Should().ContainKey(
                name,
                "README.md names snippet '{0}', so samples/MartenStudio.Sample/Program.cs must have a " +
                "'#region {0}'",
                name);

            Normalize(body).Should().Be(
                Normalize(regions[name]),
                "the README block for '{0}' must be the '#region {0}' body in " +
                "samples/MartenStudio.Sample/Program.cs, character for character - a README nobody " +
                "compiles is a README that stops being true",
                name);
        }
    }

    /// <summary>
    /// The two snippets the "Register and mount" section is built from, by name, so deleting one is a
    /// failure rather than a quietly smaller test.
    /// </summary>
    [Theory]
    [InlineData("readme_register")]
    [InlineData("readme_map")]
    public void The_readme_carries_the_registration_and_the_mapping(string name)
    {
        ReadmeSnippets().Should().ContainKey(
            name,
            "the README's registration section is compiled from '{0}'",
            name);
    }

    /// <summary>
    /// The anti-vacuity half: the parser has to be able to see a block, to tell two apart, and to
    /// notice a difference the naked eye would not.
    /// </summary>
    [Fact]
    public void The_parser_reads_marked_blocks_and_only_marked_blocks()
    {
        const string markdown = """
            Prose.

            <!-- snippet: first -->
            ```csharp
            var x = 1;
            ```
            <!-- endSnippet -->

            More prose, and a block nothing marked:

            ```csharp
            var unmarked = true;
            ```

            <!-- snippet: second -->
            ```csharp
            var y = 2;
            ```
            <!-- endSnippet -->
            """;

        var parsed = Parse(markdown);

        parsed.Should().HaveCount(2, "only the two marked blocks are snippets");
        Normalize(parsed["first"]).Should().Be("var x = 1;");
        Normalize(parsed["second"]).Should().Be("var y = 2;");
        parsed["first"].Should().NotContain("```", "the body stops at the closing fence");

        AnyCSharpFence.Matches(markdown).Should().HaveCount(3, "the unmarked block is still in the text");

        Normalize("    var x = 1;\n    var y = 2;\n").Should().Be(
            Normalize("var x = 1;\nvar y = 2;"),
            "common leading indentation and surrounding blank lines are presentation, not content");
        Normalize("var x = 1;").Should().NotBe(
            Normalize("var x = 2;"),
            "a difference inside the line is a difference");
        Normalize("var x = 1;\n  var y = 2;").Should().NotBe(
            Normalize("var x = 1;\nvar y = 2;"),
            "only the indentation common to every line is removed, so relative indentation still counts");
    }

    /// <summary>
    /// The other half: the region reader has to find a region, stop at its <c>#endregion</c>, and not
    /// confuse it with the licence header's own region.
    /// </summary>
    [Fact]
    public void The_region_reader_stops_at_endregion()
    {
        const string source = """
            #region License
            // not a snippet
            #endregion

            var before = 0;

            #region sample
            var inside = 1;
            #endregion

            var after = 2;
            """;

        var regions = ReadRegions(source);

        regions.Should().ContainKey("sample").And.ContainKey("License");
        Normalize(regions["sample"]).Should().Be("var inside = 1;");
        Normalize(regions["License"]).Should().Be("// not a snippet");
    }

    private static Dictionary<string, string> ReadmeSnippets() =>
        Parse(File.ReadAllText(RepositoryRoot.Combine("README.md")));

    private static Dictionary<string, string> SampleRegions() =>
        ReadRegions(File.ReadAllText(
            RepositoryRoot.Combine("samples", "MartenStudio.Sample", "Program.cs")));

    private static Dictionary<string, string> Parse(string markdown)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in SnippetBlock.Matches(markdown))
        {
            result[match.Groups["name"].Value] = match.Groups["body"].Value;
        }

        return result;
    }

    private static Dictionary<string, string> ReadRegions(string source)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("#region ", StringComparison.Ordinal))
            {
                continue;
            }

            string name = trimmed["#region ".Length..].Trim();
            var body = new List<string>();

            // Nested regions would need a depth counter; there are none, and a region that opened one
            // would swallow the #endregion below and fail loudly rather than silently.
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Trim().StartsWith("#endregion", StringComparison.Ordinal))
                {
                    break;
                }

                body.Add(lines[j]);
            }

            result[name] = body.Count == 0 ? string.Empty : string.Join("\n", body) + "\n";
        }

        return result;
    }

    /// <summary>
    /// A block reduced to what it says: CRLF folded to LF, surrounding blank lines dropped, trailing
    /// whitespace dropped, and the indentation common to every non-blank line removed.
    /// </summary>
    private static string Normalize(string block)
    {
        List<string> lines =
        [
            .. block
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(static x => x.TrimEnd())
        ];

        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        int common = lines
            .Where(static x => x.Length > 0)
            .Select(static x => x.Length - x.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        return string.Join(
            "\n",
            lines.Select(x => x.Length >= common ? x[common..] : x));
    }
}
