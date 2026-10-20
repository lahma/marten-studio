using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The compiled half of the fence around the shared studio: <c>PostgresStudio.dll</c> references no Marten, JasperFx or
/// Weasel assembly, and its project brings in no such package - directly, through a project, or transitively.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed when the compiler already refuses a lean.</b> The compiler refuses a <em>use</em> of Marten in a
/// project that cannot see it. It says nothing when the project is given sight of it: a <c>PackageReference</c> to
/// Marten, a <c>ProjectReference</c> to MartenStudio, or a package of our own that happens to depend on Weasel would make
/// every lean compile again, and the shared studio would be a Marten studio with a different name - one that drags
/// Marten, JasperFx and Weasel into every host that installs it for a plain Postgres database. So the project file, the
/// restore's own record of what it resolved, and the built assembly's references are each checked, and each check has a
/// synthetic theory proving it can fail.
/// </para>
/// <para>
/// The text half - Razor markup, attribute values and crefs - is <see cref="CoreFenceTests" />. These tests move to
/// <c>tests/PostgresStudio.Tests</c> when that project exists (design §7.2), where the test's own output directory can be
/// checked too.
/// </para>
/// </remarks>
public class PostgresStudioFenceTests
{
    /// <summary>The prefixes of the Marten family: Marten itself and every package it brings, and MartenStudio.</summary>
    private static readonly string[] MartenFamily = ["Marten", "JasperFx", "Weasel"];

    /// <summary>Whether a package id or an assembly name is in the Marten family - <c>Marten*</c>, <c>JasperFx*</c>, <c>Weasel*</c>.</summary>
    internal static bool IsMartenFamily(string name) =>
        MartenFamily.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string ProjectFile => RepositoryRoot.Combine("src", StudioProjects.Shared, StudioProjects.Shared + ".csproj");

    private static string AssetsFile => RepositoryRoot.Combine("src", StudioProjects.Shared, "obj", "project.assets.json");

    /// <summary>
    /// The shared studio is an assembly of its own, and the reference runs one way: MartenStudio references it, never the
    /// reverse. Everything else in this class, and every scan that reads "both assemblies", stands on that.
    /// </summary>
    [Fact]
    public void The_shared_studio_is_an_assembly_of_its_own_that_MartenStudio_references()
    {
        StudioProjects.SharedAssembly.Should().NotBeSameAs(StudioProjects.MartenAssembly);
        StudioProjects.SharedAssembly.GetName().Name.Should().Be(StudioProjects.Shared);
        StudioProjects.MartenAssembly.GetName().Name.Should().Be(StudioProjects.Marten);

        StudioProjects.MartenAssembly.GetReferencedAssemblies().Select(static x => x.Name)
            .Should().Contain(StudioProjects.Shared, "MartenStudio is built on the shared studio");
    }

    [Fact]
    public void The_shared_assembly_references_no_Marten_JasperFx_or_Weasel_assembly()
    {
        string[] references = [.. typeof(StudioCoreRegistration).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        // Not vacuous: the list is the real one - the shared framework and Npgsql are on it.
        references.Should().Contain("Npgsql", "the shared studio talks to Postgres through Npgsql alone")
            .And.Contain(static x => x.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal), "and is a Blazor library");

        references.Where(IsMartenFamily).Should().BeEmpty(
            "PostgresStudio.dll must run in a host with no Marten at all, so it may reference no Marten, JasperFx or Weasel "
            + "assembly, MartenStudio's included");
    }

    /// <summary>The family test, on synthetic assembly and package names: the family is found, and its neighbours are not.</summary>
    [Theory]
    [InlineData("Marten", true)]
    [InlineData("Marten.AspNetCore", true)]
    [InlineData("MartenStudio", true)]
    [InlineData("JasperFx", true)]
    [InlineData("JasperFx.Events", true)]
    [InlineData("jasperfx.events", true)]
    [InlineData("Weasel.Core", true)]
    [InlineData("Weasel.Postgresql", true)]
    [InlineData("Npgsql", false)]
    [InlineData("PostgresStudio", false)]
    [InlineData("Microsoft.AspNetCore.Components.Web", false)]
    [InlineData("System.Runtime", false)]
    [InlineData("Microsoft.AspNetCore.App.Internal.Assets", false)]
    public void The_family_test_finds_the_family_and_nothing_else(string name, bool family)
    {
        IsMartenFamily(name).Should().Be(family);
    }

    /// <summary>The assembly half, on a synthetic reference list, as the fact above reads it.</summary>
    [Theory]
    [InlineData("System.Runtime,Npgsql,Microsoft.AspNetCore.Components", "")]
    [InlineData("System.Runtime,Npgsql,Marten", "Marten")]
    [InlineData("System.Runtime,Weasel.Postgresql,JasperFx.Events", "Weasel.Postgresql,JasperFx.Events")]
    [InlineData("Npgsql,MartenStudio", "MartenStudio")]
    public void A_Marten_family_reference_is_found_in_a_reference_list(string references, string expected)
    {
        AssemblyName[] names = [.. references.Split(',').Select(static x => new AssemblyName(x))];

        names.Select(static x => x.Name!).Where(IsMartenFamily).Should().Equal(
            expected.Length == 0 ? Array.Empty<string>() : expected.Split(','));
    }

