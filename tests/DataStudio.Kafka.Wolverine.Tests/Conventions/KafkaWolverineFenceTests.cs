using System.Text.Json;
using System.Xml.Linq;

using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Kafka.Wolverine.Tests.Conventions;

/// <summary>
/// The adapter's fence (AGENTS.md hard rule 16, D43): DataStudio.Kafka.Wolverine references DataStudio.Kafka.Core and WolverineFx.Kafka only - no studio, no KStudio, no
/// Marten, Weasel or Npgsql - by project file, restore graph, built references and this suite's output folder. JasperFx arrives through WolverineFx,
/// so it is not forbidden here; it is forbidden in KStudio, DataStudio.Kafka.Core and the studios by their own fences. And DataStudio.Kafka.Tests must not reference the
/// adapter, so that the module's own output folder stays free of Wolverine (<c>KafkaFenceTests</c>).
/// </summary>
public class KafkaWolverineFenceTests
{
    /// <summary>What the adapter may never reference, as the third-party families' name prefixes.</summary>
    private static readonly string[] ForbiddenFamilies = ["Marten", "Weasel", "Npgsql"];

    /// <summary>The studio projects it may never reference, exactly or below (REVIEW-V2-R finding 1): the database module and the engine under it, Marten Studio and the samples.</summary>
    private static readonly string[] ForbiddenDotted = ["DataStudio.Postgres", "DataStudio.Marten", "DataStudio.Sample"];

    /// <summary>The projects it may never reference, exactly: the shell, and the Kafka module that sits above the engine (its own engine, <c>DataStudio.Kafka.Core</c>, is below it).</summary>
    private static readonly string[] ForbiddenExact = ["DataStudio", "DataStudio.Kafka"];

