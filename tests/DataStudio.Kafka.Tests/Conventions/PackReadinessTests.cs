using System.Xml.Linq;

using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Kafka.Tests.Conventions;

/// <summary>
/// What the Kafka module ships (design D-CORR 10.1 and 10.2): the three Kafka projects carry their package metadata and the exact pins,
/// are packable, take the family's one version like every other project (no project sets a version or a label of its own), and the build
/// packs and pushes them after the family's four. Read as text, so a package cannot drift unseen.
/// </summary>
public class PackReadinessTests
{
    private const string AsfLine =
        "Apache Kafka is a trademark of the Apache Software Foundation. Data Studio is not affiliated with or endorsed by the Apache Software Foundation.";

    public static TheoryData<string, string, string> Projects => new()
    {
        { "DataStudio.Kafka.Core", "kafka;blazor;studio;admin", "" },
        { "DataStudio.Kafka", "kafka;blazor;studio;admin", "DataStudioKafkaPinsFamilyExactly" },
        { "DataStudio.Kafka.Wolverine", "kafka;blazor;studio;admin;wolverine", "DataStudioKafkaWolverinePinsCoreExactly" },
    };

    private static readonly string[] Names = ["DataStudio.Kafka.Core", "DataStudio.Kafka", "DataStudio.Kafka.Wolverine"];

    private static XElement Load(string project) =>
        XDocument.Load(RepositoryRoot.Combine("src", project, project + ".csproj")).Root!;

    private static string? Property(XElement project, string name) =>
        project.Descendants().FirstOrDefault(x => x.Name.LocalName == name && x.Parent?.Name.LocalName == "PropertyGroup")?.Value;

    [Theory]
    [MemberData(nameof(Projects))]
    public void Each_project_has_its_package_metadata_is_packable_and_inherits_the_validation_baseline(string project, string tags, string pinTarget)
    {
        var root = Load(project);

        Property(root, "PackageId").Should().Be(project, "the package id is the assembly name, so K1's rename stays mechanical");
        Property(root, "AssemblyName").Should().Be(project);
        Property(root, "Description").Should().EndWith(AsfLine);
        Property(root, "PackageTags").Should().Be(tags);
        Property(root, "PackageReadmeFile").Should().Be("README.md");
        Property(root, "IsPackable").Should().Be("true", "the Kafka module ships with the family");
        Property(root, "EnablePackageValidation").Should().Be("true");
        root.Descendants().Where(static x => x.Name.LocalName == "PackageValidationBaselineVersion")
            .Should().BeEmpty("the baseline is Directory.Build.props' alone, the same for all seven packages");

        root.Descendants().Where(static x => x.Name.LocalName == "None"
                && ((string?) x.Attribute("Include"))?.Replace('\\', '/') == "../DataStudio.Kafka/README.md")
            .Should().ContainSingle("the readme packs from src/DataStudio.Kafka/README.md")
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
        var kafka = File.ReadAllText(RepositoryRoot.Combine("src", "DataStudio.Kafka", "DataStudio.Kafka.csproj"));
        kafka.Should().Contain("Condition=\"'%(Filename)' == 'DataStudio' or '%(Filename)' == 'DataStudio.Kafka.Core'\"");
        kafka.Should().Contain("<ProjectVersion>[%(ProjectVersion)]</ProjectVersion>");

        var wolverine = File.ReadAllText(RepositoryRoot.Combine("src", "DataStudio.Kafka.Wolverine", "DataStudio.Kafka.Wolverine.csproj"));
        wolverine.Should().Contain("Condition=\"'%(Filename)' == 'DataStudio.Kafka.Core'\"");
        wolverine.Should().Contain("<ProjectVersion>[%(ProjectVersion)]</ProjectVersion>");
    }

    [Fact]
    public void The_readme_exists_and_carries_the_trademark_line()
    {
        var text = File.ReadAllText(RepositoryRoot.Combine("src", "DataStudio.Kafka", "README.md")).Replace("\r\n", "\n");

        text.Should().StartWith("# Data Studio for Apache Kafka®");
        text.Replace("\n", " ").Should().Contain(AsfLine);
    }

    [Fact]
    public void The_build_packs_and_publishes_the_three_Kafka_projects_after_Marten()
    {
        string publish = File.ReadAllText(RepositoryRoot.Combine("build", "Build.Publish.cs"));

        publish.Should().Contain("const string KafkaCorePackageId = \"DataStudio.Kafka.Core\";")
            .And.Contain("const string KafkaPackageId = \"DataStudio.Kafka\";")
            .And.Contain("const string KafkaWolverinePackageId = \"DataStudio.Kafka.Wolverine\";");

        // Pack's loop runs the seven projects in dependency order, the three Kafka ones after the family's.
        publish.Should().MatchRegex(@"MartenProject,\s*KafkaCoreProject, KafkaProject, KafkaWolverineProject");
    }

    /// <summary>All seven packages have one version, the CHANGELOG's: no project sets a version, a package version or a label of its own.</summary>
    [Fact]
    public void No_project_sets_its_own_version_and_no_label_exists()
    {
        string[] root = [RepositoryRoot.Combine("src"), RepositoryRoot.Combine("samples"), RepositoryRoot.Combine("tests"), RepositoryRoot.Combine("build")];

        foreach (string file in root.Where(Directory.Exists).SelectMany(static x => Directory.EnumerateFiles(x, "*.csproj", SearchOption.AllDirectories))
                     .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string text = File.ReadAllText(file);

            text.Should().NotContain("PrereleaseLabel", $"{name}: the seven packages have one version, with no label of their own");
            text.Should().NotContain("<PackageVersion>", $"{name}: the CHANGELOG is the one version authority for every project");
            text.Should().NotContain("<InformationalVersion>", $"{name}: the SDK derives it from the CHANGELOG's version");
        }

        File.ReadAllText(RepositoryRoot.Combine("Directory.Build.props")).Should().NotContain("PrereleaseLabel");

        foreach (string file in Directory.EnumerateFiles(RepositoryRoot.Combine("build"), "*.cs"))
        {
            File.ReadAllText(file).Should().NotContain("PrereleaseLabel", $"{file}: the build packs all seven packages at the CHANGELOG's version");
        }
    }
}