    [Fact]
    public void The_shared_project_references_no_Marten_family_package_and_not_MartenStudio()
    {
        XDocument project = XDocument.Load(ProjectFile);

        string[] packages = [.. References(project, "PackageReference")];
        string[] projects = [.. References(project, "ProjectReference")];

        // Not vacuous: the reader sees the project's real references.
        packages.Should().Contain("Npgsql").And.Contain("Microsoft.AspNetCore.App.Internal.Assets");

        Offending(project).Should().BeEmpty(
            "the shared studio's own project names no Marten-family package and no project of MartenStudio's: "
            + string.Join(", ", packages.Concat(projects)));
    }

    /// <summary>The project-file half, on synthetic project text.</summary>
    [Theory]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"Npgsql\" /></ItemGroup></Project>", "")]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"Marten\" /></ItemGroup></Project>", "Marten")]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"Weasel.Postgresql\" Version=\"9.33.0\" /></ItemGroup></Project>", "Weasel.Postgresql")]
    [InlineData("<Project><ItemGroup><PackageReference Update=\"JasperFx\" /></ItemGroup></Project>", "JasperFx")]
    [InlineData("<Project><ItemGroup><ProjectReference Include=\"../MartenStudio/MartenStudio.csproj\" /></ItemGroup></Project>", "MartenStudio")]
    [InlineData("<Project><ItemGroup><ProjectReference Include=\"..\\MartenStudio\\MartenStudio.csproj\" /></ItemGroup></Project>", "MartenStudio")]
    [InlineData("<Project><ItemGroup><ProjectReference Include=\"../PostgresStudio.SampleData/PostgresStudio.SampleData.csproj\" /></ItemGroup></Project>", "")]
    public void A_Marten_family_reference_is_found_in_a_project_file(string text, string expected)
    {
        Offending(XDocument.Parse(text)).Should().Equal(expected.Length == 0 ? Array.Empty<string>() : new[] { expected });
    }

    /// <summary>
    /// What the restore resolved for the shared studio - every library of its transitive closure - holds no
    /// Marten-family library and no MartenStudio project. The project file can be clean while something it references
    /// is not; the assets file is NuGet's own record of the whole graph. Read when the project has been restored, which
    /// any build of this test project has done first.
    /// </summary>
    [Fact]
    public void What_the_shared_project_restores_holds_no_Marten_family_library()
    {
        // A checkout that has never restored the project skips, by name, rather than passing over nothing; the
        // project-file fact above still holds the line.
        Assert.SkipUnless(File.Exists(AssetsFile), "src/PostgresStudio has not been restored, so there is no project.assets.json to read");

        string[] libraries = [.. Libraries(File.ReadAllText(AssetsFile))];

        // Not vacuous: the graph is read, and Npgsql is in it.
        libraries.Should().Contain(static x => x.StartsWith("Npgsql/", StringComparison.OrdinalIgnoreCase), "the shared studio restores Npgsql");

        libraries.Where(static x => IsMartenFamily(x.Split('/')[0])).Should().BeEmpty(
            "the shared studio's restore graph must not reach Marten, JasperFx or Weasel, or MartenStudio: " + string.Join(", ", libraries));
    }

    /// <summary>The assets half, on a synthetic assets file shaped as NuGet writes one.</summary>
    [Theory]
    [InlineData("""{ "libraries": { "Npgsql/9.0.4": { "type": "package" }, "Microsoft.AspNetCore.App.Internal.Assets/10.0.12": { "type": "package" } } }""", "")]
    [InlineData("""{ "libraries": { "Npgsql/9.0.4": { "type": "package" }, "Weasel.Postgresql/9.33.0": { "type": "package" } } }""", "Weasel.Postgresql/9.33.0")]
    [InlineData("""{ "libraries": { "MartenStudio/0.5.0": { "type": "project", "path": "../MartenStudio/MartenStudio.csproj" } } }""", "MartenStudio/0.5.0")]
    public void A_Marten_family_library_is_found_in_an_assets_file(string json, string expected)
    {
        Libraries(json).Where(static x => IsMartenFamily(x.Split('/')[0])).Should().Equal(
            expected.Length == 0 ? Array.Empty<string>() : new[] { expected });
    }

    /// <summary>The <c>Include</c> or <c>Update</c> of every item of <paramref name="kind" />, as a package id or a project name.</summary>
    private static IEnumerable<string> References(XDocument project, string kind) =>
        project.Descendants()
            .Where(x => x.Name.LocalName == kind)
            .Select(static x => (string?) x.Attribute("Include") ?? (string?) x.Attribute("Update") ?? string.Empty)
            .Select(x => kind == "ProjectReference" ? Path.GetFileNameWithoutExtension(x.Replace('\\', '/')) : x);

    private static IEnumerable<string> Offending(XDocument project) =>
        References(project, "PackageReference").Concat(References(project, "ProjectReference")).Where(IsMartenFamily);

    /// <summary>The keys of an assets file's <c>libraries</c>: <c>Id/Version</c>, packages and projects alike.</summary>
    private static IEnumerable<string> Libraries(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return [.. document.RootElement.GetProperty("libraries").EnumerateObject().Select(static x => x.Name)];
    }
}
