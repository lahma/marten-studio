using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

using MartenStudio.Tests.Conventions;

namespace PostgresStudio.Tests.Conventions;

/// <summary>
/// The compiled half of the fence around the shared studio: <c>PostgresStudio.dll</c> and <c>PostgresStudio.Diagnostics.dll</c>
/// reference no Marten, JasperFx or Weasel assembly, their projects bring in no such package - directly, through a project,
/// or transitively - and neither does this test project, whose output folder is the proof that nothing drags them in
/// (design §7.2). The diagnostics assembly has a second fence of its own: it references no ASP.NET Core and no JSInterop,
/// and is no Razor project, so a worker or console host can use it with nothing of a web stack (0.11-C).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed when the compiler already refuses a lean.</b> The compiler refuses a <em>use</em> of Marten in a
/// project that cannot see it. It says nothing when the project is given sight of it: a <c>PackageReference</c> to
/// Marten, a <c>ProjectReference</c> to MartenStudio, or a package of our own that happens to depend on Weasel would make
/// every lean compile again, and the shared studio would be a Marten studio with a different name - one that drags
/// Marten, JasperFx and Weasel into every host that installs it for a plain Postgres database. So the project files, the
/// restores' own records of what they resolved, the built assemblies' references and this project's output folder are
/// each checked, and each check has a synthetic theory proving it can fail.
/// </para>
/// <para>
/// <b>A missing restore record fails; it never skips.</b> This project cannot have been built without restoring both
/// projects, so an absent <c>project.assets.json</c> is a moved file or a broken build, and a check that skipped quietly
/// would pass a CI run that checked nothing (D19's rule).
/// </para>
/// <para>
/// The text half - Razor markup, attribute values and crefs - is MartenStudio.Tests' <c>CoreFenceTests</c>, and the fact
/// that MartenStudio references this assembly is its <c>SharedStudioReferenceTests</c>.
/// </para>
/// </remarks>
public class PostgresStudioFenceTests
{
    private const string Shared = "PostgresStudio";

    private const string Diagnostics = "PostgresStudio.Diagnostics";

    private const string TestProject = "PostgresStudio.Tests";

    /// <summary>The prefixes of the Marten family: Marten itself and every package it brings, and MartenStudio.</summary>
    private static readonly string[] MartenFamily = ["Marten", "JasperFx", "Weasel"];

    /// <summary>Whether a package id or an assembly name is in the Marten family - <c>Marten*</c>, <c>JasperFx*</c>, <c>Weasel*</c>.</summary>
    internal static bool IsMartenFamily(string name) =>
        MartenFamily.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The two projects fenced, the shared studio and this test project: each project file with what it must be seen to reference - so a reader that found nothing, or the wrong file,
    /// fails rather than passing over an empty list: the shared studio names Npgsql and the assets pack itself, and this
    /// project names the shared studio.
    /// </summary>
    public static TheoryData<string, string, string> ProjectFiles => new()
    {
        { "src", Diagnostics, "Npgsql,Microsoft.Extensions.Logging.Abstractions,Microsoft.Extensions.Options,Microsoft.Extensions.Diagnostics.HealthChecks" },
        { "src", Shared, Diagnostics + ",Microsoft.AspNetCore.App.Internal.Assets" },
        { "tests", TestProject, Shared + ",bunit" },
    };

    /// <summary>
    /// Each restore graph with the libraries it must be seen to hold: both reach Npgsql and the assets pack - this project
    /// through the shared studio - so a graph read that missed either is a graph read that read nothing.
    /// </summary>
    public static TheoryData<string, string, string> RestoreGraphs => new()
    {
        { "src", Diagnostics, "Npgsql,Microsoft.Extensions.Logging.Abstractions,Microsoft.Extensions.Options,Microsoft.Extensions.Diagnostics.HealthChecks" },
        { "src", Shared, "Npgsql,Microsoft.AspNetCore.App.Internal.Assets," + Diagnostics },
        { "tests", TestProject, "Npgsql,Microsoft.AspNetCore.App.Internal.Assets," + Shared + "," + Diagnostics },
    };

