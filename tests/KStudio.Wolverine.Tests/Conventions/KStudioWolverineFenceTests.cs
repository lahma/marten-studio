using System.Text.Json;
using System.Xml.Linq;

using KStudio;

using MartenStudio.Tests.Conventions;

namespace KStudioWolverine.Tests.Conventions;

/// <summary>
/// The adapter's fence (AGENTS.md hard rule 16, D43): KStudio.Wolverine references KStudio.Core and WolverineFx.Kafka only - no studio, no KStudio, no
/// Marten, Weasel or Npgsql - by project file, restore graph, built references and this suite's output folder. JasperFx arrives through WolverineFx,
/// so it is not forbidden here; it is forbidden in KStudio, KStudio.Core and the studios by their own fences. And KStudio.Tests must not reference the
/// adapter, so that the module's own output folder stays free of Wolverine (<c>KStudioFenceTests</c>).
/// </summary>
public class KStudioWolverineFenceTests
{
    /// <summary>What the adapter may never reference, as assembly or package name prefixes. The UI assembly <c>KStudio</c> is matched exactly below.</summary>
    private static readonly string[] ForbiddenPrefixes = ["PostgresStudio", "MartenStudio", "Marten", "Weasel", "Npgsql"];

    private static bool IsForbidden(string name) =>
        string.Equals(name, "KStudio", StringComparison.OrdinalIgnoreCase)
        || ForbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_project_file_references_KStudio_Core_and_WolverineFx_Kafka_only()
    {
        XDocument project = XDocument.Load(RepositoryRoot.Combine("src", "KStudio.Wolverine", "KStudio.Wolverine.csproj"));
        string[] references = [.. project.Descendants()
            .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(static x => x.Name.LocalName == "ProjectReference"
                ? Path.GetFileNameWithoutExtension(((string?) x.Attribute("Include") ?? string.Empty).Replace('\\', '/'))
                : (string?) x.Attribute("Include") ?? string.Empty)];

        references.Should().BeEquivalentTo("KStudio.Core", "WolverineFx.Kafka");
        references.Where(IsForbidden).Should().BeEmpty();
        project.Descendants().Where(static x => x.Name.LocalName == "FrameworkReference").Should().BeEmpty("the adapter has no ASP.NET Core");
    }

    [Fact]
    public void What_the_adapter_restores_holds_no_studio_no_Marten_family_and_no_Npgsql()
    {
        string assets = RepositoryRoot.Combine("src", "KStudio.Wolverine", "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue($"src/KStudio.Wolverine has been restored to build this suite, so {assets} must exist");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        string[] libraries = [.. document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name.Split('/')[0])];

        libraries.Should().Contain("WolverineFx.Kafka").And.Contain("KStudio.Core").And.Contain("Confluent.Kafka", "the restore graph read is the real one");
        libraries.Where(IsForbidden).Should().BeEmpty(
            "WolverineFx brings JasperFx and the Schema Registry serdes, and none of the Marten family, a studio or Npgsql");
    }

    [Fact]
    public void The_built_assembly_references_no_studio_no_KStudio_and_no_Marten_family()
    {
        string[] references = [.. typeof(KafkaWolverineExtensions).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        references.Should().Contain("KStudio.Core");
        references.Should().Contain(static x => x.StartsWith("Wolverine", StringComparison.Ordinal));
        references.Where(IsForbidden).Should().BeEmpty("KStudio.Wolverine talks to KStudio.Core and Wolverine only");
    }

    [Fact]
    public void The_JasperFx_source_generator_does_not_run_over_the_adapter()
    {
        // WolverineFx references the generator with PrivateAssets="none"; its marker attribute would put a JasperFx type on a shipped assembly.
        typeof(KafkaWolverineExtensions).Assembly.GetCustomAttributes(inherit: false)
            .Select(static x => x.GetType().FullName ?? string.Empty)
            .Should().NotContain(static x => x.Contains("JasperFx", StringComparison.Ordinal));
        File.ReadAllText(RepositoryRoot.Combine("src", "KStudio.Wolverine", "KStudio.Wolverine.csproj")).Should().Contain("KStudioWolverineWithoutTheJasperFxSourceGenerator");
    }

    [Fact]
    public void This_suites_output_folder_holds_no_studio_no_KStudio_and_no_Marten_family()
    {
        string[] files = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(static x => Path.GetFileNameWithoutExtension(x))];

        files.Should().Contain("KStudio.Wolverine").And.Contain("KStudio.Core").And.Contain("Wolverine");
        files.Where(IsForbidden).Should().BeEmpty("the output folder is the proof, at the file level, that the adapter brings no studio");
    }

    [Fact]
    public void The_modules_fast_suite_does_not_reference_the_adapter()
    {
        string project = File.ReadAllText(RepositoryRoot.Combine("tests", "KStudio.Tests", "KStudio.Tests.csproj"));

        project.Should().NotContain("KStudio.Wolverine", "its output folder is fenced against Wolverine (KStudioFenceTests)");
    }

    [Fact]
    public void Neither_the_engine_nor_the_module_references_Wolverine_but_the_engine_names_the_adapter_in_its_InternalsVisibleTo()
    {
        foreach (string name in new[] { "KStudio.Core", "KStudio" })
        {
            XDocument project = XDocument.Load(RepositoryRoot.Combine("src", name, name + ".csproj"));
            string[] references = [.. project.Descendants()
                .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
                .Select(static x => ((string?) x.Attribute("Include") ?? string.Empty).Replace('\\', '/'))];

            references.Should().NotContain(static x => x.Contains("Wolverine", StringComparison.OrdinalIgnoreCase), $"{name} knows nothing of Wolverine");
        }

        XDocument core = XDocument.Load(RepositoryRoot.Combine("src", "KStudio.Core", "KStudio.Core.csproj"));
        string[] grants = [.. core.Descendants().Where(static x => x.Name.LocalName == "InternalsVisibleTo").Select(static x => (string?) x.Attribute("Include") ?? string.Empty)];

        grants.Should().Contain("KStudio.Wolverine").And.Contain("KStudio.Wolverine.Tests");
    }
}
