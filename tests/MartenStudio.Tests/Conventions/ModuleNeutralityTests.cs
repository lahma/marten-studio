using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The text half of hard rule 16's Kafka fence (D34), as <see cref="CoreFenceTests" /> is the text half of the Marten one: no C# or Razor file under
/// <c>src/PostgresStudio/</c>, <c>src/PostgresStudio.Diagnostics/</c> or <c>src/MartenStudio/</c> names Kafka, KStudio or Confluent - in code, in a
/// comment, in markup or in a string. The shell's module seam is written in terms of "a module", and a word that names the first module would be the first
/// step of the shell knowing it. <c>studio.css</c> is exempt on purpose: the one stylesheet holds every module's titled section (D26).
/// </summary>
public partial class ModuleNeutralityTests
{
    private static readonly string[] Roots = ["PostgresStudio", "PostgresStudio.Diagnostics", "MartenStudio"];

    [Fact]
    public void No_source_file_of_the_studios_names_Kafka_KStudio_or_Confluent()
    {
        string[] files = SourceFiles();

        files.Should().HaveCountGreaterThan(200, "scanning nothing would pass for the wrong reason");

        string[] offenders =
        [
            .. files
                .Where(static x => Neutrality().IsMatch(File.ReadAllText(x)))
                .Select(SourceScanner.Relative)
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty("the shell names no module: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("a Kafka page", true)]
    [InlineData("KStudio", true)]
    [InlineData("kstudio.core", true)]
    [InlineData("Confluent.Kafka", true)]
    [InlineData("a module's page, /orders/list", false)]
    [InlineData("Marten", false)]
    public void The_scan_can_fail(string text, bool offends)
    {
        Neutrality().IsMatch(text).Should().Be(offends);
    }

    [Fact]
    public void The_stylesheet_is_the_one_place_a_module_has_a_section()
    {
        File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css")).Should().Contain("KS-SHELL");
    }

    private static string[] SourceFiles() =>
    [
        .. Roots
            .Select(static x => RepositoryRoot.Combine("src", x))
            .SelectMany(static x => Directory.EnumerateFiles(x, "*.*", SearchOption.AllDirectories))
            .Where(static x => (x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                && !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
    ];

    [GeneratedRegex("kafka|kstudio|confluent", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Neutrality();
}
