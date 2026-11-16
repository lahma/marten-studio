using System.Text.Json;
using System.Xml.Linq;

using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Kafka.Tests.Conventions;

/// <summary>
/// The module's fences (AGENTS.md hard rule 16): KStudio references no Marten, JasperFx, Weasel or Wolverine - by project, restore graph, built
/// references or test output folder - and its built assembly references no Confluent assembly: the client is DataStudio.Kafka.Core's (D36), and KStudio's
/// restore graph holds it only transitively.
/// </summary>
public class KStudioFenceTests
{
    private static readonly string[] ForbiddenInKStudio = ["Marten", "JasperFx", "Weasel", "Wolverine"];

    private static readonly string[] ForbiddenInKStudioAssembly = [.. ForbiddenInKStudio, "Confluent", "librdkafka"];

    [Fact]
    public void The_project_file_references_no_Marten_family_no_Wolverine_and_no_Kafka_client_of_its_own()
    {
        XDocument project = XDocument.Load(RepositoryRoot.Combine("src", "DataStudio.Kafka", "DataStudio.Kafka.csproj"));
        string[] references = [.. project.Descendants()
            .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(static x => Path.GetFileNameWithoutExtension(((string?) x.Attribute("Include") ?? string.Empty).Replace('\\', '/')))];

        references.Should().Contain("DataStudio.Kafka.Core").And.Contain("DataStudio.Postgres", "the project file read is the real one");
        references.Where(static x => StartsWithAny(x, ForbiddenInKStudioAssembly)).Should().BeEmpty(
            "the Kafka client is DataStudio.Kafka.Core's, and a Marten host's module must not bring Marten through KStudio");
    }

    [Fact]
    public void What_KStudio_restores_holds_no_Marten_family_and_no_Wolverine_and_the_client_only_transitively()
    {
        string assets = RepositoryRoot.Combine("src", "DataStudio.Kafka", "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue($"src/DataStudio.Kafka has been restored to build this suite, so {assets} must exist");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        string[] libraries = [.. document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name.Split('/')[0])];

        libraries.Where(static x => StartsWithAny(x, ForbiddenInKStudio)).Should().BeEmpty();
        libraries.Should().Contain("Confluent.Kafka", "the restore graph is read, and the client arrives through DataStudio.Kafka.Core");

        string[] direct = [.. document.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject()
            .SelectMany(static framework => framework.Value.TryGetProperty("dependencies", out JsonElement dependencies)
                ? dependencies.EnumerateObject().Select(static x => x.Name)
                : Enumerable.Empty<string>())];
        direct.Should().NotContain("Confluent.Kafka", "KStudio takes no dependency on the client of its own");
    }

    [Fact]
    public void The_built_assembly_references_no_Marten_family_no_Wolverine_and_no_Confluent()
    {
        string[] references = [.. typeof(KafkaStudioOptions).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        references.Should().Contain("DataStudio.Kafka.Core").And.Contain("DataStudio.Postgres");
        references.Where(static x => StartsWithAny(x, ForbiddenInKStudioAssembly)).Should().BeEmpty(
            "DataStudio.Kafka.dll talks to Kafka only through DataStudio.Kafka.Core, and never names a Marten type");
    }

    [Fact]
    public void The_test_output_folder_holds_no_Marten_family_and_no_Wolverine()
    {
        string[] files = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(static x => Path.GetFileNameWithoutExtension(x))];

        files.Should().Contain("DataStudio.Kafka");
        files.Where(static x => StartsWithAny(x, ForbiddenInKStudio)).Should().BeEmpty();
    }

    private static bool StartsWithAny(string name, string[] prefixes) =>
        prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
