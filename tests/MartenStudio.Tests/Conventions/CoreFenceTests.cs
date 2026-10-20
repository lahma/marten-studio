using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The fence between the shared studio and MartenStudio, after the move: no C# or Razor file under
/// <c>src/PostgresStudio/</c> names a Marten, JasperFx or Weasel type or namespace, a <c>MartenStudio</c> namespace, or
/// any type, component or global alias MartenStudio declares - in code, in a <c>cref</c>, in Razor markup text or in an
/// attribute value.
/// </summary>
/// <remarks>
/// <para>
/// <b>What changed with the move, and what did not.</b> Before PS-2 both halves compiled into one assembly, and this fence
/// read the files a manifest said were bound for the shared one. Since PS-2 the shared studio is <c>PostgresStudio.dll</c>,
/// which references neither Marten nor MartenStudio, so the whole of <c>src/PostgresStudio/</c> is the boundary and no
/// manifest is needed for scope: every file under it is fenced, a file added there tomorrow included. A lean in code no
/// longer compiles, and <see cref="PostgresStudioFenceTests" /> holds the assembly and package half of that. What the
/// compiler cannot see is text it does not bind: Razor markup text - <c>&lt;code&gt;MartenStudioOptions.ReadOnly&lt;/code&gt;</c>
/// builds with 0 warnings, and would tell a host of the shared studio to set an option it does not have - and an
/// attribute value Razor takes as a literal. Those are the residual this fence exists for; the rest it keeps because it
/// costs nothing and fails by name where the compiler would fail by error number.
/// </para>
/// <para>
/// <b>How each file is read.</b> As code, with comments and strings blanked by <see cref="SourceScanner" />, for a
/// <c>using</c> of a Marten-family or <c>MartenStudio</c> namespace, a qualified name in one, and any identifier from the
/// stay-behind list; as documentation, for every <c>cref</c>; and, for a Razor file, its markup text (an
/// <c>@Expression</c> sits in it, and a prose word that collides with a MartenStudio type name fails loudly and is
/// reworded) and every tag's attribute values (<c>href="@Links.To(…)"</c>, <c>Capability="@nameof(…)"</c>, a component
/// parameter's bare C#), which the string pass would have blanked. Comments and C# string literals are prose and never
/// fail it. An import that brings a namespace into every file at once - a <c>global using</c>, a csproj
/// <c>&lt;Using&gt;</c>, an <c>_Imports.razor</c> line - is refused on its own
/// (<see cref="No_import_brings_a_Marten_family_namespace_into_every_shared_file" />), since no per-file scan would see it.
/// </para>
/// <para>
/// <b>The stay-behind list</b> is every top-level type and Razor component declared in a file under
/// <c>src/MartenStudio/</c>, and every <c>global using</c> alias such a file declares (<c>MartenTable</c>), less any name a
/// file under <c>src/PostgresStudio/</c> declares too (<see cref="StayBehindNames" />). It is derived, not curated. A type
/// or an alias is matched wherever its name appears; a component only where it is named as one - a tag, <c>typeof</c>,
/// <c>nameof</c>, <c>@inherits</c> - because <c>Schema</c> is as good a property name as a page name.
/// </para>
/// <para>
/// <b>Not on it, and not fenced here:</b> C# string literals. Several shared services still name
/// <c>MartenStudioOptions.*</c> in refusal and log text; routing those through the settings is PostgresStudio's own
/// packet's work, and a string pass here would fail on every one of them at once.
/// </para>
/// </remarks>
public class CoreFenceTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The shared studio's root, repository-relative: every file under it is fenced.</summary>
    private const string SharedRoot = "src/PostgresStudio/";

    /// <summary>MartenStudio's root, repository-relative: the stay-behind list is what its files declare.</summary>
    private const string MartenRoot = "src/MartenStudio/";

    /// <summary>The namespaces a shared file may not name: Marten's family, and MartenStudio's own.</summary>
    private static readonly string[] Foreign = ["MartenStudio", "Marten", "JasperFx", "Weasel"];

    /// <summary>
    /// <c>using Marten;</c>, <c>using static JasperFx.X;</c>, <c>using A = Weasel.B;</c>, <c>@using Marten.Storage</c>,
    /// <c>using MartenStudio.Services;</c>.
    /// </summary>
    private static readonly Regex ForeignUsing = new(
        @"^\s*@?(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(?:global::)?(?:MartenStudio|Marten|JasperFx|Weasel)\b",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>A qualified name: <c>Marten.Storage.IMartenDatabase</c>, <c>global::JasperFx.Descriptors</c>, <c>MartenStudio.Services.X</c>.</summary>
    private static readonly Regex ForeignQualifiedName = new(
        @"(?<![\w.])(?:global::)?(?:MartenStudio|Marten|JasperFx|Weasel)\.[A-Za-z_][\w.]*",
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

    private static readonly Regex NamespaceDeclaration = new(
        @"^\s*@?namespace\s+(?<name>[\w.]+)\s*(?:[;{]|$)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex RazorNamespace = new(@"^\s*@namespace\s", RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Lazy<StayBehindNames> StayBehind = new(CollectStayBehind);

    /// <summary>
    /// The names only MartenStudio declares: the types of its <c>.cs</c> files, matched wherever they appear, and its
    /// Razor components, matched only where a component is named as one - a tag, <c>typeof</c>, <c>nameof</c>,
    /// <c>@inherits</c> - because a component's class is its file name, and <c>Schema</c> or <c>Overview</c> is as good
    /// a property name in shared code as it is a page name in MartenStudio's.
    /// </summary>
    /// <param name="Types">Types and global aliases declared in MartenStudio's <c>.cs</c> files.</param>
    /// <param name="Components">Components declared by MartenStudio's <c>.razor</c> files.</param>
    internal sealed record StayBehindNames(IReadOnlySet<string> Types, IReadOnlySet<string> Components)
    {
        /// <summary>Every name, either kind.</summary>
        public IReadOnlySet<string> All { get; } = Types.Union(Components).ToHashSet(StringComparer.Ordinal);

        /// <summary>A set of type names alone, for the theory.</summary>
        public static StayBehindNames OfTypes(params string[] types) =>
            new(types.ToHashSet(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal) { "Overview", "Schema" });
    }

    /// <summary>Every C# and Razor file of the shared studio, repository-relative, build output excluded.</summary>
    private static string[] SharedFiles() =>
        [.. SourceScanner.ShippedSourceFiles().Select(SourceScanner.Relative).Where(static x => x.StartsWith(SharedRoot, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The one fence: every C# and Razor file under <c>src/PostgresStudio/</c> names no Marten-family type or namespace,
    /// no MartenStudio namespace, and nothing MartenStudio declares.
    /// </summary>
    [Fact]
    public void Every_file_of_the_shared_studio_names_nothing_MartenStudio_keeps()
    {
        string[] files = SharedFiles();
        files.Should().HaveCountGreaterThan(250, "the shared studio is some 265 files, so a fence over a fraction of it passes for the wrong reason");
        files.Should().Contain(static x => x.EndsWith(".razor", StringComparison.Ordinal), "the markup is the residual this fence is for")
            .And.Contain(static x => x.EndsWith(".cs", StringComparison.Ordinal));

        StayBehindNames stayBehind = StayBehind.Value;
        stayBehind.Types.Should().NotBeEmpty("a fence against nothing passes for the wrong reason");

        List<string> problems = [];

        foreach (string file in files)
        {
            string source = File.ReadAllText(RepositoryRoot.Combine(file.Split('/')));

            SourceScanner.StripCommentsAndStrings(source, out bool unterminated);
            if (unterminated)
            {
                problems.Add(file + ": the scanner could not follow the file to its end");
            }

            bool razor = file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
            foreach (string lean in Leans(source, stayBehind, razor))
            {
                problems.Add(file + ": " + lean);
            }
        }

        problems.Should().BeEmpty(
            "a file of the shared studio names no Marten, JasperFx or Weasel type, no MartenStudio namespace and nothing "
            + "MartenStudio declares - in code, a cref, markup text or an attribute value:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Each studio declares namespaces under its own root and nowhere else: every C# file of the shared studio declares,
    /// file-scoped, exactly the namespace its folder gives under <c>PostgresStudio</c>, no Razor file there overrides its
    /// folder's with <c>@namespace</c>, and no MartenStudio file declares a namespace under <c>PostgresStudio</c>. So every
    /// shared type is a <c>PostgresStudio.*</c> type, which is what its log category, its public name and the "no type on
    /// both sides" rule below are read from.
    /// </summary>
    [Fact]
    public void Each_studio_declares_namespaces_under_its_own_root_and_the_shared_one_by_its_folders()
    {
        List<string> problems = [];
        int sharedFiles = 0;
        int martenFiles = 0;

        foreach (string file in SourceScanner.ShippedSourceFiles().Select(SourceScanner.Relative))
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine(file.Split('/'))));
            string[] declared = [.. NamespaceDeclaration.Matches(code).Select(static x => x.Groups["name"].Value)];

            if (file.StartsWith(SharedRoot, StringComparison.Ordinal))
            {
                sharedFiles++;

                if (file.EndsWith(".razor", StringComparison.Ordinal))
                {
                    if (RazorNamespace.IsMatch(code))
                    {
                        problems.Add(file + ": @namespace overrides the folder's namespace");
                    }
                }
                else if (declared is not [{ } only] || only != FolderNamespace("PostgresStudio", file[SharedRoot.Length..]))
                {
                    problems.Add(file + ": declares [" + string.Join(", ", declared) + "], not " + FolderNamespace("PostgresStudio", file[SharedRoot.Length..]));
                }
            }
            else if (file.StartsWith(MartenRoot, StringComparison.Ordinal))
            {
                martenFiles++;

                problems.AddRange(declared
                    .Where(static x => x == "PostgresStudio" || x.StartsWith("PostgresStudio.", StringComparison.Ordinal))
                    .Select(x => file + ": MartenStudio declares " + x + ", the shared studio's namespace"));
            }
        }

        sharedFiles.Should().BeGreaterThan(250, "every file of the shared studio is read");
        martenFiles.Should().BeGreaterThan(100, "every file of MartenStudio is read");
        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    /// <summary>The namespace a file's folder gives under <paramref name="root" />.</summary>
    private static string FolderNamespace(string root, string pathBelowRoot)
    {
        int slash = pathBelowRoot.LastIndexOf('/');
        return slash < 0 ? root : root + "." + pathBelowRoot[..slash].Replace('/', '.');
    }

    /// <summary>
    /// No type is declared on both sides at the same place in the namespace tree - <c>MartenStudio.X.T</c> beside
    /// <c>PostgresStudio.X.T</c>. That is the shape a partial class split across the move leaves behind: the part that
    /// stayed compiles as a second type of the same name, and every file that imports both namespaces (MartenStudio's
    /// <c>_Imports.razor</c> imports both halves of every split folder) sees an ambiguity the compiler reports only where
    /// the name is used. The stay-behind list cannot see it, because a name both sides declare is left off that list on
    /// purpose (<see cref="CollectStayBehind" />).
    /// </summary>
    [Fact]
    public void No_type_is_declared_on_both_sides_of_the_move()
    {
        List<(string File, string Code, bool Shared)> files = [];

        foreach (string path in SourceScanner.ShippedSourceFiles())
        {
            string file = SourceScanner.Relative(path);
            bool shared = file.StartsWith(SharedRoot, StringComparison.Ordinal);

            if (!shared && !file.StartsWith(MartenRoot, StringComparison.Ordinal))
            {
                continue;
            }

            // A component is a class named for its file in its folder's namespace (neither side overrides it with
            // @namespace - the rule above holds the shared side), so it is read as that declaration.
            string code = file.EndsWith(".razor", StringComparison.Ordinal)
                ? "namespace " + FolderNamespace(shared ? "PostgresStudio" : "MartenStudio", file[(shared ? SharedRoot : MartenRoot).Length..])
                    + ";\ninternal partial class " + Path.GetFileNameWithoutExtension(file) + " { }"
                : SourceScanner.StripCommentsAndStrings(File.ReadAllText(path));

            files.Add((file, code, shared));
        }

        files.Should().Contain(static x => x.Shared).And.Contain(static x => !x.Shared);
        files.Should().Contain(static x => x.File.EndsWith(".razor", StringComparison.Ordinal) && x.Shared)
            .And.Contain(static x => x.File.EndsWith(".razor", StringComparison.Ordinal) && !x.Shared, "components are compared too");

        SplitDeclarations(files).Should().BeEmpty("a partial type does not survive being split between two assemblies");
    }

    /// <summary>The split-declaration rule, on synthetic files: a partial split across the line is found; a namesake elsewhere in the tree is not.</summary>
    [Theory]
    [InlineData("namespace PostgresStudio.A;\ninternal sealed partial class Counter { }", "namespace MartenStudio.A;\ninternal sealed partial class Counter { }", "A.Counter")]
    [InlineData("namespace PostgresStudio;\ninternal sealed class Counter { }", "namespace MartenStudio;\ninternal sealed class Counter { }", "Counter")]
    [InlineData("namespace PostgresStudio.A;\ninternal sealed record ReferencedBy(int X);", "namespace MartenStudio.B;\ninternal sealed class ReferencedBy { }", "")]
    [InlineData("namespace PostgresStudio.A;\ninternal sealed class Counter { }", "namespace MartenStudio.A;\ninternal static class CounterExtensions { }", "")]
    public void A_type_split_across_the_move_is_found(string shared, string stays, string expected)
    {
        SplitDeclarations([("shared.cs", shared, true), ("stays.cs", stays, false)]).Should().Equal(
            expected.Length == 0 ? Array.Empty<string>() : new[] { expected });
    }

    /// <summary>
    /// Every type name declared in a shared file and in one that stays at the same namespace below each studio's root.
    /// </summary>
    private static List<string> SplitDeclarations(IEnumerable<(string File, string Code, bool Shared)> files)
    {
        HashSet<string> shared = new(StringComparer.Ordinal);
        HashSet<string> stays = new(StringComparer.Ordinal);

        foreach ((string _, string code, bool isShared) in files)
        {
            string ns = NamespaceDeclaration.Match(code) is { Success: true } m ? m.Groups["name"].Value : string.Empty;
            string below = BelowRoot(ns, isShared ? "PostgresStudio" : "MartenStudio");

            foreach (string name in TopLevelTypes(code))
            {
                (isShared ? shared : stays).Add(below.Length == 0 ? name : below + "." + name);
            }
        }

        return [.. shared.Intersect(stays).Order(StringComparer.Ordinal)];
    }

    /// <summary><paramref name="ns" /> without its studio's root: <c>MartenStudio.Services.Health</c> is <c>Services.Health</c>.</summary>
    private static string BelowRoot(string ns, string root) =>
        ns == root ? string.Empty
        : ns.StartsWith(root + ".", StringComparison.Ordinal) ? ns[(root.Length + 1)..]
        : "(" + ns + ")";

    /// <summary>
    /// No type the shared studio declares is named for MartenStudio. The move renamed the five shared types that were
    /// (design Appendix D) - the endpoint guard, marker and tracker, the convention builder and the health check - and a
    /// <c>MartenStudio*</c> name in <c>PostgresStudio.dll</c> would be one a host of the shared studio meets in a stack
    /// trace and a log category. Types about Marten's own objects (<c>MartenDeclaredObject</c>) are a seam's vocabulary and
    /// keep their names.
    /// </summary>
    [Fact]
    public void No_type_the_shared_studio_declares_is_named_for_MartenStudio()
    {
        Dictionary<string, List<string>> declaredIn = new(StringComparer.Ordinal);

        foreach (string file in SourceScanner.ShippedSourceFiles())
        {
            string relative = SourceScanner.Relative(file);
            IEnumerable<string> names = relative.EndsWith(".razor", StringComparison.Ordinal)
                ? [Path.GetFileNameWithoutExtension(file)]
                : TopLevelTypes(SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)));

            foreach (string name in names)
            {
                (declaredIn.TryGetValue(name, out List<string>? where) ? where : declaredIn[name] = []).Add(relative);
            }
        }

        string[] named =
        [
            .. declaredIn
                .Where(static x => x.Key.StartsWith("MartenStudio", StringComparison.Ordinal))
                .SelectMany(static x => x.Value.Where(static f => f.StartsWith(SharedRoot, StringComparison.Ordinal)).Select(f => f + ": " + x.Key)),
        ];

        named.Should().BeEmpty("the shared studio's types take neutral names: " + string.Join(", ", named));

        // Not vacuous: the five renamed types are the shared studio's, and only its.
        foreach (string renamed in new[] { "StudioEndpointAuthorizationGuard", "StudioEndpointMarker", "StudioMappedEndpoints", "StudioConventionBuilder", "StudioHealthCheck" })
        {
            declaredIn.Should().ContainKey(renamed);
            declaredIn[renamed].Should().ContainSingle().Which.Should().StartWith(SharedRoot, "{0} moved and was renamed with its file", renamed);
        }

        declaredIn.Keys.Should().Contain(static x => x.StartsWith("MartenStudio", StringComparison.Ordinal), "MartenStudio's own MartenStudio* types are read, or the rule reads nothing");
    }

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
            "each is declared in a .cs file under src/MartenStudio/ - MartenTable as a global alias");

        stayBehind.Components.Should().Contain(
            ["MartenStudioApp", "Projections", "Overview", "Schema"],
            "each is a component under src/MartenStudio/");

        stayBehind.All.Should().NotContain(
            ["StudioScope", "StudioState", "IStudioSettings", "StudioDescriptor", "IStudioSourceProvider", "ResolvedSource",
             "StudioLog", "StudioRouteTable", "NavMenu", "StudioDocument", "ComponentModel", "StoreAvailability", "TenantList",
             "ReferencedBy"],
            "each is declared under src/PostgresStudio/ - ReferencedBy by RelationshipModels.cs as well as by a Marten page, "
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
    // Found: a MartenStudio namespace, which only MartenStudio has after the move.
    [InlineData("using MartenStudio.Services;\nclass C { }", "using")]
    [InlineData("@using MartenStudio.Components.Pages\n<p>x</p>", "using")]
    [InlineData("class C { MartenStudio.Components.Foo? f; }", "qualified")]
    [InlineData("<p>@MartenStudio.Components.Foo.Bar</p>", "qualified")]
    // Found: a qualified name, in code and in a cref.
    [InlineData("class C { Marten.IDocumentStore? store; }", "qualified")]
    [InlineData("class C { bool b = x is global::JasperFx.Descriptors.DatabaseCardinality.Single; }", "qualified")]
    [InlineData("/// <see cref=\"Marten.Storage.IMartenDatabase\" />\nclass C { }", "cref")]
    [InlineData("/// <inheritdoc cref=\"T:Weasel.Core.IDatabase\" />\nclass C { }", "cref")]
    [InlineData("/// <see cref=\"MartenStudio.Services.Foo\" />\nclass C { }", "cref")]
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
    [InlineData("class C { string s = \"MartenStudioOptions.ReadOnly\"; }", null)]
    [InlineData("@* MartenStudioOptions.ReadOnly *@\n<p>x</p>", null)]
    [InlineData("using PostgresStudio.Services;\nnamespace PostgresStudio.Components;\nclass C { PostgresStudio.Services.StudioScope? s; }", null)]
    [InlineData("<p>registered with Marten.</p>", null)]
    [InlineData("<p>MartenStudio's Query page renders it.</p>", null)]
    [InlineData("/// <see cref=\"StudioScope\" />\nclass C { }", null)]
    [InlineData("internal enum Owner { MartenDocument, MartenEventStore }", null)]
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
    [InlineData("<span title=\"Set MartenStudioOptions.ReadOnly to false\">x</span>", "identifier")]
    [InlineData("<a href=\"@Marten.Links.To(x)\">x</a>", "qualified")]
    [InlineData("<a href=\"@($\"{JasperFx.Core.Paths.Root}/x\")\">x</a>", "qualified")]
    [InlineData("<a href=\"@MartenStudio.Services.Links.To(x)\">x</a>", "qualified")]
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
    /// <c>global using</c> and no <c>_Imports.razor</c> line under <c>src/PostgresStudio/</c>, and no <c>&lt;Using&gt;</c>
    /// item in its project file or in a <c>Directory.Build.*</c> file, names a Marten, JasperFx, Weasel or MartenStudio
    /// namespace. MartenStudio's own imports are its business since the move: they reach only its own files.
    /// </summary>
    [Fact]
    public void No_import_brings_a_Marten_family_namespace_into_every_shared_file()
    {
        List<string> problems = [];

        string[] files = SharedFiles();
        files.Should().Contain(static x => x.EndsWith("/_Imports.razor", StringComparison.Ordinal), "the shared studio's imports are scanned");

        foreach (string file in files)
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine(file.Split('/'))));
            bool imports = Path.GetFileName(file).Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase);

            foreach (Match match in ForeignUsing.Matches(code))
            {
                if (imports || match.Value.TrimStart().StartsWith("global", StringComparison.Ordinal))
                {
                    problems.Add(file + ": " + match.Value.Trim());
                }
            }
        }

        string[] projectFiles =
        [
            .. Directory.EnumerateFiles(RepositoryRoot.Combine("src", StudioProjects.Shared), "*.csproj", SearchOption.TopDirectoryOnly),
            .. RepositoryProjectFiles
                .Select(static x => RepositoryRoot.Combine(x))
                .Where(File.Exists),
        ];

        projectFiles.Select(SourceScanner.Relative).Should().Contain(
            "src/PostgresStudio/PostgresStudio.csproj", "scanning no project file would pass for the wrong reason");

        foreach (string file in projectFiles)
        {
            foreach (Match match in ProjectUsing.Matches(File.ReadAllText(file)))
            {
                problems.Add(SourceScanner.Relative(file) + ": " + match.Value);
            }
        }

        problems.Should().BeEmpty(
            "an import that reaches every file would put a Marten or MartenStudio namespace in every shared file without a "
            + "line of it saying so: " + string.Join(", ", problems));
    }

    /// <summary>The project-file and global-import half of the rule, on synthetic input.</summary>
    [Theory]
    [InlineData("<ItemGroup><Using Include=\"Marten\" /></ItemGroup>", true)]
    [InlineData("<ItemGroup>\n  <Using Include=\"JasperFx.Events\" Static=\"true\" />\n</ItemGroup>", true)]
    [InlineData("<ItemGroup><Using Include='Weasel.Postgresql' Alias='W' /></ItemGroup>", true)]
    [InlineData("<ItemGroup><Using Include=\"MartenStudio.Services\" /></ItemGroup>", true)]
    [InlineData("<ItemGroup><Using Include=\"PostgresStudio.Services\" /></ItemGroup>", false)]
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

    /// <summary>A csproj or props <c>&lt;Using Include="…"&gt;</c> of a Marten-family or MartenStudio namespace.</summary>
    private static readonly Regex ProjectUsing = new(
        @"<Using\s+Include\s*=\s*[""'](?:MartenStudio|Marten|JasperFx|Weasel)(?:\.[\w.]*)?[""'][^>]*>",
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

        foreach (Match match in ForeignUsing.Matches(code))
        {
            found.Add("using: " + match.Value.Trim());
        }

        // A qualified name on a using line is the using's finding, not a second one.
        string withoutUsings = ForeignUsing.Replace(code, static x => new string(' ', x.Length));
        foreach (Match match in ForeignQualifiedName.Matches(withoutUsings))
        {
            found.Add("qualified: " + match.Value.TrimEnd('.'));
        }

        // An identifier inside a qualified name is that name's finding too.
        string withoutQualified = ForeignQualifiedName.Replace(withoutUsings, static x => new string(' ', x.Length));
        foreach (string name in Identifiers(withoutQualified).Where(stayBehind.Types.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            found.Add("identifier: " + name);
        }

        foreach (Match match in ComponentUse.Matches(withoutQualified))
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

            if (Foreign.Any(x => bare.StartsWith(x + ".", StringComparison.Ordinal) || bare.StartsWith("global::" + x + ".", StringComparison.Ordinal))
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
    /// Every top-level type and component declared in a file under <c>src/MartenStudio/</c>, and every global alias such a
    /// file declares, less every name a file under <c>src/PostgresStudio/</c> declares too - a name both halves declare (a
    /// Marten page and a shared record both called <c>ReferencedBy</c>) is one the text cannot attribute, and the compiler
    /// resolves it by namespace.
    /// </summary>
    private static StayBehindNames CollectStayBehind()
    {
        HashSet<string> types = new(StringComparer.Ordinal);
        HashSet<string> components = new(StringComparer.Ordinal);
        HashSet<string> shared = new(StringComparer.Ordinal);

        foreach (string file in SourceScanner.ShippedSourceFiles())
        {
            string relative = SourceScanner.Relative(file);
            bool isShared = relative.StartsWith(SharedRoot, StringComparison.Ordinal);

            if (!isShared && !relative.StartsWith(MartenRoot, StringComparison.Ordinal))
            {
                continue;
            }

            if (relative.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            {
                // A component's class is its file name; the types its @code declares are nested in it.
                (isShared ? shared : components).Add(Path.GetFileNameWithoutExtension(file));
                continue;
            }

            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));

            // A global alias reaches every file of the assembly, as a type does: MartenTableFacts.cs's MartenTable names a
            // shared type, but under a name the shared assembly does not have.
            foreach (string name in TopLevelTypes(code).Concat(GlobalAliases(code)))
            {
                (isShared ? shared : types).Add(name);
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
