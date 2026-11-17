using DataStudio.Marten.Services;
using DataStudio.Postgres.Services.Postgres;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// The mirror of <c>PostgresStudioWordsTests</c> for the product's name (v2 step 6): no sentence, title, credit, tile or heading says "Marten Studio",
/// "Postgres Studio" or "KStudio". The product is Data Studio, and a module is Marten, Postgres or Kafka. Held over every module's words (the Kafka
/// module's in its own suite) and over every string literal and every Razor text in <c>src/</c>.
/// </summary>
public class ProductNamesTests
{
    [Fact]
    public void No_sentence_of_the_Marten_or_the_Postgres_modules_words_says_a_removed_product_name()
    {
        string marten = WordsSnapshot.OfStudioText(MartenStudioText.Instance, "MartenOptions.ReadOnly");
        string postgres = WordsSnapshot.OfStudioText(PostgresStudioText.Instance, "PostgresOptions.ReadOnly");

        ProductNameScan.Names.Matches(marten).Select(static x => x.Value).Should().BeEmpty("the Marten module's words");
        ProductNameScan.Names.Matches(postgres).Select(static x => x.Value).Should().BeEmpty("the Postgres module's words");
    }

    [Fact]
    public void No_string_literal_or_Razor_text_of_src_says_a_removed_product_name()
    {
        string[] files =
        [
            .. SourceScanner.ShippedSourceFiles(),
            .. Directory.EnumerateFiles(RepositoryRoot.Combine("src"), "*.*", SearchOption.AllDirectories)
                .Where(static x => x.Replace('\\', '/').Contains("/wwwroot/", StringComparison.Ordinal)
                                   && !x.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal)
                                   && !x.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal)
                                   && ProductNameScan.ServedExtensions.Any(e => x.EndsWith(e, StringComparison.OrdinalIgnoreCase))),
        ];
        files.Should().NotBeEmpty();
        files.Should().Contain(static x => x.EndsWith("studio.js", StringComparison.Ordinal), "the served script is read too");
        files.Should().Contain(static x => x.EndsWith("studio.css", StringComparison.Ordinal), "and the served stylesheet");

        List<string> found = [];
        int texts = 0;

        foreach (string file in files)
        {
            string relative = SourceScanner.Relative(file);

            foreach (string literal in ProductNameScan.TextOf(file, File.ReadAllText(file)))
            {
                texts++;
                string text = literal;

                foreach ((string allowedFile, string allowed, _) in ProductNameScan.Allowed)
                {
                    if (allowedFile == "*" || allowedFile == relative)
                    {
                        text = text.Replace(allowed, " ", StringComparison.Ordinal);
                    }
                }

                foreach (System.Text.RegularExpressions.Match match in ProductNameScan.Names.Matches(text))
                {
                    found.Add(relative + ": " + match.Value + " in \"" + Context(text, match.Index) + "\"");
                }
            }
        }

        texts.Should().BeGreaterThan(10_000, "src/ holds thousands of literals and a scan that reads few has stopped working");
        string.Join('\n', found).Should().BeEmpty("the product is Data Studio, and a module is Marten, Postgres or Kafka");
    }

    private static string Context(string text, int at) =>
        text[Math.Max(0, at - 30)..Math.Min(text.Length, at + 50)].ReplaceLineEndings(" ");

    [Theory]
    [InlineData("string s = \"Marten Studio could not read\";", "Marten Studio")]
    [InlineData("string s = $\"{x} Postgres Studio's {y}\";", "Postgres Studio")]
    [InlineData("string s = @\"a \"\"KStudio\"\" b\";", "KStudio")]
    [InlineData("string s = \"\"\"\n    Postgres Studio says\n    \"\"\";", "Postgres Studio")]
    [InlineData("throw new ArgumentException(\"MartenStudio declares the objects of a store only.\");", "MartenStudio")]
    [InlineData("string s = \"PostgresStudio.Health\";", "PostgresStudio")]
    [InlineData("string s = \"Marten \" + \"Studio says\";", "Marten Studio")]
    [InlineData("string s = \"Postgres\" +\n    $\" Studio {x}\" + \"!\";", "Postgres Studio")]
    [InlineData("string s = \"a K\" + @\"Studio\";", "KStudio")]
    public void The_scan_finds_a_name_in_a_literal(string source, string name)
    {
        string text = string.Join('\n', ProductNameScan.TextOf("f.cs", source));

        ProductNameScan.Names.Matches(text).Select(static x => x.Value).Should().Equal(name);
    }

    [Theory]
    [InlineData("// Marten Studio in a comment\nint x;")]
    [InlineData("/// <summary>Postgres Studio</summary>\nclass C { }")]
    [InlineData("/* KStudio */ int x;")]
    [InlineData("KStudioLog.Started(logger); using KStudio.Core;")]
    [InlineData("char c = '\"'; string s = \"fine\"; // Marten Studio")]
    public void The_scan_ignores_a_comment_and_an_identifier(string source)
    {
        string text = string.Join('\n', ProductNameScan.TextOf("f.cs", source));

        ProductNameScan.Names.Matches(text).Should().BeEmpty();
    }

    [Fact]
    public void Every_allowed_name_says_why_and_is_still_where_it_was_allowed()
    {
        ProductNameScan.Allowed.Should().NotBeEmpty();

        foreach ((string file, string literal, string why) in ProductNameScan.Allowed)
        {
            why.Should().NotBeNullOrWhiteSpace(literal + " needs its reason");
            ProductNameScan.Names.IsMatch(literal).Should().BeTrue(literal + " is not one of the names, so it needs no entry");

            IEnumerable<string> files = file == "*"
                ? SourceScanner.ShippedSourceFiles().Concat(Directory.EnumerateFiles(RepositoryRoot.Combine("src", "DataStudio", "wwwroot"), "*.js", SearchOption.AllDirectories))
                : [RepositoryRoot.Combine(file.Split('/'))];

            files.Any(x => File.ReadAllText(x).Contains(literal, StringComparison.Ordinal)).Should().BeTrue(
                literal + " is no longer in " + file + ": delete its entry, so that the name cannot come back under it");
        }
    }

    [Theory]
    [InlineData("/* Marten Studio's helpers */\nwindow.helpers = {};\n// KStudio\n", true)]
    [InlineData("window.x = \"Postgres Studio says\";", false)]
    [InlineData("a { content: \"MartenStudio\"; }", false)]
    public void The_scan_reads_a_served_file_and_not_its_comments(string source, bool clean)
    {
        string text = string.Join('\n', ProductNameScan.TextOf("wwwroot/js/f.js", source));

        ProductNameScan.Names.IsMatch(text).Should().Be(!clean);
    }

    [Fact]
    public void The_scan_reads_a_Razor_files_markup_and_not_its_comments()
    {
        string source = "@* Marten Studio in a comment *@\n<!-- KStudio -->\n<p>Postgres Studio says</p>\n@code {\n    // KStudio\n    string x = \"Marten Studio\";\n}";

        string text = string.Join('\n', ProductNameScan.TextOf("f.razor", source));

        ProductNameScan.Names.Matches(text).Select(static x => x.Value).Should().Equal("Postgres Studio", "Marten Studio");
    }
}
