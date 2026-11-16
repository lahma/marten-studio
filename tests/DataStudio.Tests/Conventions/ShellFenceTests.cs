using System.Text.Json;
using System.Xml.Linq;

using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Tests.Conventions;

/// <summary>
/// The shell's fence (AGENTS.md hard rule 16, v2 step 3): <c>DataStudio</c> references ASP.NET Core and nothing else. Until step 3 the shell was a folder
/// of <c>DataStudio.Postgres</c> and a text scan and an IL scan kept it free of the database; now it is an assembly of its own, the compiler enforces
/// what those scans enforced, and what is left to hold is the reference graph - at the project file, the restore graph, the built assembly's
/// references and this suite's output folder, which is the proof at the file level.
/// </summary>
public class ShellFenceTests
{
    /// <summary>What the shell may never reference, as package or assembly name prefixes: every driver, client and module it sits beneath.</summary>
    private static readonly string[] Forbidden =
    [
        "Npgsql", "Marten", "JasperFx", "Weasel", "Confluent", "librdkafka", "Wolverine", "DataStudio.",
    ];

    /// <summary>What a built shell assembly may reference: the framework's, by prefix.</summary>
    private static readonly string[] FrameworkPrefixes = ["Microsoft.", "System.", "netstandard", "mscorlib"];

    private static readonly System.Reflection.Assembly Shell = typeof(DataStudio.Components.StudioRoutes).Assembly;

    private static bool IsForbidden(string name) =>
        Forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void The_project_file_has_no_project_reference_and_one_package()
    {
        XDocument project = XDocument.Load(RepositoryRoot.Combine("src", "DataStudio", "DataStudio.csproj"));

        project.Descendants().Where(static x => x.Name.LocalName == "ProjectReference").Should().BeEmpty("the shell sits beneath every module");

        string[] packages = [.. project.Descendants().Where(static x => x.Name.LocalName == "PackageReference").Select(static x => (string?) x.Attribute("Include") ?? string.Empty)];

        packages.Should().Equal("Microsoft.AspNetCore.App.Internal.Assets");
        project.Descendants().Where(static x => x.Name.LocalName == "FrameworkReference").Select(static x => (string?) x.Attribute("Include"))
            .Should().Equal("Microsoft.AspNetCore.App");
    }

    [Fact]
    public void What_the_shell_restores_holds_no_driver_no_Marten_family_no_Kafka_client_and_no_module()
    {
        string assets = RepositoryRoot.Combine("src", "DataStudio", "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue($"src/DataStudio has been restored to build this suite, so {assets} must exist");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        string[] libraries = [.. document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name.Split('/')[0])];

        libraries.Should().Contain("Microsoft.AspNetCore.App.Internal.Assets", "the restore graph read is the real one");
        libraries.Where(IsForbidden).Should().BeEmpty();
    }

    [Fact]
    public void The_built_assembly_references_only_the_framework()
    {
        string[] references = [.. Shell.GetReferencedAssemblies().Select(static x => x.Name!)];

        references.Should().Contain("Microsoft.AspNetCore.Components", "the scan reads real references");
        references.Where(static x => !FrameworkPrefixes.Any(prefix => x.StartsWith(prefix, StringComparison.Ordinal)) && !x.Equals("System", StringComparison.Ordinal))
            .Should().BeEmpty("DataStudio references Microsoft.*, System.* and the runtime, and no other assembly");
        references.Where(IsForbidden).Should().BeEmpty();
    }

    /// <summary>
    /// What this suite's output folder may hold beyond the shell: the Kafka module and its engine and client, which this suite references to build a
    /// Kafka-only host (<c>KafkaOnlyHostTests</c>). Everything else the shell is fenced from stays forbidden at the file level.
    /// </summary>
    private static bool IsAllowedHere(string name) =>
        name.StartsWith("DataStudio.Kafka", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Confluent", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("librdkafka", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void This_suites_output_folder_holds_no_driver_no_Marten_family_no_Wolverine_and_no_database_module()
    {
        string[] files = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(static x => Path.GetFileNameWithoutExtension(x))];

        files.Should().Contain("DataStudio");
        files.Where(static x => IsForbidden(x) && !IsAllowedHere(x) && !x.Equals("DataStudio.Tests", StringComparison.Ordinal))
            .Should().BeEmpty("the output folder is the proof, at the file level, that the shell and the Kafka module bring none of them");
    }

    [Theory]
    [InlineData("Npgsql", true)]
    [InlineData("Marten", true)]
    [InlineData("JasperFx.Events", true)]
    [InlineData("Weasel.Postgresql", true)]
    [InlineData("Confluent.Kafka", true)]
    [InlineData("librdkafka.redist", true)]
    [InlineData("WolverineFx.Kafka", true)]
    [InlineData("DataStudio.Postgres", true)]
    [InlineData("DataStudio.Marten", true)]
    [InlineData("DataStudio.Kafka.Core", true)]
    [InlineData("DataStudio", false)]
    [InlineData("Microsoft.AspNetCore.Components", false)]
    [InlineData("System.Text.Json", false)]
    public void The_fence_names_what_it_forbids(string name, bool forbidden)
    {
        IsForbidden(name).Should().Be(forbidden);
    }
}
