using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// The engine's fences (AGENTS.md D36, hard rule 16): KStudio.Core references no web stack, no studio, no Postgres driver,
/// no Marten family and no Wolverine; and the three studio projects reference no Kafka client, no Wolverine and no
/// KStudio.
/// </summary>
public class KStudioCoreFenceTests
{
    /// <summary>What KStudio.Core may never reference, as assembly or package name prefixes.</summary>
    private static readonly string[] ForbiddenInCore =
        ["Microsoft.AspNetCore", "Microsoft.JSInterop", "PostgresStudio", "Marten", "JasperFx", "Weasel", "Npgsql", "Wolverine"];

    /// <summary>What the shared studio, the diagnostics engine and Marten Studio may never reference.</summary>
    private static readonly string[] ForbiddenInStudios = ["Confluent", "librdkafka", "Wolverine", "KStudio"];

    [Fact]
    public void The_engine_assembly_references_no_web_stack_no_studio_and_no_Postgres_driver()
    {
        var references = Assembly.Load("KStudio.Core").GetReferencedAssemblies().Select(static x => x.Name!);

        references.Where(static name => StartsWithAny(name, ForbiddenInCore)).Should().BeEmpty(
            "KStudio.Core is the engine alone: no ASP.NET Core, no studio and no Postgres driver");
    }

    [Fact]
    public void The_engine_project_has_no_framework_reference_and_no_forbidden_reference()
    {
        var project = XDocument.Load(RepositoryRoot.Combine("src", "KStudio.Core", "KStudio.Core.csproj"));

        project.Descendants().Where(static x => x.Name.LocalName == "FrameworkReference").Should().BeEmpty(
            "KStudio.Core has no FrameworkReference: a worker host must not need ASP.NET Core to load it");
        References(project).Where(static name => StartsWithAny(name, ForbiddenInCore)).Should().BeEmpty();
        References(project).Should().Contain("Confluent.Kafka", "the project file read is the real one");
        ((string?) project.Root?.Attribute("Sdk")).Should().Be("Microsoft.NET.Sdk",
            "a Web or Razor SDK brings ASP.NET Core without a FrameworkReference line");
    }

    [Fact]
    public void What_the_engine_restores_holds_no_web_stack_no_studio_and_no_Postgres_driver()
    {
        var assets = RepositoryRoot.Combine("src", "KStudio.Core", "obj", "project.assets.json");

        File.Exists(assets).Should().BeTrue($"src/KStudio.Core has been restored to build this suite, so {assets} must exist");

        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        var libraries = document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name.Split('/')[0]).ToArray();

        libraries.Where(static name => StartsWithAny(name, ForbiddenInCore)).Should().BeEmpty(
            "the restore graph of KStudio.Core must not reach ASP.NET Core, a studio, Npgsql, the Marten family or Wolverine");
        libraries.Should().Contain("Confluent.Kafka", "the restore graph is read");

        var frameworks = document.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject()
            .SelectMany(static framework => framework.Value.TryGetProperty("frameworkReferences", out var references)
                ? references.EnumerateObject().Select(static reference => reference.Name)
                : Enumerable.Empty<string>())
            .ToArray();
        frameworks.Where(static name => StartsWithAny(name, ForbiddenInCore)).Should().BeEmpty(
            "the engine restores no ASP.NET Core framework reference, whatever imported it");
    }

    [Theory]
    [InlineData("src/PostgresStudio.Diagnostics/PostgresStudio.Diagnostics.csproj")]
    [InlineData("src/PostgresStudio/PostgresStudio.csproj")]
    [InlineData("src/MartenStudio/MartenStudio.csproj")]
    public void A_studio_project_references_no_Kafka_client_no_Wolverine_and_no_KStudio(string project)
    {
        var document = XDocument.Load(RepositoryRoot.Combine(project.Split('/')));

        References(document).Where(static name => StartsWithAny(name, ForbiddenInStudios)).Should().BeEmpty(
            "a host with no Kafka must never get a Kafka client, Wolverine or KStudio through a studio");
    }

    private static bool StartsWithAny(string name, string[] prefixes) =>
        prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> References(XDocument project) =>
        project.Descendants()
            .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(static x => x.Name.LocalName == "ProjectReference"
                ? Path.GetFileNameWithoutExtension(((string?) x.Attribute("Include") ?? "").Replace('\\', '/'))
                : (string?) x.Attribute("Include") ?? (string?) x.Attribute("Update") ?? "");
}
