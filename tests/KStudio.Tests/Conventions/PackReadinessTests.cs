using System.Xml.Linq;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// What K8 ships (design D-CORR 10.1 and 10.2): the three KStudio projects carry their package metadata and the exact pins, are
/// packable, take their version from the one preview label in Directory.Build.props (no other project does), and the build packs
/// and pushes them after the family's three. Read as text, so a package cannot drift unseen.
/// </summary>
public class PackReadinessTests
{
    private const string AsfLine =
        "Apache Kafka is a trademark of the Apache Software Foundation. KStudio is not affiliated with or endorsed by the Apache Software Foundation.";

    public static TheoryData<string, string, string> Projects => new()
    {
        { "KStudio.Core", "kafka;blazor;studio;admin", "" },
        { "KStudio", "kafka;blazor;studio;admin", "KStudioPinsFamilyExactly" },
        { "KStudio.Wolverine", "kafka;blazor;studio;admin;wolverine", "KStudioWolverinePinsCoreExactly" },
    };

    private static readonly string[] Names = ["KStudio.Core", "KStudio", "KStudio.Wolverine"];

    private static XElement Load(string project) =>
        XDocument.Load(RepositoryRoot.Combine("src", project, project + ".csproj")).Root!;

    private static string? Property(XElement project, string name) =>
        project.Descendants().FirstOrDefault(x => x.Name.LocalName == name && x.Parent?.Name.LocalName == "PropertyGroup")?.Value;

    [Theory]
    [MemberData(nameof(Projects))]
    public void Each_project_has_its_package_metadata_and_stays_unpackable(string project, string tags, string pinTarget)
    {
        var root = Load(project);

        Property(root, "PackageId").Should().Be(project, "the package id is the assembly name, so K1's rename stays mechanical");
        Property(root, "AssemblyName").Should().Be(project);
        Property(root, "Description").Should().EndWith(AsfLine);
        Property(root, "PackageTags").Should().Be(tags);
        Property(root, "PackageReadmeFile").Should().Be("README.md");
        Property(root, "IsPackable").Should().Be("true", "K8: KStudio ships, as a preview");
        Property(root, "EnablePackageValidation").Should().Be("true");
        Property(root, "PackageValidationBaselineVersion").Should().BeNullOrEmpty(
            "the family's baseline (Directory.Build.props) is a release on nuget.org, and these ids have none yet");

        root.Descendants().Where(static x => x.Name.LocalName == "None"
                && ((string?) x.Attribute("Include"))?.Replace('\\', '/') == "../KStudio/README.md")
            .Should().ContainSingle("the readme packs from src/KStudio/README.md")
            .Which.Attribute("Pack")!.Value.Should().Be("true");

        var targets = root.Descendants().Where(static x => x.Name.LocalName == "Target")
            .Select(static x => (string?) x.Attribute("Name")).ToList();
        if (pinTarget.Length > 0)
        {
            targets.Should().Contain(pinTarget);
        }
        else
        {
            targets.Should().NotContain(static name => name != null && name.Contains("Pins"), "the engine depends on packages, not on a sibling build");
        }
    }

    [Fact]
    public void The_pin_targets_pin_the_projects_below_each_package()
    {
        var kstudio = File.ReadAllText(RepositoryRoot.Combine("src", "KStudio", "KStudio.csproj"));
        kstudio.Should().Contain("Condition=\"'%(Filename)' == 'PostgresStudio' or '%(Filename)' == 'KStudio.Core'\"");
        kstudio.Should().Contain("<ProjectVersion>[%(ProjectVersion)]</ProjectVersion>");

        var wolverine = File.ReadAllText(RepositoryRoot.Combine("src", "KStudio.Wolverine", "KStudio.Wolverine.csproj"));
        wolverine.Should().Contain("Condition=\"'%(Filename)' == 'KStudio.Core'\"");
        wolverine.Should().Contain("<ProjectVersion>[%(ProjectVersion)]</ProjectVersion>");
    }

