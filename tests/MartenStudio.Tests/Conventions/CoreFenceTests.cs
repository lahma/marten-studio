using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// The fence between the shared studio and MartenStudio, before the move: a file bound for the shared assembly names
/// no Marten, JasperFx or Weasel type and no type MartenStudio keeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a text scanner, and what it reads.</b> Until PS-2 moves the files, both halves compile into one assembly, so
/// the compiler cannot tell a shared file that leans on MartenStudio from one that does not - and every such lean is a
/// compile error the move would discover all at once. So each fenced file (<see cref="CoreManifest" />, by packet) is
/// read twice: as code, with comments and strings blanked by <see cref="SourceScanner" />, for a <c>using</c> of a
/// Marten-family namespace, a <c>Marten.</c>/<c>JasperFx.</c>/<c>Weasel.</c> qualified name and any identifier from
/// the stay-behind list; and as documentation, for every <c>cref</c> - a cref to a type the shared assembly cannot see
/// is CS1574, an error under <c>TreatWarningsAsErrors</c>.
/// </para>
/// <para>
/// <b>The stay-behind list</b> is every top-level type and Razor component declared in a <c>src/</c> file that is not on
/// the manifest, less any name a manifest file declares too (<see cref="StayBehindNames" />). It is derived, not
/// curated: a file a packet splits off as MartenStudio's adds its types the moment it exists, and a type on a manifest
/// row is never on it. A type is matched wherever its name appears; a component only where it is named as one - a tag,
/// <c>typeof</c>, <c>nameof</c>, <c>@inherits</c> - because <c>Schema</c> is as good a property name as a page name.
/// </para>
/// <para>
/// <b>What it cannot see.</b> A call that binds to a MartenStudio extension member reads like a call on the shared type
/// (<c>StudioCapabilityGuard.OptionName(…)</c> resolves to <c>MartenServiceBridges</c>); the bridges list their
/// remaining shared-bound callers, and PS-2's compile is the proof. Razor markup text is scanned as code, which is the
/// safe direction: a prose word that collides with a MartenStudio type name fails loudly and is reworded.
/// </para>
/// </remarks>
public partial class CoreFenceTests
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
        @"(?<![\w.])(?:Marten|JasperFx|Weasel)\.[A-Za-z_]",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex Identifier = new(@"(?<![\w@])[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant, RegexTimeout);

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

    /// <summary>Every row names a file that exists, and no file is on the manifest twice.</summary>
    [Fact]
    public void Every_manifest_row_is_a_file_that_exists()
    {
        CoreManifest.Entries.Should().NotBeEmpty();
        CoreManifest.Entries.Select(static x => x.Path).Should().OnlyHaveUniqueItems(static x => x.ToLowerInvariant());

        string[] missing =
        [
            .. CoreManifest.Entries
                .Where(static x => !File.Exists(RepositoryRoot.Combine(x.RepositoryPath.Split('/'))))
                .Select(static x => x.RepositoryPath),
        ];

        missing.Should().BeEmpty("a manifest row for a file that is gone is a fence that scans nothing: " + string.Join(", ", missing));
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
             "MartenServiceBridges", "MartenComponentBridges", "DocumentComponentModel"],
            "each is declared in a .cs file the manifest leaves to MartenStudio");

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
    // Found: a stay-behind type, in code, in markup and in a cref.
    [InlineData("class C { MartenStoreRegistry? registry; }", "identifier")]
    [InlineData("@inject IOptions<MartenStudioOptions> Options\n<p>x</p>", "identifier")]
    [InlineData("<p><code>MartenStudioOptions.ReadOnly</code></p>", "identifier")]
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
    [InlineData("using MartenStudio.Services;\nnamespace MartenStudio.Components;\nclass C { MartenStudio.Services.StudioScope? s; }", null)]
    [InlineData("<p>registered with Marten.</p>", null)]
    [InlineData("/// <see cref=\"StudioScope\" />\nclass C { }", null)]
    public void The_scanner_finds_each_kind_of_lean_and_ignores_prose(string source, string? expected)
    {
        StayBehindNames stayBehind = StayBehindNames.OfTypes("MartenStoreRegistry", "MartenStudioOptions");

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
    /// Scans every row <paramref name="packet" /> fences and fails with every lean it finds, by file.
    /// </summary>
    private static void AssertFenced(CoreManifest.Packet packet)
    {
        IReadOnlyList<CoreManifest.Entry> rows = CoreManifest.Of(packet);
        rows.Should().NotBeEmpty("a fence over nothing passes for the wrong reason");

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

            foreach (string lean in Leans(source, stayBehind))
            {
                problems.Add(row.RepositoryPath + ": " + lean);
            }
        }

        problems.Should().BeEmpty(
            "a file bound for the shared assembly names no Marten, JasperFx or Weasel type and no type MartenStudio keeps:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>Every lean in <paramref name="source" />, each as "kind: what".</summary>
    private static List<string> Leans(string source, StayBehindNames stayBehind)
    {
        List<string> found = [];
        string code = SourceScanner.StripCommentsAndStrings(source);

        foreach (Match match in MartenUsing.Matches(code))
        {
            found.Add("using: " + match.Value.Trim());
        }

        // A qualified name on a using line is the using's finding, not a second one.
        string withoutUsings = MartenUsing.Replace(code, static x => new string(' ', x.Length));
        foreach (Match match in MartenQualifiedName.Matches(withoutUsings))
        {
            found.Add("qualified: " + Line(withoutUsings, match.Index));
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

        return found;
    }

    /// <summary>A component named as one: <c>&lt;Name</c>, <c>typeof(Name)</c>, <c>nameof(Name)</c>, <c>@inherits Name</c>.</summary>
    private static readonly Regex ComponentUse = new(
        @"(?:<|\btypeof\s*\(\s*|\bnameof\s*\(\s*|@inherits\s+)(?<name>[A-Z][A-Za-z0-9_]*)\b",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static IEnumerable<string> Identifiers(string code) =>
        Identifier.Matches(code).Select(static x => x.Value);

    private static string Line(string text, int index)
    {
        int start = text.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;
        int end = text.IndexOf('\n', index);
        return text[start..(end < 0 ? text.Length : end)].Trim();
    }

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

            foreach (string name in TopLevelTypes(SourceScanner.StripCommentsAndStrings(File.ReadAllText(file))))
            {
                (onManifest ? shared : types).Add(name);
            }
        }

        types.ExceptWith(shared);
        components.ExceptWith(shared);
        return new StayBehindNames(types, components);
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
