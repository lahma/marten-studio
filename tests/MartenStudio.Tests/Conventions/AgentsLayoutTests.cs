using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Keeps the <c>## Layout</c> tree in <c>AGENTS.md</c> honest about the directories that actually exist.
/// </summary>
/// <remarks>
/// <para>
/// AGENTS.md is the single source of truth, and the layout tree is the part of it an agent reads before
/// deciding where a new file goes. Nothing else in the repository reads that tree, so left unchecked it
/// would drift the moment a directory is added - and an agent would confidently put the file somewhere
/// that no longer exists, or fail to find something that does. The failure is silent in both directions,
/// which is why it is a test rather than a review item.
/// </para>
/// <para>
/// The contract is deliberately narrow and mechanical: inside the <c>```text</c> block under
/// <c>## Layout</c>, the first whitespace-delimited token of a line is a directory entry when it ends in
/// <c>/</c>. The set of those entries must equal the set of directories on disk under <c>src/</c>,
/// <c>samples/</c>, <c>tests/</c>, <c>build/</c> and <c>.claude/</c> - build output excluded. Files may be
/// listed too and are ignored here; only directories are held to account, because they are the part that
/// says "this is where that kind of code lives".
/// </para>
/// </remarks>
public class AgentsLayoutTests
{
    /// <summary>Roots whose directory structure the layout tree has to describe completely.</summary>
    private static readonly string[] DescribedRoots = ["src", "samples", "tests", "build", ".claude", "docs"];

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
            "AGENTS.md's ## Layout tree must name every directory that exists; add: " + string.Join(", ", missing));
        stale.Should().BeEmpty(
            "AGENTS.md's ## Layout tree names directories that do not exist; remove: " + string.Join(", ", stale));
    }

    [Fact]
    public void The_layout_section_is_a_single_text_fenced_block()
    {
        ExtractLayoutBlock().Should().NotBeNullOrWhiteSpace(
            "the ## Layout heading must be followed by one ```text fenced block - that block is what this " +
            "test parses, and what a reader is expected to trust");
    }

    private static HashSet<string> DirectoriesOnDisk()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in DescribedRoots)
        {
            var rootPath = RepositoryRoot.Combine(root);

            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            result.Add(root + "/");

            foreach (var directory in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(RepositoryRoot.FullPath, directory).Replace('\\', '/');

                if (relative.Split('/').Any(x => ExcludedSegments.Contains(x, StringComparer.Ordinal)))
                {
                    continue;
                }

                result.Add(relative + "/");
            }
        }

        return result;
    }

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
        var agents = File.ReadAllText(RepositoryRoot.Combine("AGENTS.md")).Replace("\r\n", "\n");

        var match = Regex.Match(
            agents,
            @"^##\s+Layout\s*$.*?^```text\s*$\n(?<block>.*?)^```\s*$",
            RegexOptions.Multiline | RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

        return match.Success ? match.Groups["block"].Value : string.Empty;
    }
}
