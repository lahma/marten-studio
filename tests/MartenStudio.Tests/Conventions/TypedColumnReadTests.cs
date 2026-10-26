using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// API design §3.8: the studio reads through the host's own data source, whose type mappings are the host's - a NodaTime plugin,
/// <c>Npgsql.EnableLegacyTimestampBehavior</c> - so a column is read with a typed getter (<c>GetFieldValue&lt;T&gt;</c>,
/// <c>GetInt64</c>), never <c>GetValue()</c>, whose CLR type a plugin decides and a cast then fails on. Held for both SQL
/// builders' folders and for the facts readers and health checks the diagnostics API returns the results of. Reflection's
/// <c>PropertyInfo.GetValue</c> and <c>FieldInfo.GetValue</c> - a receiver named <c>…Property</c> or <c>…Field</c> - are not
/// column reads.
/// </summary>
public partial class TypedColumnReadTests
{
    /// <summary>
    /// The one read that has to take what Npgsql gives: the SQL console's grid, whose columns are whatever the visitor's
    /// statement returns, of types nobody knows until it has run. It formats each value for display and catches a type with
    /// no Npgsql mapping (<c>ReadCell</c>), so a plugin's CLR type reaches a formatter, never a cast.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["src/PostgresStudio/Internal/Sql/ReadOnlySqlSession.cs"] =
            "the SQL console's result grid reads columns of types only the visitor's statement decides, and formats them for display",
    };

    /// <summary>Whether a file is one this rule covers.</summary>
    internal static bool Covered(string relative) =>
        relative.Contains("/Internal/Sql/", StringComparison.Ordinal)
        || relative.Contains("/Services/Health/Checks/", StringComparison.Ordinal)
        || (relative.StartsWith("src/PostgresStudio/Services/", StringComparison.Ordinal)
            && (Path.GetFileName(relative).Contains("Facts", StringComparison.Ordinal) || Path.GetFileName(relative).Contains("Reader", StringComparison.Ordinal)));

    /// <summary>Every untyped column read in one file's code.</summary>
    internal static IEnumerable<string> Reads(string source) =>
        UntypedRead().Matches(SourceScanner.StripCommentsAndStrings(source))
            .Select(static x => x.Groups["receiver"].Value)
            .Where(static x => !x.EndsWith("Property", StringComparison.Ordinal) && !x.EndsWith("Field", StringComparison.Ordinal));

    [Fact]
    public void The_sql_builders_and_the_facts_readers_read_every_column_with_a_typed_getter()
    {
        string[] covered = [.. SourceScanner.ShippedSourceFiles().Select(SourceScanner.Relative).Where(Covered).Where(static x => x.EndsWith(".cs", StringComparison.Ordinal))];

        covered.Should().Contain("src/PostgresStudio/Services/Storage/StorageFacts.cs")
            .And.Contain("src/PostgresStudio/Services/Database/SessionActivityReader.cs")
            .And.Contain(static x => x.StartsWith("src/MartenStudio/Internal/Sql/", StringComparison.Ordinal))
            .And.Contain(Exempt.Keys, "an exemption names a file the rule covers");

        List<string> offences = [];

        foreach (string relative in covered)
        {
            string[] reads = [.. Reads(File.ReadAllText(RepositoryRoot.Combine([.. relative.Split('/')])))];

            if (Exempt.ContainsKey(relative))
            {
                reads.Should().NotBeEmpty(relative + " is exempt for a read it still makes; drop the exemption if it no longer does");
                continue;
            }

            offences.AddRange(reads.Select(receiver => relative + ": " + receiver + ".GetValue(...) - read the column with GetFieldValue<T> or a typed getter."));
        }

        offences.Should().BeEmpty(string.Join(" | ", offences));
    }

    [Theory]
    [InlineData("var id = reader.GetValue(0);", true)]
    [InlineData("object? x = row?.GetValue(ordinal);", true)]
    [InlineData("var x = reader.GetFieldValue<long>(0);", false)]
    [InlineData("map.TryGetValue(key, out var value);", false)]
    [InlineData("var v = DeclaredFillFactorProperty?.GetValue(graph);", false)]
    [InlineData("var v = AliasCacheField?.GetValue(graph);", false)]
    [InlineData("// reader.GetValue(0) is what this file never does\nvar s = \"reader.GetValue(0)\";", false)]
    public void The_scan_finds_an_untyped_read_and_ignores_reflection_prose_and_lookalikes(string source, bool found)
    {
        Reads(source).Any().Should().Be(found);
    }

    [GeneratedRegex(@"(?<receiver>\w+)\??\.GetValue\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex UntypedRead();
}
