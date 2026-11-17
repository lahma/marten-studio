using System.Text.Json;
using System.Xml.Linq;

using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Kafka.Tests.Conventions;

/// <summary>
/// The module's fences (AGENTS.md hard rule 16): DataStudio.Kafka references no Marten, JasperFx, Weasel or Wolverine, and - since v2 step 3, when it
/// stopped referencing the database module - no Npgsql and no <c>DataStudio.Postgres*</c> assembly either, by project, restore graph and built
/// references; and its built assembly references no Confluent assembly: the client is DataStudio.Kafka.Core's (D36), and the module's restore graph
/// holds it only transitively.
/// </summary>
/// <remarks>
/// Names are matched exactly or as a dotted prefix (REVIEW-V2-R finding 1): <c>DataStudio.Marten</c> is <c>DataStudio.Marten</c> and
/// <c>DataStudio.Marten.*</c>, never a bare <c>DataStudio</c> prefix, which the shell (<c>DataStudio</c>) and this module's own engine
/// (<c>DataStudio.Kafka.Core</c>) satisfy. The third-party families (Marten, JasperFx, Weasel, Wolverine, Npgsql, Confluent) stay prefixes, as their
/// packages are named by them.
/// </remarks>
public class KStudioFenceTests
{
    /// <summary>The assemblies and packages the module must never reference, exactly or as a dotted prefix.</summary>
    private static readonly string[] ForbiddenNames =
    [
        "DataStudio.Marten", "DataStudio.Postgres", "DataStudio.Postgres.Diagnostics", "DataStudio.Kafka.Wolverine", "DataStudio.Sample",
    ];

    /// <summary>The third-party families the module must never reference, by the prefix their packages and assemblies share.</summary>
    private static readonly string[] ForbiddenFamilies = ["Marten", "JasperFx", "Weasel", "Wolverine", "Npgsql"];

    /// <summary>What the built assembly may not name either: the Kafka client, which only the engine references.</summary>
    private static readonly string[] ForbiddenInAssembly = ["Confluent", "librdkafka"];

    private static bool IsForbidden(string name) =>
        ForbiddenNames.Any(forbidden => IsExactlyOrBelow(name, forbidden))
        || ForbiddenFamilies.Any(family => name.StartsWith(family, StringComparison.OrdinalIgnoreCase));

    private static bool IsForbiddenInAssembly(string name) =>
        IsForbidden(name) || ForbiddenInAssembly.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsExactlyOrBelow(string name, string dotted) =>
        name.Equals(dotted, StringComparison.OrdinalIgnoreCase) || name.StartsWith(dotted + ".", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_project_file_references_the_shell_and_the_engine_and_no_database_module_Marten_Wolverine_or_Kafka_client_of_its_own()
    {
        XDocument project = XDocument.Load(RepositoryRoot.Combine("src", "DataStudio.Kafka", "DataStudio.Kafka.csproj"));
        string[] references = [.. project.Descendants()
            .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(static x => Path.GetFileNameWithoutExtension(((string?) x.Attribute("Include") ?? string.Empty).Replace('\\', '/')))];

        references.Should().Contain("DataStudio.Kafka.Core").And.Contain("DataStudio", "the project file read is the real one");
        references.Where(IsForbiddenInAssembly).Should().BeEmpty(
            "the Kafka client is DataStudio.Kafka.Core's, the module joins a studio through the shell alone, and a Marten host's module must not bring Marten or Npgsql through it");
    }

    [Fact]
    public void What_the_module_restores_holds_no_database_module_no_Npgsql_no_Marten_family_and_no_Wolverine_and_the_client_only_transitively()
    {
        string assets = RepositoryRoot.Combine("src", "DataStudio.Kafka", "obj", "project.assets.json");
        File.Exists(assets).Should().BeTrue($"src/DataStudio.Kafka has been restored to build this suite, so {assets} must exist");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(assets));
        string[] libraries = [.. document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name.Split('/')[0])];

        libraries.Should().Contain("DataStudio").And.Contain("Confluent.Kafka", "the restore graph is read, and the client arrives through DataStudio.Kafka.Core");
        libraries.Where(IsForbidden).Should().BeEmpty();

        string[] direct = [.. document.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject()
            .SelectMany(static framework => framework.Value.TryGetProperty("dependencies", out JsonElement dependencies)
                ? dependencies.EnumerateObject().Select(static x => x.Name)
                : Enumerable.Empty<string>())];
        direct.Should().NotContain("Confluent.Kafka", "the module takes no dependency on the client of its own");
    }

    [Fact]
    public void The_built_assembly_references_the_shell_and_the_engine_and_no_database_module_Marten_family_Npgsql_Wolverine_or_Confluent()
    {
        string[] references = [.. typeof(KafkaOptions).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        references.Should().Contain("DataStudio.Kafka.Core").And.Contain("DataStudio");
        references.Where(IsForbiddenInAssembly).Should().BeEmpty(
            "DataStudio.Kafka.dll talks to Kafka only through DataStudio.Kafka.Core, joins the studio only through DataStudio, and never names a database or Marten type");
    }

    [Fact]
    public void The_test_output_folder_holds_no_Marten_family_and_no_Wolverine()
    {
        string[] files = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(static x => Path.GetFileNameWithoutExtension(x))];

        files.Should().Contain("DataStudio.Kafka");
        // This suite's harness composes the database module beside Kafka, so it is in the output folder; the module's own fence above is the one
        // that holds the database out of DataStudio.Kafka itself.
        files.Where(static x => IsForbidden(x) && !IsExactlyOrBelow(x, "DataStudio.Postgres") && !x.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();
    }

    /// <summary>
    /// One case per name the fence must catch - the renamed studios, the engine below them, the sibling adapter and the samples - and the three it
    /// must let through: the shell, the module itself and its own engine, which a bare <c>DataStudio</c> prefix would have caught.
    /// </summary>
    [Theory]
    [InlineData("DataStudio.Marten", true)]
    [InlineData("DataStudio.Marten.Tests", true)]
    [InlineData("DataStudio.Postgres", true)]
    [InlineData("DataStudio.Postgres.Diagnostics", true)]
    [InlineData("DataStudio.Kafka.Wolverine", true)]
    [InlineData("DataStudio.Sample.Kafka", true)]
    [InlineData("DataStudio.Sample.Marten.Domain", true)]
    [InlineData("Npgsql", true)]
    [InlineData("Marten", true)]
    [InlineData("JasperFx.Events", true)]
    [InlineData("Weasel.Postgresql", true)]
    [InlineData("WolverineFx.Kafka", true)]
    [InlineData("DataStudio", false)]
    [InlineData("DataStudio.Kafka", false)]
    [InlineData("DataStudio.Kafka.Core", false)]
    [InlineData("Microsoft.AspNetCore.Components", false)]
    public void The_fence_catches_each_name_it_forbids_and_no_other(string name, bool forbidden)
    {
        IsForbidden(name).Should().Be(forbidden);
    }

    [Theory]
    [InlineData("Confluent.Kafka", true)]
    [InlineData("librdkafka.redist", true)]
    [InlineData("DataStudio.Kafka.Core", false)]
    public void The_built_assembly_also_names_no_Confluent_assembly(string name, bool forbidden)
    {
        IsForbiddenInAssembly(name).Should().Be(forbidden);
    }
}
