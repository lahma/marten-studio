using System.Text.Json;
using System.Xml.Linq;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The Kafka fence at MartenStudio (hard rule 16, D34): its project file, restore graph, built references and this suite's output folder hold no
/// <c>Confluent.*</c>, <c>librdkafka</c>, <c>Wolverine*</c> or <c>KStudio*</c>. A Marten host takes the Kafka module as a package of its own, never through Marten Studio.
/// </summary>
public class KafkaFenceTests
{
    private static readonly string[] KafkaFamily = ["Confluent", "librdkafka", "Wolverine", "KStudio"];

    private static bool IsKafkaFamily(string name) =>
        KafkaFamily.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_project_file_references_no_Kafka_client_no_Wolverine_and_no_KStudio()
    {
        XDocument document = XDocument.Load(RepositoryRoot.Combine("src", "MartenStudio", "MartenStudio.csproj"));
        string[] references =
        [
            .. document.Descendants()
                .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
                .Select(static x => Path.GetFileNameWithoutExtension(((string?) x.Attribute("Include") ?? string.Empty).Replace('\\', '/'))),
        ];

        references.Should().Contain("Marten").And.Contain("PostgresStudio", "the project file read is the real one");
        references.Where(IsKafkaFamily).Should().BeEmpty();
    }

    [Fact]
    public void What_MartenStudio_restores_holds_no_Kafka_client_no_Wolverine_and_no_KStudio()
    {
        string assets = RepositoryRoot.Combine("src", "MartenStudio", "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue($"{assets} is written by the restore that built this suite");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        string[] libraries = [.. document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name.Split('/')[0])];

        libraries.Should().Contain("Marten", "the graph read is the real one");
        libraries.Where(IsKafkaFamily).Should().BeEmpty();
    }

    [Fact]
    public void The_built_assembly_references_no_Kafka_client_no_Wolverine_and_no_KStudio()
    {
        string[] references = [.. typeof(MartenStudioOptions).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        references.Should().Contain("Marten");
        references.Where(IsKafkaFamily).Should().BeEmpty();
    }

    [Fact]
    public void The_test_output_folder_holds_no_Kafka_client_no_Wolverine_and_no_KStudio()
    {
        string[] assemblies = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>()];

        assemblies.Should().Contain("MartenStudio").And.Contain("Marten");
        assemblies.Where(IsKafkaFamily).Should().BeEmpty();
    }
}
