using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The fence between the shared studio and MartenStudio, before the move: a file bound for the shared assembly names
/// no Marten, JasperFx or Weasel type and no type MartenStudio keeps - one fence, over every source file on
/// <see cref="CoreManifest" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a text scanner, and what it reads.</b> Until PS-2 moves the files, both halves compile into one assembly, so
/// the compiler cannot tell a shared file that leans on MartenStudio from one that does not - and every such lean is a
/// compile error the move would discover all at once. So each fenced file is read twice: as code, with comments and
/// strings blanked by <see cref="SourceScanner" />, for a <c>using</c> of a Marten-family namespace, a
/// <c>Marten.</c>/<c>JasperFx.</c>/<c>Weasel.</c> qualified name and any identifier from the stay-behind list; and as
/// documentation, for every <c>cref</c> - a cref to a type the shared assembly cannot see is CS1574, an error under
/// <c>TreatWarningsAsErrors</c>. PS-1a to PS-1d each fenced their own rows; PS-1e merged the four into this one, over the
/// whole manifest, its own rows (the JSON toolkit and the shared building blocks) included.
/// </para>
/// <para>
/// <b>The stay-behind list</b> is every top-level type and Razor component declared in a <c>src/</c> file that is not on
/// the manifest, and every <c>global using</c> alias such a file declares (<c>MartenTable</c>), less any name a manifest
/// file declares too (<see cref="StayBehindNames" />). It is derived, not curated: a file a packet splits off as
/// MartenStudio's adds its types the moment it exists, and a type on a manifest row is never on it. A type or an alias is
/// matched wherever its name appears; a component only where it is named as one - a tag, <c>typeof</c>, <c>nameof</c>,
/// <c>@inherits</c> - because <c>Schema</c> is as good a property name as a page name.
/// </para>
/// <para>
/// <b>What it cannot see.</b> A call that binds to a MartenStudio extension member reads like a call on the shared type
/// (<c>StudioCapabilityGuard.OptionName(…)</c> resolves to <c>MartenServiceBridges</c>); the bridges list their callers,
/// every one MartenStudio's own since PS-1e, and PS-2's compile is the proof. Comments and C# string literals are blanked, so prose
/// and text never fail the fence - but a Razor file is read as code in two more places, because Razor puts code in them:
/// its markup text (an <c>@Expression</c> sits in it, and a prose word that collides with a MartenStudio type name fails
/// loudly and is reworded), and every tag's attribute values (<c>href="@Links.To(…)"</c>,
/// <c>Capability="@nameof(…)"</c>, a component parameter's bare C#), which the string pass would have blanked. An import
/// that brings a Marten namespace into every file at once - a <c>global using</c>, a csproj <c>&lt;Using&gt;</c>, an
/// <c>_Imports.razor</c> line - is refused on its own (<see cref="No_import_brings_a_Marten_family_namespace_into_every_file" />),
/// since no per-file scan would see it.
/// </para>
/// </remarks>
public class CoreFenceTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] MartenFamily = ["Marten", "JasperFx", "Weasel"];

    /// <summary><c>using Marten;</c>, <c>using static JasperFx.X;</c>, <c>using A = Weasel.B;</c>, <c>@using Marten.Storage</c>.</summary>
    private static readonly Regex MartenUsing = new(
        @"^\s*@?(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?(?:Marten|JasperFx|Weasel)\b(?!Studio)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>A qualified name: <c>Marten.Storage.IMartenDatabase</c>, <c>global::JasperFx.Descriptors</c>.</summary>
    private static readonly Regex MartenQualifiedName = new(
        @"(?<![\w.])(?:Marten|JasperFx|Weasel)\.[A-Za-z_][\w.]*",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>An identifier - after <c>@</c> too, which is how Razor names one in markup (<c>@MartenStoreRegistry.X</c>).</summary>
    private static readonly Regex Identifier = new(@"(?<!\w)[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// One markup tag, attributes and all, with quoted values paired so a <c>&gt;</c> inside one does not end the tag.
    /// </summary>
    private static readonly Regex Tag = new(
        @"<[A-Za-z][\w.:-]*(?:[^<>""]|""[^""]*"")*/?>",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>A Razor comment, a C# block comment, or a whole line that is a C# line comment.</summary>
    private static readonly Regex RazorProse = new(
        @"@\*[\s\S]*?\*@|/\*[\s\S]*?\*/|^[ \t]*//[^\n]*",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex Cref = new(@"cref\s*=\s*""(?<value>[^""]*)""", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex TypeDeclaration = new(
        @"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Lazy<StayBehindNames> StayBehind = new(CollectStayBehind);

    /// <summary>
    /// The names only MartenStudio declares: the types of its <c>.cs</c> files, matched wherever they appear, and its
    /// Razor components, matched only where a component is named as one - a tag, <c>typeof</c>, <c>nameof</c>,
    /// <c>@inherits</c> - because a component's class is its file name, and <c>Schema</c> or <c>Overview</c> is as good
    /// a property name in shared code as it is a page name in MartenStudio's.
    /// </summary>
    /// <param name="Types">Types declared in MartenStudio's <c>.cs</c> files.</param>
    /// <param name="Components">Components declared by MartenStudio's <c>.razor</c> files.</param>
    internal sealed record StayBehindNames(IReadOnlySet<string> Types, IReadOnlySet<string> Components)
    {
        /// <summary>Every name, either kind.</summary>
        public IReadOnlySet<string> All { get; } = Types.Union(Components).ToHashSet(StringComparer.Ordinal);

        /// <summary>A set of type names alone, for the theory.</summary>
        public static StayBehindNames OfTypes(params string[] types) =>
            new(types.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "Overview", "Schema" });
    }

    /// <summary>
    /// The one fence: every C# and Razor file on the manifest names no Marten-family type and no type MartenStudio keeps.
    /// </summary>
    [Fact]
    public void Every_file_bound_for_the_shared_assembly_names_nothing_MartenStudio_keeps()
    {
        CoreManifest.Entry[] rows = [.. CoreManifest.Entries.Where(static x => x.IsSource)];
        rows.Should().HaveCountGreaterThan(250, "a fence over a fraction of the manifest passes for the wrong reason");

        foreach (CoreManifest.Packet packet in Enum.GetValues<CoreManifest.Packet>())
        {
            rows.Should().Contain(x => x.Owner == packet, "every packet's rows are fenced, " + packet + "'s included");
        }

        StayBehindNames stayBehind = StayBehind.Value;
        List<string> problems = [];

        foreach (CoreManifest.Entry row in rows)
        {
            string source = File.ReadAllText(RepositoryRoot.Combine(row.RepositoryPath.Split('/')));

            SourceScanner.StripCommentsAndStrings(source, out bool unterminated);
            if (unterminated)
            {
                problems.Add(row.RepositoryPath + ": the scanner could not follow the file to its end");
            }

            bool razor = row.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
            foreach (string lean in Leans(source, stayBehind, razor))
            {
                problems.Add(row.RepositoryPath + " (" + row.Owner + "): " + lean);
            }
        }

        problems.Should().BeEmpty(
            "a file bound for the shared assembly names no Marten, JasperFx or Weasel type and no type MartenStudio keeps:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Every row names a file that exists, no file is on the manifest twice, and no two rows land on one path.
    /// </summary>
    [Fact]
    public void Every_manifest_row_is_a_file_that_exists_and_lands_on_a_path_of_its_own()
    {
        CoreManifest.Entries.Should().NotBeEmpty();
        CoreManifest.Entries.Select(static x => x.Path).Should().OnlyHaveUniqueItems(static x => x.ToLowerInvariant());
        CoreManifest.Entries.Select(static x => x.TargetPath).Should().OnlyHaveUniqueItems(static x => x.ToLowerInvariant());

        string[] missing =
        [
            .. CoreManifest.Entries
                .Where(static x => !File.Exists(RepositoryRoot.Combine(x.RepositoryPath.Split('/'))))
                .Select(static x => x.RepositoryPath),
        ];

        missing.Should().BeEmpty("a manifest row for a file that is gone is a fence that scans nothing: " + string.Join(", ", missing));

        CoreManifest.Entries.Where(static x => x.NewPath is not null)
            .Should().OnlyContain(static x => x.NewPath != x.Path, "a new path that is the old one is no move")
            .And.OnlyContain(static x => Path.GetExtension(x.NewPath) == Path.GetExtension(x.Path), "a move keeps the file's kind");
    }

    /// <summary>
    /// What makes PS-2's rename a table: every C# file on the manifest declares, file-scoped, exactly the namespace its
    /// folder gives under <c>MartenStudio</c>, and no Razor file on it overrides its folder's with <c>@namespace</c> - so
    /// each one's namespace after the move is its new folder's under <c>PostgresStudio</c>.
    /// </summary>
    [Fact]
    public void Every_file_bound_for_the_shared_assembly_declares_the_namespace_its_folder_gives()
    {
        List<string> problems = [];

        foreach (CoreManifest.Entry row in CoreManifest.Entries.Where(static x => x.IsSource))
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine(row.RepositoryPath.Split('/'))));
            string[] declared = [.. NamespaceDeclaration.Matches(code).Select(static x => x.Groups["name"].Value)];

            if (row.Path.EndsWith(".razor", StringComparison.Ordinal))
            {
                if (RazorNamespace.IsMatch(code))
                {
                    problems.Add(row.RepositoryPath + ": @namespace overrides the folder's namespace");
                }
            }
            else if (declared is not [{ } only] || only != row.OldNamespace)
            {
                problems.Add(row.RepositoryPath + ": declares [" + string.Join(", ", declared) + "], not " + row.OldNamespace);
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));

        CoreManifest.Entries.Single(static x => x.Path == "Services/Documents/SearchChip.cs").NewNamespace
            .Should().Be("PostgresStudio.Services.Query", "the namespace after the move is the new folder's");
        CoreManifest.Entries.Single(static x => x.Path == "StudioConventionBuilder.cs").NewNamespace.Should().Be("PostgresStudio");
        CoreManifest.Entries.Single(static x => x.Path == "wwwroot/css/marten-studio.css").NewNamespace.Should().BeNull();
    }

    /// <summary>
    /// Each type the move renames (<see cref="CoreManifest.TypeRenames" />) is declared in exactly one manifest file, whose
    /// new name is the type's new name, and the new name is declared nowhere in <c>src/</c> yet.
    /// </summary>
    [Fact]
    public void Every_renamed_type_moves_with_its_file_and_takes_a_name_nobody_has()
    {
        Dictionary<string, List<string>> declaredIn = new(StringComparer.Ordinal);

        foreach (string file in SourceScanner.ShippedSourceFiles().Where(static x => x.EndsWith(".cs", StringComparison.Ordinal)))
        {
            foreach (string name in TopLevelTypes(SourceScanner.StripCommentsAndStrings(File.ReadAllText(file))))
            {
                (declaredIn.TryGetValue(name, out List<string>? files) ? files : declaredIn[name] = []).Add(SourceScanner.Relative(file));
            }
        }

        foreach ((string oldName, string newName) in CoreManifest.TypeRenames)
        {
            declaredIn.Should().ContainKey(oldName);
            string file = declaredIn[oldName].Should().ContainSingle().Subject;

            CoreManifest.Entry row = CoreManifest.Entries.Single(x => x.RepositoryPath == file);
            Path.GetFileNameWithoutExtension(row.TargetPath).Should().Be(newName, "the file is renamed with its type");

            declaredIn.Should().NotContainKey(newName, newName + " is free to take");
        }
    }

    /// <summary>
    /// No type is declared on both sides of the move in one namespace - a partial class with a part on the manifest and a
    /// part off it. A partial type cannot span two assemblies: after the move the part left behind compiles as a second
    /// type of the same name, and every file that sees both namespaces is ambiguous. The stay-behind list cannot see it,
    /// because a name both sides declare is left off that list on purpose (<see cref="CollectStayBehind" />).
    /// </summary>
    [Fact]
    public void No_type_is_declared_on_both_sides_of_the_move()
    {
        HashSet<string> manifest = new(CoreManifest.Entries.Select(static x => x.RepositoryPath), StringComparer.OrdinalIgnoreCase);
        List<(string File, string Code, bool Shared)> files =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static x => x.EndsWith(".cs", StringComparison.Ordinal))
                .Select(x => (SourceScanner.Relative(x), SourceScanner.StripCommentsAndStrings(File.ReadAllText(x)), manifest.Contains(SourceScanner.Relative(x)))),
        ];

        files.Should().Contain(static x => x.Shared).And.Contain(static x => !x.Shared);
        SplitDeclarations(files).Should().BeEmpty("a partial type does not survive being split between two assemblies");
    }

    /// <summary>The split-declaration rule, on synthetic files: a partial split across the line is found; a namesake in another namespace is not.</summary>
    [Theory]
    [InlineData("namespace A;\ninternal sealed partial class Counter { }", "namespace A;\ninternal sealed partial class Counter { }", "A.Counter")]
    [InlineData("namespace A;\ninternal sealed record ReferencedBy(int X);", "namespace B;\ninternal sealed class ReferencedBy { }", "")]
    [InlineData("namespace A;\ninternal sealed class Counter { }", "namespace A;\ninternal static class CounterExtensions { }", "")]
    public void A_type_split_across_the_move_is_found(string shared, string stays, string expected)
    {
        SplitDeclarations([("shared.cs", shared, true), ("stays.cs", stays, false)]).Should().Equal(
            expected.Length == 0 ? Array.Empty<string>() : new[] { expected });
    }

    /// <summary>Every namespace-qualified type name declared both in a shared file and in one that stays.</summary>
    private static List<string> SplitDeclarations(IEnumerable<(string File, string Code, bool Shared)> files)
    {
        HashSet<string> shared = new(StringComparer.Ordinal);
        HashSet<string> stays = new(StringComparer.Ordinal);

        foreach ((string _, string code, bool isShared) in files)
        {
            string ns = NamespaceDeclaration.Match(code) is { Success: true } m ? m.Groups["name"].Value : string.Empty;

            foreach (string name in TopLevelTypes(code))
            {
                (isShared ? shared : stays).Add(ns + "." + name);
            }
        }

        return [.. shared.Intersect(stays).Order(StringComparer.Ordinal)];
    }

    private static readonly Regex NamespaceDeclaration = new(
        @"^\s*namespace\s+(?<name>[\w.]+)\s*[;{]",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex RazorNamespace = new(@"^\s*@namespace\s", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// The collector's own anti-vacuity check: the types MartenStudio keeps are on the list, and the shared ones are not.
    /// </summary>
    [Fact]
    public void The_stay_behind_list_holds_MartenStudios_types_and_none_of_the_shared_ones()
    {
        StayBehindNames stayBehind = StayBehind.Value;

        stayBehind.Types.Should().Contain(
            ["MartenStudioOptions", "MartenStoreResource", "MartenStoreRegistry", "MartenStoreRegistration", "ResolvedScope",
             "StudioScopeResolver", "TenantDiscovery", "MartenStudioSettings", "MartenStudioDescriptor", "MartenLog",
             "MartenServiceBridges", "MartenComponentBridges", "DocumentComponentModel", "MartenTable"],
            "each is declared in a .cs file the manifest leaves to MartenStudio - MartenTable as a global alias");

        stayBehind.Components.Should().Contain(
            ["MartenStudioApp", "Projections", "Overview", "Schema"],
            "each is a component the manifest leaves to MartenStudio");

        stayBehind.All.Should().NotContain(
            ["StudioScope", "StudioState", "IStudioSettings", "StudioDescriptor", "IStudioSourceProvider", "ResolvedSource",
             "StudioLog", "StudioRouteTable", "NavMenu", "StudioDocument", "ComponentModel", "StoreAvailability", "TenantList",
             "ReferencedBy"],
            "each is declared in a file on the manifest - ReferencedBy by RelationshipModels.cs as well as by a Marten page, "
            + "and a name both halves declare is not one the fence can attribute");
    }

    /// <summary>The anti-vacuity theory: the scan finds each kind of lean, and ignores each kind of prose.</summary>
    [Theory]
    // Found: a Marten-family using, in C# and in Razor.
    [InlineData("using Marten;\nclass C { }", "using")]
    [InlineData("using Marten.Storage;\nclass C { }", "using")]
    [InlineData("using static JasperFx.Core.Strings;\nclass C { }", "using")]
    [InlineData("using Db = Weasel.Postgresql.PostgresqlDatabase;\nclass C { }", "using")]
    [InlineData("@using JasperFx.Descriptors\n<p>x</p>", "using")]
    [InlineData("global using Marten;", "using")]
    // Found: a qualified name, in code and in a cref.
    [InlineData("class C { Marten.IDocumentStore? store; }", "qualified")]
    [InlineData("class C { bool b = x is global::JasperFx.Descriptors.DatabaseCardinality.Single; }", "qualified")]
    [InlineData("/// <see cref=\"Marten.Storage.IMartenDatabase\" />\nclass C { }", "cref")]
    [InlineData("/// <inheritdoc cref=\"T:Weasel.Core.IDatabase\" />\nclass C { }", "cref")]
    // Found: a stay-behind type, in code, in markup and in a cref - and a stay-behind global alias, which is matched as one.
    [InlineData("class C { MartenStoreRegistry? registry; }", "identifier")]
    [InlineData("class C { MartenTable? table; }", "identifier")]
    [InlineData("@inject IOptions<MartenStudioOptions> Options\n<p>x</p>", "identifier")]
    [InlineData("<p><code>MartenStudioOptions.ReadOnly</code></p>", "identifier")]
    [InlineData("<p>@MartenStoreRegistry.DefaultStoreKey</p>", "identifier")]
    [InlineData("/// <see cref=\"MartenStudioOptions.Path\" />\nclass C { }", "cref")]
    // Found: a MartenStudio component, named as a component.
    [InlineData("<div><Overview /></div>", "component")]
    [InlineData("class C { Type t = typeof(Overview); }", "component")]
    [InlineData("@inherits Schema\n<p>x</p>", "component")]
    [InlineData("/// <see cref=\"Overview\" />\nclass C { }", "cref")]
    // Ignored: a member that shares a component's name, prose and strings, and the shared assembly's own names.
    [InlineData("record R(string Schema, int Overview);", null)]
    [InlineData("class C { string s = x.Schema + Overview; }", null)]
    [InlineData("<p>Overview of the Schema</p>", null)]
    [InlineData("// using Marten;\nclass C { }", null)]
    [InlineData("/// <c>MartenStoreRegistry</c> used to do this.\nclass C { }", null)]
    [InlineData("class C { string s = \"Marten.IDocumentStore\"; }", null)]
    [InlineData("@* MartenStudioOptions.ReadOnly *@\n<p>x</p>", null)]
    [InlineData("using MartenStudio.Services;\nnamespace MartenStudio.Components;\nclass C { PostgresStudio.Services.StudioScope? s; }", null)]
    [InlineData("<p>registered with Marten.</p>", null)]
    [InlineData("/// <see cref=\"StudioScope\" />\nclass C { }", null)]
    public void The_scanner_finds_each_kind_of_lean_and_ignores_prose(string source, string? expected)
    {
        StayBehindNames stayBehind = StayBehindNames.OfTypes("MartenStoreRegistry", "MartenStudioOptions", "MartenTable");

        IReadOnlyList<string> found = Leans(source, stayBehind);

        if (expected is null)
        {
            found.Should().BeEmpty();
        }
        else
        {
            found.Should().ContainSingle().Which.Should().StartWith(expected);
        }
    }

    /// <summary>
    /// The Razor half of the anti-vacuity theory: an attribute value is code, which the string pass alone would blank,
    /// and a comment or plain attribute text still is not.
    /// </summary>
    [Theory]
    [InlineData("<span title=\"@MartenStudioOptions.Title\">x</span>", "identifier")]
    [InlineData("<CapabilityDisabled Capability=\"@nameof(MartenStoreRegistry)\" />", "identifier")]
    [InlineData("<StatCard Value=\"MartenStoreRegistry.DefaultStoreKey\" />", "identifier")]
    [InlineData("<a href=\"@Marten.Links.To(x)\">x</a>", "qualified")]
    [InlineData("<a href=\"@($\"{JasperFx.Core.Paths.Root}/x\")\">x</a>", "qualified")]
    [InlineData("<Overview Title=\"x\" />", "component")]
    [InlineData("<p title=\"registered with Marten.\">x</p>", null)]
    [InlineData("<button class=\"ms-btn\" title=\"Store unavailable: retry\">x</button>", null)]
    [InlineData("@* <a href=\"@MartenStoreRegistry.X\">x</a> *@\n<p>x</p>", null)]
    [InlineData("<p>x</p>\n@code {\n    // <a href=\"@MartenStoreRegistry.X\">\n}", null)]
    public void The_scanner_reads_a_razor_attribute_value_as_code(string source, string? expected)
    {
        IReadOnlyList<string> found = Leans(source, StayBehindNames.OfTypes("MartenStoreRegistry", "MartenStudioOptions"), razor: true);

        if (expected is null)
        {
            found.Should().BeEmpty();
        }
        else
        {
            found.Should().ContainSingle().Which.Should().StartWith(expected);
        }
    }

    /// <summary>
    /// An import that reaches every file at once is invisible to a per-file scan, so it is refused on its own: no
    /// <c>global using</c> in <c>src/</c>, no <c>&lt;Using&gt;</c> item in a <c>src/</c> project or a
    /// <c>Directory.Build.*</c> file, and no <c>_Imports.razor</c> line names a Marten, JasperFx or Weasel namespace.
    /// </summary>
    [Fact]
    public void No_import_brings_a_Marten_family_namespace_into_every_file()
    {
        List<string> problems = [];

        foreach (string file in SourceScanner.ShippedSourceFiles())
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));
            bool imports = Path.GetFileName(file).Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase);

            foreach (Match match in MartenUsing.Matches(code))
            {
                if (imports || match.Value.TrimStart().StartsWith("global", StringComparison.Ordinal))
                {
                    problems.Add(SourceScanner.Relative(file) + ": " + match.Value.Trim());
                }
            }
        }

        string[] projectFiles =
        [
            .. Directory.EnumerateFiles(RepositoryRoot.Combine("src"), "*.csproj", SearchOption.AllDirectories),
            .. RepositoryProjectFiles
                .Select(static x => RepositoryRoot.Combine(x))
                .Where(File.Exists),
        ];

        projectFiles.Should().NotBeEmpty("scanning no project file would pass for the wrong reason");

        foreach (string file in projectFiles)
        {
            foreach (Match match in ProjectUsing.Matches(File.ReadAllText(file)))
            {
                problems.Add(SourceScanner.Relative(file) + ": " + match.Value);
            }
        }

        problems.Should().BeEmpty(
            "an import that reaches every file would put a Marten namespace in every shared file without a line of it saying so: "
            + string.Join(", ", problems));
    }

    /// <summary>The project-file and global-import half of the rule, on synthetic input.</summary>
    [Theory]
    [InlineData("<ItemGroup><Using Include=\"Marten\" /></ItemGroup>", true)]
    [InlineData("<ItemGroup>\n  <Using Include=\"JasperFx.Events\" Static=\"true\" />\n</ItemGroup>", true)]
    [InlineData("<ItemGroup><Using Include='Weasel.Postgresql' Alias='W' /></ItemGroup>", true)]
    [InlineData("<ItemGroup><Using Include=\"MartenStudio.Services\" /></ItemGroup>", false)]
    [InlineData("<ItemGroup><PackageReference Include=\"Marten\" /></ItemGroup>", false)]
    [InlineData("<!-- <Using Include=\"Marten\" /> -->", true)]
    public void A_project_using_of_a_Marten_family_namespace_is_found(string project, bool expected)
    {
        // A commented-out item is reported too: the rule is about the file's text, and a comment that would only need
        // its markers removed to import Marten everywhere is worth a look either way.
        ProjectUsing.IsMatch(project).Should().Be(expected);
    }

    /// <summary>The repository-wide MSBuild files every <c>src/</c> project imports.</summary>
    private static readonly string[] RepositoryProjectFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    /// <summary>A csproj or props <c>&lt;Using Include="…"&gt;</c> of a Marten-family namespace.</summary>
    private static readonly Regex ProjectUsing = new(
        @"<Using\s+Include\s*=\s*[""'](?:Marten|JasperFx|Weasel)(?:\.[\w.]*)?[""'][^>]*>",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>Every lean in <paramref name="source" />, each as "kind: what", each once.</summary>
    /// <param name="source">The file's text.</param>
    /// <param name="stayBehind">The names only MartenStudio declares.</param>
    /// <param name="razor">
    /// Whether it is a Razor file, whose every tag - attribute values included, which the string pass blanks - is read as
    /// code too: an attribute value is where Razor puts an expression.
    /// </param>
    private static List<string> Leans(string source, StayBehindNames stayBehind, bool razor = false)
    {
        List<string> found = [];
        string code = SourceScanner.StripCommentsAndStrings(source);

        if (razor)
        {
            string markup = RazorProse.Replace(source, static x => new string(' ', x.Length));
            code += "\n" + string.Join("\n", Tag.Matches(markup).Select(static x => x.Value));
        }

        foreach (Match match in MartenUsing.Matches(code))
        {
            found.Add("using: " + match.Value.Trim());
        }

        // A qualified name on a using line is the using's finding, not a second one.
        string withoutUsings = MartenUsing.Replace(code, static x => new string(' ', x.Length));
        foreach (Match match in MartenQualifiedName.Matches(withoutUsings))
        {
            found.Add("qualified: " + match.Value.TrimEnd('.'));
        }

        foreach (string name in Identifiers(withoutUsings).Where(stayBehind.Types.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            found.Add("identifier: " + name);
        }

        foreach (Match match in ComponentUse.Matches(withoutUsings))
        {
            if (stayBehind.Components.Contains(match.Groups["name"].Value))
            {
                found.Add("component: " + match.Value.Trim());
            }
        }

        foreach (Match match in Cref.Matches(source))
        {
            string value = match.Groups["value"].Value;
            string bare = value.Length > 2 && value[1] == ':' ? value[2..] : value;

            if (MartenFamily.Any(x => bare.StartsWith(x + ".", StringComparison.Ordinal) || bare.StartsWith("global::" + x + ".", StringComparison.Ordinal))
                || Identifiers(bare).Any(stayBehind.Types.Contains)
                || stayBehind.Components.Contains(bare))
            {
                found.Add("cref: " + value);
            }
        }

        // A tag read twice - once in the markup, once with its attributes - is one finding.
        return [.. found.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A component named as one: <c>&lt;Name</c>, <c>typeof(Name)</c>, <c>nameof(Name)</c>, <c>@inherits Name</c>.</summary>
    private static readonly Regex ComponentUse = new(
        @"(?:<|\btypeof\s*\(\s*|\bnameof\s*\(\s*|@inherits\s+)(?<name>[A-Z][A-Za-z0-9_]*)\b",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static IEnumerable<string> Identifiers(string code) =>
        Identifier.Matches(code).Select(static x => x.Value);

    /// <summary>
    /// Every top-level type and component declared in a <c>src/</c> file that is not on the manifest, less every name a
    /// manifest file declares too - a name both halves declare (a Marten page and a shared record both called
    /// <c>ReferencedBy</c>) is one the text cannot attribute, and the compiler resolves it by namespace.
    /// </summary>
    private static StayBehindNames CollectStayBehind()
    {
        HashSet<string> manifest = new(
            CoreManifest.Entries.Select(static x => x.RepositoryPath),
            StringComparer.OrdinalIgnoreCase);

        HashSet<string> types = new(StringComparer.Ordinal);
        HashSet<string> components = new(StringComparer.Ordinal);
        HashSet<string> shared = new(StringComparer.Ordinal);

        foreach (string file in SourceScanner.ShippedSourceFiles())
        {
            string relative = SourceScanner.Relative(file);
            bool onManifest = manifest.Contains(relative);

            if (relative.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            {
                // A component's class is its file name; the types its @code declares are nested in it.
                (onManifest ? shared : components).Add(Path.GetFileNameWithoutExtension(file));
                continue;
            }

            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));

            // A global alias reaches every file of the assembly, as a type does: MartenTableFacts.cs's MartenTable names a
            // shared type, but under a name the shared assembly will not have.
            foreach (string name in TopLevelTypes(code).Concat(GlobalAliases(code)))
            {
                (onManifest ? shared : types).Add(name);
            }
        }

        types.ExceptWith(shared);
        components.ExceptWith(shared);
        return new StayBehindNames(types, components);
    }

    /// <summary>The names <paramref name="code" /> declares with <c>global using Name = …;</c>.</summary>
    internal static IEnumerable<string> GlobalAliases(string code) =>
        GlobalAlias.Matches(code).Select(static x => x.Groups["name"].Value);

    private static readonly Regex GlobalAlias = new(
        @"^\s*global\s+using\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>The alias collector, on synthetic files: a global alias is found; a local alias and a plain using are not.</summary>
    [Theory]
    [InlineData("global using MartenTable = PostgresStudio.Services.Sources.StudioTable;\nnamespace N;", "MartenTable")]
    [InlineData("  global using Db = Marten.Storage.IMartenDatabase;\nglobal using Other=System.String;", "Db,Other")]
    [InlineData("using Local = System.String;\nnamespace N;", "")]
    [InlineData("global using System.Text;\nglobal using static System.Math;", "")]
    public void The_collector_takes_global_aliases_too(string source, string expected)
    {
        string[] names = expected.Length == 0 ? Array.Empty<string>() : expected.Split(',');

        GlobalAliases(SourceScanner.StripCommentsAndStrings(source)).Should().Equal(names);
    }

    /// <summary>
    /// The types declared at namespace level in <paramref name="code" />: brace depth 0 under a file-scoped namespace,
    /// 1 inside a block-scoped one. A nested type is invisible to another file and so is no lean to fence.
    /// </summary>
    internal static IEnumerable<string> TopLevelTypes(string code)
    {
        bool fileScoped = Regex.IsMatch(code, @"^\s*namespace\s+[\w.]+\s*;", RegexOptions.Multiline, RegexTimeout);
        int namespaceDepth = fileScoped ? 0 : 1;

        int depth = 0;
        int position = 0;
        foreach (Match match in TypeDeclaration.Matches(code))
        {
            for (; position < match.Index; position++)
            {
                if (code[position] == '{')
                {
                    depth++;
                }
                else if (code[position] == '}')
                {
                    depth--;
                }
            }

            // Every type here is PascalCase; a lower-case "name" is a keyword the pattern caught after a constraint
            // (where T : class where U : …), and a keyword in the list would fence half the language.
            string name = match.Groups["name"].Value;
            if (depth == namespaceDepth && char.IsUpper(name[0]))
            {
                yield return name;
            }
        }
    }

    /// <summary>The collector's nesting rule, on a synthetic file of each namespace shape.</summary>
    [Theory]
    [InlineData("namespace N;\ninternal sealed class Outer { private sealed record Inner(int X); }\ninternal enum Kind { A }", "Outer,Kind")]
    [InlineData("namespace N\n{\n    internal sealed class Outer { private sealed class Inner { } }\n    internal record struct Pair(int A);\n}", "Outer,Pair")]
    [InlineData("namespace N;\ninternal static class Bridges { extension(Foo) { } }\ninternal interface IThing { }", "Bridges,IThing")]
    public void The_collector_takes_namespace_level_types_only(string source, string expected)
    {
        TopLevelTypes(SourceScanner.StripCommentsAndStrings(source)).Should().Equal(expected.Split(','));
    }
}
