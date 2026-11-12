using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// D34's words and assets: KStudio ships no static web asset and no Blazor JS initializer (D24), its stylesheet is a titled section of the one
/// stylesheet (D26) whose classes are prefixed <c>ms-k-</c>, and every class the module's markup uses has a rule there.
/// </summary>
public partial class KStudioAssetsTests
{
    private const string Header = "/* ===== KS-SHELL: the module seams (cluster selector, the Kafka capability group, the trademark line) ===== */";

    [Fact]
    public void KStudio_ships_no_wwwroot()
    {
        Directory.Exists(RepositoryRoot.Combine("src", "KStudio", "wwwroot")).Should().BeFalse(
            "StudioEndpointMapper mirrors only _content/PostgresStudio/ under the mount, so a _content/KStudio/ file would be unreachable under a path base");
    }

    [Fact]
    public void KStudio_ships_no_js_initializer_and_no_script()
    {
        string root = RepositoryRoot.Combine("src", "KStudio");
        string[] files = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))];

        files.Should().NotBeEmpty();
        files.Where(static x => x.EndsWith(".lib.module.js", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".js", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty(
            "a *.lib.module.js in a package would be loaded into every Blazor app in the process (D24), and a helper belongs in PostgresStudio's studio.js");
        File.ReadAllText(Path.Combine(root, "KStudio.csproj")).Should().NotContain("StaticWebAsset");
    }

    [Fact]
    public void The_shell_section_is_in_the_one_stylesheet_after_the_earlier_ones()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));

        css.Should().Contain(Header);
        css.IndexOf(Header, StringComparison.Ordinal).Should().BeGreaterThan(css.IndexOf("/* ===== 1.2-FINAL-FIX-4", StringComparison.Ordinal), "sections are appended in cascade order (D26)");

        string section = css[css.IndexOf(Header, StringComparison.Ordinal)..];
        ClassSelectors(section).Where(static x => !x.StartsWith("ms-k-", StringComparison.Ordinal)).Should().BeEmpty(
            "the module's classes are prefixed ms-k-; it re-declares no host class");
    }

    [Fact]
    public void Every_ms_k_class_the_markup_uses_has_a_rule_in_the_stylesheet()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));
        string[] used =
        [
            .. Directory.EnumerateFiles(RepositoryRoot.Combine("src", "KStudio"), "*.razor", SearchOption.AllDirectories)
                .SelectMany(static x => MsK().Matches(File.ReadAllText(x)).Select(static m => m.Value))
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

        used.Should().NotBeEmpty();
        used.Where(x => !css.Contains("." + x, StringComparison.Ordinal)).Should().BeEmpty("a class with no rule is dead markup");
    }

    private static IEnumerable<string> ClassSelectors(string css) =>
        ClassName().Matches(Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromSeconds(5)))
            .Select(static m => m.Groups[1].Value)
            .Where(static x => !char.IsDigit(x[0]))
            .Distinct();

    [GeneratedRegex(@"\bms-k-[a-z0-9-]+", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex MsK();

    [GeneratedRegex(@"\.([a-zA-Z][a-zA-Z0-9_-]*)(?=[\s,{:.>+~\[])", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ClassName();
}
