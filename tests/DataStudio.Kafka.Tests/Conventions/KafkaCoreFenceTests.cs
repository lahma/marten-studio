using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Kafka.Tests.Conventions;

/// <summary>
/// The engine's fences (AGENTS.md D36, hard rule 16): DataStudio.Kafka.Core references no web stack, no studio, no Postgres driver,
/// no Marten family and no Wolverine; and the shell and the three studio projects reference no Kafka client, no Wolverine and no
/// KStudio.
/// </summary>
/// <remarks>
/// Names are matched exactly or as a dotted prefix (REVIEW-V2-R finding 1), never by a bare <c>DataStudio</c> prefix, which the shell, the module and
/// the engine itself all satisfy: <c>DataStudio.Marten</c> is <c>DataStudio.Marten</c> and <c>DataStudio.Marten.*</c>. The third-party families stay
/// prefixes, as their packages are named by them.
/// </remarks>
public class KafkaCoreFenceTests
{
    /// <summary>What DataStudio.Kafka.Core may never reference, as assembly or package name prefixes: the web stack and the third-party families.</summary>
    private static readonly string[] ForbiddenFamiliesInCore =
        ["Microsoft.AspNetCore", "Microsoft.JSInterop", "Marten", "JasperFx", "Weasel", "Npgsql", "Wolverine"];

    /// <summary>
    /// The projects the engine may never reference, matched exactly: the shell, and the Kafka module that sits above the engine (the engine is
    /// <c>DataStudio.Kafka.Core</c>, which a dotted rule on <c>DataStudio.Kafka</c> would catch).
    /// </summary>
    private static readonly string[] ForbiddenExactInCore = ["DataStudio", "DataStudio.Kafka"];

    /// <summary>The projects the engine may never reference, exactly or below: Marten Studio, the database module and the engine under it, the Kafka adapter and the samples.</summary>
    private static readonly string[] ForbiddenDottedInCore = ["DataStudio.Marten", "DataStudio.Postgres", "DataStudio.Kafka.Wolverine", "DataStudio.Sample"];

    /// <summary>What the shell and the database and Marten studios may never reference, by prefix: the client and the adapter's package.</summary>
    private static readonly string[] ForbiddenFamiliesInStudios = ["Confluent", "librdkafka", "Wolverine"];

    /// <summary>Nor any Kafka project of ours, exactly or below: the module, its engine and the adapter.</summary>
    private static readonly string[] ForbiddenDottedInStudios = ["DataStudio.Kafka"];

