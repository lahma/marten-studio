using System.Text.Json;
using System.Xml.Linq;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Conventions;

/// <summary>
/// The other half of hard rule 16, for the Kafka module (D34): PostgresStudio and PostgresStudio.Diagnostics reference no <c>Confluent.*</c>,
/// <c>librdkafka</c>, <c>Wolverine*</c> or <c>KStudio*</c> assembly, package or project - at their project files, restore graphs, built references
/// and this suite's output folder - so a host with no Kafka never gets a Kafka client through the shell. (<c>InternalsVisibleTo</c> is an attribute,
/// not a reference, and grants the module its seams without either assembly knowing it exists.)
/// </summary>
public class PostgresStudioKafkaFenceTests
{
    private static readonly string[] KafkaFamily = ["Confluent", "librdkafka", "Wolverine", "KStudio"];

    private static bool IsKafkaFamily(string name) =>
        KafkaFamily.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("PostgresStudio.Diagnostics")]
    [InlineData("PostgresStudio")]
    public void The_project_file_references_no_Kafka_client_no_Wolverine_and_no_KStudio(string project)
    {
        XDocument document = XDocument.Load(RepositoryRoot.Combine("src", project, project + ".csproj"));
        string[] references =
        [
            .. document.Descendants()
                .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
                .Select(static x => Path.GetFileNameWithoutExtension(((string?) x.Attribute("Include") ?? string.Empty).Replace('\\', '/'))),
        ];

        references.Should().Contain(project == "PostgresStudio" ? "PostgresStudio.Diagnostics" : "Npgsql", "the project file read is the real one");
        references.Where(IsKafkaFamily).Should().BeEmpty();
    }

    [Theory]
    [InlineData("PostgresStudio.Diagnostics")]
    [InlineData("PostgresStudio")]
    public void What_the_project_restores_holds_no_Kafka_client_no_Wolverine_and_no_KStudio(string project)
    {
        string assets = RepositoryRoot.Combine("src", project, "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue($"{assets} is written by the restore that built this suite");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        string[] libraries = [.. document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name.Split('/')[0])];

        libraries.Should().Contain("Npgsql", "the graph read is the real one");
        libraries.Where(IsKafkaFamily).Should().BeEmpty();
    }

    [Fact]
    public void The_built_assemblies_reference_no_Kafka_client_no_Wolverine_and_no_KStudio()
    {
        foreach (System.Reflection.Assembly assembly in new[] { typeof(PostgresStudioOptions).Assembly, typeof(PostgresDiagnosticsOptions).Assembly })
        {
            string[] references = [.. assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

            references.Should().Contain("Npgsql");
            references.Where(IsKafkaFamily).Should().BeEmpty(assembly.GetName().Name + " runs in a host with no Kafka at all");
        }
    }

    [Fact]
    public void The_test_output_folder_holds_no_Kafka_client_no_Wolverine_and_no_KStudio()
    {
        string[] assemblies = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>()];

        assemblies.Should().Contain("PostgresStudio").And.Contain("Npgsql");
        assemblies.Where(IsKafkaFamily).Should().BeEmpty("nothing this suite references may bring a Kafka client with it");
    }
}