    private static bool IsForbidden(string name) =>
        ForbiddenExact.Any(forbidden => name.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
        || ForbiddenDotted.Any(forbidden => name.Equals(forbidden, StringComparison.OrdinalIgnoreCase) || name.StartsWith(forbidden + ".", StringComparison.OrdinalIgnoreCase))
        || ForbiddenFamilies.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>One case per name the fence must catch, and the three the adapter is built on or is itself.</summary>
    [Theory]
    [InlineData("DataStudio", true)]
    [InlineData("DataStudio.Kafka", true)]
    [InlineData("DataStudio.Marten", true)]
    [InlineData("DataStudio.Postgres", true)]
    [InlineData("DataStudio.Postgres.Diagnostics", true)]
    [InlineData("DataStudio.Sample.Kafka", true)]
    [InlineData("Npgsql", true)]
    [InlineData("Weasel.Postgresql", true)]
    [InlineData("DataStudio.Kafka.Core", false)]
    [InlineData("DataStudio.Kafka.Wolverine", false)]
    [InlineData("WolverineFx.Kafka", false)]
    public void The_fence_catches_each_name_it_forbids_and_no_other(string name, bool forbidden)
    {
        IsForbidden(name).Should().Be(forbidden);
    }

    [Fact]
    public void The_project_file_references_KStudio_Core_and_WolverineFx_Kafka_only()
    {
        XDocument project = XDocument.Load(RepositoryRoot.Combine("src", "DataStudio.Kafka.Wolverine", "DataStudio.Kafka.Wolverine.csproj"));
        string[] references = [.. project.Descendants()
            .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(static x => x.Name.LocalName == "ProjectReference"
                ? Path.GetFileNameWithoutExtension(((string?) x.Attribute("Include") ?? string.Empty).Replace('\\', '/'))
                : (string?) x.Attribute("Include") ?? string.Empty)];

        references.Should().BeEquivalentTo("DataStudio.Kafka.Core", "WolverineFx.Kafka");
        references.Where(IsForbidden).Should().BeEmpty();
        project.Descendants().Where(static x => x.Name.LocalName == "FrameworkReference").Should().BeEmpty("the adapter has no ASP.NET Core");
    }

    [Fact]
    public void What_the_adapter_restores_holds_no_studio_no_Marten_family_and_no_Npgsql()
    {
        string assets = RepositoryRoot.Combine("src", "DataStudio.Kafka.Wolverine", "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue($"src/DataStudio.Kafka.Wolverine has been restored to build this suite, so {assets} must exist");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        string[] libraries = [.. document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name.Split('/')[0])];

        libraries.Should().Contain("WolverineFx.Kafka").And.Contain("DataStudio.Kafka.Core").And.Contain("Confluent.Kafka", "the restore graph read is the real one");
        libraries.Where(IsForbidden).Should().BeEmpty(
            "WolverineFx brings JasperFx and the Schema Registry serdes, and none of the Marten family, a studio or Npgsql");
    }

    [Fact]
    public void The_built_assembly_references_no_studio_no_KStudio_and_no_Marten_family()
    {
        string[] references = [.. typeof(KafkaWolverineExtensions).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        references.Should().Contain("DataStudio.Kafka.Core");
        references.Should().Contain(static x => x.StartsWith("Wolverine", StringComparison.Ordinal));
        references.Where(IsForbidden).Should().BeEmpty("DataStudio.Kafka.Wolverine talks to DataStudio.Kafka.Core and Wolverine only");
    }

    [Fact]
    public void The_JasperFx_source_generator_does_not_run_over_the_adapter()
    {
        // WolverineFx references the generator with PrivateAssets="none"; its marker attribute would put a JasperFx type on a shipped assembly.
        typeof(KafkaWolverineExtensions).Assembly.GetCustomAttributes(inherit: false)
            .Select(static x => x.GetType().FullName ?? string.Empty)
            .Should().NotContain(static x => x.Contains("JasperFx", StringComparison.Ordinal));
        File.ReadAllText(RepositoryRoot.Combine("src", "DataStudio.Kafka.Wolverine", "DataStudio.Kafka.Wolverine.csproj")).Should().Contain("KStudioWolverineWithoutTheJasperFxSourceGenerator");
    }

    [Fact]
    public void This_suites_output_folder_holds_no_studio_no_KStudio_and_no_Marten_family()
    {
        string[] files = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(static x => Path.GetFileNameWithoutExtension(x))];

        files.Should().Contain("DataStudio.Kafka.Wolverine").And.Contain("DataStudio.Kafka.Core").And.Contain("Wolverine");
        files.Where(IsForbidden).Should().BeEmpty("the output folder is the proof, at the file level, that the adapter brings no studio");
    }

    [Fact]
    public void The_modules_fast_suite_does_not_reference_the_adapter()
    {
        string project = File.ReadAllText(RepositoryRoot.Combine("tests", "DataStudio.Kafka.Tests", "DataStudio.Kafka.Tests.csproj"));

        project.Should().NotContain("DataStudio.Kafka.Wolverine", "its output folder is fenced against Wolverine (KafkaFenceTests)");
    }

    [Fact]
    public void Neither_the_engine_nor_the_module_references_Wolverine_but_the_engine_names_the_adapter_in_its_InternalsVisibleTo()
    {
        foreach (string name in new[] { "DataStudio.Kafka.Core", "DataStudio.Kafka" })
        {
            XDocument project = XDocument.Load(RepositoryRoot.Combine("src", name, name + ".csproj"));
            string[] references = [.. project.Descendants()
                .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
                .Select(static x => ((string?) x.Attribute("Include") ?? string.Empty).Replace('\\', '/'))];

            references.Should().NotContain(static x => x.Contains("Wolverine", StringComparison.OrdinalIgnoreCase), $"{name} knows nothing of Wolverine");
        }

        XDocument core = XDocument.Load(RepositoryRoot.Combine("src", "DataStudio.Kafka.Core", "DataStudio.Kafka.Core.csproj"));
        string[] grants = [.. core.Descendants().Where(static x => x.Name.LocalName == "InternalsVisibleTo").Select(static x => (string?) x.Attribute("Include") ?? string.Empty)];

        grants.Should().Contain("DataStudio.Kafka.Wolverine").And.Contain("DataStudio.Kafka.Wolverine.Tests");
    }
}
