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
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty();

        List<string> found = [];
        int texts = 0;

        foreach (string file in files)
        {
            string relative = SourceScanner.Relative(file);

            foreach (string text in ProductNameScan.TextOf(file, File.ReadAllText(file)))
            {
                texts++;

                foreach (System.Text.RegularExpressions.Match match in ProductNameScan.Names.Matches(text))
                {
                    if (!ProductNameScan.Allowed.Any(x => x.File == relative && text.Contains(x.Literal, StringComparison.Ordinal)))
                    {
                        found.Add(relative + ": " + match.Value + " in \"" + Context(text, match.Index) + "\"");
                    }
                }
            }
        }

        texts.Should().BeGreaterThan(10_000, "src/ holds thousands of literals and a scan that reads few has stopped working");
        found.Should().BeEmpty("the product is Data Studio, and a module is Marten, Postgres or Kafka");
    }

    private static string Context(string text, int at) =>
        text[Math.Max(0, at - 30)..Math.Min(text.Length, at + 50)].ReplaceLineEndings(" ");

    [Theory]
    [InlineData("string s = \"Marten Studio could not read\";", "Marten Studio")]
    [InlineData("string s = $\"{x} Postgres Studio's {y}\";", "Postgres Studio")]
    [InlineData("string s = @\"a \"\"KStudio\"\" b\";", "KStudio")]
    [InlineData("string s = \"\"\"\n    Postgres Studio says\n    \"\"\";", "Postgres Studio")]
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
    public void The_scan_reads_a_Razor_files_markup_and_not_its_comments()
    {
        string source = "@* Marten Studio in a comment *@\n<!-- KStudio -->\n<p>Postgres Studio says</p>\n@code {\n    // KStudio\n    string x = \"Marten Studio\";\n}";

        string text = string.Join('\n', ProductNameScan.TextOf("f.razor", source));

        ProductNameScan.Names.Matches(text).Select(static x => x.Value).Should().Equal("Postgres Studio", "Marten Studio");
    }
}
