using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Conventions;

/// <summary>
/// 1.3 removed the 1.2 AppContext switch <c>PostgresStudio.DisableNavigationInterception</c>, which restored 1.1's navigation. Nothing the
/// packages ship may read it again: a host that still sets it gets the default navigation, and a source file that names it would revive a
/// second navigation mode the browser suites no longer cover.
/// </summary>
public class RemovedNavigationSwitchTests
{
    private const string SwitchName = "PostgresStudio.DisableNavigationInterception";

    [Fact]
    public void No_source_file_names_the_removed_navigation_switch()
    {
        string root = RepositoryRoot.Combine("src");
        string[] files = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(static x => !IsBuildOutput(x))];

        files.Should().NotBeEmpty("the scan reads the real source tree");
        files.Where(static x => File.ReadAllText(x).Contains(SwitchName, StringComparison.Ordinal))
            .Select(x => Path.GetRelativePath(root, x))
            .Should().BeEmpty("the switch was removed in 1.3");
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(static x => x is "bin" or "obj");
}
