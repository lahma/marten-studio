using System.Xml.Linq;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// What M4 leaves ready for K8 (design D-CORR 10.1): the three KStudio projects carry their package metadata and the exact
/// pins, stay unpackable until KStudio first ships, and the build does not pack them yet. Read as text, so a package that
/// is not packed cannot drift unseen.
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
        Property(root, "IsPackable").Should().Be("false", "nothing KStudio ships is packed before K8");

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
    public void The_build_does_not_pack_the_KStudio_projects_yet()
    {
        foreach (var file in Directory.EnumerateFiles(RepositoryRoot.Combine("build"), "*.cs"))
        {
            File.ReadAllText(file).Should().NotContain("KStudio.Core\"", $"{file}: K8's packet adds the packages to Pack and Publish")
                .And.NotContain("PackageId = \"KStudio", $"{file}: K8's packet adds the packages to Pack and Publish");
        }

        File.ReadAllText(RepositoryRoot.Combine("build", "Build.Publish.cs")).Should().NotContain("\"KStudio");
    }
}
