using System.Text.RegularExpressions;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// Keeps the layout tree in <c>docs/maintainers/layout.md</c> honest about the directories that actually exist.
/// </summary>
/// <remarks>
/// <para>
/// AGENTS.md points every agent at the layout tree, and the tree is what an agent reads before
/// deciding where a new file goes. Nothing else in the repository reads that tree, so left unchecked it
/// would drift the moment a directory is added - and an agent would confidently put the file somewhere
/// that no longer exists, or fail to find something that does. The failure is silent in both directions,
/// which is why it is a test rather than a review item.
/// </para>
/// <para>
/// The contract is deliberately narrow and mechanical: inside the <c>```text</c> block under
/// <c># Layout</c>, the first whitespace-delimited token of a line is a directory entry when it ends in
/// <c>/</c>. The set of those entries must equal the set of directories on disk under <c>src/</c>,
/// <c>analyzers/</c>, <c>samples/</c>, <c>tests/</c>, <c>build/</c>, <c>.claude/</c> and <c>docs/</c> - build output excluded. Files may be
/// listed too and are ignored here; only directories are held to account, because they are the part that
/// says "this is where that kind of code lives".
/// </para>
/// <para>
/// A directory on disk counts when a file outside build output lies somewhere below it, which is what a clone carries. The folder
/// of a renamed project, left holding only its <c>bin/</c> and <c>obj/</c>, and an empty directory, which git does not carry, are
/// no directory of the repository's, so a machine that built the 1.x projects passes as CI does
/// (<see cref="A_directory_counts_only_when_it_holds_a_file_outside_build_output" />).
/// </para>
/// </remarks>
public class AgentsLayoutTests
{
    /// <summary>Roots whose directory structure the layout tree has to describe completely.</summary>
    private static readonly string[] DescribedRoots = ["src", "analyzers", "samples", "tests", "build", ".claude", "docs"];

    /// <summary>Directory names that are build output, never source, and never listed.</summary>
    private static readonly string[] ExcludedSegments = ["bin", "obj", "artifacts"];

    [Fact]
    public void The_layout_tree_names_every_directory_that_exists_and_nothing_that_does_not()
    {
        var onDisk = DirectoriesOnDisk();
        var documented = DocumentedDirectories();

        onDisk.Should().NotBeEmpty("the repository has directories, so a vacuous pass is a broken test");

        var missing = onDisk.Except(documented).Order(StringComparer.Ordinal).ToArray();
        var stale = documented.Except(onDisk).Order(StringComparer.Ordinal).ToArray();

        missing.Should().BeEmpty(
            "docs/maintainers/layout.md's tree must name every directory that exists; add: " + string.Join(", ", missing));
        stale.Should().BeEmpty(
            "docs/maintainers/layout.md's tree names directories that do not exist; remove: " + string.Join(", ", stale));
    }

    [Fact]
    public void A_directory_counts_only_when_it_holds_a_file_outside_build_output()
    {
        string root = Path.Combine(Path.GetTempPath(), "agents-layout-" + Guid.NewGuid().ToString("N"));

        try
        {
            Write(root, "src/Leftover/bin/Debug/net10.0/Leftover.dll");
            Write(root, "src/Leftover/obj/project.assets.json");
            Write(root, "src/Real/Code.cs");
            Write(root, "src/Real/bin/Debug/Real.dll");
            Write(root, "src/Nested/Deep/Code.cs");
            Directory.CreateDirectory(Path.Combine(root, "src", "Empty"));
            Write(root, "samples/Leftover/obj/project.assets.json");

            DirectoriesUnder(root).Order(StringComparer.Ordinal).Should().Equal("src/", "src/Nested/", "src/Nested/Deep/", "src/Real/");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static void Write(string root, string relative)
        {
            string file = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Empty);
        }
    }

    [Fact]
    public void The_layout_section_is_a_single_text_fenced_block()
    {
        ExtractLayoutBlock().Should().NotBeNullOrWhiteSpace(
            "the # Layout heading must be followed by one ```text fenced block - that block is what this " +
            "test parses, and what a reader is expected to trust");
    }

    private static HashSet<string> DirectoriesOnDisk() => DirectoriesUnder(RepositoryRoot.FullPath);

    /// <summary>The directories of the described roots under <paramref name="repository" /> that hold a file outside build output.</summary>
    private static HashSet<string> DirectoriesUnder(string repository)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in DescribedRoots)
        {
            var rootPath = Path.Combine(repository, root);

            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            foreach (var directory in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories).Prepend(rootPath))
            {
                var relative = Path.GetRelativePath(repository, directory).Replace('\\', '/');

                if (relative.Split('/').Any(x => ExcludedSegments.Contains(x, StringComparer.Ordinal)) || !HoldsSource(directory))
                {
                    continue;
                }

                result.Add(relative + "/");
            }
        }

        return result;
    }

    /// <summary>Whether a file lies in <paramref name="directory" /> or below it, outside build output.</summary>
    private static bool HoldsSource(string directory) =>
        Directory.EnumerateFiles(directory).Any()
        || Directory.EnumerateDirectories(directory)
            .Any(x => !ExcludedSegments.Contains(Path.GetFileName(x), StringComparer.Ordinal) && HoldsSource(x));

    private static HashSet<string> DocumentedDirectories()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in ExtractLayoutBlock().Split('\n'))
        {
            var token = line.Trim().Split(' ', '\t')[0];

            if (token.EndsWith('/') && DescribedRoots.Any(x => token.StartsWith(x + "/", StringComparison.Ordinal)))
            {
                result.Add(token);
            }
        }

        return result;
    }

    private static string ExtractLayoutBlock()
    {
        var agents = File.ReadAllText(RepositoryRoot.Combine("docs", "maintainers", "layout.md")).Replace("\r\n", "\n");

        var match = Regex.Match(
            agents,
            @"^#\s+Layout\s*$.*?^```text\s*$\n(?<block>.*?)^```\s*$",
            RegexOptions.Multiline | RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        return match.Success ? match.Groups["block"].Value : string.Empty;
    }
}