    [Fact]
    public void The_readme_exists_and_carries_the_trademark_line()
    {
        var text = File.ReadAllText(RepositoryRoot.Combine("src", "KStudio", "README.md")).Replace("\r\n", "\n");

        text.Should().StartWith("# KStudio for Apache Kafka®");
        text.Replace("\n", " ").Should().Contain(AsfLine);
    }

    [Fact]
    public void The_build_packs_and_publishes_the_three_KStudio_projects_after_MartenStudio()
    {
        string publish = File.ReadAllText(RepositoryRoot.Combine("build", "Build.Publish.cs"));

        publish.Should().Contain("const string KStudioCorePackageId = \"KStudio.Core\";")
            .And.Contain("const string KStudioPackageId = \"KStudio\";")
            .And.Contain("const string KStudioWolverinePackageId = \"KStudio.Wolverine\";");

        // Pack's loop runs the six projects in dependency order, the three KStudio ones after the family's.
        publish.Should().MatchRegex(@"StudioProject,\s*KStudioCoreProject, KStudioProject, KStudioWolverineProject");
    }

    /// <summary>The label lives in Directory.Build.props and nowhere else; the three projects derive their version from it.</summary>
    [Fact]
    public void The_preview_label_is_one_property_the_three_projects_derive_their_version_from()
    {
        XElement props = XDocument.Load(RepositoryRoot.Combine("Directory.Build.props")).Root!;
        List<XElement> labels = [.. props.Descendants().Where(static x => x.Name.LocalName == "KStudioPrereleaseLabel")];

        labels.Should().ContainSingle("the label has exactly one home").Which.Value.Should().NotBeNullOrWhiteSpace();
        labels[0].Value.Should().MatchRegex(@"^[0-9A-Za-z]+(\.[0-9A-Za-z]+)*$", "a SemVer prerelease label");

        foreach (string project in Names)
        {
            string text = File.ReadAllText(RepositoryRoot.Combine("src", project, project + ".csproj"));

            text.Should().Contain("<PackageVersion>$(KStudioBaseVersion)-$(KStudioPrereleaseLabel)</PackageVersion>", $"{project} packs at <version>-<label>")
                .And.Contain("<InformationalVersion>$(KStudioBaseVersion)-$(KStudioPrereleaseLabel)</InformationalVersion>")
                .And.Contain("Condition=\"'$(KStudioPrereleaseLabel)' != ''\"", "an empty label packs at the family version")
                .And.Contain("<KStudioBaseVersion>$(Version)</KStudioBaseVersion>", "the CHANGELOG's version, read and never set")
                .And.NotContain("<Version>", "Version is a global property that Pack passes");
            text.Should().NotContain("<AssemblyVersion>").And.NotContain("<FileVersion>", "both stay numeric");
        }
    }

    [Fact]
    public void No_other_project_reads_the_label_or_sets_its_own_package_version()
    {
        string[] kstudio = [.. Names];
        string[] root = [RepositoryRoot.Combine("src"), RepositoryRoot.Combine("samples"), RepositoryRoot.Combine("tests"), RepositoryRoot.Combine("build")];

        foreach (string file in root.Where(Directory.Exists).SelectMany(static x => Directory.EnumerateFiles(x, "*.csproj", SearchOption.AllDirectories))
                     .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            string name = Path.GetFileNameWithoutExtension(file);

            if (kstudio.Contains(name))
            {
                continue;
            }

            string text = File.ReadAllText(file);

            text.Should().NotContain("KStudioPrereleaseLabel", $"{name}: only the three KStudio projects read the preview label");
            text.Should().NotContain("<PackageVersion>", $"{name}: the CHANGELOG is the one version authority for every other project");
        }

        foreach (string file in Directory.EnumerateFiles(RepositoryRoot.Combine("build"), "*.cs"))
        {
            // The build reads the label to name the packages; it never sets it.
            File.ReadAllText(file).Should().NotContain("SetProperty(\"KStudioPrereleaseLabel", $"{file}: the label is Directory.Build.props's alone");
        }
    }
}