    /// <summary>Whether <paramref name="name" /> is one of <paramref name="dotted" />, or below one of them.</summary>
    private static bool IsOrIsBelow(string name, IEnumerable<string> dotted) =>
        dotted.Any(forbidden => name.Equals(forbidden, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(forbidden + ".", StringComparison.OrdinalIgnoreCase));

    private static bool IsForbiddenInCore(string name) =>
        ForbiddenFamiliesInCore.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || ForbiddenExactInCore.Any(forbidden => name.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
        || IsOrIsBelow(name, ForbiddenDottedInCore);

    private static bool IsForbiddenInStudios(string name) =>
        ForbiddenFamiliesInStudios.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || IsOrIsBelow(name, ForbiddenDottedInStudios);

    [Fact]
    public void The_engine_assembly_references_no_web_stack_no_studio_and_no_Postgres_driver()
    {
        var references = Assembly.Load("DataStudio.Kafka.Core").GetReferencedAssemblies().Select(static x => x.Name!);

        references.Where(IsForbiddenInCore).Should().BeEmpty(
            "DataStudio.Kafka.Core is the engine alone: no ASP.NET Core, no studio and no Postgres driver");
    }

    [Fact]
    public void The_engine_project_has_no_framework_reference_and_no_forbidden_reference()
    {
        var project = XDocument.Load(RepositoryRoot.Combine("src", "DataStudio.Kafka.Core", "DataStudio.Kafka.Core.csproj"));

        project.Descendants().Where(static x => x.Name.LocalName == "FrameworkReference").Should().BeEmpty(
            "DataStudio.Kafka.Core has no FrameworkReference: a worker host must not need ASP.NET Core to load it");
        References(project).Where(IsForbiddenInCore).Should().BeEmpty();
        References(project).Should().Contain("Confluent.Kafka", "the project file read is the real one");
        ((string?) project.Root?.Attribute("Sdk")).Should().Be("Microsoft.NET.Sdk",
            "a Web or Razor SDK brings ASP.NET Core without a FrameworkReference line");
    }

    [Fact]
    public void What_the_engine_restores_holds_no_web_stack_no_studio_and_no_Postgres_driver()
    {
        var assets = RepositoryRoot.Combine("src", "DataStudio.Kafka.Core", "obj", "project.assets.json");

        File.Exists(assets).Should().BeTrue($"src/DataStudio.Kafka.Core has been restored to build this suite, so {assets} must exist");

        using var document = JsonDocument.Parse(File.ReadAllText(assets));
        var libraries = document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name.Split('/')[0]).ToArray();

        libraries.Where(IsForbiddenInCore).Should().BeEmpty(
            "the restore graph of DataStudio.Kafka.Core must not reach ASP.NET Core, a studio, Npgsql, the Marten family or Wolverine");
        libraries.Should().Contain("Confluent.Kafka", "the restore graph is read");

        var frameworks = document.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject()
            .SelectMany(static framework => framework.Value.TryGetProperty("frameworkReferences", out var references)
                ? references.EnumerateObject().Select(static reference => reference.Name)
                : Enumerable.Empty<string>())
            .ToArray();
        frameworks.Where(IsForbiddenInCore).Should().BeEmpty(
            "the engine restores no ASP.NET Core framework reference, whatever imported it");
    }

    [Theory]
    [InlineData("src/DataStudio/DataStudio.csproj")]
    [InlineData("src/DataStudio.Postgres.Diagnostics/DataStudio.Postgres.Diagnostics.csproj")]
    [InlineData("src/DataStudio.Postgres/DataStudio.Postgres.csproj")]
    [InlineData("src/DataStudio.Marten/DataStudio.Marten.csproj")]
    public void A_studio_project_references_no_Kafka_client_no_Wolverine_and_no_KStudio(string project)
    {
        var document = XDocument.Load(RepositoryRoot.Combine(project.Split('/')));

        References(document).Where(IsForbiddenInStudios).Should().BeEmpty(
            "a host with no Kafka must never get a Kafka client, Wolverine or KStudio through a studio");
    }

    /// <summary>One case per name the engine's fence must catch, and those it must let through: itself, the Confluent client it exists for and the framework's abstractions.</summary>
    [Theory]
    [InlineData("DataStudio", true)]
    [InlineData("DataStudio.Kafka", true)]
    [InlineData("DataStudio.Marten", true)]
    [InlineData("DataStudio.Marten.Tests", true)]
    [InlineData("DataStudio.Postgres", true)]
    [InlineData("DataStudio.Postgres.Diagnostics", true)]
    [InlineData("DataStudio.Kafka.Wolverine", true)]
    [InlineData("DataStudio.Sample.Kafka", true)]
    [InlineData("Microsoft.AspNetCore.Components", true)]
    [InlineData("Npgsql", true)]
    [InlineData("WolverineFx.Kafka", true)]
    [InlineData("DataStudio.Kafka.Core", false)]
    [InlineData("Confluent.Kafka", false)]
    [InlineData("Microsoft.Extensions.Logging.Abstractions", false)]
    public void The_engines_fence_catches_each_name_it_forbids_and_no_other(string name, bool forbidden)
    {
        IsForbiddenInCore(name).Should().Be(forbidden);
    }

    /// <summary>The studios' fence, name by name: every Kafka project, the client and the adapter's package; and not the shell, which is the studios' own.</summary>
    [Theory]
    [InlineData("DataStudio.Kafka", true)]
    [InlineData("DataStudio.Kafka.Core", true)]
    [InlineData("DataStudio.Kafka.Wolverine", true)]
    [InlineData("Confluent.Kafka", true)]
    [InlineData("librdkafka.redist", true)]
    [InlineData("WolverineFx.Kafka", true)]
    [InlineData("DataStudio", false)]
    [InlineData("DataStudio.Postgres", false)]
    [InlineData("DataStudio.Marten", false)]
    [InlineData("Npgsql", false)]
    public void The_studios_fence_catches_each_name_it_forbids_and_no_other(string name, bool forbidden)
    {
        IsForbiddenInStudios(name).Should().Be(forbidden);
    }

    private static IEnumerable<string> References(XDocument project) =>
        project.Descendants()
            .Where(static x => x.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(static x => x.Name.LocalName == "ProjectReference"
                ? Path.GetFileNameWithoutExtension(((string?) x.Attribute("Include") ?? "").Replace('\\', '/'))
                : (string?) x.Attribute("Include") ?? (string?) x.Attribute("Update") ?? "");
}