    [Fact]
    public void The_shared_assembly_references_no_Marten_JasperFx_or_Weasel_assembly()
    {
        string[] references = [.. typeof(PostgresStudioOptions).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        // Not vacuous: the list is the real one - the shared framework is on it, and Npgsql is reached through the engine.
        references.Should().Contain(Diagnostics, "the shared studio is built on the diagnostics engine")
            .And.Contain(static x => x.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal), "and is a Blazor library");

        references.Where(IsMartenFamily).Should().BeEmpty(
            "PostgresStudio.dll must run in a host with no Marten at all, so it may reference no Marten, JasperFx or Weasel "
            + "assembly, MartenStudio's included");
    }

    [Fact]
    public void The_diagnostics_assembly_references_no_Marten_JasperFx_or_Weasel_assembly()
    {
        string[] references = [.. typeof(PostgresDiagnosticsOptions).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        // Not vacuous: the list is the real one - Npgsql and the Microsoft.Extensions abstractions are on it.
        references.Should().Contain("Npgsql", "the engine talks to Postgres through Npgsql alone")
            .And.Contain("Microsoft.Extensions.Logging.Abstractions", "and logs through the abstractions");

        references.Where(IsMartenFamily).Should().BeEmpty(
            "PostgresStudio.Diagnostics.dll must run in a host with no Marten at all, so it may reference no Marten, JasperFx or Weasel assembly");
    }

    /// <summary>
    /// The diagnostics assembly's own fence, at the built assembly: it references no ASP.NET Core, no JSInterop and no
    /// Components assembly, so a worker host that references it loads none of them.
    /// </summary>
    [Fact]
    public void The_diagnostics_assembly_references_no_ASP_NET_Core_JSInterop_or_Components_assembly()
    {
        string[] references = [.. typeof(PostgresDiagnosticsOptions).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        references.Should().Contain("Npgsql", "the list is the real one");

        references.Where(IsWebStack).Should().BeEmpty(
            "PostgresStudio.Diagnostics.dll is the engine alone: " + string.Join(", ", references.Where(IsWebStack)));
    }

    /// <summary>The diagnostics project file names no ASP.NET Core package or framework and is no Razor project.</summary>
    [Fact]
    public void The_diagnostics_project_file_names_no_ASP_NET_Core_framework_package_or_Razor_SDK()
    {
        XDocument document = XDocument.Load(RepositoryRoot.Combine("src", Diagnostics, Diagnostics + ".csproj"));

        // Not vacuous: the file is the real one.
        References(document, "PackageReference").Should().Contain("Npgsql");

        WebStackInProject(document).Should().BeEmpty("the engine's project names nothing of a web stack: " + string.Join(", ", WebStackInProject(document)));

        Directory.EnumerateFiles(RepositoryRoot.Combine("src", Diagnostics), "*.razor", SearchOption.AllDirectories)
            .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Should().BeEmpty("the engine has no Razor component");
    }

    /// <summary>The diagnostics restore resolved no ASP.NET Core library and no framework reference to it.</summary>
    [Fact]
    public void What_the_diagnostics_project_restores_holds_no_ASP_NET_Core_library_or_framework_reference()
    {
        string assets = RepositoryRoot.Combine("src", Diagnostics, "obj", "project.assets.json");

        File.Exists(assets).Should().BeTrue(
            $"src/{Diagnostics} has been restored to build this suite, so {assets} must exist; if the restore output moved, move this check with it");

        string json = File.ReadAllText(assets);

        // Not vacuous: the graph is read.
        Libraries(json).Select(static x => x.Split('/')[0]).Should().Contain("Npgsql");

        WebStackInAssets(json).Should().BeEmpty("the engine's restore graph holds nothing of a web stack: " + string.Join(", ", WebStackInAssets(json)));
    }

    /// <summary>The web-stack test, on synthetic assembly names: ASP.NET Core, JSInterop and Components are found, and their neighbours are not.</summary>
    [Theory]
    [InlineData("Microsoft.AspNetCore", true)]
    [InlineData("Microsoft.AspNetCore.Http.Abstractions", true)]
    [InlineData("Microsoft.AspNetCore.Components.Web", true)]
    [InlineData("Microsoft.JSInterop", true)]
    [InlineData("Microsoft.Extensions.Logging.Abstractions", false)]
    [InlineData("Microsoft.Extensions.Diagnostics.HealthChecks", false)]
    [InlineData("Npgsql", false)]
    [InlineData("System.Runtime", false)]
    public void The_web_stack_test_finds_ASP_NET_Core_and_JSInterop_and_nothing_else(string name, bool webStack)
    {
        IsWebStack(name).Should().Be(webStack);
    }

    /// <summary>The project-file half of the web-stack fence, on synthetic project text.</summary>
    [Theory]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Npgsql\" /></ItemGroup></Project>", "")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><FrameworkReference Include=\"Microsoft.AspNetCore.App\" /></ItemGroup></Project>", "FrameworkReference Microsoft.AspNetCore.App")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.AspNetCore.Http.Abstractions\" /></ItemGroup></Project>", "Microsoft.AspNetCore.Http.Abstractions")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.JSInterop\" /></ItemGroup></Project>", "Microsoft.JSInterop")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk.Razor\"><ItemGroup><PackageReference Include=\"Npgsql\" /></ItemGroup></Project>", "Sdk Microsoft.NET.Sdk.Razor")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk.Web\" />", "Sdk Microsoft.NET.Sdk.Web")]
    public void A_web_stack_reference_is_found_in_a_project_file(string text, string expected)
    {
        WebStackInProject(XDocument.Parse(text)).Should().Equal(expected.Length == 0 ? Array.Empty<string>() : new[] { expected });
    }

    /// <summary>The assets half of the web-stack fence, on a synthetic assets file shaped as NuGet writes one.</summary>
    [Theory]
    [InlineData("""{ "libraries": { "Npgsql/9.0.4": { "type": "package" } }, "project": { "frameworks": { "net10.0": { "frameworkReferences": { "Microsoft.NETCore.App": { "privateAssets": "all" } } } } } }""", "")]
    [InlineData("""{ "libraries": { "Microsoft.AspNetCore.Http.Abstractions/2.3.0": { "type": "package" } } }""", "Microsoft.AspNetCore.Http.Abstractions/2.3.0")]
    [InlineData("""{ "libraries": { "Npgsql/9.0.4": { "type": "package" } }, "project": { "frameworks": { "net10.0": { "frameworkReferences": { "Microsoft.AspNetCore.App": { "privateAssets": "none" } } } } } }""", "framework Microsoft.AspNetCore.App")]
    public void A_web_stack_library_is_found_in_an_assets_file(string json, string expected)
    {
        WebStackInAssets(json).Should().Equal(expected.Length == 0 ? Array.Empty<string>() : new[] { expected });
    }

    [Fact]
    public void This_test_assembly_references_no_Marten_JasperFx_or_Weasel_assembly()
    {
        string[] references = [.. typeof(PostgresStudioFenceTests).Assembly.GetReferencedAssemblies().Select(static x => x.Name!)];

        references.Should().Contain(Shared, "the suite tests the shared studio").And.Contain(Diagnostics, "and its diagnostics engine");

        references.Where(IsMartenFamily).Should().BeEmpty(
            "PostgresStudio.Tests stands for a host with only Npgsql: it may reference no Marten-family assembly");
    }

    /// <summary>
    /// What this project's build copied next to it - every assembly of its closure - holds no Marten-family file. A
    /// reference list says what the compiler bound; the output folder says what anything dragged in at run time.
    /// </summary>
    [Fact]
    public void The_test_output_folder_holds_no_Marten_JasperFx_or_Weasel_assembly()
    {
        string[] assemblies = [.. Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>()];

        // Not vacuous: the folder is the real one - the studio, its engine and Npgsql are in it.
        assemblies.Should().Contain(Shared).And.Contain(Diagnostics).And.Contain("Npgsql");

        assemblies.Where(IsMartenFamily).Should().BeEmpty(
            "nothing this suite references may bring Marten, JasperFx or Weasel with it: " + string.Join(", ", assemblies.Where(IsMartenFamily)));
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

    /// <summary>The assembly half, on a synthetic reference list, as the facts above read it.</summary>
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

    [Theory]
    [MemberData(nameof(ProjectFiles))]
    public void The_project_references_no_Marten_family_package_and_not_MartenStudio(string root, string project, string expected)
    {
        XDocument document = XDocument.Load(RepositoryRoot.Combine(root, project, project + ".csproj"));

        string[] packages = [.. References(document, "PackageReference")];
        string[] projects = [.. References(document, "ProjectReference")];

        // Not vacuous: the reader sees the project's real references, the ones this project is known to name.
        packages.Concat(projects).Should().Contain(expected.Split(','), "the project file is read");

        Offending(document).Should().BeEmpty(
            "the project names no Marten-family package and no project of MartenStudio's: "
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
    /// What the restore resolved for the project - every library of its transitive closure - holds no Marten-family
    /// library and no MartenStudio project. The project file can be clean while something it references is not; the
    /// assets file is NuGet's own record of the whole graph.
    /// </summary>
    [Theory]
    [MemberData(nameof(RestoreGraphs))]
    public void What_the_project_restores_holds_no_Marten_family_library(string root, string project, string expected)
    {
        string assets = RepositoryRoot.Combine(root, project, "obj", "project.assets.json");

        // Fails, never skips: this assembly exists only because both projects were restored, so a missing record is a
        // moved file or a broken build - and a fence that skipped would be a green run that fenced nothing.
        File.Exists(assets).Should().BeTrue(
            $"{root}/{project} has been restored to build this suite, so {assets} must exist; if the restore output moved, move this check with it");

        string[] libraries = [.. Libraries(File.ReadAllText(assets))];

        // Not vacuous: the graph is read, and the libraries this project is known to reach are in it.
        libraries.Select(static x => x.Split('/')[0]).Should().Contain(expected.Split(','), "the project's restore graph is read");

        libraries.Where(static x => IsMartenFamily(x.Split('/')[0])).Should().BeEmpty(
            "the restore graph must not reach Marten, JasperFx or Weasel, or MartenStudio: " + string.Join(", ", libraries));
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

    /// <summary>Whether an assembly or package name is of the web stack: ASP.NET Core, JSInterop (and so Components).</summary>
    private static bool IsWebStack(string name) =>
        name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.JSInterop", StringComparison.OrdinalIgnoreCase);

    /// <summary>What a project file names of the web stack: a package, a framework reference or a web SDK.</summary>
    private static IEnumerable<string> WebStackInProject(XDocument project)
    {
        foreach (string package in References(project, "PackageReference").Where(IsWebStack))
        {
            yield return package;
        }

        foreach (string framework in References(project, "FrameworkReference").Where(IsWebStack))
        {
            yield return "FrameworkReference " + framework;
        }

        if ((string?) project.Root?.Attribute("Sdk") is { } sdk && (sdk.Contains(".Razor", StringComparison.Ordinal) || sdk.Contains(".Web", StringComparison.Ordinal)))
        {
            yield return "Sdk " + sdk;
        }
    }

    /// <summary>What an assets file holds of the web stack: a library, or a framework reference the project declared.</summary>
    private static IEnumerable<string> WebStackInAssets(string json)
    {
        foreach (string library in Libraries(json).Where(static x => IsWebStack(x.Split('/')[0])))
        {
            yield return library;
        }

        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.TryGetProperty("project", out JsonElement project)
            && project.TryGetProperty("frameworks", out JsonElement frameworks))
        {
            foreach (JsonProperty framework in frameworks.EnumerateObject())
            {
                if (framework.Value.TryGetProperty("frameworkReferences", out JsonElement references))
                {
                    foreach (JsonProperty reference in references.EnumerateObject().Where(static x => IsWebStack(x.Name)))
                    {
                        yield return "framework " + reference.Name;
                    }
                }
            }
        }
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
