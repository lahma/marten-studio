using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The shell names an icon of its own, and the icon is a file the package ships. Issue #33.
/// </summary>
/// <remarks>
/// A document that declares no icon makes the browser ask the host for <c>/favicon.ico</c>, and a host that serves none
/// answers 404 on every studio page - the host's console changed by a page the host mapped. The studio's own icon, at a
/// relative <c>href</c> like the script's (D24), keeps the host out of it. The endpoint tests prove the mirror serves it;
/// this holds the two halves they replace with a fake web root: that the file is really in the package's <c>wwwroot</c>,
/// and that the shell really asks for that file.
/// </remarks>
public partial class ShellIconTests
{
    private const string Icon = "_content/PostgresStudio/favicon.svg";

    private static readonly string IconFile = RepositoryRoot.Combine("src", StudioProjects.Shared, "wwwroot", "favicon.svg");

    [GeneratedRegex(@"<link\s+rel=""icon""[^>]*\shref=""(?<href>[^""]*)""", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex IconLink();

    [Fact]
    public void The_shell_links_the_packaged_icon_by_a_relative_address()
    {
        string shell = File.ReadAllText(RepositoryRoot.Combine("src", StudioProjects.Shared, "Components", "StudioDocument.razor"));

        Match link = IconLink().Match(shell);

        link.Success.Should().BeTrue("a document with no icon makes the browser ask the host for /favicon.ico");
        link.Groups["href"].Value.Should().Be(Icon, "relative, so it resolves against the studio-rooted <base href> to the mount path's mirror (D11, D24)");
        shell.Should().NotContain("data:image", "an icon of the studio's own, not an empty one");
    }

    [Fact]
    public void The_icon_is_a_small_hand_written_SVG_that_runs_nothing_and_loads_nothing()
    {
        File.Exists(IconFile).Should().BeTrue("the shell asks for it");

        string text = File.ReadAllText(IconFile);
        text.Length.Should().BeLessThan(1024, "a few lines of paths, not an asset");

        XDocument document = XDocument.Parse(text);
        document.Root!.Name.Should().Be(XName.Get("svg", "http://www.w3.org/2000/svg"));
        document.Root.Attribute("viewBox").Should().NotBeNull("an icon scales to the tab");

        string[] elements = [.. document.Descendants().Select(static x => x.Name.LocalName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        elements.Should().OnlyContain(static x => x == "svg" || x == "rect" || x == "g" || x == "ellipse" || x == "path",
            "shapes only: no script, no image, no style, no reference to anything outside the file; found " + string.Join(", ", elements));
        text.Should().NotContain("href", "nothing outside the file");
    }
}
